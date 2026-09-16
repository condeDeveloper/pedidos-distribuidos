using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public static IServiceCollection AdicionarInfra(this IServiceCollection services, IConfiguration config)
    {
        var op = LerOpcoes(config);
        services.AddSingleton(op);

        services.AddDbContext<PedidosDbContext>(o =>
        {
            if (op.Banco.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
                o.UseNpgsql(config.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres ausente"));
            else
                o.UseSqlite(config.GetConnectionString("Sqlite") ?? "Data Source=pedidos.db");
        });
        services.AddScoped<IRepositorioPedidos, RepositorioPedidos>();
        services.AddScoped<IConsultaPedidos, ConsultaPedidos>();
        services.AddScoped<IUnidadeDeTrabalho, UnidadeDeTrabalho>();
        services.AddScoped<RelayOutbox>();
        services.AddScoped<ProcessadorFaturamento>();

        if (op.Mensageria.Equals("RabbitMq", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new ConexaoRabbitMq(config.GetConnectionString("RabbitMq") ?? throw new InvalidOperationException("ConnectionStrings:RabbitMq ausente")));
            services.AddSingleton<IBarramento, BarramentoRabbitMq>();
        }
        else
        {
            services.AddSingleton<BarramentoEmMemoria>();
            services.AddSingleton<IBarramento>(sp => sp.GetRequiredService<BarramentoEmMemoria>());
        }

        if (op.Cache.Equals("Redis", StringComparison.OrdinalIgnoreCase))
            services.AddStackExchangeRedisCache(o => { o.Configuration = config.GetConnectionString("Redis"); o.InstanceName = "pedidos:"; });
        else
            services.AddDistributedMemoryCache();

        return services;
    }

    /// <summary>Worker: relay do outbox e consumidor de faturamento, na variante do barramento configurado.</summary>
    public static IServiceCollection AdicionarWorker(this IServiceCollection services, IConfiguration config)
    {
        var op = LerOpcoes(config);
        services.AddHostedService<ServicoRelayOutbox>();
        if (op.Mensageria.Equals("RabbitMq", StringComparison.OrdinalIgnoreCase)) services.AddHostedService<ConsumidorFaturamentoRabbitMq>();
        else services.AddHostedService<ConsumidorFaturamentoEmMemoria>();
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
