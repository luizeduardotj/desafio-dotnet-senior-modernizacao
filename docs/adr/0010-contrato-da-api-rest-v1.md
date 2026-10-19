# ADR-0010 — Contrato da API REST v1

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0004](0004-comportamentos-legados-nao-documentados.md), [0007](0007-arquitetura-do-servico-novo.md), [0009](0009-sombra-escrita-unica-e-idempotencia.md), [0012](0012-observabilidade-seguranca-e-lgpd.md)

## Contexto

O novo app e o novo portal do contribuinte precisam de uma API moderna. O design é livre (versionamento, paginação, erros com ProblemDetails, OpenAPI), mas a API não pode herdar os defeitos do contrato SOAP ([ADR-0004](0004-comportamentos-legados-nao-documentados.md)) e precisa de autorização, que o legado não tem.

## Decisão

### Versionamento

- **Versão no caminho:** `/api/v1/...`.
- Mudança aditiva (campo novo, endpoint novo) fica na v1. Mudança que quebra cria a v2, com convivência mínima de 6 meses e headers `Deprecation` e `Sunset` na versão antiga.
- Quebras detectadas no CI comparando o OpenAPI gerado com o snapshot versionado ([ADR-0013](0013-paridade-como-implementada.md); oasdiff fica para quando houver mais de uma versão publicada).

### Recursos

| Método e caminho | Uso | Autorização |
| --- | --- | --- |
| `GET /api/v1/prestadores/{inscricao}/nfse` | notas emitidas pelo prestador | token do prestador `{inscricao}` |
| `GET /api/v1/prestadores/{inscricao}/nfse/{numero}` | uma nota | token do prestador |
| `GET /api/v1/tomador/nfse` | notas recebidas pelo titular do token | documento vem do token, **nunca da URL** |
| `POST /api/v1/prestadores/{inscricao}/nfse/consultas` | notas emitidas filtradas por tomador (documento no corpo) | token do prestador |
| `POST /api/v1/prestadores/{inscricao}/nfse/{numero}/cancelamento` | cancelar | token do prestador |

O CPF do tomador nunca aparece no caminho nem na query string: URLs vão para access logs do ingress e para o atributo `url.path` dos spans ([ADR-0012](0012-observabilidade-seguranca-e-lgpd.md)). Por isso o filtro por tomador usa `POST .../consultas` (consulta sem efeito colateral, com o filtro no corpo), e o `GET` de listagem não aceita documento.

Filtros de listagem: `dataInicial` e `dataFinal` (obrigatórios, `yyyy-MM-dd`, **inclusivos**, máximo de 31 dias), `tomador` (só no corpo de `POST .../consultas`), `situacao` (opcional: `normal`, `cancelada`, `substituida`).

### Paginação por cursor (keyset)

- `?limite=50&cursor=...`, com `limite` padrão 50 e máximo 100.
- O cursor é opaco (base64url de `(dataEmissao, id)` + hash dos filtros), seguindo a ordenação `DT_EMISSAO, ID` (C08).
- Resposta: `{ "itens": [...], "proximoCursor": "..." | null }`.
- Motivo principal: **estabilidade**. O lote emite notas o tempo todo; com offset, uma nota emitida entre duas páginas desloca o resultado e o cliente vê repetição ou perde itens. Com keyset, não.
- O custo não é o argumento: com janela de 31 dias, o offset já é barato, e não existe índice `(PRESTADOR_ID, DT_EMISSAO, ID)` (criá-lo seria DDL no legado). O plano de execução das duas formas é medido no teste de carga.

### Representação

- Documentos sempre como **string** com 11 ou 14 dígitos, preservando zeros, acompanhados de `tipo` (`cpf`/`cnpj`).
- Instantes em ISO 8601 **com offset** (`2026-10-09T15:26:34-03:00`); competência como `yyyy-MM`.
- Valores monetários como número decimal com 2 casas; alíquota como fração decimal (`0.05`); `issRetido` booleano.
- Situação explícita (`normal`/`cancelada`/`substituida`) com `cancelamento { dataHora, motivo }` ou `substituidaPor { numero }`.
- `dataEmissao` documentada no OpenAPI como "instante de emissão da NFS-e (processamento do lote)" (C14).

