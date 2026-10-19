# Schema NFSE_MODERNO

Objetos do serviço novo no Oracle do legado ([ADR-0002](../../docs/adr/0002-propriedade-dos-dados-na-transicao.md),
[ADR-0003](../../docs/adr/0003-regras-de-cancelamento-em-plsql.md)). Nenhuma tabela do legado é alterada.

| Script | Executado como | O que faz |
| --- | --- | --- |
| `01_usuarios.sql` | `SYSTEM` (ou DBA) no PDB | cria `NFSE_MODERNO` (dono), `NFSE_MODERNO_APP` (runtime) e `NFSE_MODERNO_RO` (sombra) |
| `02_grants_legado.sql` | `NFSE_OWNER` | `READ ... WITH GRANT OPTION` nas tabelas lidas, `SELECT` só em `NFSE` (lock do wrapper), `EXECUTE` em `PKG_NFSE` |
| `03_views.sql` | `NFSE_MODERNO` | views finas de leitura, repassadas a `_APP` e `_RO` |
| `04_pkg_cancelamento_api.sql` | `NFSE_MODERNO` | wrapper do cancelamento com lock e snapshot, executável só por `_APP` |

As senhas vêm de variáveis de substituição do SQL*Plus (`&senha_moderno`, `&senha_app`, `&senha_ro`); nunca ficam no
arquivo.

No compose, [`init.sh`](init.sh) aplica os quatro scripts na primeira subida do Oracle, depois da carga do legado
(`/container-entrypoint-initdb.d/20_moderno.sh`), com as senhas de `NFSE_MODERNO_PASSWORD`,
`NFSE_MODERNO_APP_PASSWORD` e `NFSE_MODERNO_RO_PASSWORD`. Se um script falhar ou sobrar objeto inválido no schema, o
init termina com erro e o container do Oracle para; corrija e suba de novo com `docker compose down -v`.
