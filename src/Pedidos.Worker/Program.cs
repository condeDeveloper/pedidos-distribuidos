using Pedidos.Aplicacao;
using Pedidos.Infra;
using Serilog;
using Serilog.Formatting.Compact;

// Worker: processo separado da API que faz o relay do outbox para o RabbitMQ e consome a fila de faturamento.
// Escala independente da API e não compete com as requisições HTTP.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((sp, cfg) => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("servico", "pedidos-worker")
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AdicionarAplicacao();
builder.Services.AdicionarInfra(builder.Configuration);
builder.Services.AdicionarWorker(builder.Configuration);

var host = builder.Build();
await RegistroInfra.PrepararBancoAsync(host.Services);
await host.RunAsync();
