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
- A fachada cria um span por requisição com `soap.operacao`, `migracao.modo`, `migracao.destino` (`legado`/`novo`), `migracao.motivo_roteamento` (kill switch, lista, percentual, fallback) e um span filho para a chamada ao legado. A latência e os erros do legado são vistos pela fachada; por dentro, o legado é instrumentado só por fora do código (subseção abaixo).
- A sombra roda em trace **ligado** (span link) ao da requisição original, para não inflar a latência do trace do cliente.
- No novo: spans de ASP.NET Core e HttpClient. Spans do Oracle pela extensão oficial `Oracle.ManagedDataAccess.OpenTelemetry` (fonte `Oracle.ManagedDataAccess.Core`, [TelemetriaDoOracle.cs](../../src/Nfse.Infra.Oracle/TelemetriaDoOracle.cs)): sem texto do SQL, sem binds (o ODP.NET não os captura), sem atributos da conexão e sem a exceção, porque a mensagem de um `ORA-` pode citar valores. Só a chamada da procedure sai em `db.statement`, com placeholders. Conferido no Tempo em 2026-10-19: o span do Oracle é filho do span do novo e não leva CNPJ nem IM **[M]**.

#### Legado instrumentado por fora (opcional)

A decisão acima continua valendo: os critérios de fase usam o que a fachada mede, e o legado não muda. Como complemento (2026-10-19), o container do legado leva a **auto-instrumentação OpenTelemetry .NET** (`opentelemetry-dotnet-instrumentation` v1.17.0, zip e SHA-256 fixados no `Dockerfile` da raiz). Nenhuma linha de C# ou VB.NET muda, e o `legado/` também não.

- **Ligada só pelo ambiente.** A imagem leva o agente dormente. O compose liga com `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`, `DOTNET_STARTUP_HOOKS`, `DOTNET_ADDITIONAL_DEPS` e `DOTNET_SHARED_STORE`, e exporta como `OTEL_SERVICE_NAME=nfse-legado` para o mesmo otel-lgtm. Sem essas variáveis, o processo é o de produção. Em produção (.NET Framework 4.8 no IIS), o equivalente é o instalador Windows do mesmo projeto, com `COR_*`: é deploy de infraestrutura, sem release do legado, e fica para depois do M0, se a fachada não bastar.
- **O que aparece:** span de servidor do ASP.NET Core (filho do span de cliente da fachada, pelo `traceparent`), spans do ODP.NET (`Fill(DataTable)` e `SendExecuteRequest`, com `db.system=oracle` e o número de linhas), logs do ASP.NET Core no Loki com `trace_id`, e a métrica `http.server.request.duration` com `service_name=nfse-legado`. O dashboard ganhou o painel **Legado: p99 visto pela fachada × medido dentro do legado**. O legado não conhece a operação SOAP (ela vem no `SOAPAction`), então a curva dele soma todas as operações.
- **LGPD:** `OTEL_DOTNET_AUTO_ORACLEMDA_SET_DBSTATEMENT_FOR_TEXT=false` fica explícito (é o padrão): `db.statement` não sai, e os valores de bind nunca saem. A propagação de contexto até o servidor Oracle (`..._DATABASE_OPENTELEMETRY_TRACING`) fica desligada, porque exige Oracle 26ai. Nenhum cabeçalho é capturado, o corpo SOAP não entra em span e `url.query` sai redigida. O legado só escreve no console com `Console.WriteLine`, que o agente não exporta. Resta um risco aceito: o log `Request starting` do ASP.NET Core leva a query string. O SOAP só usa `?wsdl`, mas um cliente que pusesse documento na URL o mandaria ao Loki.
- **Verificado no compose [M]** (2026-10-19, arm64, máquina compartilhada com outros ambientes):
  - 42 de 42 traces de `ConsultarNfse` pela fachada vieram com os três níveis (fachada → legado → Oracle) no mesmo `trace_id`;
  - nenhum dos CPFs/CNPJs/IMs dos exemplos `04*`/`05` apareceu em 159 traces nem nos logs do Loki;
  - a resposta do `04` continuou igual à do exemplo;
  - imagem cerca de 20 MB maior.
  - A documentação da v1.17.0 diz que a instrumentação `ORACLEMDA` não suporta ARM64, mas os spans vêm do `ActivitySource` do próprio ODP.NET 23.x e apareceram em arm64.

