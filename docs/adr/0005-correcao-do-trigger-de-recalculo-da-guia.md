# ADR-0005 — Correção do trigger de recálculo da guia

- **Status:** **Proposto** (aplicação depende de aprovação da área de negócio/Fazenda; não faz parte do caminho crítico da migração)
- **Data:** 2026-10-16 (scripts e teste da proposta em 2026-10-19)
- **Relacionados:** [0003](0003-regras-de-cancelamento-em-plsql.md), [0004](0004-comportamentos-legados-nao-documentados.md) (K07)

## Contexto

O manual diz: "ao cancelar uma nota, a guia da competência é recalculada". Em produção o recálculo acontece, mas com a foto errada:

```sql
-- db/04_triggers.sql
CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
AFTER UPDATE OF SITUACAO ON NFSE
FOR EACH ROW
DECLARE
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    PKG_GUIA.recalcular_guia(:NEW.PRESTADOR_ID, :NEW.COMPETENCIA);
    COMMIT;
END;
```

- `recalcular_guia` soma o ISS das notas `SITUACAO = 'N'` da competência (`db/02_pkg_guia.sql:16-31`).
- A transação autônoma só enxerga o estado commitado, no qual a nota que está sendo cancelada **ainda é `'N'`**. O valor da guia continua incluindo o ISS dela.
- Consequências:
  - a guia fica sempre **um cancelamento atrasada**: o próximo cancelamento da competência desconta o anterior; o último nunca é descontado (até o próximo recálculo por lote);
  - no "cancelamento administrativo em massa" citado no comentário do trigger, **nenhuma** nota do comando é descontada;
  - o contribuinte paga ISS de nota cancelada.
- Causa provável: um trigger `FOR EACH ROW` que consulta a própria `NFSE` dispara `ORA-04091` (tabela mutante). O `PRAGMA AUTONOMOUS_TRANSACTION` é o contorno clássico, e introduziu o defeito.

Restrições: mudanças no legado devem ser mínimas, justificadas em ADR e compatíveis com Oracle 19c; não alterar tabelas do legado.

### Premissas e nível de verificação

