using Pedidos.Dominio;

namespace Pedidos.Tests;

public class DominioTests
{
    private static readonly DateTime Agora = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static Pedido Novo() => Pedido.Criar("cliente-1", [new ItemPedido("Teclado", 2, 150m), new ItemPedido("Mouse", 1, 80.50m)], Agora);

    [Fact]
    public void CriaComTotalEEvento()
    {
        var p = Novo();
        p.Status.Should().Be(StatusPedido.Criado);
        p.Total.Should().Be(380.50m);
        p.Itens.Should().HaveCount(2);
        p.Eventos.Should().ContainSingle().Which.Should().BeOfType<PedidoCriado>().Which.QuantidadeDeItens.Should().Be(2);
        p.Versao.Should().NotBeEmpty();
    }

    [Fact]
    public void RejeitaPedidosInvalidos()
    {
        var semItens = () => Pedido.Criar("c", [], Agora);
        semItens.Should().Throw<RegraDeNegocioException>().WithMessage("*sem itens*");
        var semCliente = () => Pedido.Criar(" ", [new ItemPedido("x", 1, 1m)], Agora);
        semCliente.Should().Throw<RegraDeNegocioException>();
        var qtdZero = () => new ItemPedido("x", 0, 1m);
        qtdZero.Should().Throw<RegraDeNegocioException>();
        var gratis = () => Pedido.Criar("c", [new ItemPedido("brinde", 1, 0m)], Agora);
        gratis.Should().Throw<RegraDeNegocioException>().WithMessage("*positivo*");
        var demais = () => Pedido.Criar("c", Enumerable.Range(0, 51).Select(i => new ItemPedido($"p{i}", 1, 1m)), Agora);
        demais.Should().Throw<RegraDeNegocioException>();
    }

    [Fact]
    public void FluxoFelizCriadoPagoFaturado()
    {
        var p = Novo();
        var v0 = p.Versao;
        p.ConfirmarPagamento("pix-123", Agora.AddMinutes(1));
        p.Status.Should().Be(StatusPedido.Pago);
        p.Versao.Should().NotBe(v0, "cada transição gera nova versão para concorrência otimista");
        p.Faturar("NF-1", Agora.AddMinutes(2));
        p.Status.Should().Be(StatusPedido.Faturado);
        p.NumeroNotaFiscal.Should().Be("NF-1");
        p.Eventos.Select(e => e.Nome).Should().Equal("pedido.criado", "pedido.pago", "pedido.faturado");
        p.LimparEventos();
        p.Eventos.Should().BeEmpty();
    }

    [Fact]
    public void TransicoesInvalidas()
    {
        var p = Novo();
        var faturarSemPagar = () => p.Faturar("NF", Agora);
        faturarSemPagar.Should().Throw<RegraDeNegocioException>().WithMessage("*Criado não pode ir para Faturado*");
        p.ConfirmarPagamento("ref", Agora);
        var pagarDuasVezes = () => p.ConfirmarPagamento("ref2", Agora);
        pagarDuasVezes.Should().Throw<RegraDeNegocioException>();
        p.Faturar("NF", Agora);
        var cancelarFaturado = () => p.Cancelar("desisti", Agora);
        cancelarFaturado.Should().Throw<RegraDeNegocioException>().WithMessage("*faturado*");
    }

    [Fact]
    public void CancelaCriadoOuPago()
    {
        var a = Novo();
        a.Cancelar("sem estoque", Agora);
        a.Status.Should().Be(StatusPedido.Cancelado);
        a.MotivoCancelamento.Should().Be("sem estoque");

        var b = Novo();
        b.ConfirmarPagamento("ref", Agora);
        b.Cancelar("estorno", Agora);
        b.Status.Should().Be(StatusPedido.Cancelado);
        b.Eventos.Last().Should().BeOfType<PedidoCancelado>();
    }
}
