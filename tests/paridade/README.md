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

Divergências esperadas, por decisão do catálogo: `023` (C28, DTD recusada) e `209` (K11 com C20: Fault sem a mensagem do Oracle).

`capturar.sh novo <url>` também grava `novo.xml` ao lado, para inspeção manual.

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
