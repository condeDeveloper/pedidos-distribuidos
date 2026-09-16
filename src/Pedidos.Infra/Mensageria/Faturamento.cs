using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pedidos.Aplicacao;
using Pedidos.Dominio;
using Pedidos.Infra.Persistencia;
using Polly;
using Polly.Retry;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Pedidos.Infra.Mensageria;

/// <summary>
/// Reage ao evento <c>pedido.pago</c> emitindo a nota fiscal (simulada) e marcando o pedido como faturado.
/// Idempotente: se a mensagem chegar duas vezes, a segunda não faz nada.
/// </summary>
public sealed class ProcessadorFaturamento(PedidosDbContext db, IUnidadeDeTrabalho uow, IRelogio relogio, IDistributedCache cache, ILogger<ProcessadorFaturamento> logger)
{
    public async Task<bool> ProcessarAsync(string corpo, CancellationToken ct)
    {
        var evento = JsonSerializer.Deserialize<PedidoPago>(corpo, UnidadeDeTrabalho.Json) ?? throw new InvalidOperationException("mensagem sem corpo");
        var pedido = await db.Pedidos.Include(p => p.Itens).FirstOrDefaultAsync(p => p.Id == evento.PedidoId, ct)
            ?? throw new InvalidOperationException($"pedido {evento.PedidoId} não existe");
        if (pedido.Status != StatusPedido.Pago)
        {
            logger.LogInformation("pedido {PedidoId} já está {Status}; mensagem ignorada", pedido.Id, pedido.Status);
            return false;
        }
        var agora = relogio.AgoraUtc;
        var numeroNota = $"NF-{agora:yyyyMMdd}-{pedido.Id.ToString("N")[..8].ToUpperInvariant()}";
        pedido.Faturar(numeroNota, agora);
        await uow.SalvarAsync(ct);
        await cache.RemoveAsync(Chaves.Pedido(pedido.Id), ct); // a API pode ter o pedido em cache como Pago
        logger.LogInformation("pedido {PedidoId} faturado com {NotaFiscal}", pedido.Id, numeroNota);
        return true;
    }
}

/// <summary>
/// Consumidor RabbitMQ da fila de faturamento: prefetch controlado, ack manual, retry com backoff exponencial
/// (Polly) e, esgotadas as tentativas, nack sem requeue para a fila morta.
/// </summary>
public sealed class ConsumidorFaturamentoRabbitMq(ConexaoRabbitMq conexao, IServiceScopeFactory escopos, ILogger<ConsumidorFaturamentoRabbitMq> logger) : BackgroundService
{
    private static readonly ResiliencePipeline Retry = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions { MaxRetryAttempts = 3, BackoffType = DelayBackoffType.Exponential, Delay = TimeSpan.FromMilliseconds(200), UseJitter = true })
        .Build();

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var canal = conexao.CriarCanal();
        canal.BasicQos(prefetchSize: 0, prefetchCount: 10, global: false);
        var consumidor = new AsyncEventingBasicConsumer(canal);
        consumidor.Received += async (_, ea) =>
        {
            var corpo = Encoding.UTF8.GetString(ea.Body.Span);
            try
            {
                await Retry.ExecuteAsync(async token =>
                {
                    await using var escopo = escopos.CreateAsyncScope();
                    await escopo.ServiceProvider.GetRequiredService<ProcessadorFaturamento>().ProcessarAsync(corpo, token);
                }, ct);
                canal.BasicAck(ea.DeliveryTag, multiple: false);
            }
            catch (Exception e)
            {
                logger.LogError(e, "mensagem {MensagemId} enviada para a fila morta", ea.BasicProperties?.MessageId);
                canal.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
            }
        };
        canal.BasicConsume(Topologia.FilaFaturamento, autoAck: false, consumer: consumidor);
        logger.LogInformation("consumindo {Fila}", Topologia.FilaFaturamento);
        ct.Register(() => canal.Dispose());
        return Task.CompletedTask;
    }
}

/// <summary>Versão em memória do consumidor: assina o tópico no barramento em memória.</summary>
public sealed class ConsumidorFaturamentoEmMemoria(BarramentoEmMemoria barramento, IServiceScopeFactory escopos) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        barramento.Assinar(Topologia.RoutingKeyFaturamento, async corpo =>
        {
            await using var escopo = escopos.CreateAsyncScope();
            await escopo.ServiceProvider.GetRequiredService<ProcessadorFaturamento>().ProcessarAsync(corpo, CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
