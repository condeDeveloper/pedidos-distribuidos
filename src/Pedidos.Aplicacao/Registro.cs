using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Pedidos.Aplicacao;

public static class RegistroAplicacao
{
    public static IServiceCollection AdicionarAplicacao(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<CriarPedidoComando>();
            cfg.AddOpenBehavior(typeof(LogBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidacaoBehavior<,>));
        });
        services.AddValidatorsFromAssemblyContaining<CriarPedidoValidador>(includeInternalTypes: true);
        services.AddSingleton<IRelogio, RelogioDoSistema>();
        return services;
    }
}
