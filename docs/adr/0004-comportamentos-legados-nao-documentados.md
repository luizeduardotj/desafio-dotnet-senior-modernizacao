# ADR-0004 — Comportamentos legados não documentados

- **Status:** Aceito (catálogo vivo: novos itens entram por PR neste arquivo)
- **Data:** 2026-10-16
- **Relacionados:** [0003](0003-regras-de-cancelamento-em-plsql.md), [0005](0005-correcao-do-trigger-de-recalculo-da-guia.md), [0006](0006-camada-anticorrupcao.md), [0010](0010-contrato-da-api-rest-v1.md), [0011](0011-paridade-golden-master.md)

## Contexto

O manual de integração é a única documentação, e "o comportamento em produção é a especificação real". A leitura do código e dos exemplos (`tools/exemplos/`) revelou comportamentos que o manual não descreve ou contradiz. ERPs podem depender de qualquer um deles.

## Decisão

### Política

1. **O contrato SOAP preserva o comportamento observável do legado**, inclusive defeitos, salvo os itens de segurança. A preservação vive **só** na camada anticorrupção ([ADR-0006](0006-camada-anticorrupcao.md)), nunca no domínio.
2. **A API REST nasce correta** e segue o manual quando ele for claro ([ADR-0010](0010-contrato-da-api-rest-v1.md)).
3. Todo item tem **classificação**, **decisão** e **teste de caracterização** ([ADR-0011](0011-paridade-golden-master.md)) com o ID abaixo no nome.
4. Toda divergência nova encontrada pela sombra entra neste catálogo **antes** de a operação avançar de fase.

### Classificação

| Classe | Significado |
| --- | --- |
| **PS** | Preservar no SOAP, corrigir na REST |
| **PA** | Preservar nos dois (é semântica de negócio, não defeito) |
| **CA** | Corrigir nos dois (quebra comunicada; só se o efeito é claramente danoso ao contribuinte) |
| **CN** | Corrigir só no caminho novo, porque a correção é invisível ao ERP (a resposta só muda em cenário de defeito técnico) |
| **BL** | Bug do legado com efeito de negócio, preservado na migração e tratado por ADR próprio |
| **SEG** | Defeito de segurança/privacidade: decisão explícita com risco aceito ou correção |

### Catálogo — consultas (`ConsultarNfse`, `ConsultarNfseServicoTomado`)

