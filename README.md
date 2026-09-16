# Pedidos Distribuídos

[![CI](https://github.com/condeDeveloper/pedidos-distribuidos/actions/workflows/ci.yml/badge.svg)](https://github.com/condeDeveloper/pedidos-distribuidos/actions/workflows/ci.yml)

Sistema de pedidos em C# e .NET 8 montado como se fosse para produção: API e worker separados, PostgreSQL com migrations, RabbitMQ com padrão outbox, Redis como cache, JWT, health checks, logs estruturados, tracing distribuído e métricas, tudo em containers com um `docker compose up`. A CI roda os testes contra PostgreSQL, RabbitMQ e Redis de verdade e publica as imagens no GitHub Container Registry.

## Sobe com um comando

```bash
docker compose up --build
```

| Serviço | Endereço | Para quê |
|---|---|---|
| API | http://localhost:8080/docs | Swagger com autenticação Bearer |
| RabbitMQ | http://localhost:15672 (pedidos / pedidos) | filas, exchange e fila morta |
| Jaeger | http://localhost:16686 | traces de cada requisição, do HTTP ao SQL |
| Prometheus | http://localhost:9090 | métricas brutas |
| Grafana | http://localhost:3000 (admin / admin) | dashboard "Pedidos API" já provisionado |

Fluxo de ponta a ponta:

```bash
TOKEN=$(curl -s localhost:8080/api/auth/token -H 'Content-Type: application/json' -d '{"usuario":"admin","senha":"admin"}' | jq -r .token)

ID=$(curl -s localhost:8080/api/pedidos -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"clienteId":"cliente-1","itens":[{"produto":"Teclado","quantidade":2,"precoUnitario":150},{"produto":"Mouse","quantidade":1,"precoUnitario":80.5}]}' | jq -r .id)

curl -s -X POST localhost:8080/api/pedidos/$ID/pagamento -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"referencia":"pix-123"}'

sleep 3   # relay do outbox + consumidor do worker
curl -s localhost:8080/api/pedidos/$ID -H "Authorization: Bearer $TOKEN" | jq '{status, numeroNotaFiscal}'
# => { "status": "Faturado", "numeroNotaFiscal": "NF-20260916-3F2A9C1B" }
```

## Como funciona

```
                 ┌─────────────────────────┐          ┌──────────────────────────┐
  HTTP + JWT ──▶ │  Pedidos.Api            │          │  Pedidos.Worker (x2)     │
                 │  MediatR → handlers     │          │  RelayOutbox ──────────┐ │
                 │  EF Core (transação)    │          │  ConsumidorFaturamento │ │
                 └──────┬─────────┬────────┘          └──────▲─────────────────┼─┘
                        │         │                          │                 │
                  pedidos   outbox (mesma tx)         fila pedidos.faturamento │
                        │         │                          │                 ▼
                 ┌──────▼─────────▼────────┐          ┌──────┴──────────────────┐
                 │  PostgreSQL             │◀─ poll ──│  RabbitMQ (exchange     │
                 └─────────────────────────┘          │  topic + DLX)           │
                 ┌─────────────────────────┐          └─────────────────────────┘
                 │  Redis (cache-aside)    │
                 └─────────────────────────┘
      logs JSON ──▶ stdout      traces ──▶ Jaeger (OTLP)      métricas ◀── Prometheus
```

1. `POST /api/pedidos` passa pelo pipeline do MediatR (log + validação FluentValidation), o handler cria o agregado e a **unidade de trabalho grava o pedido e o evento `pedido.criado` no outbox na mesma transação**.
2. `POST /pagamento` muda o estado (com **concorrência otimista** por versão) e grava `pedido.pago` no outbox; o cache do pedido é invalidado.
3. O **worker** lê o outbox em ordem e publica no RabbitMQ (exchange `pedidos`, topic). Se o broker cair, a mensagem fica pendente e é tentada de novo: entrega pelo menos uma vez.
4. O **consumidor** da fila `pedidos.faturamento` emite a nota (simulada) e marca o pedido como faturado. Ack manual, retry com backoff exponencial (Polly), e depois de esgotar vai para a **fila morta** `pedidos.faturamento.erros`. O processador é **idempotente**: mensagem duplicada não faz nada.
5. `GET /api/pedidos/{id}` usa **cache-aside** no Redis por 30 segundos.

## O que tem aqui, na linguagem das vagas

| Pedem | Onde está |
|---|---|
| Clean Architecture / DDD | `Dominio` (agregado com invariantes e eventos), `Aplicacao` (casos de uso), `Infra` (EF, RabbitMQ, Redis), `Api` e `Worker` finos |
| CQRS + MediatR | comandos e consultas separados, pipeline behaviors de log e validação |
| FluentValidation | validadores por comando, erros como ProblemDetails 400 com o campo |
| EF Core + PostgreSQL + Migrations | `PedidosDbContext`, migration `Inicial` gerada com `dotnet ef`, aplicada na subida |
| Mensageria (RabbitMQ) | exchange topic, fila durável, DLX, prefetch, ack manual, consumidores competindo |
| Outbox pattern / consistência eventual | tabela `outbox` na mesma transação, relay em background, tentativas e erro registrados |
| Idempotência | consumidor ignora mensagens já processadas; `MessageId` em cada publicação |
| Resiliência (Polly) | retry exponencial com jitter no consumidor; conexão RabbitMQ com recuperação automática |
| Redis / cache distribuído | `IDistributedCache` com cache-aside e invalidação nos comandos |
| Autenticação JWT | Bearer com emissor, audiência e chave simétrica; Swagger já pede o token |
| Rate limiting | janela fixa por usuário autenticado (ou IP), 429 |
| Health checks | `/health/live` e `/health/ready` (banco, barramento, cache), usados pelo `HEALTHCHECK` da imagem e pelo `depends_on` do compose |
| Observabilidade | Serilog em JSON com correlation id, OpenTelemetry (ASP.NET Core, HttpClient, Npgsql, spans próprios) para o Jaeger, métricas Prometheus com contadores de negócio, dashboard Grafana provisionado |
| Docker | Dockerfiles multi-stage, imagem final `aspnet`/`runtime`, usuário sem privilégio, `.dockerignore`, cache de camadas de restore |
| Docker Compose | 8 serviços com health checks, volumes, ordem de subida, variáveis com padrão, `replicas: 2` no worker |
| CI/CD | GitHub Actions: testes rápidos em SQLite, testes de integração com Postgres, RabbitMQ e Redis como service containers, validação do compose, build e push das imagens para o GHCR com cache |
| Testes | unitários do domínio, outbox e concorrência em SQLite, API completa com `WebApplicationFactory`, mesma suíte roda contra dependências reais com `PEDIDOS_INTEGRACAO=1` |
| Configuração 12-factor | tudo por `appsettings` ou variáveis de ambiente (`Banco__Provedor`, `ConnectionStrings__Postgres`...) |

## Rodar sem Docker

Os provedores são trocáveis por configuração. O padrão é SQLite, barramento em memória e cache em memória, então funciona sem instalar nada:

```bash
dotnet run --project src/Pedidos.Api        # http://localhost:5000/docs
dotnet run --project src/Pedidos.Worker     # relay e consumidor em memória (mesmo arquivo SQLite)
dotnet test                                 # 15 testes, 3 segundos
```

Com as dependências reais rodando em outro lugar:

```bash
PEDIDOS_INTEGRACAO=1 PEDIDOS_POSTGRES="Host=...;..." PEDIDOS_RABBITMQ="amqp://..." PEDIDOS_REDIS="..." dotnet test --filter FullyQualifiedName~ApiTests
```

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| POST | `/api/auth/token` | emite o JWT (usuário e senha de demonstração em `Auth`) |
| POST | `/api/pedidos` | cria o pedido (201 + Location) |
| GET | `/api/pedidos/{id}` | detalhe com itens, versão e nota fiscal |
| GET | `/api/pedidos?clienteId=&status=&pagina=&tamanho=` | lista paginada |
| POST | `/api/pedidos/{id}/pagamento` | confirma pagamento (204) |
| POST | `/api/pedidos/{id}/cancelamento` | cancela criado ou pago (204); faturado dá 422 |
| GET | `/health/live` `/health/ready` `/metrics` | operação |

Erros seguem RFC 7807: 400 validação (com `erros` por campo), 404, 409 conflito de concorrência, 422 regra de negócio, 429 limite.

## Estrutura

```
src/Pedidos.Dominio      Pedido, ItemPedido, StatusPedido, eventos de domínio
src/Pedidos.Aplicacao    comandos, consultas, validadores, behaviors, portas
src/Pedidos.Infra        EF Core (Postgres/SQLite), migrations, outbox, RabbitMQ, Redis, relay, consumidor
src/Pedidos.Api          minimal API, JWT, rate limit, health, Serilog, OpenTelemetry, Prometheus, Dockerfile
src/Pedidos.Worker       host genérico com relay do outbox e consumidor de faturamento, Dockerfile
tests/Pedidos.Tests      domínio, outbox, concorrência, API (SQLite ou dependências reais)
infra/                   prometheus.yml e provisionamento do Grafana
docker-compose.yml       api, worker x2, postgres, rabbitmq, redis, jaeger, prometheus, grafana
```

## Licença

MIT
