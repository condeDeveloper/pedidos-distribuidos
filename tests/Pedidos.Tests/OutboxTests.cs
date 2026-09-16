using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pedidos.Aplicacao;
using Pedidos.Dominio;
using Pedidos.Infra.Mensageria;
using Pedidos.Infra.Persistencia;

namespace Pedidos.Tests;

public class OutboxTests : IDisposable
{
    private sealed class RelogioFixo(DateTime agora) : IRelogio { public DateTime AgoraUtc { get; set; } = agora; }

    private sealed class BarramentoQueFalha : IBarramento
    {
        public int Chamadas;
        public Task PublicarAsync(string topico, string corpo, Guid mensagemId, CancellationToken ct) { Chamadas++; throw new IOException("broker fora"); }
    }

    private readonly string _arquivo = Path.Combine(Path.GetTempPath(), $"outbox-{Guid.NewGuid():N}.db");
    private readonly RelogioFixo _relogio = new(new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

    private PedidosDbContext NovoContexto()
    {
        var db = new PedidosDbContext(new DbContextOptionsBuilder<PedidosDbContext>().UseSqlite($"Data Source={_arquivo}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task SalvarGravaEventosNoOutboxNaMesmaTransacao()
    {
        await using var db = NovoContexto();
        var uow = new UnidadeDeTrabalho(db, _relogio);
        var pedido = Pedido.Criar("c1", [new ItemPedido("x", 1, 10m)], _relogio.AgoraUtc);
        db.Pedidos.Add(pedido);
        await uow.SalvarAsync(CancellationToken.None);

        var mensagem = await db.Outbox.SingleAsync();
        mensagem.Topico.Should().Be("pedido.criado");
        mensagem.Tipo.Should().Be(nameof(PedidoCriado));
        mensagem.Corpo.Should().Contain(pedido.Id.ToString()).And.Contain("\"total\":10");
        mensagem.PublicadoEm.Should().BeNull();
        pedido.Eventos.Should().BeEmpty("eventos são drenados ao salvar");
    }

    [Fact]
    public async Task RelayPublicaEmOrdemEMarcaPublicadas()
    {
        await using var db = NovoContexto();
        var uow = new UnidadeDeTrabalho(db, _relogio);
        var pedido = Pedido.Criar("c1", [new ItemPedido("x", 1, 10m)], _relogio.AgoraUtc);
        db.Pedidos.Add(pedido);
        await uow.SalvarAsync(CancellationToken.None);
        _relogio.AgoraUtc = _relogio.AgoraUtc.AddSeconds(1);
        pedido.ConfirmarPagamento("ref", _relogio.AgoraUtc);
        await uow.SalvarAsync(CancellationToken.None);

        var barramento = new BarramentoEmMemoria();
        var relay = new RelayOutbox(db, barramento, _relogio, NullLogger<RelayOutbox>.Instance);
        (await relay.PublicarPendentesAsync()).Should().Be(2);
        barramento.Publicadas.Select(p => p.Topico).Should().Equal("pedido.criado", "pedido.pago");
        (await db.Outbox.CountAsync(m => m.PublicadoEm == null)).Should().Be(0);
        (await relay.PublicarPendentesAsync()).Should().Be(0, "nada pendente na segunda rodada");
    }

    [Fact]
    public async Task RelayContaTentativasEDesisteNoLimite()
    {
        await using var db = NovoContexto();
        var uow = new UnidadeDeTrabalho(db, _relogio);
        db.Pedidos.Add(Pedido.Criar("c1", [new ItemPedido("x", 1, 10m)], _relogio.AgoraUtc));
        await uow.SalvarAsync(CancellationToken.None);

        var barramento = new BarramentoQueFalha();
        var relay = new RelayOutbox(db, barramento, _relogio, NullLogger<RelayOutbox>.Instance);
        for (var k = 0; k < RelayOutbox.MaximoDeTentativas + 3; k++) (await relay.PublicarPendentesAsync()).Should().Be(0);

        barramento.Chamadas.Should().Be(RelayOutbox.MaximoDeTentativas, "depois do limite a mensagem não é mais tentada");
        var m = await db.Outbox.SingleAsync();
        m.Tentativas.Should().Be(RelayOutbox.MaximoDeTentativas);
        m.UltimoErro.Should().Be("broker fora");
        m.PublicadoEm.Should().BeNull();
    }

    [Fact]
    public async Task ConcorrenciaOtimistaDetectaEscritaPerdida()
    {
        Guid id;
        await using (var db = NovoContexto())
        {
            var p = Pedido.Criar("c1", [new ItemPedido("x", 1, 10m)], _relogio.AgoraUtc);
            db.Pedidos.Add(p);
            await new UnidadeDeTrabalho(db, _relogio).SalvarAsync(CancellationToken.None);
            id = p.Id;
        }
        await using var a = NovoContexto();
        await using var b = NovoContexto();
        var pa = await a.Pedidos.SingleAsync(p => p.Id == id);
        var pb = await b.Pedidos.SingleAsync(p => p.Id == id);
        pa.ConfirmarPagamento("a", _relogio.AgoraUtc);
        await new UnidadeDeTrabalho(a, _relogio).SalvarAsync(CancellationToken.None);
        pb.Cancelar("b", _relogio.AgoraUtc);
        var salvarB = () => new UnidadeDeTrabalho(b, _relogio).SalvarAsync(CancellationToken.None);
        await salvarB.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_arquivo); } catch (IOException) { }
    }
}
