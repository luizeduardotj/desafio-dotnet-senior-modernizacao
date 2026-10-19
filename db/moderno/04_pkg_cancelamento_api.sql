-- Executar como NFSE_MODERNO. Wrapper do cancelamento (ADR-0003): a regra continua em NFSE_OWNER.PKG_NFSE.cancelar_nfse;
-- o wrapper só serializa cancelamentos da mesma nota (FOR UPDATE) e devolve o snapshot lido sob lock e o
-- DT_CANCELAMENTO na mesma transação. O COMMIT é do chamador, uma única vez.
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

CREATE OR REPLACE PACKAGE PKG_CANCELAMENTO_API AUTHID DEFINER AS
    PROCEDURE cancelar(p_prestador_id     IN  NUMBER,
                       p_numero           IN  NUMBER,
                       p_motivo           IN  VARCHAR2,
                       o_codigo_erro      OUT VARCHAR2,
                       o_situacao_antes   OUT VARCHAR2,
                       o_competencia      OUT DATE,
                       o_guia_paga_antes  OUT NUMBER,
                       o_agora_banco      OUT DATE,
                       o_dt_cancelamento  OUT DATE);
END PKG_CANCELAMENTO_API;
/

CREATE OR REPLACE PACKAGE BODY PKG_CANCELAMENTO_API AS

    PROCEDURE cancelar(p_prestador_id     IN  NUMBER,
                       p_numero           IN  NUMBER,
                       p_motivo           IN  VARCHAR2,
                       o_codigo_erro      OUT VARCHAR2,
                       o_situacao_antes   OUT VARCHAR2,
                       o_competencia      OUT DATE,
                       o_guia_paga_antes  OUT NUMBER,
                       o_agora_banco      OUT DATE,
                       o_dt_cancelamento  OUT DATE) IS
    BEGIN
        BEGIN
            SELECT SITUACAO, COMPETENCIA
              INTO o_situacao_antes, o_competencia
              FROM NFSE_OWNER.NFSE
             WHERE PRESTADOR_ID = p_prestador_id
               AND NUMERO = p_numero
               FOR UPDATE WAIT 5;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                o_codigo_erro := 'E050';   -- mesmo resultado que cancelar_nfse daria
                RETURN;
        END;                               -- ORA-30006 (lock não obtido em 5 s) sobe como erro técnico

        SELECT SYSDATE INTO o_agora_banco FROM DUAL;

        SELECT COUNT(*)
          INTO o_guia_paga_antes
          FROM NFSE_OWNER.GUIAS
         WHERE PRESTADOR_ID = p_prestador_id
           AND COMPETENCIA = o_competencia
           AND SITUACAO = 'P';

        NFSE_OWNER.PKG_NFSE.cancelar_nfse(p_prestador_id, p_numero, p_motivo, o_codigo_erro);

        SELECT DT_CANCELAMENTO
          INTO o_dt_cancelamento
          FROM NFSE_OWNER.NFSE
         WHERE PRESTADOR_ID = p_prestador_id
           AND NUMERO = p_numero;
    END cancelar;

END PKG_CANCELAMENTO_API;
/

SHOW ERRORS

-- Falha explícita se o corpo não compilou (SHOW ERRORS sozinho não interrompe o script).
WHENEVER SQLERROR EXIT FAILURE
DECLARE
    v_status VARCHAR2(10);
BEGIN
    SELECT STATUS INTO v_status FROM USER_OBJECTS
     WHERE OBJECT_NAME = 'PKG_CANCELAMENTO_API' AND OBJECT_TYPE = 'PACKAGE BODY';
    IF v_status <> 'VALID' THEN
        RAISE_APPLICATION_ERROR(-20001, 'PKG_CANCELAMENTO_API inválido');
    END IF;
END;
/

GRANT EXECUTE ON PKG_CANCELAMENTO_API TO NFSE_MODERNO_APP;

EXIT
