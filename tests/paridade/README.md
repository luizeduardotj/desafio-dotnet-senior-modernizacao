# Paridade: golden master do legado

Corpus de caracterização das três operações extraídas ([ADR-0011](../../docs/adr/0011-paridade-golden-master.md)).
Cada pasta em `casos/` tem:

| Arquivo | Conteúdo |
| --- | --- |
| `operacao` | operação SOAP (`ConsultarNfse`, `ConsultarNfseServicoTomado`, `CancelarNfse`) |
| `entrada.xml` | XML de negócio que vai escapado dentro de `inputXML` |
| `descricao` | o que o caso prova, com o ID do catálogo ([ADR-0004](../../docs/adr/0004-comportamentos-legados-nao-documentados.md)) |
| `legado.xml`, `legado.status` | resposta aprovada do legado (corpo HTTP e status) |
| `legado.estado` | só nos `2xx`: `NFSE` e `GUIAS` da competência da nota pedida depois do caso, no legado ([`estado-depois-do-cancelamento.sql`](estado-depois-do-cancelamento.sql)) |

Todo ID do catálogo (C01–C29, K01–K11) aparece na `descricao` de algum caso ou em [`catalogo-sem-caso`](catalogo-sem-caso), com o motivo e onde está coberto; `CoberturaDoCatalogoTests` falha o build se não (roda no job `moderno`, sem Oracle).

## Massa de teste

O seed do legado não exercita C11 (prestador pessoa física), C13 (competência posterior à emissão) nem C15 (CPF e CNPJ de mesmo valor numérico). [`massa/c11-c13-c15.sql`](massa/c11-c13-c15.sql) insere o prestador 31 (CPF, IM `10000031`) e cinco notas com chaves fixas, sem alterar tabela nem `db/0*`: notas 1–3 em agosto para um tomador novo, a 2 com competência 09/2026; notas 4–5 em julho para o CNPJ `00074579869109`, que como número é igual ao CPF válido `74579869109`, sem nota nenhuma. Nenhum outro caso lê esse prestador nem esses tomadores, e `INSERT` não dispara o trigger da guia: contagens, paginação e guias dos casos existentes não mudam (a recaptura do CI confere). `massa-de-teste.sh` aplica como SYS no `FREEPDB1`, é idempotente e entra logo depois do `compose up`: a linha de base da paridade é **seed + massa**.

O serviço novo é comparado contra `legado.xml` byte a byte pelos testes de `tests/Nfse.Paridade.Testes`:

| Teste | Casos | Pré-condição |
| --- | --- | --- |
| `ParidadeLeiturasTests` | `0xx` e `1xx` (ConsultarNfse e ServicoTomado) | `NFSE_PARIDADE_ORACLE` com a connection string do `NFSE_MODERNO_APP`; não depende do relógio nem dos cancelamentos |
| `ParidadeCancelamentoTests` | `2xx`, na ordem, resposta **e** `legado.estado` | linha de base restaurada, `db/moderno` aplicado, `FIXED_DATE` e `NFSE_PARIDADE_CANCELAMENTO=1`; muda o banco, roda uma vez |
| `CoberturaDoCatalogoTests` | todos (só `descricao`) | nenhuma |
| `GarantiasDeGrantsTests` | — (grants do `NFSE_MODERNO`) | `NFSE_PARIDADE_ORACLE` e/ou `NFSE_PARIDADE_ORACLE_RO`; cada teste roda com o usuário que confere; não escreve |
| `GarantiaK08Tests` | — (K08 pelo wrapper) | `NFSE_PARIDADE_ORACLE` e `NFSE_PARIDADE_K08=1`; cancela a nota 27/5, roda uma vez por banco na linha de base |

Para rodar o cancelamento, com o compose de pé e o banco na linha de base: `tests/paridade/cancelamento-fixed-date.sh` (exige `NFSE_PARIDADE_ORACLE`). Ele confere que as notas 1 e 2 do prestador 25 ainda não foram canceladas, aplica `FIXED_DATE` como SYS no `FREEPDB1`, roda só o `ParidadeCancelamentoTests` e volta `FIXED_DATE = NONE` ao sair, passe ou falhe.