| ID | Comportamento | Evidência | Classe | SOAP | REST |
| --- | --- | --- | --- | --- | --- |
| C01 | **`DataFinal` é exclusiva**: notas emitidas no próprio dia final não voltam (`DT_EMISSAO < :fim` com `fim = DataFinal 00:00`) | `NfseRepositorio.cs:114`, `:154` | PS | preserva | período inclusivo |
| C02 | `DataInicial = DataFinal` passa na validação e sempre devolve `E040` | consequência de C01; `ConsultaNfseServico.cs:39` | PS | preserva | devolve as notas do dia |
| C03 | Limite de período é `(fim − ini) > 31 dias` → aceita 32 dias de calendário (31 efetivos por causa de C01) | `ConsultaNfseServico.cs:40` | PS | preserva | máximo 31 dias inclusivos |
| C04 | Datas aceitas por `DateTime.TryParse(InvariantCulture)`: formatos não ISO (`MM/dd/yyyy`) são aceitos; offset é convertido para o fuso do servidor antes de truncar a hora | `ConsultaNfseServico.cs:35-38` | PS | preserva (ERPs antigos podem mandar outros formatos) | ISO 8601 `yyyy-MM-dd` estrito |
| C05 | `Pagina` ausente, inválida ou `< 1` vira 1 em silêncio | `ConsultaNfseServico.cs:44-50` | PS | preserva | `400` com ProblemDetails |
| C06 | Página além do fim devolve `E040` ("nenhuma NFS-e"), igual a consulta sem resultado | `ConsultaNfseServico.cs:99-100` | PS | preserva | `200` com lista vazia |
| C07 | Paginação: base 1, 50 por página, `ProximaPagina` calculado buscando 51 linhas | `NfseRepositorio.cs:10`, `:127-128`; `ConsultaNfseServico.cs:60-74` | PS | preserva | cursor ([ADR-0010](0010-contrato-da-api-rest-v1.md)) |
| C08 | Ordenação `DT_EMISSAO, ID` ascendente | `NfseRepositorio.cs:115`, `:155` | PA | preserva | mesma ordem |
| C09 | Sem filtro de situação: canceladas e substituídas voltam, com blocos `NfseCancelamento` / `NfseSubstituicao` irmãos de `InfNfse` | `XmlNfseBuilder.cs:102-113` | PA | preserva | `situacao` explícita no recurso |
| C10 | **Documento do tomador perde zero à esquerda** na saída (`Convert.ToInt64`), e a tag `Cpf`/`Cnpj` é escolhida pelo tamanho da string gravada (ex.: `01234567000195` sai `<Cnpj>1234567000195</Cnpj>`) | `XmlNfseBuilder.cs:92-96`; `tools/exemplos/04e-*.resposta.xml` | PS | preserva | string com 11/14 dígitos |
| C11 | Prestador sempre sai na tag `<Cnpj>`, mesmo quando o documento é CPF | `XmlNfseBuilder.cs:86` | PS | preserva | campo `documento` + `tipo` |
| C12 | Validação de CPF/CNPJ remove **qualquer** caractere não numérico (letras incluídas) e decide pelo tamanho, ignorando a tag (CPF em `<Cnpj>` é aceito) | `ValidadorDocumento.vb:6-13`, `:72-77` | PS | preserva | só dígitos; tipo coerente com tamanho |
| C13 | Filtro extra `COMPETENCIA <= :fim` em `ConsultarNfse` (não existe em `ServicoTomado`) | `NfseRepositorio.cs:113` | PS | preserva | filtra só por emissão |
| C14 | `DataEmissao` é o instante do **processamento do lote** (`SYSDATE` em `gerar_nfse`), não a data do RPS | `db/03_pkg_nfse.sql:60` | PA | preserva | documentado no OpenAPI |
| C15 | `ServicoTomado` compara documento como **número** (`TOMADOR_DOCUMENTO = :doc` com bind `Int64` sobre `VARCHAR2`): ignora zeros à esquerda e permite colisão entre CPF `X` e CNPJ `000X` (notas de outro tomador) | `NfseRepositorio.cs:153`, `:160` | **SEG** | **corrige**: compara como string de 11/14 dígitos (equivalente para todo documento válido, exceto a colisão) | string |
| C16 | Datas de saída sem offset (`yyyy-MM-ddTHH:mm:ss`, horário de São Paulo) | `XmlNfseBuilder.cs:21-23` | PS | preserva | ISO 8601 com offset `-03:00` |
| C17 | Valores com ponto e 2 casas; alíquota como fração `0.####`; `IssRetido` `1`/`2` | `XmlNfseBuilder.cs:33-38`, `:80` | PS | preserva | números decimais; booleano |
| C18 | Prestador identificado **só pela Inscrição Municipal**; CNPJ do prestador nunca é validado nem comparado | `ConsultaNfseServico.cs:18-27` | **SEG** | preserva, risco aceito (ver abaixo) | prestador vem da identidade autenticada |
| C19 | `ServicoTomado` não tem autorização: qualquer CPF/CNPJ válido lista notas de todos os prestadores | `NfseRepositorio.cs:144-165` | **SEG** | preserva, risco aceito | consulente autenticado = documento consultado |
| C20 | Erro inesperado vira `soap:Fault` HTTP 500 com `ex.Message` (vaza mensagem do Oracle) | `Program.cs:91-95` | **SEG** | **corrige**: mesmo Fault `soap:Server` com texto genérico e `traceId` | ProblemDetails `500` sem detalhe interno |
| C21 | Despacho só pelo header `SOAPAction` (sufixo após o último `/`); o elemento do corpo é ignorado | `Program.cs:59-60` | PA | preserva (a fachada roteia pela mesma regra) | n/a |
| C22 | `inputXML` é o **primeiro** elemento com esse nome local em qualquer lugar do envelope, em qualquer namespace | `Program.cs:111-124` | PS | preserva | n/a |
| C23 | Elementos lidos por XPath sem namespace e com `InnerText.Trim()`: um `xmlns` padrão no XML de negócio faz os caminhos falharem (`E010` em `ConsultarNfse`, `E020` em `ServicoTomado`); prefixo na raiz → `E001`; espaços nas bordas são ignorados | `LeitorLoteRps.cs:27-32`; `ConsultaNfseServico.cs:83`, `:111` | PS | preserva | n/a |
| C24 | Ordem de validação **por operação**: `ConsultarNfse` E001 → E010 → E020 (tomador opcional) → E030; `ServicoTomado` E001 → E020 (obrigatório, sem E010) → E030; `CancelarNfse` E001 (inclui `Numero` não numérico) → E010 → PL/SQL | `ConsultaNfseServico.cs:76-128`; `CancelamentoNfseServico.cs:15-47` | PA | preserva | validação agregada em `400` |
| C25 | Prestador inativo responde `E010`, igual a inexistente | `ConsultaNfseServico.cs:25`; `CancelamentoNfseServico.cs:42` | PS | preserva | `404`/`403` conforme o caso |
| C26 | `ServicoTomado` lê `Consulente/CpfCnpj` e, se vazio, `Tomador/CpfCnpj`; dentro de `CpfCnpj`, `Cnpj` tem precedência sobre `Cpf` | `ConsultaNfseServico.cs:52-58`, `:114-116` | PS | preserva | documento vem do token |
| C27 | **Overflow de `int` na paginação**: `pagina * 50 + 1` sem checagem; páginas enormes produzem limites negativos ou pequenos (ex.: `Pagina=85899346` devolve as primeiras notas com `ProximaPagina=false`) | `NfseRepositorio.cs:127-128`, `:163-164` | PS | preserva (reproduz a mesma aritmética `int` não checada) | `limite` validado |
| C28 | **DTD interna aceita**: `XmlDocument.LoadXml` processa DTD e expande entidades internas do XML de negócio | `LeitorLoteRps.cs:20-25` | **SEG** | **corrige**: DTD rejeitada → `E001` (decisão consciente, risco de *entity expansion*) | JSON |
| C29 | Comparações exatas e sensíveis a maiúsculas: nome da raiz, `SOAPAction`, `ATIVO = "S"` | `Program.cs:73`; `ConsultaNfseServico.cs:83`; `Prestador.cs:23` | PA | preserva | n/a |

