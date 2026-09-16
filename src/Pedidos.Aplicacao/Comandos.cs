using FluentValidation;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Pedidos.Dominio;

namespace Pedidos.Aplicacao;

public sealed record NovoItem(string Produto, int Quantidade, decimal PrecoUnitario);

public sealed record CriarPedidoComando(string ClienteId, IReadOnlyList<NovoItem> Itens) : IRequest<Guid>;
public sealed record ConfirmarPagamentoComando(Guid PedidoId, string Referencia) : IRequest;
public sealed record CancelarPedidoComando(Guid PedidoId, string Motivo) : IRequest;

public sealed class CriarPedidoValidador : AbstractValidator<CriarPedidoComando>
{
    public CriarPedidoValidador()
    {
        RuleFor(c => c.ClienteId).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Itens).NotEmpty().WithMessage("informe ao menos um item").Must(i => i.Count <= Pedido.MaximoDeItens).WithMessage($"máximo de {Pedido.MaximoDeItens} itens");
        RuleForEach(c => c.Itens).ChildRules(item =>
        {
            item.RuleFor(i => i.Produto).NotEmpty().MaximumLength(120);
            item.RuleFor(i => i.Quantidade).InclusiveBetween(1, 10_000);
            item.RuleFor(i => i.PrecoUnitario).GreaterThanOrEqualTo(0).LessThanOrEqualTo(1_000_000);
        });
    }
}

public sealed class ConfirmarPagamentoValidador : AbstractValidator<ConfirmarPagamentoComando>
{
    public ConfirmarPagamentoValidador()
    {
        RuleFor(c => c.PedidoId).NotEmpty();
        RuleFor(c => c.Referencia).NotEmpty().MaximumLength(64);
    }
}

public sealed class CancelarPedidoValidador : AbstractValidator<CancelarPedidoComando>
{
    public CancelarPedidoValidador()
    {
        RuleFor(c => c.PedidoId).NotEmpty();
        RuleFor(c => c.Motivo).NotEmpty().MaximumLength(200);
    }
}

public sealed class CriarPedidoHandler(IRepositorioPedidos repositorio, IUnidadeDeTrabalho uow, IRelogio relogio) : IRequestHandler<CriarPedidoComando, Guid>
{
    public async Task<Guid> Handle(CriarPedidoComando c, CancellationToken ct)
    {
        var pedido = Pedido.Criar(c.ClienteId, c.Itens.Select(i => new ItemPedido(i.Produto, i.Quantidade, i.PrecoUnitario)), relogio.AgoraUtc);
        await repositorio.AdicionarAsync(pedido, ct);
        await uow.SalvarAsync(ct);
        return pedido.Id;
    }
}

public sealed class ConfirmarPagamentoHandler(IRepositorioPedidos repositorio, IUnidadeDeTrabalho uow, IRelogio relogio, IDistributedCache cache) : IRequestHandler<ConfirmarPagamentoComando>
{
    public async Task Handle(ConfirmarPagamentoComando c, CancellationToken ct)
    {
        var pedido = await repositorio.ObterAsync(c.PedidoId, ct) ?? throw new RecursoNaoEncontradoException("pedido", c.PedidoId);
        pedido.ConfirmarPagamento(c.Referencia, relogio.AgoraUtc);
        await uow.SalvarAsync(ct);
        await cache.RemoveAsync(Chaves.Pedido(c.PedidoId), ct);
    }
}

public sealed class CancelarPedidoHandler(IRepositorioPedidos repositorio, IUnidadeDeTrabalho uow, IRelogio relogio, IDistributedCache cache) : IRequestHandler<CancelarPedidoComando>
{
    public async Task Handle(CancelarPedidoComando c, CancellationToken ct)
    {
        var pedido = await repositorio.ObterAsync(c.PedidoId, ct) ?? throw new RecursoNaoEncontradoException("pedido", c.PedidoId);
        pedido.Cancelar(c.Motivo, relogio.AgoraUtc);
        await uow.SalvarAsync(ct);
        await cache.RemoveAsync(Chaves.Pedido(c.PedidoId), ct);
    }
}

public static class Chaves
{
    public static string Pedido(Guid id) => $"pedido:{id}";
}
