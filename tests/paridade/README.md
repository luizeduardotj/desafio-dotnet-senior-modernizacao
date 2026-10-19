# Paridade: golden master do legado

Corpus de caracterização das três operações extraídas ([ADR-0011](../../docs/adr/0011-paridade-golden-master.md)).
Cada pasta em `casos/` tem:

| Arquivo | Conteúdo |
| --- | --- |
| `operacao` | operação SOAP (`ConsultarNfse`, `ConsultarNfseServicoTomado`, `CancelarNfse`) |
| `entrada.xml` | XML de negócio que vai escapado dentro de `inputXML` |
| `descricao` | o que o caso prova, com o ID do catálogo ([ADR-0004](../../docs/adr/0004-comportamentos-legados-nao-documentados.md)) |
| `legado.xml`, `legado.status` | resposta aprovada do legado (corpo HTTP e status) |

O serviço novo é comparado contra `legado.xml` byte a byte (`capturar.sh novo <url>` grava `novo.xml` ao lado).

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
