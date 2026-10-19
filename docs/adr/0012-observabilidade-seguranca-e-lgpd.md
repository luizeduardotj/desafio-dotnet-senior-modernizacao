# ADR-0012 — Observabilidade da migração, segurança e LGPD

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0004](0004-comportamentos-legados-nao-documentados.md), [0008](0008-fachada-yarp-roteamento-e-flags.md), [0009](0009-sombra-escrita-unica-e-idempotencia.md), [plano de migração](../plano-de-migracao.md)

## Contexto

Requisitos:

- traces OpenTelemetry de ponta a ponta (fachada → legado ou novo → Oracle) com propagação W3C;
- logs estruturados correlacionados ao trace;
- métricas de requisições e latência por operação e modo, e de divergências na sombra;
- backend no compose.

A observabilidade precisa **decidir avanço e retorno de fase**, não só existir. Instrumentar o legado é opcional; a fachada precisa dar visibilidade sobre ele.

Os dados trafegados incluem CPF de pessoas físicas (tomadores) e razão social; o legado não autentica nada ([ADR-0004](0004-comportamentos-legados-nao-documentados.md): C18, C19, K04).

## Decisão

### Traces

- OpenTelemetry SDK na fachada e no serviço novo; propagação W3C `traceparent`.
- A fachada cria um span por requisição com `soap.operacao`, `migracao.modo`, `migracao.destino` (`legado`/`novo`), `migracao.motivo_roteamento` (kill switch, lista, percentual, fallback) e um span filho para a chamada ao legado. **O legado não é instrumentado**: sua latência e erros são vistos pela fachada.
- A sombra roda em trace **ligado** (span link) ao da requisição original, para não inflar a latência do trace do cliente.
- No novo: spans de ASP.NET Core, HttpClient e Oracle (instrumentação OpenTelemetry do ODP.NET), com o SQL sem valores de bind.

### Métricas (Prometheus/OTLP)

| Métrica | Labels | Uso |
| --- | --- | --- |
| `nfse_requisicoes_total` | `operacao`, `modo`, `destino`, `resultado` (`ok`, `erro_negocio`, `fault`) | volume e taxa de erro por lado |
| `nfse_duracao_segundos` (histograma) | `operacao`, `destino` | p50/p95/p99 legado × novo |
| `fachada_overhead_segundos` (histograma) | `operacao` | orçamento da fachada ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)) |
| `nfse_fallback_total` | `operacao`, `causa` | fragilidade do novo |
| `sombra_comparacoes_total` | `operacao`, `resultado` (`igual`, `divergente`) | taxa de paridade |
| `sombra_divergencias_total` | `operacao`, `classe` | critério de avanço |
| `sombra_descartadas_total`, `sombra_atraso_segundos` | `operacao` | saúde da sombra |
| `cancelamento_regra_divergente_total` | `codigo_plsql`, `decisao_dominio` | dual-run ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md)) |
| `soap_cnpj_inconsistente_total` | `operacao` | dado para corrigir C18/K04 no futuro |
| `sombra_reexecucoes_total` | `operacao`, `resultado` | custo da reexecução de leituras divergentes |
| `migracao_modo_info` (gauge) | `operacao`, `modo`, `percentual` | estado atual das flags |
| `kill_switch_ativo` (gauge) | — | alerta imediato |

**Regra de cardinalidade:** prestador, documento, número de nota e protocolo **nunca** são labels. Esses dados ficam em traces e logs (pseudonimizados).

### Logs

JSON estruturado, com `trace_id` e `span_id` em todo evento; categoria de log por classe real (não `ILogger<OutraClasse>`, um dos problemas do PR-212).

### Backend

`grafana/otel-lgtm` no compose (OpenTelemetry Collector + Tempo + Prometheus + Loki + Grafana em uma imagem), com dashboards provisionados:

1. **Migração por operação:** modo atual, distribuição legado × novo, p99 por destino, taxa de erro, fallback.
2. **Sombra:** taxa de divergência por classe, descartes, atraso; link para os logs das divergências.
3. **Cancelamento:** dual-run, códigos por destino, concorrência (espera de lock no wrapper).