| # | Premissa | Fonte | Nível | Como confirmar | Se for falsa |
| --- | --- | --- | --- | --- | --- |
| P1 | O defeito existe (premissa P2 da ADR-0003) | inferência | **confirmada em execução** ([spike](../spikes/2026-10-19-premissas-oracle.md)) | teste K07 | esta ADR é arquivada |
| P2 | Consultar `NFSE` no `AFTER STATEMENT` de um compound trigger não dispara `ORA-04091` | documentação Oracle 11g+ | **confirmada em execução** no 23ai/26ai ([teste da proposta](#resultado-medido)); 19c segue a confirmar em homologação | [tests/propostas/0005-trigger-guia.sh](../../tests/propostas/0005-trigger-guia.sh) | usar tabela temporária global como alternativa |
| P3 | Segurar o lock de `GUIAS` até o commit não causa deadlock com o lote | — | hipótese; entre dois cancelamentos pelo wrapper, **confirmada**: a segunda sessão espera ~2 s e conclui | teste de carga lote + cancelamento na mesma competência | ordenar as operações ou manter o recálculo do lote fora da transação |
| P4 | Não há notas com `COMPETENCIA` nula em produção | coluna nullable em `db/01_schema.sql:71` | **0 no seed**; produção desconhecida | consulta no 19c antes da aplicação; o `aplicar.sql` também confere e aborta sem alterar nada | o novo trigger falha como o antigo (`ORA-01400`); tratar antes |
| P5 | Duas sessões cancelando notas diferentes da mesma competência chegam ao valor certo | — | **confirmada em execução**, com um ajuste no SQL original (ver [Decisão proposta](#decisão-proposta)) | teste da proposta, passo 4 | — |

## Decisão proposta

Substituir o trigger por um **compound trigger** (disponível desde o 11g, compatível com 19c) que coleta as competências afetadas por linha e recalcula **uma vez por competência, ao fim do comando, na mesma transação**, sem `PRAGMA` e sem `COMMIT`:

O SQL versionado está em [db/propostas/0005-correcao-trigger-guia/aplicar.sql](../../db/propostas/0005-correcao-trigger-guia/aplicar.sql). Ele não roda em nenhum init, compose ou CI de paridade ([db/propostas](../../db/propostas/README.md)). A estrutura é a que esta ADR propôs em 2026-10-16:

- `BEFORE STATEMENT` limpa a coleção;
- `AFTER EACH ROW` guarda `(prestador, competência)` numa coleção indexada pela chave, o que deduplica;
- `AFTER STATEMENT` chama `PKG_GUIA.recalcular_guia` uma vez por chave.

O teste trouxe três ajustes:

- **Guia ainda inexistente e cancelamentos concorrentes.** Com o SQL original desta ADR, duas sessões que cancelam notas da mesma competência sem guia caem as duas no `INSERT` do `MERGE`. A segunda espera a primeira e recebe `ORA-00001`, e o cancelamento dela falha **[M]**. O trigger antigo não tem esse erro, porque a transação autônoma confirma na hora. O `aplicar.sql` captura `DUP_VAL_ON_INDEX` e refaz o recálculo, que então já enxerga a guia criada pela outra sessão.
- **Trava explícita da guia antes do `MERGE`.** Com a guia existente, o SQL original chegou ao valor certo no 23ai/26ai: a segunda sessão esperou o lock, e o Oracle reiniciou o `MERGE` dela, que somou de novo já com a nota da primeira **[M]**. A documentação não garante esse reinício para o 19c. Por isso o trigger faz `SELECT ... FOR UPDATE` na guia e só então chama o `MERGE`, que é outro comando, com outra leitura consistente.
- **Chave com o prestador preenchido com zeros e pré-condição P4.** A coleção é percorrida em ordem de chave, e com os zeros essa ordem é numérica e igual em todas as sessões, então duas sessões travam as guias na mesma sequência. Antes de criar o trigger, o `aplicar.sql` aborta se houver nota com `COMPETENCIA` nula.

A deduplicação é **obrigatória**: sem ela, um cancelamento em massa de N notas da mesma competência faria N `MERGE`, cada um agregando a competência inteira, justamente o cenário que motiva a correção.

### Pacote de mudança

Em [db/propostas/0005-correcao-trigger-guia](../../db/propostas/0005-correcao-trigger-guia/), com o rito de aprovação no [README da pasta](../../db/propostas/README.md):

1. `aplicar.sql`: novo trigger, com a pré-condição P4 conferida antes e a validade do objeto depois.
2. `reverter.sql`: DDL original, cópia byte a byte de `db/04_triggers.sql`. O teste confere que o DDL depois da reversão é idêntico ao da linha de base.
3. `relatorio-reconciliacao.sql`: só leitura (`SET TRANSACTION READ ONLY`). Lista em CSV as guias `'A'` cujo `VALOR_ISS` difere do recálculo atual, com o total. **Guias `'P'` nunca são alteradas**; o tratamento de guias pagas a maior é decisão da Fazenda. Pela [ADR-0012](0012-observabilidade-seguranca-e-lgpd.md), o relatório leva só o ID da guia e o ID interno do prestador. CNPJ/CPF, inscrição municipal e razão social ficam fora: a Fazenda localiza o contribuinte pelo ID no próprio sistema. Mesmo assim o arquivo é dado pessoal pseudonimizado, e circula só entre DBA e Fazenda.
4. Teste em Oracle descartável: [tests/propostas/0005-trigger-guia.sh](../../tests/propostas/0005-trigger-guia.sh), sobre um banco recém-carregado, com projeto compose próprio e sem porta publicada. No CI, só por `workflow_dispatch` ([propostas.yml](../../.github/workflows/propostas.yml)). É um script, e não um teste .NET, porque o que se testa é DDL e PL/SQL no schema do legado, com três usuários (dono, runtime do wrapper e SYS para contar `MERGE` no `V$SQL`), duas sessões concorrentes e um banco que sobe e cai com o teste. Isso é `sqlplus` dentro do container; um projeto .NET só acrescentaria uma camada de driver. Fica fora da solução e da paridade, que testam o legado como ele está.

### Implantação

Independente da migração: pode ser aplicado antes, durante ou depois, sem relação com as flags da fachada. Recomendação: aplicar **antes** de `CancelarNfse` entrar em `novo`, para que a sombra e o canário rodem já sobre o comportamento definitivo, ou **depois** de `novo` 100%, nunca no meio de um canário.

## Fora deste ADR

| Pendência | Dono | Trava |
| --- | --- | --- |
| Aprovação de negócio para mudar o valor das guias | Fazenda / área de arrecadação | a aplicação desta ADR |
| Verificação de notas com `COMPETENCIA` nula em produção (premissa P4) | DBA | a aplicação desta ADR |
| Tratamento de guias já pagas a maior | Fazenda | nada técnico; fora do escopo da migração |

## Alternativas consideradas

- **Não corrigir (só documentar).** Válida e é o estado padrão enquanto este ADR não for aprovado: o comportamento é preservado e coberto por teste (K07).
- **Recalcular a guia no serviço novo após o commit** (mitigação só no caminho novo). Rejeitada: guias passariam a ter valores diferentes conforme quem cancelou; cancelamentos em massa e pelo legado continuariam errados.
- **Trigger `AFTER STATEMENT` simples.** Rejeitada: não sabe quais competências foram afetadas sem varrer a tabela.
- **Mover o recálculo para dentro de `cancelar_nfse`.** Rejeitada: não cobre `UPDATE` em massa feito fora da procedure, que é justamente o caso citado no comentário de 2019.

## Consequências

- **Positivas:** a guia passa a refletir o cancelamento na mesma transação; o rollback do chamador desfaz também o recálculo; cancelamento em massa fica correto e atômico.
- **Negativas e riscos:**
  - **mudança de comportamento visível**: valores de guia mudam a partir da aplicação; contribuintes com guia `'A'` defasada verão o valor cair;
  - **lock**: o `MERGE` em `GUIAS` passa a acontecer dentro da transação principal, segurando o lock da linha da guia até o commit. Pode haver espera com o recálculo do processamento de lote da mesma competência (serialização curta); medir no teste de carga;
  - **erro no recálculo agora aborta o cancelamento** (antes, o erro da transação autônoma também propagava, então o efeito prático é o mesmo, mas deve ser testado);
  - toca um objeto do schema do legado (nenhuma tabela é alterada).

## Reversão

Executar `reverter.sql` (recria o trigger original; DDL conferido no teste). As guias recalculadas durante a vigência permanecem corretas; nenhum dado precisa ser revertido.

## Resultado medido

**[M]** 2026-10-19, `tests/propostas/0005-trigger-guia.sh`, Oracle descartável do compose de `tests/propostas`. A imagem `gvenzl/oracle-free:23-slim` hoje entrega o *26ai Free Release 23.26.3*. O seed `01`–`06` foi recém-carregado (208 s até ficar saudável, arm64, máquina compartilhada).

| Passo | Resultado |
| --- | --- |
| Linha de base, guia do prestador 25 (competência 2026-10) | 1608,58 |
| Relatório de reconciliação na linha de base | 10.097 guias em aberto, **0 divergentes** |
| Correção aplicada; 25/1 cancelada, lida **antes do commit** | 1557,01 |
| 25/1 e 25/2 canceladas, lida antes do commit | **1267,86** |
| `ROLLBACK` | 1608,58 (o recálculo é desfeito com o cancelamento) |
| Três comandos na mesma transação (prestador 27) | 853,55 → 624,40 → 427,68, igual à soma das notas restantes a cada passo; `ROLLBACK` devolve 1115,12 |
| `UPDATE` em massa: 390 notas de um prestador do seed de volume, 24 competências | **24** `MERGE` (um por competência), 10 ms; guias abertas zeradas antes do commit, pagas inalteradas |
| Mesmo `UPDATE` com o trigger original | **390** `MERGE` (um por nota), 60 ms; nenhuma guia descontada |
| Concorrência pelo wrapper, guia existente: A segura a transação 3 s, B cancela outra nota | B espera 1,95 s; guia final igual à soma das notas restantes |
| Concorrência pelo wrapper, guia ainda inexistente | B espera 1,98 s e conclui (com o SQL original desta ADR: `ORA-00001`); uma guia, com o valor certo |
| Relatório com a correção | 0 divergentes |
| `reverter.sql` | DDL do trigger idêntico ao da linha de base |
| Trigger original de volta: 25/1 e 25/2 em commits separados | **1557,01** (K07 de volta; o correto seria 1267,86) |
| Relatório depois da reversão | **1 divergente**: guia do prestador 25, 2026-10, 289,15 cobrados a mais, 2 notas canceladas |

Fica para homologação 19c (README de `db/propostas`, passo 3): repetir o teste nessa versão, onde os códigos de erro e o reinício do `MERGE` podem diferir. Fica também o teste de carga de lote e cancelamento na mesma competência (P3).

## Como verificar

- Integração, **feita** ([Resultado medido](#resultado-medido)): cancelar e ler a guia na mesma transação antes do commit, com o `ROLLBACK` devolvendo o valor anterior; vários cancelamentos na mesma competência; `UPDATE` em massa com um `MERGE` por competência, contado no `V$SQL`; concorrência pelo wrapper com e sem guia; reversão. Para repetir: `tests/propostas/0005-trigger-guia.sh`.
- Teste de carga: lote e cancelamentos concorrentes na mesma competência não geram deadlock; p99 do processamento de lote dentro do baseline.
