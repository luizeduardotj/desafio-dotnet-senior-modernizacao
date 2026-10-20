# Resiliência: Oracle lento ou fora do ar para o serviço novo

Os testes da fachada (`tests/Nfse.Fachada.Testes`) cobrem "novo lento" e "novo fora do ar" com fakes. Aqui o sistema
roda de verdade, com fachada, legado, serviço novo e Oracle, e o [Toxiproxy](https://github.com/Shopify/toxiproxy)
fica entre o novo e o banco. O legado continua ligado direto no Oracle. Assim dá para deixar o banco lento ou fora do
ar só para o novo e conferir, pela fachada, o que a [ADR-0008](../../docs/adr/0008-fachada-yarp-roteamento-e-flags.md)
("Modo novo e falhas") e a [ADR-0009](../../docs/adr/0009-sombra-escrita-unica-e-idempotencia.md) (sombra fora do
caminho do cliente) prometem.

| Arquivo | Conteúdo |
| --- | --- |
| `docker-compose.resiliencia.yml` | override do compose padrão: serviço `toxiproxy` (`ghcr.io/shopify/toxiproxy:2.12.0`) e o novo apontando para `toxiproxy:1521` |
| `toxiproxy.json` | o proxy `oracle`: `0.0.0.0:1521` → `oracle:1521` |
| `rodar.sh` | sobe o ambiente do zero, roda `tests/Nfse.Resiliencia.Testes` e derruba tudo, passe ou falhe |
| `../Nfse.Resiliencia.Testes` | os testes, em caixa-preta: HTTP na fachada e no novo, API do Toxiproxy e ODP.NET direto no Oracle |

O compose padrão não muda. O override só vale com `-f` e com project name próprio (`nfse-resiliencia`). Nessa
combinação ele:

- troca as portas publicadas (`!override`), para não colidir com outro compose de pé na mesma máquina;
- tira o `otel-lgtm` (profile `observabilidade`, que ocupa ~2 GB), porque os testes leem os logs da fachada;
- monta as flags da fachada de `tests/resiliencia/.flags` (fora do git), que os testes reescrevem;
- reduz o canal da sombra a 2 cópias com 1 worker, para o descarte aparecer numa rajada curta.

## Como rodar

```bash
tests/resiliencia/rodar.sh
```

Com `MANTER=1`, o ambiente fica de pé no fim. Rodar de novo reaproveita o que está de pé e só refaz os testes. Cada
execução cancela duas notas do prestador 25, entre a 3 e a 8: com o banco recém-carregado dá para repetir três vezes.
Depois disso, rode sem `MANTER` (que começa com `down -v`).

| Variável | Padrão | Para quê |
| --- | --- | --- |
| `NFSE_RESILIENCIA` | `1` no script | habilita os testes. Sem ela, `dotnet test Nfse.Moderno.slnx` os mostra como ignorados |
| `NFSE_RESILIENCIA_PORTA_FACHADA`, `_NOVO`, `_TOXIPROXY`, `_ORACLE` | 18080, 18090, 18474, 11521 | portas no host, só em 127.0.0.1 |
| `NFSE_RESILIENCIA_FACHADA`, `_NOVO`, `_TOXIPROXY`, `_ORACLE` | as URLs/connection string dessas portas | se as portas mudarem |
| `NFSE_RESILIENCIA_MEDICOES` | `TestResults/resiliencia-medicoes.md` | tabela dos tempos medidos; o CI a põe no resumo do job |

No CI é o job `resiliencia`, separado do `compose` para não alongá-lo.

## Cenários

Entre um teste e outro, o Toxiproxy é zerado (`POST /reset`) e as flags voltam a `legado`. Antes de medir, cada teste
espera a fachada aplicar as flags: envia uma sonda e procura a linha de roteamento dela no log, pelo `traceparent`.

| | Toxic | Flags | Esperado |
| --- | --- | --- | --- |
| (a) | `latency` 12 s na volta | `ConsultarNfse` em `novo` 100% | timeout de 5 s da fachada; uma tentativa no legado; resposta igual ao golden master (caso 001); cliente em ≤ 5 s + 2 s |
| (b) | proxy desligado (conexão recusada) | idem | fallback imediato, resposta do legado; `/health/ready` do novo vira 503, `/health/live` continua 200 |
| (b) | `timeout` 0 na ida (partição: a conexão fica pendurada) | idem | fallback depois dos 5 s; `/health/ready` responde 503 em menos de 2 s (o `timeoutSeconds` do probe no k8s) |
| (c) | `latency` 12 s na volta | `ConsultarNfse` em `sombra` 100% | 10 requisições: a mediana do cliente não sobe mais que 100 ms em relação ao modo `legado`, e todas ficam abaixo de 1 s com bytes do legado; log com `erro_novo (timeout)` e `cópia descartada (canal_cheio)` |
| (d) | nenhum: um `SELECT ... FOR UPDATE` do NFSE_OWNER segura a nota | `CancelarNfse` em `novo` | `FOR UPDATE WAIT 5` dá ORA-30006; Fault `soap:Server` em ~5 s, sem fallback ao legado; nada gravado depois que o lock sai; o reenvio cancela uma vez e o segundo reenvio devolve os mesmos bytes (K01) |
| (d) | `latency` 6 s na volta | idem | Fault da fachada aos 10 s. O novo termina sozinho, com commit ou rollback pelo `CommandTimeout`. Nos dois desfechos o reenvio fica com uma única gravação |
| (e) | lento e depois fora do ar, e toxic removido | `ConsultarNfse` em `novo` 100% | a leitura seguinte já é atendida pelo novo, sem fallback e sem restart (`StartedAt`/`RestartCount` do container iguais); `/health/ready` volta a 200 |

## Medições [M]

Execução de `rodar.sh` do zero, em 2026-10-19: Docker Desktop 29.7.2 em macOS arm64, 8 CPUs e 11,7 GB para o Docker,
com outros dois composes e um k3d de pé na máquina. O ambiente ficou saudável em 3 min 52 s, os testes levaram
2 min 1 s e o script inteiro, com build e `down -v`, 6 min 5 s.

| Cenário | Medida | Valor |
| --- | --- | --- |
| (a) Oracle +12 s | leitura normal pelo novo | 5 ms |
| (a) Oracle +12 s | leitura com fallback ao legado | 5.005 ms |
| (b) conexão recusada | leitura com fallback ao legado | 24 ms |
| (b) conexão recusada | da queda ao `/health/ready` 503 | 123 ms |
| (b) Oracle sem resposta | leitura com fallback ao legado | 5.079 ms |
| (b) Oracle sem resposta | duração do `/health/ready` (503) | 1.511 ms |
| (c) sombra 100%, Oracle +12 s | mediana do cliente sem sombra e com sombra (10 req.) | 6 ms e 6 ms |
| (c) sombra 100%, Oracle +12 s | máximo do cliente com sombra | 48 ms |
| (c) sombra 100%, Oracle +12 s | cópias descartadas (`canal_cheio`) | 7 de 10 |
| (d) lock além do WAIT 5 | Fault do cancelamento travado | 5.009 ms |
| (d) lock além do WAIT 5 | reenvio depois do lock | 36 ms |
| (d) Oracle +6 s na volta | Fault da fachada | 10.004 ms |
| (d) Oracle +6 s na volta | desfecho no novo | gravado: o commit chegou ao banco antes de a fachada desistir e a resposta veio depois (em outra execução foi rollback pelo `CommandTimeout`) |
| (d) Oracle +6 s na volta | reenvio (K01, sem nova escrita) | 19 ms |
| (e) recuperação | do fim da lentidão à 1ª leitura pelo novo | 70 ms |
| (e) recuperação | da volta do Oracle à 1ª leitura pelo novo | 82 ms |
| (e) recuperação | daí ao `/health/ready` 200 | 2 ms |

## O que os testes mostraram

Dois defeitos reais, corrigidos em commits `fix(...)` próprios:

1. **Oracle fora do ar não levava a leitura ao legado.** O novo respondia o Fault de erro interno (500), e a
   fachada, de propósito, entrega o 500 ao cliente sem fallback: é o Fault que o legado também devolve (C20). Com o
   banco inalcançável só para o novo (partição, pool esgotado, senha rotacionada), o cliente recebia Fault enquanto o
   legado estava saudável. Agora uma `DbException` numa leitura vira 503, e a fachada já trata o 503 como
   indisponibilidade e tenta o legado uma vez. O cancelamento continua com o Fault, porque depois do envio não há
   fallback (ADR-0008).
2. **`/health/ready` do novo não olhava o Oracle.** Era o `TODO(ADR-0007)` do `Program.cs`: o ready respondia 200 com
   o banco fora, e o Kubernetes mantinha no Service uma réplica que não atende nada. Agora o check `oracle` faz
   `SELECT 1 FROM DUAL` pelo pool do runtime e desiste em 1,5 s: numa partição o `OpenAsync` do ODP.NET só volta no
   `Connection Timeout` (15 s), e o probe do k8s desiste em 2 s. O `/health/live` continua sem olhar o banco, para o
   processo não ser reiniciado por causa de uma dependência. Sem réplica pronta, a fachada recebe conexão recusada:
   as leituras vão ao legado e o cancelamento também, porque nada foi enviado.

Observações, sem mudança de código:

- **O orçamento do cancelamento não é total.** A ADR-0007 diz que "o serviço novo nunca commita depois que a fachada
  já desistiu" (lock 5 s + `CommandTimeout` 3 s; no código, 8 s). Com o Oracle lento só na volta, o commit chega ao
  banco antes dos 10 s e a resposta depois: a fachada devolve Fault com a nota já cancelada. Isso é inevitável quando
  a demora está na volta, e é seguro porque o cancelamento é idempotente (K01). O reenvio devolve sucesso com o
  `DataHora` gravado, sem segunda escrita, como o teste confere.
- **Na sombra, o 503 do novo conta como divergência, não como `erro_novo`.** O comparador só classifica como
  `erro_novo` o timeout e a falha de transporte. Com o banco fora e o novo respondendo 503, a cópia vira
  `nao_classificada` (status 200 × 503). Fica para o módulo de sombra da fachada.
- O descarte da sombra só aparece no log em `Debug`. O override liga esse nível só para o `PublicadorDaSombra`.

## Limites

- O Toxiproxy atrasa ou corta o TCP. Não simula erro dentro do Oracle (ORA-04031, sessão morta pelo DBA), que cai no
  mesmo caminho da `DbException`.
- O Oracle fica fora do ar só para o novo. Com o banco fora para todos, o legado também falha e o cliente recebe o
  Fault dele, como hoje.
- Sem `otel-lgtm`, os testes não conferem métricas (`nfse_fallback_total`, `sombra_descartadas_total`), só os logs
  equivalentes.
