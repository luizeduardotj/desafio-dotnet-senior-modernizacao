# Spike 2026-10-19 — premissas no Oracle local

Execução das premissas que as ADRs marcavam como "lida no código". Ambiente: `docker compose` local, Oracle **23ai Free** (`gvenzl/oracle-free:23-slim`, arm64), seed `01`–`06` recém-carregado (2.000.412 notas, carga em 48 s), `TZ=America/Sao_Paulo`. Produção é **19c**: onde o comportamento depende da versão, está indicado.

## Resultados

| Premissa | ADR | Resultado | Evidência |
| --- | --- | --- | --- |
| Objetos PL/SQL do legado compilam | 0011 (D1) | **confirmada**: `PKG_GUIA`, `PKG_NFSE` e o trigger `VALID` | `USER_OBJECTS` |
| Nenhuma `COMPETENCIA` nula ou fora do dia 1 | 0003 P7, 0005 P4 | **confirmada no seed** (0 de 2.000.412); produção segue a confirmar | `COUNT` por condição |
| `SYSDATE` no fuso de São Paulo | 0003 P8 | **local apenas**: `SYSDATE` segue o fuso do SO do container; `DBTIMEZONE = +00:00`, `SESSIONTIMEZONE = -03:00`. Produção segue a confirmar com o DBA | `SELECT SYSDATE, DBTIMEZONE, SESSIONTIMEZONE` |
| `FIXED_DATE` congela `SYSDATE` e não `SYSTIMESTAMP` | 0011 P1 | **confirmada** | `SYSDATE = 2026-10-09 15:00:00`, `SYSTIMESTAMP` real |
| `FIXED_DATE` funciona dentro do PDB `FREEPDB1` | 0011 P2 | **confirmada**; vale para sessões novas de outros usuários | `ALTER SYSTEM` como SYS conectado ao PDB |
| Snapshot do volume reutilizável | 0011 P4 | **parcial**: cópia do volume com o banco parado (5,6 GB em 13 s) e reinício saudável; restauração exercitada depois, na paridade do cancelamento ([ADR-0011](../adr/0011-paridade-golden-master.md) P4) | volume `nfse-oracle-baseline` |
| **K07: guia não desconta a nota cancelada** | 0003 P2, 0005 P1 | **confirmada em execução** | prestador 25, competência 2026-10: após cancelar as notas 1 e 2, a guia ficou em **1557,01**; o valor correto é **1267,86**. Ela descontou só a nota 1 (51,57), atrasada em um cancelamento |
| **K08: cancelamentos concorrentes gravam duas vezes** | 0003 P4 | **confirmada em execução** | nota (27,3): a sessão B esperou o commit da A, recebeu "sucesso" e **sobrescreveu** o motivo ("motivo B") |
| Wrapper com `FOR UPDATE` elimina a escrita dupla | 0003 | **confirmada** | nota (27,4) pelo wrapper: B esperou, viu `C` e saiu pelo caminho idempotente; motivo de A preservado |
| Wrapper devolve os mesmos códigos que `cancelar_nfse` | 0003 | **confirmada** para `E050`, `E051`, `E052`, `E053` e já cancelada, com snapshot e `SYSDATE` lidos sob lock | chamadas pelo `NFSE_MODERNO_APP` |
| `SELECT ... FOR UPDATE` em tabela de outro schema com `SELECT` direto ao dono | 0003 P9 | **confirmada** | o wrapper compila e trava a linha |
| Lock não obtido em 5 s vira erro técnico | 0003 | **confirmada, com outro código**: no 23ai sai `ORA-00054` + `ORA-40097`; a documentação do 19c indica `ORA-30006` para `WAIT n`. O serviço novo deve tratar os dois como o mesmo erro técnico | segunda sessão esperando 5 s |
| `READ WITH GRANT OPTION` permite repassar leitura das views | 0002 P2 | **confirmada** | `03_views.sql` aplicado sem `ORA-01720` |
| Usuário de runtime não acessa tabelas do legado | 0002 P4 | **confirmada**: `ORA-00942` em `UPDATE`/`SELECT` em `NFSE_OWNER.NFSE`; `PLS-00201` em `PKG_NFSE.gerar_nfse` | `NFSE_MODERNO_APP` |
| `READ` não permite `FOR UPDATE` nem DML na view | 0002 P1 | **confirmada, com código do 23ai**: `ORA-41900` (missing SELECT/UPDATE privilege). No 19c o esperado é `ORA-01031` | `NFSE_MODERNO_APP` |
| Sombra (usuário RO) não executa o wrapper | 0002, 0009 | **confirmada**: `PLS-00201` | `NFSE_MODERNO_RO` |

## O que muda nas decisões

- **K07 e K08 deixam de ser inferência.** O ADR-0005 (correção do trigger) passa a ter evidência executada, e a escolha do lock no wrapper (ADR-0003) está validada.
- **Códigos de erro dependem da versão.** O ambiente local é 23ai e produção é 19c: testes de integração verificam a **classe** do erro (sem privilégio, lock indisponível), aceitando os códigos das duas versões, e não um código fixo.
- **Golden master capturado** com `FIXED_DATE` ([tests/paridade](../../tests/paridade/README.md)). Comportamentos do catálogo confirmados em execução: C01, C02, C03, C04, C05, C06, C07, C09, C10, C12, C18, C23, C24, C25, C26, C27, C28, K01, K02, K05, K06 e K11.

## Pendências

- ~~Restaurar o volume `nfse-oracle-baseline`~~: feito na paridade do cancelamento (0011 P4 confirmada).
- 0003 P5 (rollback do chamador com guia criada pelo `COMMIT` autônomo) e P6 (idempotência do `recalcular_guia`): não exercitadas.
- 0002 P6 (carga das consultas × lote): depende do teste de carga.
- Premissas de produção (fuso, competência nula, grants aprovados pelo DBA): só com o DBA do 19c.
