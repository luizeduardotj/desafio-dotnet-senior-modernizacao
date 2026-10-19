# Teste de carga das leituras (k6)

Compara `ConsultarNfse` e `ConsultarNfseServicoTomado` com o legado e com o novo atendendo, sempre pela fachada e com o mesmo volume. O critério de avanço vem do [plano de migração, §6.1](../../docs/plano-de-migracao.md): p99 do novo ≤ 1,2 × p99 do legado.

## Como rodar

Com o compose de pé e as flags vindas do flagd (`FACHADA_FLAGS_FONTE=openfeature docker compose --profile flagd up -d`), a troca de modo é feita em `deploy/flags/flagd.json` (variante ativa de `ConsultarNfse.config` e `ConsultarNfseServicoTomado.config`). A fachada aplica a mudança em menos de 2 s.

```bash
k6() { docker run --rm --network container:$(docker compose ps -q fachada) -v $PWD/tests/carga:/carga grafana/k6:0.54.0 "$@"; }
```

```bash
k6 run -e ALVO=legado --summary-export /carga/legado.json /carga/leituras.js
```

Depois de pôr as duas operações em `Modo: novo` com `Percentual: 100`:

```bash
k6 run -e ALVO=novo --summary-export /carga/novo.json /carga/leituras.js
```

```bash
python3 tests/carga/comparar.py tests/carga/legado.json tests/carga/novo.json
```

`RPM_CONSULTAR` e `RPM_TOMADO` mudam a taxa (requisições por minuto; padrão 1200 e 300). Os `.json` gerados ficam fora do git.

O roteiro usa uma consulta leve (prestador 1, um mês) e uma pesada (tomador com centenas de notas no mês, que no legado é full scan sobre 2 milhões de notas). Os limites absolutos do script (p95 < 500 ms e < 3 s, falhas < 1%) são **[H]**, só sanidade. O critério de decisão é o relativo, calculado por `comparar.py`.

## Resultado em 2026-10-19 **[M]**

Compose local: Docker Desktop com 8 CPUs e 8 GB, Oracle 23ai Free, todos os serviços na mesma máquina. Execuções de 60 s com taxa constante.

**Sem fila** (2 + 0,5 req/s):

| Operação | Métrica | Legado (ms) | Novo (ms) | Novo/legado |
| --- | --- | ---: | ---: | ---: |
| ConsultarNfse | mediana | 2,0 | 1,9 | 0,97 |
| ConsultarNfse | p95 | 3,9 | 2,6 | 0,66 |
| ConsultarNfse | p99 | 15,2 | 5,5 | 0,36 |
| ConsultarNfseServicoTomado | mediana | 273,9 | 268,5 | 0,98 |
| ConsultarNfseServicoTomado | p95 | 369,4 | 300,3 | 0,81 |
| ConsultarNfseServicoTomado | p99 | 546,9 | 315,3 | 0,58 |

152 requisições por alvo, 0% de falha, 100% dos checks. Com 120 requisições de `ConsultarNfse`, o p99 é praticamente o segundo maior valor: serve para comparar os dois alvos, não como estimativa da cauda.

**Taxa do padrão** (20 + 5 req/s):

| Operação | Métrica | Legado (ms) | Novo (ms) |
| --- | --- | ---: | ---: |
| ConsultarNfse | mediana | 1 987,6 | 2,7 |
| ConsultarNfse | p99 | 5 283,8 | 9,7 |
| ConsultarNfseServicoTomado | mediana | 15 558,1 | 362,6 |
| ConsultarNfseServicoTomado | p99 | 21 868,4 | 493,0 |

O legado satura nessa taxa: o k6 descartou 151 iterações por falta de VUs (1 350 requisições contra 1 501 do novo), e a latência é quase toda fila. O novo sustentou a taxa sem descarte.

## Como ler

- **Sem fila, a latência é a mesma.** As medianas ficam a 3% uma da outra: as duas implementações gastam o tempo na mesma query. O ganho do novo está na cauda, no p95 e no p99.
- **Na taxa alta, a medida é de capacidade, não de latência.** A razão de ~0,005 (2,7 ms contra 1 987,6 ms na mediana) não quer dizer que o novo é 500 vezes mais rápido. Quer dizer que o legado, um processo com o pool e as threads do ASP.NET clássico, enfileira antes de chegar ao Oracle. Por isso o critério do §6.1 compara o novo com o legado **medido no M0 em produção**, com o tráfego real, e não com este número.
- **O que este teste não prova:** carga sobre o lote (premissa 0002 P6, que precisa de homologação com o volume do M0), Oracle 19c com o hardware de produção e o custo da sombra ligada. O overhead da fachada fica no histograma `fachada_overhead_seconds`, fora do k6.
