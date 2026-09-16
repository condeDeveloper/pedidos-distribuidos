using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pedidos.Infra.Mensageria;

namespace Pedidos.Tests;

/// <summary>
/// Sobe a API com SQLite em arquivo temporário, barramento em memória e cache em memória, e liga o consumidor de
/// faturamento em memória para o fluxo completo rodar dentro do processo de teste. Com PEDIDOS_INTEGRACAO=1, usa
/// Postgres, RabbitMQ e Redis reais pelas connection strings do ambiente.
/// </summary>
public sealed class FabricaDeTeste : WebApplicationFactory<Program>
{
    public static readonly bool Integracao = Environment.GetEnvironmentVariable("PEDIDOS_INTEGRACAO") == "1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _arquivo = Path.Combine(Path.GetTempPath(), $"pedidos-teste-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var valores = new Dictionary<string, string?>
            {
                ["Banco:Provedor"] = Integracao ? "Postgres" : "Sqlite",
                ["Mensageria:Provedor"] = Integracao ? "RabbitMq" : "Memoria",
                ["Cache:Provedor"] = Integracao ? "Redis" : "Memoria",
                ["ConnectionStrings:Sqlite"] = $"Data Source={_arquivo}",
                ["RateLimit:PorMinuto"] = "100000",
            };
            if (Integracao)
            {
                valores["ConnectionStrings:Postgres"] = Environment.GetEnvironmentVariable("PEDIDOS_POSTGRES") ?? "Host=localhost;Port=5432;Database=pedidos;Username=pedidos;Password=pedidos";
                valores["ConnectionStrings:RabbitMq"] = Environment.GetEnvironmentVariable("PEDIDOS_RABBITMQ") ?? "amqp://pedidos:pedidos@localhost:5672/";
                valores["ConnectionStrings:Redis"] = Environment.GetEnvironmentVariable("PEDIDOS_REDIS") ?? "localhost:6379";
            }
            cfg.AddInMemoryCollection(valores);
        });
        builder.ConfigureServices(s =>
        {
            // o consumidor normalmente vive no worker; aqui entra no host da API para o fluxo fechar no teste
            if (Integracao) s.AddHostedService<ConsumidorFaturamentoRabbitMq>();
            else s.AddHostedService<ConsumidorFaturamentoEmMemoria>();
        });
    }

    public async Task<HttpClient> ClienteAutenticadoAsync()
    {
        var http = CreateClient();
        var r = await http.PostAsJsonAsync("/api/auth/token", new { usuario = "admin", senha = "admin" });
        r.EnsureSuccessStatusCode();
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("token").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>Roda o relay do outbox uma vez, como o worker faria.</summary>
    public async Task<int> PublicarOutboxAsync()
    {
        await using var escopo = Services.CreateAsyncScope();
        return await escopo.ServiceProvider.GetRequiredService<RelayOutbox>().PublicarPendentesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !Integracao)
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(_arquivo); } catch (IOException) { }
        }
    }
}
