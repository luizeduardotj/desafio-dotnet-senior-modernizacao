SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

CREATE OR REPLACE PACKAGE PKG_GUIA AS
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE);
END PKG_GUIA;
/

CREATE OR REPLACE PACKAGE BODY PKG_GUIA AS

    -- guia (DAM) = soma do ISS das notas normais, nao retidas, da competencia
    -- vencimento: dia 10 do mes seguinte
    PROCEDURE recalcular_guia(p_prestador_id NUMBER, p_competencia DATE) IS
        v_venc DATE := ADD_MONTHS(TRUNC(p_competencia, 'MM'), 1) + 9;
    BEGIN
        MERGE INTO GUIAS g
        USING (SELECT p_prestador_id AS PRESTADOR_ID,
                      p_competencia  AS COMPETENCIA,
                      NVL(SUM(ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2)), 0) AS VALOR
                 FROM NFSE n
                WHERE n.PRESTADOR_ID = p_prestador_id
                  AND n.COMPETENCIA  = p_competencia
                  AND n.SITUACAO     = 'N'
                  AND n.ISS_RETIDO   = 'N') s
           ON (g.PRESTADOR_ID = s.PRESTADOR_ID AND g.COMPETENCIA = s.COMPETENCIA)
         WHEN MATCHED THEN
              UPDATE SET g.VALOR_ISS = s.VALOR, g.DT_ATUALIZACAO = SYSDATE
               WHERE g.SITUACAO = 'A'
         WHEN NOT MATCHED THEN
              INSERT (ID, PRESTADOR_ID, COMPETENCIA, VALOR_ISS, SITUACAO, DT_VENCIMENTO, DT_ATUALIZACAO)
              VALUES (SEQ_GUIAS.NEXTVAL, s.PRESTADOR_ID, s.COMPETENCIA, s.VALOR, 'A', v_venc, SYSDATE);
    END recalcular_guia;

END PKG_GUIA;
/

SHOW ERRORS
EXIT
