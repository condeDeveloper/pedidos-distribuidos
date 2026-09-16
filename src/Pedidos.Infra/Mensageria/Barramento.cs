using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Pedidos.Infra.Mensageria;

/// <summary>Publicação de mensagens por tópico. RabbitMQ em produção, memória nos testes.</summary>
public interface IBarramento
{
    Task PublicarAsync(string topico, string corpo, Guid mensagemId, CancellationToken ct);
}

public static class Topologia
{
    public const string Exchange = "pedidos";
    public const string ExchangeErros = "pedidos.dlx";
    public const string FilaFaturamento = "pedidos.faturamento";
    public const string FilaFaturamentoErros = "pedidos.faturamento.erros";
    public const string RoutingKeyFaturamento = "pedido.pago";

    /// <summary>Declara exchanges e filas de forma idempotente. Chamado por quem conecta primeiro.</summary>
    public static void Declarar(IModel canal)
    {
        canal.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true);
        canal.ExchangeDeclare(ExchangeErros, ExchangeType.Fanout, durable: true);
        canal.QueueDeclare(FilaFaturamentoErros, durable: true, exclusive: false, autoDelete: false);
        canal.QueueBind(FilaFaturamentoErros, ExchangeErros, "");
        canal.QueueDeclare(FilaFaturamento, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object> { ["x-dead-letter-exchange"] = ExchangeErros });
        canal.QueueBind(FilaFaturamento, Exchange, RoutingKeyFaturamento);
    }
}

/// <summary>Conexão única e preguiçosa com o RabbitMQ, compartilhada por publicador, consumidor e health check.</summary>
public sealed class ConexaoRabbitMq(string uri) : IDisposable
{
    private readonly Lazy<IConnection> _conexao = new(() =>
    {
        var fabrica = new ConnectionFactory { Uri = new Uri(uri), DispatchConsumersAsync = true, AutomaticRecoveryEnabled = true, ClientProvidedName = "pedidos" };
        return fabrica.CreateConnection();
    });

    public IConnection Conexao => _conexao.Value;
    public bool Aberta => _conexao.IsValueCreated && _conexao.Value.IsOpen;

    public IModel CriarCanal()
    {
        var canal = Conexao.CreateModel();
        Topologia.Declarar(canal);
        return canal;
    }

    public void Dispose()
    {
        if (_conexao.IsValueCreated) _conexao.Value.Dispose();
    }
}

public sealed class BarramentoRabbitMq(ConexaoRabbitMq conexao, ILogger<BarramentoRabbitMq> logger) : IBarramento, IDisposable
{
    private readonly Lazy<IModel> _canal = new(conexao.CriarCanal);
    private readonly SemaphoreSlim _trava = new(1, 1); // IModel não é thread-safe

    public async Task PublicarAsync(string topico, string corpo, Guid mensagemId, CancellationToken ct)
    {
        await _trava.WaitAsync(ct);
        try
        {
            var canal = _canal.Value;
            var props = canal.CreateBasicProperties();
            props.Persistent = true;
            props.ContentType = "application/json";
            props.MessageId = mensagemId.ToString();
            props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            canal.BasicPublish(Topologia.Exchange, topico, mandatory: false, props, Encoding.UTF8.GetBytes(corpo));
            logger.LogDebug("publicado {Topico} {MensagemId}", topico, mensagemId);
        }
        finally { _trava.Release(); }
    }

    public void Dispose()
    {
        if (_canal.IsValueCreated) _canal.Value.Dispose();
        _trava.Dispose();
    }
}

/// <summary>Barramento em memória para testes e para rodar a API sem broker. Entrega síncrona aos assinantes do tópico.</summary>
public sealed class BarramentoEmMemoria : IBarramento
{
    private readonly ConcurrentDictionary<string, List<Func<string, Task>>> _assinantes = new();
    public ConcurrentQueue<(string Topico, string Corpo, Guid Id)> Publicadas { get; } = new();

    public void Assinar(string topico, Func<string, Task> tratar) => _assinantes.GetOrAdd(topico, _ => new()).Add(tratar);

    public async Task PublicarAsync(string topico, string corpo, Guid mensagemId, CancellationToken ct)
    {
        Publicadas.Enqueue((topico, corpo, mensagemId));
        if (_assinantes.TryGetValue(topico, out var lista))
            foreach (var tratar in lista.ToList()) await tratar(corpo);
    }
}
