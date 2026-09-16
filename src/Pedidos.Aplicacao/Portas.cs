using Pedidos.Dominio;

namespace Pedidos.Aplicacao;

/// <summary>Portas que a infraestrutura implementa. A aplicação não conhece EF, RabbitMQ nem Redis.</summary>
public interface IRepositorioPedidos
{
    Task AdicionarAsync(Pedido pedido, CancellationToken ct);
    Task<Pedido?> ObterAsync(Guid id, CancellationToken ct);
}

public interface IUnidadeDeTrabalho
{
    /// <summary>Persiste as mudanças e grava os eventos de domínio pendentes no outbox, na mesma transação.</summary>
    Task<int> SalvarAsync(CancellationToken ct);
}

public interface IConsultaPedidos
{
    Task<PedidoDto?> ObterAsync(Guid id, CancellationToken ct);
    Task<PaginaDto<PedidoResumoDto>> ListarAsync(string? clienteId, StatusPedido? status, int pagina, int tamanho, CancellationToken ct);
}

public interface IRelogio
{
    DateTime AgoraUtc { get; }
}

public sealed class RelogioDoSistema : IRelogio
{
    public DateTime AgoraUtc => DateTime.UtcNow;
}

public sealed class RecursoNaoEncontradoException(string recurso, object id) : Exception($"{recurso} '{id}' não encontrado");

public sealed record ItemDto(string Produto, int Quantidade, decimal PrecoUnitario, decimal Subtotal);

public sealed record PedidoDto(Guid Id, string ClienteId, StatusPedido Status, decimal Total, IReadOnlyList<ItemDto> Itens,
    string? ReferenciaPagamento, string? NumeroNotaFiscal, string? MotivoCancelamento, DateTime CriadoEm, DateTime AtualizadoEm, Guid Versao);

public sealed record PedidoResumoDto(Guid Id, string ClienteId, StatusPedido Status, decimal Total, int QuantidadeDeItens, DateTime CriadoEm);

public sealed record PaginaDto<T>(IReadOnlyList<T> Itens, int Pagina, int Tamanho, int Total)
{
    public int TotalDePaginas => Tamanho == 0 ? 0 : (int)Math.Ceiling(Total / (double)Tamanho);
}
