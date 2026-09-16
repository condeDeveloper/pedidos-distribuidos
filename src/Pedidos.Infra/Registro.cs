using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pedidos.Aplicacao;
using Pedidos.Infra.Mensageria;
using Pedidos.Infra.Persistencia;

namespace Pedidos.Infra;

/// <summary>Opções lidas de appsettings ou variáveis de ambiente (Banco__Provedor, Mensageria__Provedor, Cache__Provedor).</summary>
public sealed class OpcoesInfra
{
    public string Banco { get; set; } = "Sqlite";       // Postgres | Sqlite
    public string Mensageria { get; set; } = "Memoria"; // RabbitMq | Memoria
    public string Cache { get; set; } = "Memoria";      // Redis | Memoria
    public bool AplicarMigrations { get; set; } = true;
}

public static class RegistroInfra
{
    public static OpcoesInfra LerOpcoes(IConfiguration config) => new()
    {
        Banco = config["Banco:Provedor"] ?? "Sqlite",
        Mensageria = config["Mensageria:Provedor"] ?? "Memoria",
        Cache = config["Cache:Provedor"] ?? "Memoria",
        AplicarMigrations = config.GetValue("Banco:AplicarMigrations", true),
    };

    /// <summary>
    /// Os provedores são decididos quando o container resolve cada serviço, e não na hora de registrar. Assim a
    /// configuração que chega depois (variáveis de ambiente, WebApplicationFactory nos testes) ainda é respeitada.
    /// </summary>
    public static IServiceCollection AdicionarInfra(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(sp => LerOpcoes(sp.GetRequiredService<IConfiguration>()));

        services.AddDbContext<PedidosDbContext>((sp, o) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            if (Eh(sp, op => op.Banco, "Postgres"))
                o.UseNpgsql(cfg.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres ausente"));
            else
                o.UseSqlite(cfg.GetConnectionString("Sqlite") ?? "Data Source=pedidos.db");
        });
        services.AddScoped<IRepositorioPedidos, RepositorioPedidos>();
        services.AddScoped<IConsultaPedidos, ConsultaPedidos>();
        services.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();
        services.AddScoped<RelayOutbox>();
        services.AddScoped<ProcessadorFaturamento>();

        services.AddSingleton<BarramentoEmMemoria>();
        services.AddSingleton(sp => new ConexaoRabbitMq(sp.GetRequiredService<IConfiguration>().GetConnectionString("RabbitMq") ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq ausente")));
        services.AddSingleton<IBarramento>(sp => Eh(sp, op => op.Mensageria, "RabbitMq")
            ? new BarramentoRabbitMq(sp.GetRequiredService<ConexaoRabbitMq>(), sp.GetRequiredService<ILogger<BarramentoRabbitMq>>())
            : sp.GetRequiredService<BarramentoEmMemoria>());

        services.AddSingleton<IDistributedCache>(sp => Eh(sp, op => op.Cache, "Redis")
            ? new RedisCache(Options.Create(new RedisCacheOptions { Configuration = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis"), InstanceName = "pedidos:" }))
            : new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

        return services;
    }

    private static bool Eh(IServiceProvider sp, Func<OpcoesInfra, string> campo, string valor) =>
        campo(sp.GetRequiredService<OpcoesInfra>()).Equals(valor, StringComparison.OrdinalIgnoreCase);

    /// <summary>Worker: relay do outbox e consumidor de faturamento, na variante do barramento configurado.</summary>
    public static IServiceCollection AdicionarWorker(this IServiceCollection services, IConfiguration config)
    {
        services.AddHostedService<ServicoRelayOutbox>();
        services.AddSingleton<IHostedService>(sp => Eh(sp, op => op.Mensageria, "RabbitMq")
            ? ActivatorUtilities.CreateInstance<ConsumidorFaturamentoRabbitMq>(sp)
            : ActivatorUtilities.CreateInstance<ConsumidorFaturamentoEmMemoria>(sp));
        return services;
    }

    /// <summary>Postgres: aplica migrations pendentes. SQLite: cria o esquema direto (uso local e testes).</summary>
    public static async Task PrepararBancoAsync(IServiceProvider sp, CancellationToken ct = default)
    {
        await using var escopo = sp.CreateAsyncScope();
        var op = escopo.ServiceProvider.GetRequiredService<OpcoesInfra>();
        var db = escopo.ServiceProvider.GetRequiredService<PedidosDbContext>();
        if (!op.AplicarMigrations) return;
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);
    }
}