`recapturar-leituras-legado.sh` faz o caminho inverso para as leituras: recaptura os casos `0xx` e `1xx` direto do legado do compose (um curl na rede da fachada, para `webservices:8080`) e falha se `git diff --exit-code tests/paridade/casos` mostrar diferença. Prova que o golden master ainda descreve o legado, e não só que o novo o reproduz. Não há campo volátil a mascarar: as leituras não dependem do relógio (datas fixas no seed, nenhuma data de processamento na resposta) e rodam sem `FIXED_DATE`. Os `2xx` ficam de fora porque escrevem; o legado deles é conferido pela captura com `FIXED_DATE` descrita abaixo.

## Garantias do banco

Premissas que o serviço novo assume do Oracle, antes conferidas à mão no [spike de 2026-10-19](../../docs/spikes/2026-10-19-premissas-oracle.md), agora em `GarantiasDoBancoTests.cs`. Ficam neste projeto, e não num projeto de integração novo, porque usam o mesmo banco, as mesmas variáveis e o mesmo step do CI que a paridade; um projeto à parte duplicaria a configuração sem isolar nada.

| Garantia | Usuário | Esperado |
| --- | --- | --- |
| a sombra lê as views `V_NFSE`, `V_CONTRIBUINTE`, `V_GUIA` | `NFSE_MODERNO_RO` | consulta funciona |
| a sombra não lê `NFSE_OWNER.NFSE` | `NFSE_MODERNO_RO` | `ORA-00942` (o 23ai pode dar `ORA-41900`) |
| a sombra não executa o wrapper ([ADR-0009](../../docs/adr/0009-sombra-escrita-unica-e-idempotencia.md)) | `NFSE_MODERNO_RO` | `ORA-06550` com `PLS-00201` |
| o runtime não lê nem altera `NFSE_OWNER.NFSE` e não chama `PKG_NFSE` direto ([ADR-0002](../../docs/adr/0002-propriedade-dos-dados-na-transicao.md)) | `NFSE_MODERNO_APP` | `ORA-00942`/`ORA-41900`; `PLS-00201` |
| `READ` nas views não permite `FOR UPDATE` nem DML | `NFSE_MODERNO_APP` | `ORA-01031` no 19c, `ORA-41900` no 23ai |
| K08 corrigido: dois cancelamentos paralelos da mesma nota pelo wrapper ([ADR-0003](../../docs/adr/0003-regras-de-cancelamento-em-plsql.md)) | `NFSE_MODERNO_APP` | a 2ª sessão espera o lock, vê `C` e sai sem `UPDATE`: `DT_CANCELAMENTO` e motivo da 1ª, guia recalculada uma vez |

Os códigos variam entre o 23ai do compose e o 19c de produção; os testes aceitam os das duas versões para a mesma classe de erro. Os testes de negativa que poderiam escrever, se um grant mudasse, usam `WHERE 1 = 0` e `ROLLBACK`.

O K08 escreve. Usa a nota **27/5**: nenhum caso do corpus a usa (o cancelamento mexe em 25/1 e 25/2; 27/2 só aparece no K11, que falha sem gravar), o tomador dela não aparece nas consultas por tomador e a competência é a do mês da carga, com o prazo aberto pelo relógio real, sem `FIXED_DATE`. A sessão A cancela e segura o lock por 3 s; a B chama durante esse tempo. Se B gravasse de novo, o `SYSDATE` dela ficaria ao menos 3 s à frente no `DT_CANCELAMENTO` e no `DT_ATUALIZACAO` da guia. O `VALOR_ISS` da guia fica igual ao de antes porque o trigger autônomo não vê a nota cancelada (K07, preservado na migração). Para repetir localmente, restaure a linha de base.

Divergências esperadas, por decisão do catálogo: `023` (C28, DTD recusada), `112` (C15: o legado devolve ao CPF as notas do CNPJ de mesmo número, o novo responde E040) e `209` (K11 com C20: Fault sem a mensagem do Oracle).