### Métricas (Prometheus/OTLP)

| Métrica | Labels | Uso |
| --- | --- | --- |
| `nfse_requisicoes_total` | `operacao`, `modo`, `destino`, `resultado` (`ok`, `erro_negocio`, `fault`) | volume e taxa de erro por lado |
| `nfse_duracao` (histograma, unidade `s`; `nfse_duracao_seconds` no Prometheus) | `operacao`, `destino` | p50/p95/p99 legado × novo |
| `fachada_overhead` (histograma, unidade `s`; `fachada_overhead_seconds` no Prometheus) | `operacao` | orçamento da fachada ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)) |
| `nfse_fallback_total` | `operacao`, `causa` | fragilidade do novo |
| `sombra_comparacoes_total` | `operacao`, `resultado` (`igual`, `divergente`) | taxa de paridade |
| `sombra_divergencias_total` | `operacao`, `classe` | critério de avanço |
| `sombra_descartadas_total`, `sombra_atraso_segundos` | `operacao` | saúde da sombra |
| `cancelamento_regra_divergente_total` | `codigo_plsql`, `decisao_dominio` | dual-run ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md)) |
| `soap_cnpj_inconsistente_total`, `soap_cnpj_conferido_total` | `operacao` | dado para corrigir C18/K04 no futuro: pedidos com CNPJ do prestador diferente do cadastrado para a IM, sobre os conferidos (abaixo) |
| `sombra_reexecucoes_total` | `operacao`, `resultado` | custo da reexecução de leituras divergentes |
| `sombra_reproducoes_total` | `operacao`, `resultado` (`guardada`, `limitada`, `erro`) | payload cifrado das divergências (fase 2 da ADR-0009) |
| `migracao_modo_info` (gauge) | `operacao`, `modo`, `percentual` | estado atual das flags |
| `kill_switch_ativo` (gauge) | — | alerta imediato |

**CNPJ inconsistente (C18/K04).** É medido nos workers da sombra, fora do caminho do cliente e sem consulta a mais ao Oracle. No `ConsultarNfse`, o legado já devolve o CNPJ do cadastro em cada nota (`PrestadorServico/IdentificacaoPrestador/Cnpj` vem do join com `CONTRIBUINTES`), e a fachada o compara com o `Prestador/Cnpj` do pedido. No `CancelarNfse`, a resposta só ecoa o pedido (K10). Quem confere ali é o novo, que já lê o cadastro pela IM na avaliação da sombra e devolve só o booleano `cnpjConfere`. A comparação usa só os dígitos e ignora zeros à esquerda. Pedido sem CNPJ, ou resposta sem nota (`E010`, `E040`, Fault), não entra em nenhum dos dois contadores. `ConsultarNfseServicoTomado` não tem prestador. Limites: a taxa vale para a **amostra** da sombra, e as chaves raras, sempre copiadas, puxam a taxa para os casos de erro. No M0 estrito (tudo em `legado`) não há cópia. A medida começa com a sombra (M1), ou no M0 com a operação em `sombra` e amostragem baixa. Descartamos medir no caminho do cliente: em `legado`, o corpo atravessa em streaming, e ler pedido e resposta ali somaria parsing ao `fachada_overhead` de toda requisição.

**Regra de cardinalidade:** prestador, documento, número de nota e protocolo **nunca** são labels. Esses dados ficam em traces e logs (pseudonimizados).

### Logs

JSON estruturado, com `trace_id` e `span_id` em todo evento; categoria de log por classe real (não `ILogger<OutraClasse>`, um dos problemas do PR-212).

### Backend

