# Paridade: golden master do legado

Corpus de caracterização das três operações extraídas ([ADR-0011](../../docs/adr/0011-paridade-golden-master.md)).
Cada pasta em `casos/` tem:

| Arquivo | Conteúdo |
| --- | --- |
| `operacao` | operação SOAP (`ConsultarNfse`, `ConsultarNfseServicoTomado`, `CancelarNfse`) |
| `entrada.xml` | XML de negócio que vai escapado dentro de `inputXML` |
| `descricao` | o que o caso prova, com o ID do catálogo ([ADR-0004](../../docs/adr/0004-comportamentos-legados-nao-documentados.md)) |
| `legado.xml`, `legado.status` | resposta aprovada do legado (corpo HTTP e status) |

O serviço novo é comparado contra `legado.xml` byte a byte pelos testes de `tests/Nfse.Paridade.Testes`:

| Teste | Casos | Pré-condição |
| --- | --- | --- |
| `ParidadeLeiturasTests` | `0xx` e `1xx` (ConsultarNfse e ServicoTomado) | `NFSE_PARIDADE_ORACLE` com a connection string do `NFSE_MODERNO_APP`; não depende do relógio nem dos cancelamentos |
| `ParidadeCancelamentoTests` | `2xx`, na ordem | linha de base restaurada, `db/moderno` aplicado, `FIXED_DATE` e `NFSE_PARIDADE_CANCELAMENTO=1`; muda o banco, roda uma vez |

Para rodar o cancelamento, com o compose de pé e o banco na linha de base: `tests/paridade/cancelamento-fixed-date.sh` (exige `NFSE_PARIDADE_ORACLE`). Ele confere que as notas 1 e 2 do prestador 25 ainda não foram canceladas, aplica `FIXED_DATE` como SYS no `FREEPDB1`, roda só o `ParidadeCancelamentoTests` e volta `FIXED_DATE = NONE` ao sair, passe ou falhe.

Divergências esperadas, por decisão do catálogo: `023` (C28, DTD recusada) e `209` (K11 com C20: Fault sem a mensagem do Oracle).

`capturar.sh novo <url>` também grava `novo.xml` ao lado, para inspeção manual.

## No CI

O job `compose` sobe o banco do zero e roda a paridade em dois steps, nesta ordem:

1. **Paridade**: leituras e sombra do cancelamento, sem `FIXED_DATE`. Nenhuma delas escreve, e o cancelamento fica em skip porque `NFSE_PARIDADE_CANCELAMENTO` não está definida.
2. **Paridade do cancelamento (FIXED_DATE)**: `cancelamento-fixed-date.sh`. É o último uso do banco no job, e o `down -v` do fim o descarta. Assim as leituras nunca veem as notas canceladas. Numa execução local do job inteiro, num banco recém-carregado, as leituras também passaram depois do cancelamento (35 de 35). Mesmo assim, o CI mantém a ordem para não depender disso.

No CI o seed carrega com o relógio real, e o `FIXED_DATE` só entra depois. Isso não muda o resultado dos casos `2xx`. As notas dos prestadores 1 a 24 têm datas fixas (julho a outubro de 2026). Os prestadores 25 a 27 ficam na competência do mês da carga, que nunca é anterior a outubro de 2026, então o prazo continua aberto em 09/10/2026. O mês da competência também não aparece na resposta do `CancelarNfse`, e o `DataHora` é o `SYSDATE` congelado.

## Como capturar de forma reproduzível

1. Banco no estado da linha de base: seed `01`–`06` recém-carregado (volume `nfse-oracle-baseline`, ver abaixo).
2. Relógio congelado, como SYS no PDB: `ALTER SYSTEM SET FIXED_DATE = '2026-10-09-15:00:00';`
3. `tests/paridade/capturar.sh legado`. Os casos `2xx` (cancelamento) mudam o estado: rodam por último, na ordem dos nomes.
4. Restaurar a linha de base antes de capturar outro alvo.

Restauração da linha de base (com o compose parado):

```bash
docker run --rm -v nfse-oracle-baseline:/de:ro -v desafio-dotnet-senior-modernizacao_oracle-data:/para alpine sh -c 'rm -rf /para/* && cp -a /de/. /para/'
```

## Fora do corpus por ora

- C13 (`COMPETENCIA <= fim`) e C15 (colisão CPF × CNPJ numérico): o seed não tem dados que os exercitem; precisam de massa própria.
- C20 por erro interno arbitrário: coberto só pelo K11 (209), que provoca `ORA-12899`.
- Emissão por lote: não determinística (`DBMS_RANDOM`, `DateTime.Now`) e fora do escopo.
