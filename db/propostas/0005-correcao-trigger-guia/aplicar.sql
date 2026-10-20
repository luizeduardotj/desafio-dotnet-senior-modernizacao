-- ADR-0005 (Proposto), correção do K07: o recálculo da guia passa a acontecer na transação do cancelamento.
-- NÃO é aplicado por nenhum init, compose ou CI de paridade. Só com a aprovação descrita em db/propostas/README.md.
-- Executar como NFSE_OWNER. Desfaz com reverter.sql (trigger original byte a byte).
SET DEFINE OFF
SET SERVEROUTPUT ON
WHENEVER SQLERROR EXIT FAILURE

-- Premissa P4 da ADR-0005: nota sem competência faria o recálculo falhar (ORA-01400 no INSERT da guia), como já
-- falha hoje. A correção não é aplicada sobre um dado que ninguém examinou: o DBA trata essas notas antes.
DECLARE
    v_sem_competencia NUMBER;
BEGIN
    SELECT COUNT(*) INTO v_sem_competencia FROM NFSE WHERE COMPETENCIA IS NULL;
    IF v_sem_competencia > 0 THEN
        RAISE_APPLICATION_ERROR(-20010, v_sem_competencia || ' nota(s) com COMPETENCIA nula: premissa P4 da ADR-0005; nada foi alterado');
    END IF;
END;
/

CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
FOR UPDATE OF SITUACAO ON NFSE
COMPOUND TRIGGER
    -- ADR-0005: substitui o trigger autônomo de 2019 (K07). Coleta as competências afetadas por linha e recalcula uma
    -- vez por (prestador, competência) ao fim do comando, na transação do chamador: sem PRAGMA e sem COMMIT.
    TYPE t_chave IS RECORD (prestador_id NFSE.PRESTADOR_ID%TYPE, competencia NFSE.COMPETENCIA%TYPE);
    -- chave = prestador com zeros à esquerda | competência: deduplica e percorre sempre na mesma ordem, então duas
    -- sessões travam as guias na mesma sequência
    TYPE t_chaves IS TABLE OF t_chave INDEX BY VARCHAR2(40);
    g_chaves t_chaves;
    v_k      VARCHAR2(40);

    -- Trava a guia antes de somar as notas. Em READ COMMITTED o MERGE seguinte é outro comando, com outra leitura
    -- consistente, e enxerga o cancelamento que outra sessão acabou de confirmar na mesma competência. No 23ai/26ai o
    -- MERGE sem a trava também chega ao valor certo, porque o Oracle reinicia o comando que esperou pela linha
    -- (medido no teste da proposta); a trava tira a correção da dependência desse reinício, que a documentação não
    -- garante para o 19c.
    PROCEDURE travar_guia(p_prestador_id NUMBER, p_competencia DATE) IS
        v_id GUIAS.ID%TYPE;
    BEGIN
        SELECT ID INTO v_id
          FROM GUIAS
         WHERE PRESTADOR_ID = p_prestador_id
           AND COMPETENCIA = p_competencia
           FOR UPDATE;
    EXCEPTION
        WHEN NO_DATA_FOUND THEN
            NULL;                                              -- guia ainda não existe: o MERGE a cria
    END travar_guia;

    PROCEDURE recalcular(p_prestador_id NUMBER, p_competencia DATE) IS
    BEGIN
        travar_guia(p_prestador_id, p_competencia);
        PKG_GUIA.recalcular_guia(p_prestador_id, p_competencia);
    EXCEPTION
        -- Guia ainda inexistente e duas sessões na mesma competência: as duas caem no INSERT do MERGE e a segunda,
        -- depois de esperar a primeira, recebe ORA-00001. Sem este tratamento o cancelamento dela falharia (medido no
        -- teste com o SQL original da ADR); com ele, refaz o recálculo já vendo a guia criada pela outra sessão.
        WHEN DUP_VAL_ON_INDEX THEN
            travar_guia(p_prestador_id, p_competencia);
            PKG_GUIA.recalcular_guia(p_prestador_id, p_competencia);
    END recalcular;

    BEFORE STATEMENT IS
    BEGIN
        g_chaves.DELETE;                                       -- estado limpo mesmo após erro em comando anterior
    END BEFORE STATEMENT;

    AFTER EACH ROW IS
    BEGIN
        v_k := LPAD(TO_CHAR(:NEW.PRESTADOR_ID), 15, '0') || '|' || TO_CHAR(:NEW.COMPETENCIA, 'YYYYMMDDHH24MISS');
        g_chaves(v_k).prestador_id := :NEW.PRESTADOR_ID;
        g_chaves(v_k).competencia  := :NEW.COMPETENCIA;
    END AFTER EACH ROW;

    AFTER STATEMENT IS
    BEGIN
        v_k := g_chaves.FIRST;
        WHILE v_k IS NOT NULL LOOP                              -- um recálculo por (prestador, competência)
            recalcular(g_chaves(v_k).prestador_id, g_chaves(v_k).competencia);
            v_k := g_chaves.NEXT(v_k);
        END LOOP;
        g_chaves.DELETE;
    END AFTER STATEMENT;
END TRG_NFSE_SITUACAO_GUIA;
/

SHOW ERRORS

-- Um trigger inválido faria todo UPDATE de SITUACAO falhar. CREATE com erro de compilação é só aviso no SQL*Plus,
-- por isso a conferência explícita.
DECLARE
    v_status VARCHAR2(10);
BEGIN
    SELECT STATUS INTO v_status FROM USER_OBJECTS WHERE OBJECT_NAME = 'TRG_NFSE_SITUACAO_GUIA' AND OBJECT_TYPE = 'TRIGGER';
    IF v_status <> 'VALID' THEN
        RAISE_APPLICATION_ERROR(-20011, 'TRG_NFSE_SITUACAO_GUIA inválido: execute reverter.sql agora');
    END IF;
    DBMS_OUTPUT.PUT_LINE('ADR-0005 aplicada: TRG_NFSE_SITUACAO_GUIA compound, VALID');
END;
/

EXIT