Em Kubernetes, o mesmo OTLP aponta para o backend da plataforma.

### Alertas ligados aos critérios do plano de migração

Os alertas usam só as classes **automáticas** do [ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md); `bug_novo`/`bug_legado` são resultado de triagem humana e alimentam o plano, não alertas.

Exemplos de alertas que **disparam rollback** (automático para o modo anterior da operação):

- `cancelamento_regra_divergente_total` > 0 (dual-run) ou divergência `nao_classificada` > 0 em cancelamento;
- p99 do novo > 1,2 × p99 do legado por 15 min;
- taxa de `fault` do novo > taxa do legado + 0,1 p.p.

Exemplos de alertas que **bloqueiam avanço**:

- qualquer divergência `nao_classificada` pendente de triagem;
- `sombra_descartadas_total` > 1% das amostras.

Os limites exatos estão no [plano de migração](../plano-de-migracao.md).

### Segurança e LGPD

- **Dados pessoais em telemetria:** CPF/CNPJ, razões sociais e inscrição municipal nunca aparecem em claro em logs, traces ou métricas. Um processador de logs troca documentos e IMs por HMAC (sem máscara parcial: uma máscara que mantém 6 dígitos de um CPF, com os dígitos verificadores calculáveis, deixa só cerca de mil candidatos). Divergências usam o mesmo HMAC com chave rotacionável ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)). Todo CNPJ e toda IM são tratados como dado pessoal, porque podem ser de MEI ou empresário individual.
- **URLs:** nenhum documento em caminho ou query string ([ADR-0010](0010-contrato-da-api-rest-v1.md)); o atributo `url.path` dos spans da REST é registrado com o template da rota.
- **Fuso:** fachada e serviço novo fixam `America/Sao_Paulo` de forma explícita no código onde ele importa (C04, formatação de datas do SOAP); o fuso do pod (UTC em Kubernetes) nunca é usado implicitamente.
- **SOAP:** sem autenticação, como hoje. O risco é aceito e registrado (C18, C19, K04), com medição para decisão futura. A fachada **não** adiciona rate limiting nesta fase (mudaria o comportamento para ERPs); fica como evolução com medição prévia.
- **REST:** JWT obrigatório e autorização por recurso ([ADR-0010](0010-contrato-da-api-rest-v1.md)).
- **Segredos:** strings de conexão, chaves HMAC/AES e credenciais só por variável de ambiente/Secret do Kubernetes. Nunca em `appsettings*.json` versionado (outro problema do PR-212).
- **Erros:** nenhuma resposta (SOAP ou REST) inclui mensagem de exceção ou stack trace (C20).

## Alternativas consideradas

- **Jaeger + Prometheus + Grafana separados.** Válida, mas com mais containers e configuração para o mesmo resultado; `otel-lgtm` reduz memória no compose, que já tem o Oracle com 2–3 GB.
- **Instrumentar o legado (.NET Framework 4.8).** Rejeitada como requisito: exigiria deploy do legado; a fachada já mede latência, erros e destino.
- **Prestador como label para segmentar canário.** Rejeitada: explosão de cardinalidade (5.000+ prestadores); a segmentação vem de traces/exemplars e de `motivo_roteamento`.

## Consequências

- **Positivas:** decisões de fase baseadas em números visíveis; nenhum dado pessoal em telemetria.
- **Negativas:** mais um conjunto de containers no compose; o mascaramento precisa de testes próprios.
- **Riscos:** vazamento de dado pessoal por log novo sem mascaramento — mitigado por teste que injeta CPF/CNPJ conhecidos e verifica a saída de logs e spans.

## Reversão

A telemetria não altera o comportamento; pode ser desligada por configuração.

## Como verificar

- Teste de integração: uma requisição pela fachada produz um trace com spans fachada → novo → Oracle e o mesmo `trace_id` nos logs dos dois processos.
- Teste: logs e spans de casos com CPF/CNPJ não contêm os documentos em claro.
- Dashboards provisionados aparecem no Grafana do compose com dados após `just lote` e chamadas de exemplo.
