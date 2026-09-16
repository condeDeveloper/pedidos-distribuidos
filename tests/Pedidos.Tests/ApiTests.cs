using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pedidos.Infra.Mensageria;
using Pedidos.Infra.Persistencia;

namespace Pedidos.Tests;

public class ApiTests : IClassFixture<FabricaDeTeste>
{
    private readonly FabricaDeTeste _fabrica;

    public ApiTests(FabricaDeTeste fabrica) => _fabrica = fabrica;

    private static object PedidoValido(string cliente = "cliente-1") => new { clienteId = cliente, itens = new[] { new { produto = "Teclado", quantidade = 2, precoUnitario = 150.0 }, new { produto = "Mouse", quantidade = 1, precoUnitario = 80.5 } } };

    [Fact]
    public async Task ExigeToken()
    {
        var anonimo = _fabrica.CreateClient();
        (await anonimo.GetAsync("/api/pedidos")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var r = await anonimo.PostAsJsonAsync("/api/auth/token", new { usuario = "admin", senha = "errada" });
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FluxoCompletoCriarPagarEFaturarPeloOutbox()
    {
        var http = await _fabrica.ClienteAutenticadoAsync();

        var criado = await http.PostAsJsonAsync("/api/pedidos", PedidoValido());
        criado.StatusCode.Should().Be(HttpStatusCode.Created);
        criado.Headers.Contains("X-Correlation-Id").Should().BeTrue();
        var id = (await criado.Content.ReadFromJsonAsync<JsonElement>(FabricaDeTeste.Json)).GetProperty("id").GetGuid();

        var dto = await http.GetFromJsonAsync<JsonElement>($"/api/pedidos/{id}", FabricaDeTeste.Json);
        dto.GetProperty("status").GetString().Should().Be("Criado");
        dto.GetProperty("total").GetDecimal().Should().Be(380.50m);
        dto.GetProperty("itens").GetArrayLength().Should().Be(2);

        // outbox tem o pedido.criado ainda não publicado
        await using (var escopo = _fabrica.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<PedidosDbContext>();
            (await db.Outbox.Where(m => m.Corpo.Contains(id.ToString())).ToListAsync()).Should().ContainSingle(m => m.Topico == "pedido.criado" && m.PublicadoEm == null);
        }

        var pago = await http.PostAsJsonAsync($"/api/pedidos/{id}/pagamento", new { referencia = "pix-abc" });
        pago.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // cache foi invalidado: leitura já mostra Pago
        (await http.GetFromJsonAsync<JsonElement>($"/api/pedidos/{id}", FabricaDeTeste.Json)).GetProperty("status").GetString().Should().Be("Pago");

        // o relay publica; o consumidor fatura
        var publicadas = await _fabrica.PublicarOutboxAsync();
        publicadas.Should().BeGreaterThanOrEqualTo(2);
        if (FabricaDeTeste.Integracao) await Task.Delay(1500); // consumidor RabbitMQ é assíncrono

        var final = await EsperarStatus(http, id, "Faturado");
        final.GetProperty("numeroNotaFiscal").GetString().Should().StartWith("NF-");

        await using (var escopo = _fabrica.Services.CreateAsyncScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<PedidosDbContext>();
            var mensagens = await db.Outbox.Where(m => m.Corpo.Contains(id.ToString())).OrderBy(m => m.CriadoEm).ToListAsync();
            mensagens.Select(m => m.Topico).Should().Equal("pedido.criado", "pedido.pago", "pedido.faturado");
            mensagens.Take(2).Should().OnlyContain(m => m.PublicadoEm != null);
        }

        // faturado não cancela: 422
        var cancelar = await http.PostAsJsonAsync($"/api/pedidos/{id}/cancelamento", new { motivo = "tarde demais" });
        cancelar.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await cancelar.Content.ReadFromJsonAsync<JsonElement>(FabricaDeTeste.Json)).GetProperty("title").GetString().Should().Contain("faturado");
    }

    [Fact]
    public async Task ValidacaoENaoEncontrado()
    {
        var http = await _fabrica.ClienteAutenticadoAsync();
        var invalido = await http.PostAsJsonAsync("/api/pedidos", new { clienteId = "", itens = new[] { new { produto = "", quantidade = 0, precoUnitario = -1 } } });
        invalido.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problema = await invalido.Content.ReadFromJsonAsync<JsonElement>(FabricaDeTeste.Json);
        problema.GetProperty("title").GetString().Should().Be("dados inválidos");
        problema.GetProperty("erros").EnumerateObject().Select(p => p.Name).Should().Contain("ClienteId");

        (await http.GetAsync($"/api/pedidos/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var pagarInexistente = await http.PostAsJsonAsync($"/api/pedidos/{Guid.NewGuid()}/pagamento", new { referencia = "x" });
        pagarInexistente.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListaPaginadaComFiltros()
    {
        var http = await _fabrica.ClienteAutenticadoAsync();
        var cliente = $"lista-{Guid.NewGuid():N}";
        for (var k = 0; k < 3; k++) (await http.PostAsJsonAsync("/api/pedidos", PedidoValido(cliente))).EnsureSuccessStatusCode();

        var pagina = await http.GetFromJsonAsync<JsonElement>($"/api/pedidos?clienteId={cliente}&pagina=1&tamanho=2", FabricaDeTeste.Json);
        pagina.GetProperty("total").GetInt32().Should().Be(3);
        pagina.GetProperty("itens").GetArrayLength().Should().Be(2);
        pagina.GetProperty("totalDePaginas").GetInt32().Should().Be(2);
        pagina.GetProperty("itens")[0].GetProperty("quantidadeDeItens").GetInt32().Should().Be(2);

        var filtrado = await http.GetFromJsonAsync<JsonElement>($"/api/pedidos?clienteId={cliente}&status=Pago", FabricaDeTeste.Json);
        filtrado.GetProperty("total").GetInt32().Should().Be(0);

        (await http.GetAsync("/api/pedidos?tamanho=1000")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SaudeMetricasEDocumentacao()
    {
        var http = _fabrica.CreateClient();
        (await http.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        var ready = await http.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        var corpo = await ready.Content.ReadFromJsonAsync<JsonElement>(FabricaDeTeste.Json);
        corpo.GetProperty("status").GetString().Should().Be("Healthy");
        corpo.GetProperty("verificacoes").EnumerateArray().Select(v => v.GetProperty("nome").GetString()).Should().BeEquivalentTo("banco", "barramento", "cache");

        var metricas = await http.GetStringAsync("/metrics");
        metricas.Should().Contain("pedidos_criados_total").And.Contain("http_request_duration_seconds");
        (await http.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProcessadorDeFaturamentoEhIdempotente()
    {
        var http = await _fabrica.ClienteAutenticadoAsync();
        var criado = await http.PostAsJsonAsync("/api/pedidos", PedidoValido());
        var id = (await criado.Content.ReadFromJsonAsync<JsonElement>(FabricaDeTeste.Json)).GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/pedidos/{id}/pagamento", new { referencia = "r" })).EnsureSuccessStatusCode();

        await using var escopo = _fabrica.Services.CreateAsyncScope();
        var db = escopo.ServiceProvider.GetRequiredService<PedidosDbContext>();
        var corpo = (await db.Outbox.SingleAsync(m => m.Topico == "pedido.pago" && m.Corpo.Contains(id.ToString()))).Corpo;

        var processador = escopo.ServiceProvider.GetRequiredService<ProcessadorFaturamento>();
        (await processador.ProcessarAsync(corpo, CancellationToken.None)).Should().BeTrue();
        (await processador.ProcessarAsync(corpo, CancellationToken.None)).Should().BeFalse("segunda entrega da mesma mensagem não faz nada");
    }

    private static async Task<JsonElement> EsperarStatus(HttpClient http, Guid id, string status)
    {
        JsonElement dto = default;
        for (var k = 0; k < 40; k++)
        {
            dto = await http.GetFromJsonAsync<JsonElement>($"/api/pedidos/{id}?t={k}", FabricaDeTeste.Json);
            if (dto.GetProperty("status").GetString() == status) return dto;
            await Task.Delay(250);
        }
        dto.GetProperty("status").GetString().Should().Be(status);
        return dto;
    }
}
