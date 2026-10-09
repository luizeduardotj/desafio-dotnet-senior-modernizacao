SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

CREATE OR REPLACE PACKAGE PKG_NFSE AS
    PROCEDURE gerar_nfse(p_rps_id             NUMBER,
                         p_prestador_id       NUMBER,
                         p_competencia        DATE,
                         p_item               VARCHAR2,
                         p_discriminacao      VARCHAR2,
                         p_valor_servicos     NUMBER,
                         p_valor_deducoes     NUMBER,
                         p_aliquota           NUMBER,
                         p_valor_iss          NUMBER,
                         p_iss_retido         VARCHAR2,
                         p_tomador_doc        VARCHAR2,
                         p_tomador_razao      VARCHAR2,
                         o_nfse_id        OUT NUMBER,
                         o_numero         OUT NUMBER,
                         o_codigo_verificacao OUT VARCHAR2);

    PROCEDURE cancelar_nfse(p_prestador_id NUMBER,
                            p_numero       NUMBER,
                            p_motivo       VARCHAR2,
                            o_codigo_erro  OUT VARCHAR2);
END PKG_NFSE;
/

CREATE OR REPLACE PACKAGE BODY PKG_NFSE AS

    PROCEDURE gerar_nfse(p_rps_id             NUMBER,
                         p_prestador_id       NUMBER,
                         p_competencia        DATE,
                         p_item               VARCHAR2,
                         p_discriminacao      VARCHAR2,
                         p_valor_servicos     NUMBER,
                         p_valor_deducoes     NUMBER,
                         p_aliquota           NUMBER,
                         p_valor_iss          NUMBER,
                         p_iss_retido         VARCHAR2,
                         p_tomador_doc        VARCHAR2,
                         p_tomador_razao      VARCHAR2,
                         o_nfse_id        OUT NUMBER,
                         o_numero         OUT NUMBER,
                         o_codigo_verificacao OUT VARCHAR2) IS
        v_numero NUMBER;
    BEGIN
        -- numeracao sequencial por prestador, sem buracos (exigencia fiscal)
        SELECT NVL(MAX(NUMERO), 0) + 1
          INTO v_numero
          FROM NFSE
         WHERE PRESTADOR_ID = p_prestador_id;

        o_nfse_id := SEQ_NFSE.NEXTVAL;
        o_numero := v_numero;
        o_codigo_verificacao := DBMS_RANDOM.STRING('X', 9);

        INSERT INTO NFSE (ID, PRESTADOR_ID, NUMERO, CODIGO_VERIFICACAO, DT_EMISSAO, COMPETENCIA, ITEM_SERVICO,
                          DISCRIMINACAO, VALOR_SERVICOS, VALOR_DEDUCOES, BASE_CALCULO, ALIQUOTA, VALOR_ISS,
                          ISS_RETIDO, TOMADOR_DOCUMENTO, TOMADOR_RAZAO, SITUACAO, RPS_ID)
        VALUES (o_nfse_id, p_prestador_id, o_numero, o_codigo_verificacao, SYSDATE, p_competencia, p_item,
                p_discriminacao, p_valor_servicos, p_valor_deducoes, p_valor_servicos - p_valor_deducoes,
                p_aliquota, p_valor_iss, p_iss_retido, p_tomador_doc, p_tomador_razao, 'N', p_rps_id);

        UPDATE RPS SET NFSE_ID = o_nfse_id WHERE ID = p_rps_id;
    END gerar_nfse;

    PROCEDURE cancelar_nfse(p_prestador_id NUMBER,
                            p_numero       NUMBER,
                            p_motivo       VARCHAR2,
                            o_codigo_erro  OUT VARCHAR2) IS
        v_situacao    NFSE.SITUACAO%TYPE;
        v_competencia NFSE.COMPETENCIA%TYPE;
        v_paga        NUMBER;
    BEGIN
        o_codigo_erro := NULL;

        BEGIN
            SELECT SITUACAO, COMPETENCIA
              INTO v_situacao, v_competencia
              FROM NFSE
             WHERE PRESTADOR_ID = p_prestador_id
               AND NUMERO = p_numero;
        EXCEPTION
            WHEN NO_DATA_FOUND THEN
                o_codigo_erro := 'E050';
                RETURN;
        END;

        IF v_situacao = 'C' THEN
            RETURN;
        END IF;

        IF v_situacao = 'S' THEN
            o_codigo_erro := 'E051';
            RETURN;
        END IF;

        IF NOT (SYSDATE < ADD_MONTHS(v_competencia, 1) + 10) THEN
            o_codigo_erro := 'E052';
            RETURN;
        END IF;

        SELECT COUNT(*)
          INTO v_paga
          FROM GUIAS
         WHERE PRESTADOR_ID = p_prestador_id
           AND COMPETENCIA = v_competencia
           AND SITUACAO = 'P';
        IF v_paga > 0 THEN
            o_codigo_erro := 'E053';
            RETURN;
        END IF;

        UPDATE NFSE
           SET SITUACAO = 'C',
               DT_CANCELAMENTO = SYSDATE,
               MOTIVO_CANCELAMENTO = SUBSTR(p_motivo, 1, 255)
         WHERE PRESTADOR_ID = p_prestador_id
           AND NUMERO = p_numero;
    END cancelar_nfse;

END PKG_NFSE;
/

SHOW ERRORS
EXIT
