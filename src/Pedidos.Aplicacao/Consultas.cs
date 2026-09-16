using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Pedidos.Dominio;

namespace Pedidos.Aplicacao;

public sealed record ObterPedidoConsulta(Guid PedidoId) : IRequest<PedidoDto?>;
public sealed record ListarPedidosConsulta(string? ClienteId, StatusPedido? Status, int Pagina = 1, int Tamanho = 20) : IRequest<PaginaDto<PedidoResumoDto>>;

public sealed class ListarPedidosValidador : AbstractValidator<ListarPedidosConsulta>
{
    public ListarPedidosValidador()
    {
        RuleFor(c => c.Pagina).GreaterThanOrEqualTo(1);
        RuleFor(c => c.Tamanho).InclusiveBetween(1, 100);
    }
}

/// <summary>Leitura com cache-aside: tenta o cache distribuído, senão consulta e guarda por 30 segundos.</summary>
public sealed class ObterPedidoHandler(IConsultaPedidos consulta, IDistributedCache cache) : IRequestHandler<ObterPedidoConsulta, PedidoDto?>
{
    private static readonly DistributedCacheEntryOptions Opcoes = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };

    public async Task<PedidoDto?> Handle(ObterPedidoConsulta c, CancellationToken ct)
    {
        var chave = Chaves.Pedido(c.PedidoId);
        var emCache = await cache.GetStringAsync(chave, ct);
        if (emCache is not null) return JsonSerializer.Deserialize<PedidoDto>(emCache);

        var dto = await consulta.ObterAsync(c.PedidoId, ct);
        if (dto is not null) await cache.SetStringAsync(chave, JsonSerializer.Serialize(dto), Opcoes, ct);
        return dto;
    }
}

public sealed class ListarPedidosHandler(IConsultaPedidos consulta) : IRequestHandler<ListarPedidosConsulta, PaginaDto<PedidoResumoDto>>
{
    public Task<PaginaDto<PedidoResumoDto>> Handle(ListarPedidosConsulta c, CancellationToken ct) => consulta.ListarAsync(c.ClienteId, c.Status, c.Pagina, c.Tamanho, ct);
}
