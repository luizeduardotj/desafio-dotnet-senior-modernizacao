# ADR-0011 — Paridade: golden master, contrato e pipeline de testes

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0003](0003-regras-de-cancelamento-em-plsql.md), [0004](0004-comportamentos-legados-nao-documentados.md), [0006](0006-camada-anticorrupcao.md), [0009](0009-sombra-escrita-unica-e-idempotencia.md), [0010](0010-contrato-da-api-rest-v1.md)

## Contexto

O README pede:

- testes de caracterização (golden master) que rodem contra o legado **e** contra o serviço novo;
- testes de contrato para a REST;
- divergências da sombra explicadas;
- CI com testes unitários, de integração (Oracle real), de contrato e de paridade.

Obstáculos ao determinismo:

- `db/05_seed_base.sql` cria notas e uma guia paga na **competência corrente** (`TRUNC(SYSDATE,'MM')`, prestadores 25–27): o seed muda conforme o mês da carga;
- prazo de cancelamento depende de `SYSDATE` ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md));
- cancelamentos mudam o estado: rodar legado e novo no mesmo banco contamina o segundo;
- `db/06_seed_volume.sql` (2 milhões de notas) leva minutos.

### Premissas e nível de verificação

| # | Premissa | Fonte | Nível | Como confirmar | Se for falsa |
| --- | --- | --- | --- | --- | --- |
| P1 | `FIXED_DATE` congela `SYSDATE` (não `SYSTIMESTAMP`) | documentação Oracle | **confirmada** ([spike](../spikes/2026-10-19-premissas-oracle.md)) | spike: `SELECT SYSDATE` após o `ALTER SYSTEM` | plano B descrito abaixo |
| P2 | `FIXED_DATE` tem efeito dentro do PDB `FREEPDB1` | — | **confirmada** ([spike](../spikes/2026-10-19-premissas-oracle.md)) | spike no primeiro dia | definir no CDB, ou plano B |
| P3 | Com `FIXED_DATE`, o seed `01`–`05` é determinístico | `db/05_seed_base.sql:44` é a única leitura de relógio; não usa `DBMS_RANDOM` | lida | carregar duas vezes e comparar `NFSE` e `GUIAS` | normalizar campos no golden master |
| P4 | Uma imagem com o seed já aplicado pode ser criada e reutilizada no CI | imagem `gvenzl/oracle-free` | **confirmada**: cópia e restauração do volume exercitadas; a paridade do cancelamento roda sobre a base restaurada | spike: `docker commit` após o init, ou volume pré-carregado | carregar o seed a cada job (mais lento, sem o volume de 2 milhões) |

## Decisão

### Determinismo do banco

- **Relógio congelado:** antes dos scripts de `db/`, um script de teste executado **como SYS** (só na imagem de teste, sem alterar `db/`; o `init.sh` do legado roda como `NFSE_OWNER` e não tem esse privilégio) faz `ALTER SYSTEM SET FIXED_DATE = '2026-10-09-15:00:00'`. Com isso, o `SYSDATE` do seed, da regra de prazo e de `DT_CANCELAMENTO` fica fixo para legado e novo.
  - **A validar primeiro:** se o parâmetro tem efeito dentro do PDB `FREEPDB1` do Oracle Free (ou se precisa ser definido no CDB). Se não funcionar, o plano B é rodar o seed com competências parametrizadas por um script de teste e aceitar `SYSDATE` real, normalizando apenas `DataHora` do cancelamento.
  - O relógio congelado vale **só para o golden master**. A reconstrução de estado da sombra ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)) compara `DT_CANCELAMENTO` com `T0`, o que não faz sentido com `SYSDATE` parado; ela é testada com relógio injetado em testes de unidade e de integração sem `FIXED_DATE`.
- **Imagem de teste pré-carregada:** uma imagem Oracle com `01`–`05` já aplicados (sem o volume) é construída uma vez por mudança em `db/` e cacheada no CI.
- **Isolamento por alvo:** a suíte de paridade sobe **um container novo a partir da mesma imagem para cada alvo** (legado, depois novo). Ambos partem do mesmo estado inicial; as escritas de um não afetam o outro.

### Golden master

