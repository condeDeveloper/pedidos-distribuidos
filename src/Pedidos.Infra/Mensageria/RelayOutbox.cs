using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pedidos.Aplicacao;
using Pedidos.Infra.Persistencia;

namespace Pedidos.Infra.Mensageria;

/// <summary>
/// Lê o outbox em ordem e publica no barramento, marcando cada mensagem como publicada. Se o broker cair no meio,
/// a mensagem fica sem PublicadoEm e é tentada de novo (entrega pelo menos uma vez; consumidores são idempotentes).
/// </summary>
public sealed class RelayOutbox(PedidosDbContext db, IBarramento barramento, IRelogio relogio, ILogger<RelayOutbox> logger)
{
    public const int MaximoDeTentativas = 10;

    public async Task<int> PublicarPendentesAsync(int lote = 50, CancellationToken ct = default)
    {
        var pendentes = await db.Outbox
            .Where(m => m.PublicadoEm == null && m.Tentativas < MaximoDeTentativas)
            .OrderBy(m => m.CriadoEm).Take(lote).ToListAsync(ct);
        var publicadas = 0;
        foreach (var m in pendentes)
        {
            try
            {
                await barramento.PublicarAsync(m.Topico, m.Corpo, m.Id, ct);
                m.PublicadoEm = relogio.AgoraUtc;
                m.UltimoErro = null;
                publicadas++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                m.Tentativas++;
                m.UltimoErro = e.Message.Length > 1000 ? e.Message[..1000] : e.Message;
                logger.LogWarning(e, "falha ao publicar {MensagemId} ({Topico}), tentativa {Tentativa}", m.Id, m.Topico, m.Tentativas);
            }
        }
        if (pendentes.Count > 0) await db.SaveChangesAsync(ct);
        return publicadas;
    }
}

/// <summary>Roda o relay em ciclo, com um escopo de DI por rodada.</summary>
public sealed class ServicoRelayOutbox(IServiceScopeFactory escopos, ILogger<ServicoRelayOutbox> logger) : BackgroundService
{
    public static TimeSpan Intervalo { get; set; } = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation("relay do outbox iniciado, intervalo {Intervalo}", Intervalo);
        using var timer = new PeriodicTimer(Intervalo);
        do
        {
            try
            {
                await using var escopo = escopos.CreateAsyncScope();
                var n = await escopo.ServiceProvider.GetRequiredService<RelayOutbox>().PublicarPendentesAsync(ct: ct);
                if (n > 0) logger.LogInformation("outbox: {Quantidade} mensagem(ns) publicada(s)", n);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "erro no ciclo do relay");
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }
}
