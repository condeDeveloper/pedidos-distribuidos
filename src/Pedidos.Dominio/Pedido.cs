namespace Pedidos.Dominio;

public enum StatusPedido
{
    Criado,
    Pago,
    Faturado,
    Cancelado,
}

/// <summary>Violação de regra de negócio. A API responde 422 com a mensagem.</summary>
public sealed class RegraDeNegocioException(string mensagem) : Exception(mensagem);

public sealed class ItemPedido
{
    private ItemPedido() { }

    public ItemPedido(string produto, int quantidade, decimal precoUnitario)
    {
        if (string.IsNullOrWhiteSpace(produto)) throw new RegraDeNegocioException("item sem produto");
        if (quantidade <= 0) throw new RegraDeNegocioException($"quantidade inválida para '{produto}'");
        if (precoUnitario < 0) throw new RegraDeNegocioException($"preço negativo para '{produto}'");
        Produto = produto.Trim();
        Quantidade = quantidade;
        PrecoUnitario = precoUnitario;
    }

    public string Produto { get; private set; } = "";
    public int Quantidade { get; private set; }
    public decimal PrecoUnitario { get; private set; }
    public decimal Subtotal => Quantidade * PrecoUnitario;
}

/// <summary>
/// Agregado de pedido. Toda mudança de estado passa por um método que valida a transição, atualiza a versão
/// (token de concorrência otimista) e registra um evento de domínio.
/// </summary>
public sealed class Pedido
{
    public const int MaximoDeItens = 50;

    private readonly List<ItemPedido> _itens = new();
    private readonly List<EventoDeDominio> _eventos = new();

    private Pedido() { }

    public Guid Id { get; private set; }
    public string ClienteId { get; private set; } = "";
    public StatusPedido Status { get; private set; }
    public decimal Total { get; private set; }
    public string? ReferenciaPagamento { get; private set; }
    public string? NumeroNotaFiscal { get; private set; }
    public string? MotivoCancelamento { get; private set; }
    public DateTime CriadoEm { get; private set; }
    public DateTime AtualizadoEm { get; private set; }
    public Guid Versao { get; private set; }
    public IReadOnlyList<ItemPedido> Itens => _itens;
    public IReadOnlyList<EventoDeDominio> Eventos => _eventos;

    public static Pedido Criar(string clienteId, IEnumerable<ItemPedido> itens, DateTime agora)
    {
        if (string.IsNullOrWhiteSpace(clienteId)) throw new RegraDeNegocioException("cliente obrigatório");
        var lista = itens.ToList();
        if (lista.Count == 0) throw new RegraDeNegocioException("pedido sem itens");
        if (lista.Count > MaximoDeItens) throw new RegraDeNegocioException($"pedido com mais de {MaximoDeItens} itens");
        var total = lista.Sum(i => i.Subtotal);
        if (total <= 0) throw new RegraDeNegocioException("total do pedido deve ser positivo");

        var pedido = new Pedido
        {
            Id = Guid.NewGuid(),
            ClienteId = clienteId.Trim(),
            Status = StatusPedido.Criado,
            Total = total,
            CriadoEm = agora,
            AtualizadoEm = agora,
            Versao = Guid.NewGuid(),
        };
        pedido._itens.AddRange(lista);
        pedido._eventos.Add(new PedidoCriado(pedido.Id, pedido.ClienteId, total, lista.Count, agora));
        return pedido;
    }

    public void ConfirmarPagamento(string referencia, DateTime agora)
    {
        if (string.IsNullOrWhiteSpace(referencia)) throw new RegraDeNegocioException("referência do pagamento obrigatória");
        Transicionar(StatusPedido.Pago, agora, de: StatusPedido.Criado);
        ReferenciaPagamento = referencia.Trim();
        _eventos.Add(new PedidoPago(Id, ClienteId, Total, ReferenciaPagamento, agora));
    }

    public void Cancelar(string motivo, DateTime agora)
    {
        if (string.IsNullOrWhiteSpace(motivo)) throw new RegraDeNegocioException("motivo do cancelamento obrigatório");
        if (Status == StatusPedido.Faturado) throw new RegraDeNegocioException("pedido faturado não pode ser cancelado");
        Transicionar(StatusPedido.Cancelado, agora, StatusPedido.Criado, StatusPedido.Pago);
        MotivoCancelamento = motivo.Trim();
        _eventos.Add(new PedidoCancelado(Id, MotivoCancelamento, agora));
    }

    public void Faturar(string numeroNotaFiscal, DateTime agora)
    {
        Transicionar(StatusPedido.Faturado, agora, de: StatusPedido.Pago);
        NumeroNotaFiscal = numeroNotaFiscal;
        _eventos.Add(new PedidoFaturado(Id, numeroNotaFiscal, agora));
    }

    public void LimparEventos() => _eventos.Clear();

    private void Transicionar(StatusPedido para, DateTime agora, params StatusPedido[] de)
    {
        if (!de.Contains(Status)) throw new RegraDeNegocioException($"pedido {Status} não pode ir para {para}");
        Status = para;
        AtualizadoEm = agora;
        Versao = Guid.NewGuid();
    }
}