- **Corpus** em `tests/paridade/casos/`, um arquivo por caso: operação, request SOAP, `SOAPAction`, IDs do catálogo cobertos ([ADR-0004](0004-comportamentos-legados-nao-documentados.md)).
- Fontes do corpus:
  1. exemplos de `tools/exemplos/`;
  2. pelo menos um caso por ID do catálogo (C01–C29, K01–K11), incluindo fronteiras (`DataFinal`, 31/32 dias, página 0, página além do fim, documento com zero à esquerda, CPF em `<Cnpj>`, letras no documento, prazo dia 10 23:59:59 / dia 11 00:00:00 com `FIXED_DATE` alternativo);
  3. casos gerados a partir do seed (todas as combinações de situação × guia paga × prazo para o cancelamento).
- **Aprovação:** as respostas do legado são gravadas como arquivos aprovados (`*.aprovado.xml`, biblioteca Verify). O legado é a referência; o novo precisa reproduzir.
- **Comparação:** em bytes do envelope. Em falha, o relatório mostra a diferença em XML canônico. Nenhuma normalização de campos por padrão (com relógio congelado e containers isolados, `DataHora` e dados são determinísticos). Exceções só por caso, explicadas e ligadas a um ID do catálogo (ex.: C20 muda o texto do Fault de propósito).
- **O corpus não emite notas pelo lote.** O lote usa fontes de não determinismo fora do `SYSDATE` (`DBMS_RANDOM` no código de verificação, `db/03_pkg_nfse.sql:55`; `DateTime.Now` no protocolo e em RPS sem data, `LoteRepositorio.cs:15`, `LeitorLoteRps.cs:81`). As três operações extraídas são exercitadas sobre as notas do seed, que são determinísticas.
- **Estado do banco após escrita:** casos de cancelamento também comparam um snapshot de `NFSE` e `GUIAS` da competência afetada. Isso cobre K07 (guia) e K08 (contagem de `UPDATE`).

### Contrato REST

- **Validação de schema:** todas as respostas dos testes de integração da REST validam contra o OpenAPI gerado.
- **Compatibilidade:** oasdiff compara o OpenAPI do PR com o da `main`; quebra na v1 falha o build.
- **Propriedades:** testes de propriedade para período (inclusivo, ≤ 31 dias), cursor (sem repetição nem perda ao percorrer todas as páginas) e documentos (zeros preservados).

### Pipeline (GitHub Actions)

| Job | Conteúdo | Banco |
| --- | --- | --- |
| `build` | legado (SDK 8) e serviço novo + fachada (SDK 10), analyzers | — |
| `unitarios` | domínio, aplicação, decisão de destino da fachada, arquitetura | — |
| `integracao` | views, grants (`ORA-00942`/`PLS-00201` esperados), wrapper, dual-run, concorrência, reconstrução da sombra | Oracle real (imagem de teste) |
| `contrato` | REST contra OpenAPI + oasdiff | Oracle real |
| `paridade` | golden master: legado (container A) × novo (container B) | Oracle real |
| `imagens` | build das imagens (legado, fachada, novo) | — |

### Divergências da sombra em produção

Toda divergência triada como `bug_novo` ou `bug_legado` vira um caso no corpus antes da correção. Assim, o golden master cresce com a realidade de produção.

## Alternativas consideradas

- **Gravar tráfego real de produção como corpus.** Rejeitada como fonte principal (dados pessoais); usada só via divergências pseudonimizadas da sombra.
- **Normalizar `DataHora`, códigos de verificação e similares.** Rejeitada como padrão: esconderia diferenças reais; o relógio congelado torna isso desnecessário.
- **Mesmo banco para legado e novo com limpeza entre execuções.** Rejeitada: limpeza de NFSE/GUIAS dispara o trigger e não garante estado idêntico.
- **Pact (contrato dirigido pelo consumidor).** Rejeitada por ora: app e portal ainda não existem como consumidores; o OpenAPI é o contrato. Revisitar quando houver consumidores.

## Consequências

- **Positivas:** paridade reproduzível no CI; cada decisão do catálogo tem prova executável.
- **Negativas:** custo de CI com Oracle (~2–3 GB por container, sequencial); manter a imagem de teste.
- **Riscos:** `FIXED_DATE` afeta `SYSDATE` mas não `SYSTIMESTAMP`. Nas três operações extraídas, legado e novo dependem só de `SYSDATE`; um teste de guarda falha se aparecer `SYSTIMESTAMP`/`CURRENT_DATE`/`DateTime.Now` no código do serviço novo.

## Reversão

Ferramental de teste; sem efeito em produção.

## Como verificar

- O job `paridade` falha se qualquer caso divergir sem exceção documentada.
- Relatório do job lista cobertura por ID do catálogo; ID sem caso falha o build.
