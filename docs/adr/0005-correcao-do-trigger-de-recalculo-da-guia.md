# ADR-0005 — Correção do trigger de recálculo da guia

- **Status:** **Proposto** (aplicação depende de aprovação da área de negócio/Fazenda; não faz parte do caminho crítico da migração)
- **Data:** 2026-10-16
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

## Decisão proposta

Substituir o trigger por um **compound trigger** (disponível desde o 11g, compatível com 19c) que coleta as competências afetadas por linha e recalcula **uma vez por competência, ao fim do comando, na mesma transação**, sem `PRAGMA` e sem `COMMIT`:

```sql
CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
FOR UPDATE OF SITUACAO ON NFSE
COMPOUND TRIGGER
    TYPE t_chave IS RECORD (prestador_id NFSE.PRESTADOR_ID%TYPE, competencia NFSE.COMPETENCIA%TYPE);
    TYPE t_chaves IS TABLE OF t_chave INDEX BY VARCHAR2(40);   -- chave = prestador|competência: deduplica
    g_chaves t_chaves;
    v_k      VARCHAR2(40);

    BEFORE STATEMENT IS
    BEGIN
        g_chaves.DELETE;                                       -- estado limpo mesmo após erro em comando anterior
    END BEFORE STATEMENT;

    AFTER EACH ROW IS
    BEGIN
        v_k := TO_CHAR(:NEW.PRESTADOR_ID) || '|' || TO_CHAR(:NEW.COMPETENCIA, 'YYYYMMDDHH24MISS');
        g_chaves(v_k).prestador_id := :NEW.PRESTADOR_ID;
        g_chaves(v_k).competencia  := :NEW.COMPETENCIA;
    END AFTER EACH ROW;

    AFTER STATEMENT IS
    BEGIN
        v_k := g_chaves.FIRST;
        WHILE v_k IS NOT NULL LOOP                              -- um recálculo por (prestador, competência)
            PKG_GUIA.recalcular_guia(g_chaves(v_k).prestador_id, g_chaves(v_k).competencia);
            v_k := g_chaves.NEXT(v_k);
        END LOOP;
        g_chaves.DELETE;
    END AFTER STATEMENT;
END TRG_NFSE_SITUACAO_GUIA;
```

A deduplicação é **obrigatória**: sem ela, um cancelamento em massa de N notas da mesma competência faria N `MERGE`, cada um agregando a competência inteira, justamente o cenário que motiva a correção.

### Pacote de mudança

1. `db/legado-mudancas/0005_trg_nfse_situacao_guia.sql` — novo trigger.
2. `db/legado-mudancas/0005_rollback.sql` — DDL original, versionado byte a byte.
3. Relatório de reconciliação (somente leitura): guias `'A'` cujo `VALOR_ISS` difere do recálculo atual. **Guias `'P'` nunca são alteradas**; o tratamento de guias pagas a maior é decisão da Fazenda.
4. Teste que prova o defeito (vermelho após a correção) e teste que prova a correção.

### Implantação

Independente da migração: pode ser aplicado antes, durante ou depois, sem relação com as flags da fachada. Recomendação: aplicar **antes** de `CancelarNfse` entrar em `novo`, para que a sombra e o canário rodem já sobre o comportamento definitivo, ou **depois** de `novo` 100%, nunca no meio de um canário.

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

Executar `0005_rollback.sql` (recria o trigger original). As guias recalculadas durante a vigência permanecem corretas; nenhum dado precisa ser revertido.

## Como verificar

- Integração: cancelar uma nota e, **na mesma sessão antes do commit**, ler a guia: o valor já desconta a nota. Após `ROLLBACK`, guia volta ao valor anterior.
- Integração: `UPDATE NFSE SET SITUACAO = 'C' WHERE PRESTADOR_ID = :p AND COMPETENCIA = :c` (massa, 400 notas do seed de volume) deixa a guia com o valor das notas restantes, executa **um** recálculo por competência (`DT_ATUALIZACAO` único) e tem tempo medido contra o trigger original.
- Teste de carga: lote e cancelamentos concorrentes na mesma competência não geram deadlock; p99 do processamento de lote dentro do baseline.
