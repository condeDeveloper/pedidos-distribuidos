using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pedidos.Api;
using Pedidos.Aplicacao;
using Pedidos.Dominio;
using Pedidos.Infra;
using Pedidos.Infra.Mensageria;
using Pedidos.Infra.Persistencia;
using Prometheus;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// ---- logs estruturados (JSON compacto, um evento por linha, prontos para Loki/ELK) ----
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("servico", "pedidos-api")
    .WriteTo.Console(new CompactJsonFormatter()));

// ---- camadas ----
builder.Services.AdicionarAplicacao();
builder.Services.AdicionarInfra(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ---- rastreamento distribuído: ASP.NET Core, HttpClient, Npgsql e os spans da aplicação, exportados por OTLP ----
var otlp = builder.Configuration["Otlp:Endpoint"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("pedidos-api", serviceVersion: "1.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health") && !ctx.Request.Path.StartsWithSegments("/metrics"))
         .AddHttpClientInstrumentation()
         .AddNpgsql()
         .AddSource("Pedidos.Aplicacao");
        if (!string.IsNullOrWhiteSpace(otlp)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlp));
    });

// ---- autenticação JWT ----
var jwt = builder.Configuration.GetSection("Jwt");
var chave = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Chave"] ?? throw new InvalidOperationException("Jwt:Chave ausente")));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
{
    ValidIssuer = jwt["Emissor"],
    ValidAudience = jwt["Audiencia"],
    IssuerSigningKey = chave,
    ClockSkew = TimeSpan.FromSeconds(30),
});
builder.Services.AddAuthorization();

// ---- rate limiting por cliente ----
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:PorMinuto", 120), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// ---- health checks: liveness (processo vivo) e readiness (dependências) ----
builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<PedidosDbContext>("banco", tags: ["ready"])
    .AddCheck<VerificacaoBarramento>("barramento", tags: ["ready"])
    .AddCheck<VerificacaoCache>("cache", tags: ["ready"]);

// ---- erros como ProblemDetails (RFC 7807) ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<TratadorDeExcecoes>();

// ---- swagger com esquema Bearer ----
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Pedidos", Version = "v1", Description = "API de pedidos com CQRS, outbox transacional, RabbitMQ, PostgreSQL, Redis e observabilidade." });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", Description = "Obtenha o token em POST /api/auth/token" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] });
});

var app = builder.Build();

await RegistroInfra.PrepararBancoAsync(app.Services);

app.UseExceptionHandler();
app.UseMiddleware<CorrelacaoMiddleware>();
app.UseSerilogRequestLogging();
app.UseHttpMetrics();
app.UseAuthentication();
app.UseRateLimiter(); // depois da autenticação, para particionar por usuário
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI(o => { o.RoutePrefix = "docs"; o.DocumentTitle = "Pedidos"; });

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = RespostaDeSaude.Escrever });
app.MapMetrics("/metrics");

var auth = app.MapGroup("/api/auth").WithTags("Autenticação");
auth.MapPost("/token", (CredenciaisRequest cred, IConfiguration config) =>
{
    if (cred.Usuario != config["Auth:Usuario"] || cred.Senha != config["Auth:Senha"]) return Results.Unauthorized();
    var expira = DateTime.UtcNow.AddHours(8);
    var token = new JwtSecurityToken(jwt["Emissor"], jwt["Audiencia"],
        [new Claim(ClaimTypes.Name, cred.Usuario), new Claim(ClaimTypes.Role, "operador")],
        expires: expira, signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));
    return Results.Ok(new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), expira));
}).WithSummary("Emite um JWT para o usuário configurado (demonstração)");

var pedidos = app.MapGroup("/api/pedidos").WithTags("Pedidos").RequireAuthorization();

pedidos.MapPost("/", async (CriarPedidoComando comando, ISender mediator, CancellationToken ct) =>
{
    var id = await mediator.Send(comando, ct);
    Metricas.PedidosCriados.Inc();
    return Results.Created($"/api/pedidos/{id}", new { id });
}).WithSummary("Cria um pedido e registra o evento pedido.criado no outbox");

pedidos.MapGet("/{id:guid}", async (Guid id, ISender mediator, CancellationToken ct) =>
    await mediator.Send(new ObterPedidoConsulta(id), ct) is { } dto ? Results.Ok(dto) : Results.NotFound())
    .WithSummary("Consulta um pedido (cache-aside de 30 s)");

pedidos.MapGet("/", async ([FromQuery] string? clienteId, [FromQuery] StatusPedido? status, [FromQuery] int? pagina, [FromQuery] int? tamanho, ISender mediator, CancellationToken ct) =>
    Results.Ok(await mediator.Send(new ListarPedidosConsulta(clienteId, status, pagina ?? 1, tamanho ?? 20), ct)))
    .WithSummary("Lista pedidos paginados com filtros");

pedidos.MapPost("/{id:guid}/pagamento", async (Guid id, PagamentoRequest req, ISender mediator, CancellationToken ct) =>
{
    await mediator.Send(new ConfirmarPagamentoComando(id, req.Referencia), ct);
    Metricas.PedidosPagos.Inc();
    return Results.NoContent();
}).WithSummary("Confirma o pagamento; o evento pedido.pago dispara o faturamento no worker");

pedidos.MapPost("/{id:guid}/cancelamento", async (Guid id, CancelamentoRequest req, ISender mediator, CancellationToken ct) =>
{
    await mediator.Send(new CancelarPedidoComando(id, req.Motivo), ct);
    return Results.NoContent();
}).WithSummary("Cancela um pedido criado ou pago");

app.MapGet("/", () => Results.Redirect("/docs")).ExcludeFromDescription();

app.Run();

namespace Pedidos.Api
{
    public sealed record CredenciaisRequest(string Usuario, string Senha);
    public sealed record TokenResponse(string Token, DateTime ExpiraEm);
    public sealed record PagamentoRequest(string Referencia);
    public sealed record CancelamentoRequest(string Motivo);

    public static class Metricas
    {
        public static readonly Counter PedidosCriados = Prometheus.Metrics.CreateCounter("pedidos_criados_total", "Pedidos criados pela API");
        public static readonly Counter PedidosPagos = Prometheus.Metrics.CreateCounter("pedidos_pagos_total", "Pagamentos confirmados pela API");
    }

}

public partial class Program { }
