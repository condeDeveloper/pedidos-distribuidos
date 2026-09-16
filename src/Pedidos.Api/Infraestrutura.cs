using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pedidos.Aplicacao;
using Pedidos.Dominio;
using Pedidos.Infra.Mensageria;
using Serilog.Context;

namespace Pedidos.Api;

/// <summary>Converte exceções conhecidas em ProblemDetails com o status certo; o resto vira 500 sem vazar detalhes.</summary>
public sealed class TratadorDeExcecoes(IProblemDetailsService problemas, ILogger<TratadorDeExcecoes> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception e, CancellationToken ct)
    {
        var (status, titulo, extensoes) = e switch
        {
            ValidationException v => (StatusCodes.Status400BadRequest, "dados inválidos",
                new Dictionary<string, object?> { ["erros"] = v.Errors.GroupBy(f => f.PropertyName).ToDictionary(g => g.Key, g => g.Select(f => f.ErrorMessage).ToArray()) }),
            RecursoNaoEncontradoException => (StatusCodes.Status404NotFound, e.Message, null),
            RegraDeNegocioException => (StatusCodes.Status422UnprocessableEntity, e.Message, null),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "o pedido foi alterado por outra operação; tente novamente", null),
            BadHttpRequestException b => (b.StatusCode, b.Message, null),
            _ => (StatusCodes.Status500InternalServerError, "erro interno", null),
        };
        if (status >= 500) logger.LogError(e, "erro não tratado em {Caminho}", ctx.Request.Path);
        ctx.Response.StatusCode = status;
        return await problemas.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = e,
            ProblemDetails = new() { Status = status, Title = titulo, Extensions = extensoes ?? new Dictionary<string, object?>() },
        });
    }
}

/// <summary>Propaga ou cria o X-Correlation-Id e o coloca em todos os logs da requisição.</summary>
public sealed class CorrelacaoMiddleware(RequestDelegate proximo)
{
    public const string Cabecalho = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers.TryGetValue(Cabecalho, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : Guid.NewGuid().ToString("N");
        ctx.Response.Headers[Cabecalho] = id;
        using (LogContext.PushProperty("CorrelationId", id))
            await proximo(ctx);
    }
}

public sealed class VerificacaoBarramento(IBarramento barramento, IServiceProvider sp) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct)
    {
        if (barramento is BarramentoEmMemoria) return Task.FromResult(HealthCheckResult.Healthy("memória"));
        try
        {
            var conexao = sp.GetRequiredService<ConexaoRabbitMq>();
            using var canal = conexao.Conexao.CreateModel();
            return Task.FromResult(canal.IsOpen ? HealthCheckResult.Healthy("rabbitmq") : HealthCheckResult.Unhealthy("canal fechado"));
        }
        catch (Exception e) { return Task.FromResult(HealthCheckResult.Unhealthy(e.Message)); }
    }
}

public sealed class VerificacaoCache(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct)
    {
        try
        {
            await cache.SetStringAsync("health", "ok", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5) }, ct);
            return await cache.GetStringAsync("health", ct) == "ok" ? HealthCheckResult.Healthy() : HealthCheckResult.Degraded("leitura divergente");
        }
        catch (Exception e) { return HealthCheckResult.Unhealthy(e.Message); }
    }
}

public static class RespostaDeSaude
{
    public static Task Escrever(HttpContext ctx, HealthReport relatorio)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = relatorio.Status.ToString(),
            duracaoMs = relatorio.TotalDuration.TotalMilliseconds,
            verificacoes = relatorio.Entries.Select(e => new { nome = e.Key, status = e.Value.Status.ToString(), descricao = e.Value.Description, duracaoMs = e.Value.Duration.TotalMilliseconds }),
        }));
    }
}
