namespace Pedidos.Dominio;

/// <summary>Fato que aconteceu no agregado. Vira mensagem no outbox e depois no barramento, com o nome como routing key.</summary>
public abstract record EventoDeDominio(Guid PedidoId, DateTime OcorridoEm)
{
    public abstract string Nome { get; }
}

public sealed record PedidoCriado(Guid PedidoId, string ClienteId, decimal Total, int QuantidadeDeItens, DateTime OcorridoEm) : EventoDeDominio(PedidoId, OcorridoEm)
{
    public override string Nome => "pedido.criado";
}

public sealed record PedidoPago(Guid PedidoId, string ClienteId, decimal Total, string ReferenciaPagamento, DateTime OcorridoEm) : EventoDeDominio(PedidoId, OcorridoEm)
{
    public override string Nome => "pedido.pago";
}

public sealed record PedidoCancelado(Guid PedidoId, string Motivo, DateTime OcorridoEm) : EventoDeDominio(PedidoId, OcorridoEm)
{
    public override string Nome => "pedido.cancelado";
}

public sealed record PedidoFaturado(Guid PedidoId, string NumeroNotaFiscal, DateTime OcorridoEm) : EventoDeDominio(PedidoId, OcorridoEm)
{
    public override string Nome => "pedido.faturado";
}
