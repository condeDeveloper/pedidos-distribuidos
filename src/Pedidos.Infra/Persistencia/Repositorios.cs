using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pedidos.Aplicacao;
using Pedidos.Dominio;

namespace Pedidos.Infra.Persistencia;

public sealed class RepositorioPedidos(PedidosDbContext db) : IRepositorioPedidos
{
    public async Task AdicionarAsync(Pedido pedido, CancellationToken ct) => await db.Pedidos.AddAsync(pedido, ct);

    public Task<Pedido?> ObterAsync(Guid id, CancellationToken ct) => db.Pedidos.Include(p => p.Itens).FirstOrDefaultAsync(p => p.Id == id, ct);
}

/// <summary>
/// Salva o agregado e converte seus eventos de domínio em linhas do outbox dentro da mesma transação.
/// Assim nunca existe pedido pago sem evento, nem evento sem pedido pago.
/// </summary>
public sealed class UnidadeDeTrabalho(PedidosDbContext db, IRelogio relogio) : IUnidadeDeTrabalho
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> SalvarAsync(CancellationToken ct)
    {
        var agregados = db.ChangeTracker.Entries<Pedido>().Select(e => e.Entity).Where(p => p.Eventos.Count > 0).ToList();
        foreach (var pedido in agregados)
        {
            foreach (var evento in pedido.Eventos)
            {
                db.Outbox.Add(new OutboxMensagem
                {
                    Id = Guid.NewGuid(),
                    Topico = evento.Nome,
                    Tipo = evento.GetType().Name,
                    Corpo = JsonSerializer.Serialize(evento, evento.GetType(), Json),
                    CriadoEm = relogio.AgoraUtc,
                });
            }
            pedido.LimparEventos();
        }
        return await db.SaveChangesAsync(ct);
    }
}

public sealed class ConsultaPedidos(PedidosDbContext db) : IConsultaPedidos
{
    public async Task<PedidoDto?> ObterAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Pedidos.AsNoTracking().Include(p => p.Itens).FirstOrDefaultAsync(p => p.Id == id, ct);
        return p is null ? null : new PedidoDto(p.Id, p.ClienteId, p.Status, p.Total,
            p.Itens.Select(i => new ItemDto(i.Produto, i.Quantidade, i.PrecoUnitario, i.Subtotal)).ToList(),
            p.ReferenciaPagamento, p.NumeroNotaFiscal, p.MotivoCancelamento, p.CriadoEm, p.AtualizadoEm, p.Versao);
    }

    public async Task<PaginaDto<PedidoResumoDto>> ListarAsync(string? clienteId, StatusPedido? status, int pagina, int tamanho, CancellationToken ct)
    {
        var q = db.Pedidos.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(clienteId)) q = q.Where(p => p.ClienteId == clienteId);
        if (status is { } s) q = q.Where(p => p.Status == s);
        var total = await q.CountAsync(ct);
        var itens = await q.OrderByDescending(p => p.CriadoEm).Skip((pagina - 1) * tamanho).Take(tamanho)
            .Select(p => new PedidoResumoDto(p.Id, p.ClienteId, p.Status, p.Total, p.Itens.Count, p.CriadoEm))
            .ToListAsync(ct);
        return new PaginaDto<PedidoResumoDto>(itens, pagina, tamanho, total);
    }
}
