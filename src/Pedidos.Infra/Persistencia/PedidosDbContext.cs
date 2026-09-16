using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pedidos.Dominio;

namespace Pedidos.Infra.Persistencia;

/// <summary>Mensagem gravada na mesma transação do agregado e publicada depois pelo relay (padrão outbox).</summary>
public sealed class OutboxMensagem
{
    public Guid Id { get; set; }
    public string Topico { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string Corpo { get; set; } = "";
    public DateTime CriadoEm { get; set; }
    public DateTime? PublicadoEm { get; set; }
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
}

public sealed class PedidosDbContext(DbContextOptions<PedidosDbContext> opcoes) : DbContext(opcoes)
{
    public DbSet<Pedido> Pedidos => Set<Pedido>();
    public DbSet<OutboxMensagem> Outbox => Set<OutboxMensagem>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Pedido>(b =>
        {
            b.ToTable("pedidos");
            b.HasKey(p => p.Id);
            b.Property(p => p.ClienteId).HasMaxLength(64).IsRequired();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.Total).HasPrecision(18, 2);
            b.Property(p => p.ReferenciaPagamento).HasMaxLength(64);
            b.Property(p => p.NumeroNotaFiscal).HasMaxLength(32);
            b.Property(p => p.MotivoCancelamento).HasMaxLength(200);
            b.Property(p => p.Versao).IsConcurrencyToken();
            b.HasIndex(p => p.ClienteId);
            b.HasIndex(p => new { p.Status, p.CriadoEm });
            b.Ignore(p => p.Eventos);
            b.OwnsMany(p => p.Itens, i =>
            {
                i.ToTable("itens_pedido");
                i.WithOwner().HasForeignKey("PedidoId");
                i.Property<int>("Id").ValueGeneratedOnAdd();
                i.HasKey("Id");
                i.Property(x => x.Produto).HasMaxLength(120).IsRequired();
                i.Property(x => x.PrecoUnitario).HasPrecision(18, 2);
                i.Ignore(x => x.Subtotal);
            });
            b.Navigation(p => p.Itens).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        mb.Entity<OutboxMensagem>(b =>
        {
            b.ToTable("outbox");
            b.HasKey(m => m.Id);
            b.Property(m => m.Topico).HasMaxLength(100).IsRequired();
            b.Property(m => m.Tipo).HasMaxLength(200).IsRequired();
            b.Property(m => m.Corpo).IsRequired();
            b.Property(m => m.UltimoErro).HasMaxLength(1000);
            b.HasIndex(m => new { m.PublicadoEm, m.CriadoEm });
        });
    }
}

/// <summary>Usado só pela ferramenta `dotnet ef` para gerar migrations contra o provedor PostgreSQL.</summary>
public sealed class FabricaDesignTime : IDesignTimeDbContextFactory<PedidosDbContext>
{
    public PedidosDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PedidosDbContext>().UseNpgsql("Host=localhost;Database=pedidos;Username=pedidos;Password=pedidos").Options);
}