### Catálogo — cancelamento (`CancelarNfse`)

| ID | Comportamento | Evidência | Classe | SOAP | REST |
| --- | --- | --- | --- | --- | --- |
| K01 | Cancelar nota já cancelada devolve **sucesso**, com o `DataHora` original, **mesmo fora do prazo** | `db/03_pkg_nfse.sql:89-91` | PA | preserva (torna reenvio seguro) | `200` com o cancelamento existente |
| K02 | Ordem: `E050` → já cancelada → `E051` → `E052` → `E053` (prazo vence guia paga) | `db/03_pkg_nfse.sql:77-112` | PA | preserva | mesma precedência |
| K03 | Prazo até o dia 10 às 23:59:59 do mês seguinte, pelo relógio do **banco** | `db/03_pkg_nfse.sql:98` | PA | preserva | idem ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md)) |
| K04 | CNPJ do pedido é ignorado; prestador vem só da IM do pedido | `CancelamentoNfseServico.cs:32-43` | **SEG** | preserva, risco aceito | identidade autenticada |
| K05 | `CodigoCancelamento` é ignorado (default `"1"`), apenas ecoado na resposta | `CancelamentoNfseServico.cs:34`, `:55` | PS | preserva o eco | não aceito (não há onde gravar) |
| K06 | `Motivo` vazio vira texto padrão; motivo longo é truncado em 255 **caracteres** | `CancelamentoNfseServico.cs:35-36`; `db/03_pkg_nfse.sql:117` | PS | preserva | obrigatório, máx. 255 bytes UTF-8 (ver K11), senão `400` |
| K07 | **A guia não desconta a nota cancelada** (trigger autônomo lê estado commitado) | `db/04_triggers.sql:9-12` | BL | preserva na migração; correção em [ADR-0005](0005-correcao-do-trigger-de-recalculo-da-guia.md) | idem |
| K08 | Cancelamentos concorrentes da mesma nota fazem dois `UPDATE` | `db/03_pkg_nfse.sql:78-119` (sem lock) | CN | **corrige no caminho novo** (lock no wrapper) | idem |
| K09 | Falha na releitura pós-commit devolve Fault 500 com o cancelamento já efetivado | `CancelamentoNfseServico.cs:49-50` | CN | **corrige no caminho novo** (wrapper devolve `DT_CANCELAMENTO` na mesma transação) | idem |
| K11 | **Motivo com acentos estoura a coluna**: `MOTIVO_CANCELAMENTO VARCHAR2(255)` usa semântica de bytes (padrão) em banco AL32UTF8, mas o `SUBSTR` corta em caracteres. Um motivo de 255 caracteres acentuados gera `ORA-12899` e Fault 500 com a mensagem do Oracle | `db/01_schema.sql:84`; `db/03_pkg_nfse.sql:117`; `Program.cs:91-95` | PA (com C20) | preserva o Fault, sem a mensagem interna | rejeita acima de 255 bytes com `400` |
| K10 | Resposta de sucesso ecoa `Numero`, `Cnpj`, `InscricaoMunicipal`, `CodigoCancelamento` do pedido e não traz protocolo | `CancelamentoNfseServico.cs:52-57` | PS | preserva byte a byte | recurso `cancelamento` |