`grafana/otel-lgtm` no compose (OpenTelemetry Collector + Tempo + Prometheus + Loki + Grafana em uma imagem), com dashboards provisionados:

Um dashboard, **Migração por operação** ([deploy/grafana](../../deploy/grafana/dashboards/migracao-por-operacao.json)), com quatro seções:

1. **Por operação:** distribuição legado × novo, p99 por destino, taxa de erro, fallback.
2. **Sombra:** comparações, divergências por classe, descartes.
3. **Legado:** p99 visto pela fachada × medido dentro do legado.
4. **Cancelamento:** `cancelamento_regra_divergente_total` do dual-run, o gatilho de rollback do §6.2 do plano. A espera de lock no wrapper ainda não tem painel.

Em Kubernetes, o mesmo OTLP aponta para o backend da plataforma.

### Alertas ligados aos critérios do plano de migração

Os alertas usam só as classes **automáticas** do [ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md); `bug_novo`/`bug_legado` são resultado de triagem humana e alimentam o plano, não alertas.

Exemplos de alertas que **pedem rollback imediato**. Hoje o alerta chama o plantão, que aciona o nível 1 do kill switch (medido em 0,12–0,24 s no cluster); a volta automática pela própria fachada está prevista para o M1. Automáticos de verdade hoje são só o fail-safe para `legado` quando as flags não carregam e o abort do canário pela análise do Argo Rollouts:

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
- **Payload cifrado da sombra (fase 2 da [ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md), desligado por padrão).** É o único lugar em que o pedido de uma divergência existe por inteiro. Por isso a chave AES-256 tem dono e lista de acesso:
  - **Dono:** o responsável pela segurança do Nota Carioca. Ele gera a chave, guarda-a no cofre de segredos da plataforma (Secret via External Secrets) e a rotaciona a cada retenção (15 dias **[H]**) ou quando alguém sai do papel. O time de desenvolvimento não tem a chave.
  - **Fachada:** só a chave atual, que só cifra. As anteriores nunca entram no deployment dela.
  - **Quem decifra:** o papel de operação da sombra, isto é, o plantão designado para triar divergências, com a lista conhecida pelo DPO. A ferramenta roda num Job efêmero do namespace de produção, com RBAC próprio, a chave atual e as anteriores ainda na retenção. Nunca roda em máquina pessoal. O acesso fica na auditoria do cluster.
  - **Texto decifrado:** só no diretório efêmero do Job, apagado ao fim. Nunca vai para ticket, chat ou golden master: o caso que entra na [ADR-0011](0011-paridade-golden-master.md) é refeito com dados do seed.
  - **Destruição:** depois de 15 dias sem arquivos dela, a chave anterior é destruída, o que deixa ilegível qualquer cópia que tenha escapado da limpeza (crypto-shredding).
  - **Limite aceito:** a chave é simétrica, então quem lê o Secret da fachada, ou entra no pod dela, também decifra. Evolução possível: a fachada guardar só uma chave pública (envelope RSA-OAEP ou ECIES), e a privada ficar só com a operação.
- **Erros:** nenhuma resposta (SOAP ou REST) inclui mensagem de exceção ou stack trace (C20).

### Números desta ADR

| Valor | Origem | Onde calibrar |
| --- | --- | --- |
| Rollback se p99 do novo > 1,2 × p99 do legado por 15 min | [H] | variação natural do p99 do legado medida no M0 |
| Rollback se taxa de `fault` do novo > legado + 0,1 p.p. | [H] | taxa de fault do legado medida no M0 |
| Bloqueia avanço se descartes da sombra > 1% | [H] | capacidade do canal no teste de carga |
| Legado: 85% do tempo de `ConsultarNfse` no Oracle (mediana de 42 traces) | [M] compose local, 2026-10-19 | M0 em produção, se o legado for instrumentado lá |
| Rede + fila entre a chamada da fachada e o servidor do legado: 7 ms p50, 49 ms p95 (42 traces) | [M] compose local, 2026-10-19, máquina sob carga | M0 |

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