## Estado do banco depois do cancelamento

Cada `2xx` compara também `NFSE` e `GUIAS` da competência da nota pedida com `legado.estado`. O snapshot é um único SQL ([`estado-depois-do-cancelamento.sql`](estado-depois-do-cancelamento.sql)) lido dos dois lados: pelo `estado-do-banco.sh` (sqlplus como SYS) na captura do legado e pelo teste (ODP.NET com o `NFSE_MODERNO_APP`, só as views). Para não depender do mês da carga do seed, as datas saem relativas ao início da competência, e o que o próprio caso gravou sai como `agora` (o `SYSDATE` congelado); o `DT_ATUALIZACAO` da guia vira `agora`/`antes`. O `210` mostra o K07: a nota 1 do prestador 25 fica `C` e a guia é atualizada `agora` com o mesmo `VALOR_ISS`.

`capturar.sh novo <url>` também grava `novo.xml` ao lado, para inspeção manual.

## No CI

O job `compose` sobe o banco do zero, aplica a massa (`massa-de-teste.sh`) e, depois do smoke, roda nesta ordem:

1. **Golden master ainda descreve o legado**: `recapturar-leituras-legado.sh`, antes de qualquer escrita.
2. **Paridade e garantias do banco**: leituras, sombra do cancelamento e grants, sem `FIXED_DATE`. Nenhum deles escreve; o cancelamento e o K08 ficam em skip porque `NFSE_PARIDADE_CANCELAMENTO` e `NFSE_PARIDADE_K08` não estão definidas.
3. **Paridade do cancelamento (FIXED_DATE)**: `cancelamento-fixed-date.sh`, respostas e estado do banco.
4. **K08 corrigido pelo wrapper**: só o `GarantiaK08Tests`, com `NFSE_PARIDADE_K08=1`. Cancela a nota 27/5. Vem depois do 3 porque o `legado.estado` do `209` (prestador 27) foi capturado sem esse cancelamento. Último step que escreve; o `down -v` do fim descarta o banco.
5. **Leituras conferidas de novo**: só o `ParidadeLeiturasTests`, para provar que as escritas dos steps 3 e 4 não tocam o golden master das leituras.

No CI o seed carrega com o relógio real, e o `FIXED_DATE` só entra depois. Isso não muda o resultado dos casos `2xx`. As notas dos prestadores 1 a 24 têm datas fixas (julho a outubro de 2026). Os prestadores 25 a 27 ficam na competência do mês da carga, que nunca é anterior a outubro de 2026, então o prazo continua aberto em 09/10/2026. O mês da competência também não aparece na resposta do `CancelarNfse`, e o `DataHora` é o `SYSDATE` congelado.

## Como capturar de forma reproduzível

1. Banco no estado da linha de base: seed `01`–`06` recém-carregado (volume `nfse-oracle-baseline`, ver abaixo) e `massa-de-teste.sh` aplicado.
2. Relógio congelado, como SYS no PDB: `ALTER SYSTEM SET FIXED_DATE = '2026-10-09-15:00:00';`
3. `ESTADO=1 tests/paridade/capturar.sh legado <url do legado>`. Os casos `2xx` (cancelamento) mudam o estado: rodam por último, na ordem dos nomes, e cada um grava também `legado.estado`.
4. Restaurar a linha de base antes de capturar outro alvo.

Restauração da linha de base (com o compose parado):

```bash
docker run --rm -v nfse-oracle-baseline:/de:ro -v desafio-dotnet-senior-modernizacao_oracle-data:/para alpine sh -c 'rm -rf /para/* && cp -a /de/. /para/'
```

## Fora do corpus por ora

- Os IDs de [`catalogo-sem-caso`](catalogo-sem-caso) (C14, C21, C22, K08, K09), cada um com o motivo e o teste que o cobre fora do corpus.
- C20 por erro interno arbitrário: coberto só pelo K11 (209), que provoca `ORA-12899`.
- Emissão por lote: não determinística (`DBMS_RANDOM`, `DateTime.Now`) e fora do escopo.
