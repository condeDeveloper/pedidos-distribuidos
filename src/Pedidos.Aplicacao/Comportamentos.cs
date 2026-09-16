using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Pedidos.Aplicacao;

/// <summary>Roda todos os validadores registrados para o pedido antes do handler. Falhas viram 400 na API.</summary>
public sealed class ValidacaoBehavior<TPedido, TResposta>(IEnumerable<IValidator<TPedido>> validadores) : IPipelineBehavior<TPedido, TResposta>
    where TPedido : notnull
{
    public async Task<TResposta> Handle(TPedido pedido, RequestHandlerDelegate<TResposta> proximo, CancellationToken ct)
    {
        var lista = validadores.ToList();
        if (lista.Count > 0)
        {
            var contexto = new ValidationContext<TPedido>(pedido);
            var falhas = (await Task.WhenAll(lista.Select(v => v.ValidateAsync(contexto, ct)))).SelectMany(r => r.Errors).Where(f => f is not null).ToList();
            if (falhas.Count > 0) throw new ValidationException(falhas);
        }
        return await proximo();
    }
}

/// <summary>Log estruturado de cada comando ou consulta com a duração, mais um span de rastreamento próprio.</summary>
public sealed class LogBehavior<TPedido, TResposta>(ILogger<LogBehavior<TPedido, TResposta>> logger) : IPipelineBehavior<TPedido, TResposta>
    where TPedido : notnull
{
    public static readonly ActivitySource Fonte = new("Pedidos.Aplicacao");

    public async Task<TResposta> Handle(TPedido pedido, RequestHandlerDelegate<TResposta> proximo, CancellationToken ct)
    {
        var nome = typeof(TPedido).Name;
        using var atividade = Fonte.StartActivity(nome);
        var relogio = Stopwatch.StartNew();
        try
        {
            var resposta = await proximo();
            logger.LogInformation("{Pedido} concluído em {DuracaoMs} ms", nome, relogio.ElapsedMilliseconds);
            return resposta;
        }
        catch (Exception e)
        {
            atividade?.SetStatus(ActivityStatusCode.Error, e.Message);
            logger.LogWarning(e, "{Pedido} falhou em {DuracaoMs} ms: {Erro}", nome, relogio.ElapsedMilliseconds, e.Message);
            throw;
        }
    }
}