### Erros (RFC 9457 ProblemDetails)

| `type` (sufixo de `https://api.notacarioca.rio.gov.br/problemas/`) | HTTP | Origem |
| --- | --- | --- |
| `parametros-invalidos` | 400 | período, documento, limite ou cursor inválidos (`errors` por campo) |
| `nao-autenticado` / `sem-permissao` | 401 / 403 | autenticação / autorização |
| `nfse-nao-encontrada` | 404 | E050 |
| `nfse-substituida` | 409 | E051 |
| `prazo-de-cancelamento-expirado` | 422 | E052 |
| `guia-paga` | 422 | E053 |
| `erro-interno` | 500 | sem detalhe interno; com `traceId` (inclui espera de lock esgotada) |

O domínio não conhece esses códigos: o mapeamento é do adaptador REST, como o mapeamento para `E0xx` é do adaptador SOAP.

### Cancelamento

- Corpo: `{ "motivo": "..." }`, obrigatório, máximo de **255 bytes em UTF-8** (a coluna usa semântica de bytes, K11). O OpenAPI documenta o limite em bytes.
- `200` com o recurso de cancelamento, **inclusive quando a nota já estava cancelada** (K01: o recurso existente é devolvido; `cancelamento.dataHora` mostra quando ocorreu). Repetir a chamada é seguro; `Idempotency-Key` é opcional e serve só para correlação ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)).

### Autenticação e autorização

- Bearer JWT (OIDC). Claims esperadas: `inscricoes` (prestadores que o titular representa) e `documento` (titular).
- No ambiente local, emissor de desenvolvimento (`dotnet user-jwts`); o provedor real (gov.br/IdP da Prefeitura) é configurado por ambiente.

### OpenAPI

Gerado pelo próprio serviço (`Microsoft.AspNetCore.OpenApi`), com exemplos e todos os ProblemDetails documentados; publicado como artefato do CI.

### Números desta ADR

| Valor | Origem | Onde calibrar |
| --- | --- | --- |
| Período máximo de 31 dias, inclusivo | [E] | manual de integração |
| `limite` padrão 50, máximo 100 | 50 [E], 100 [H] | 50 vem do manual; 100 conforme uso do app/portal |
| Convivência de 6 meses entre versões | [H] | ciclo de release do app e do portal |
| Motivo ≤ 255 bytes | [L] | `db/01_schema.sql:84` (semântica de bytes) |

## Alternativas consideradas

- **Versionar por header ou media type.** Rejeitada: menos visível para clientes novos e para cache/observabilidade, sem benefício aqui.
- **Paginação por página/offset** (como o legado). Rejeitada pelo custo crescente e instabilidade.
- **Espelhar o contrato SOAP em JSON.** Rejeitada: transportaria os defeitos C01–C17 para um contrato novo.
- **Sem autenticação** (como o legado e o PR-212). Rejeitada: a REST é nova e não tem ERPs a preservar.

## Consequências

- **Positivas:** contrato estável e evolutivo; autorização desde o primeiro dia; paginação estável sob emissão contínua; nenhum documento pessoal em URL.
- **Negativas:** dois contratos (SOAP e REST) para manter; o portal depende de um IdP.
- **Riscos:** cursores ficam inválidos se a ordenação mudar — o hash dos filtros no cursor e a versão do formato permitem rejeitar com `400` claro.

## Reversão

A REST é aditiva e não afeta os ERPs. Uma versão com problema pode ser retirada do ingress sem impacto no SOAP.

## Como verificar

- Testes de contrato ([ADR-0011](0011-paridade-golden-master.md)): o snapshot do OpenAPI no CI bloqueia qualquer mudança não revisada na v1, e as respostas reais (sucesso e ProblemDetails) são validadas contra esse snapshot ([ADR-0013](0013-paridade-como-implementada.md)).
- Testes de autorização: prestador A não lê nem cancela nota do prestador B (`403`); tomador só lê o próprio documento.
- Testes: período de 31 dias inclusivo aceito, 32 rejeitado; `dataInicial = dataFinal` devolve as notas do dia.