### Riscos de segurança aceitos (C18, C19, K04)

Corrigir exige autenticação no SOAP, que os ERPs não suportam hoje: qualquer correção quebraria milhares de integrações, o oposto do objetivo. Decisão:

- **Aceitar o risco no SOAP durante a migração**, registrado como dívida com dono (responsável pela segurança do Nota Carioca) e revisão a cada fase.
- **Medir antes de corrigir**: a sombra registra, sem dados pessoais, quantas requisições trazem CNPJ diferente do cadastrado para a IM (`soap_cnpj_inconsistente_total{operacao}`). Esse número decide se uma futura validação de CNPJ é viável sem quebra.
- A API REST não herda nenhum desses riscos.

### Itens corrigidos no SOAP (C15, C20, C28, K08, K09)

São correções em que a resposta só muda em cenário de defeito (colisão de documentos, erro interno, DTD maliciosa, concorrência, falha pós-commit). Cada uma aparece como divergência **esperada** na sombra, classificada como "decisão consciente" ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)).

## Alternativas consideradas

- **Corrigir tudo no SOAP também.** Rejeitada: contraria "sem que nenhum contribuinte perceba". Um ERP que completa zeros à esquerda (C10) ou que envia o último dia + 1 para compensar C01 quebraria.
- **Preservar tudo, inclusive os defeitos de segurança, na REST.** Rejeitada: a REST é um contrato novo; herdar defeitos seria dívida gratuita.
- **Implementar a compatibilidade no domínio com flags.** Rejeitada: vazaria o legado para o domínio; a compatibilidade é responsabilidade da camada anticorrupção.

## Consequências

- **Positivas:** cada comportamento tem decisão e teste; divergências da sombra têm onde ser explicadas.
- **Negativas:** a camada anticorrupção carrega lógica deliberadamente "errada", que precisa ser lida como intencional (cada trecho referencia o ID deste catálogo).
- **Riscos:** comportamentos ainda não descobertos — mitigado pela sombra em produção e pela política 4.
- **Fora do catálogo de propósito:** comportamentos do lote (`RecepcionarLoteRps`, `ConsultarLoteRps`), como `DateTime.Now` para RPS sem data, numeração `MAX+1` sem lock e lista de resultados estática compartilhada entre threads. Não fazem parte da extração, mas afetam os dados que o serviço novo lê (ex.: C14).

## Reversão

Cada item é uma regra isolada na camada anticorrupção, com teste próprio. Reverter uma decisão = trocar a regra e o golden master correspondente.

## Como verificar

- Teste de caracterização por ID (`C01_DataFinalExclusiva`, ...) roda contra o legado e contra o serviço novo ([ADR-0011](0011-paridade-golden-master.md)).
- Busca no código da camada anticorrupção: todo desvio intencional referencia um ID deste catálogo.
