-- Carga base (dados sinteticos)
SET DEFINE OFF
SET SERVEROUTPUT ON
WHENEVER SQLERROR EXIT FAILURE

INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('01.01', 'Análise e desenvolvimento de sistemas', 0.0200);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('01.02', 'Programação', 0.0200);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('01.03', 'Processamento, armazenamento ou hospedagem de dados', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('01.07', 'Suporte técnico em informática', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('02.01', 'Serviços de pesquisas e desenvolvimento de qualquer natureza', 0.0200);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('04.01', 'Medicina e biomedicina', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('04.03', 'Hospitais, clínicas, laboratórios e congêneres', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('05.01', 'Medicina veterinária e zootecnia', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('06.01', 'Barbearia, cabeleireiros, manicuros e congêneres', 0.0200);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('07.02', 'Execução de obras de construção civil', 0.0300);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('07.05', 'Reparação, conservação e reforma de edifícios', 0.0400);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('08.01', 'Ensino regular pré-escolar, fundamental, médio e superior', 0.0200);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('09.01', 'Hospedagem de qualquer natureza em hotéis e congêneres', 0.0500);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('10.02', 'Agenciamento de títulos e valores mobiliários', 0.0500);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('11.02', 'Vigilância, segurança ou monitoramento de bens e pessoas', 0.0400);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('13.02', 'Fonografia ou gravação de sons', 0.0350);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('14.01', 'Lubrificação, limpeza, lustração e conserto de máquinas', 0.0350);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('16.01', 'Serviços de transporte coletivo municipal', 0.0250);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('17.01', 'Assessoria ou consultoria de qualquer natureza', 0.0500);
INSERT INTO ITENS_SERVICO (CODIGO, DESCRICAO, ALIQUOTA) VALUES ('17.06', 'Propaganda e publicidade', 0.0500);
COMMIT;

DECLARE
    TYPE t_str IS TABLE OF VARCHAR2(150) INDEX BY PLS_INTEGER;
    TYPE t_num IS TABLE OF NUMBER INDEX BY PLS_INTEGER;
    tom_doc    t_str;
    tom_razao  t_str;
    it_cod     t_str;
    it_desc    t_str;
    it_aliq    t_num;
    n_itens    PLS_INTEGER := 0;

    v_regime   CONTRIBUINTES.REGIME%TYPE;
    v_numero   NUMBER;
    v_id       NUMBER;
    v_id2      NUMBER;
    v_inicio   DATE := DATE '2026-07-01';
    v_mes      DATE;
    v_comp_rel DATE := TRUNC(SYSDATE, 'MM');
    v_dt       DATE;
    v_seed     NUMBER;
    v_cnt      PLS_INTEGER;
    v_dia      PLS_INTEGER;
    v_ix       PLS_INTEGER;
    v_serv     NUMBER(15,2);
    v_ded      NUMBER(15,2);
    v_base     NUMBER(15,2);
    v_aliq     NUMBER(5,4);
    v_iss      NUMBER(15,2);
    v_ret      CHAR(1);
    v_sit      CHAR(1);
    v_tom      PLS_INTEGER;
    v_rps_id   NUMBER;
    v_cancel   DATE;
    v_motivo   VARCHAR2(255);
    v_doc      VARCHAR2(14);

    -- digitos verificadores
    FUNCTION dv_cnpj(p_base VARCHAR2) RETURN VARCHAR2 IS
        TYPE t_pesos IS VARRAY(13) OF PLS_INTEGER;
        p1 t_pesos := t_pesos(5,4,3,2,9,8,7,6,5,4,3,2);
        p2 t_pesos := t_pesos(6,5,4,3,2,9,8,7,6,5,4,3,2);
        v_soma PLS_INTEGER := 0;
        v_d1 PLS_INTEGER;
        v_d2 PLS_INTEGER;
    BEGIN
        FOR i IN 1..12 LOOP
            v_soma := v_soma + TO_NUMBER(SUBSTR(p_base, i, 1)) * p1(i);
        END LOOP;
        v_d1 := CASE WHEN MOD(v_soma, 11) < 2 THEN 0 ELSE 11 - MOD(v_soma, 11) END;
        v_soma := 0;
        FOR i IN 1..12 LOOP
            v_soma := v_soma + TO_NUMBER(SUBSTR(p_base, i, 1)) * p2(i);
        END LOOP;
        v_soma := v_soma + v_d1 * p2(13);
        v_d2 := CASE WHEN MOD(v_soma, 11) < 2 THEN 0 ELSE 11 - MOD(v_soma, 11) END;
        RETURN p_base || v_d1 || v_d2;
    END;

    FUNCTION dv_cpf(p_base VARCHAR2) RETURN VARCHAR2 IS
        v_soma PLS_INTEGER := 0;
        v_d1 PLS_INTEGER;
        v_d2 PLS_INTEGER;
    BEGIN
        FOR i IN 1..9 LOOP
            v_soma := v_soma + TO_NUMBER(SUBSTR(p_base, i, 1)) * (11 - i);
        END LOOP;
        v_d1 := MOD(v_soma * 10, 11);
        IF v_d1 = 10 THEN v_d1 := 0; END IF;
        v_soma := 0;
        FOR i IN 1..9 LOOP
            v_soma := v_soma + TO_NUMBER(SUBSTR(p_base, i, 1)) * (12 - i);
        END LOOP;
        v_soma := v_soma + v_d1 * 2;
        v_d2 := MOD(v_soma * 10, 11);
        IF v_d2 = 10 THEN v_d2 := 0; END IF;
        RETURN p_base || v_d1 || v_d2;
    END;

    FUNCTION item_idx(p_codigo VARCHAR2) RETURN PLS_INTEGER IS
    BEGIN
        FOR i IN 1..n_itens LOOP
            IF it_cod(i) = p_codigo THEN RETURN i; END IF;
        END LOOP;
        RETURN NULL;
    END;

    PROCEDURE inserir_nota(p_prest NUMBER, p_regime VARCHAR2, p_dt DATE, p_item VARCHAR2, p_serv NUMBER, p_ded NUMBER,
                           p_ret VARCHAR2, p_tom PLS_INTEGER, p_sit VARCHAR2, p_cancel DATE, p_iss_forcado NUMBER,
                           p_id NUMBER, p_subst_id NUMBER, p_rps_id NUMBER) IS
        v_ix2   PLS_INTEGER := item_idx(p_item);
        v_aliq2 NUMBER(5,4);
        v_base2 NUMBER(15,2) := p_serv - p_ded;
        v_iss2  NUMBER(15,2);
    BEGIN
        v_aliq2 := CASE WHEN p_regime = 'M' THEN 0 ELSE it_aliq(v_ix2) END;
        v_iss2 := NVL(p_iss_forcado, ROUND(v_base2 * v_aliq2, 2));
        v_numero := v_numero + 1;
        INSERT INTO NFSE (ID, PRESTADOR_ID, NUMERO, CODIGO_VERIFICACAO, DT_EMISSAO, COMPETENCIA, ITEM_SERVICO, DISCRIMINACAO,
                          VALOR_SERVICOS, VALOR_DEDUCOES, BASE_CALCULO, ALIQUOTA, VALOR_ISS, ISS_RETIDO, TOMADOR_DOCUMENTO,
                          TOMADOR_RAZAO, SITUACAO, DT_CANCELAMENTO, MOTIVO_CANCELAMENTO, NFSE_SUBSTITUIDORA_ID, RPS_ID)
        VALUES (p_id, p_prest, v_numero, 'V' || LPAD(TO_CHAR(MOD(p_id * 7919, 99999999)), 8, '0'), p_dt, TRUNC(p_dt, 'MM'), p_item,
                'Serviços prestados: ' || it_desc(v_ix2) || ' - ref. contrato ' || LPAD(TO_CHAR(MOD(p_id * 31, 9999)), 4, '0'),
                p_serv, p_ded, v_base2, v_aliq2, v_iss2, p_ret, tom_doc(p_tom), tom_razao(p_tom), p_sit, p_cancel,
                CASE WHEN p_sit = 'C' THEN 'Cancelamento a pedido do tomador' ELSE NULL END,
                p_subst_id, p_rps_id);
    END;

BEGIN
    -- itens em memoria
    FOR r IN (SELECT CODIGO, DESCRICAO, ALIQUOTA FROM ITENS_SERVICO ORDER BY CODIGO) LOOP
        n_itens := n_itens + 1;
        it_cod(n_itens) := r.CODIGO;
        it_desc(n_itens) := r.DESCRICAO;
        it_aliq(n_itens) := r.ALIQUOTA;
    END LOOP;

    -- tomadores sinteticos: 1..30 CNPJ (1..6 com zero a esquerda), 31..40 CPF (31..33 com zero a esquerda)
    FOR i IN 1..40 LOOP
        IF i <= 30 THEN
            IF i <= 6 THEN
                tom_doc(i) := dv_cnpj('0' || LPAD(TO_CHAR(MOD(i * 1234567, 9999999)), 7, '0') || '0001');
            ELSE
                tom_doc(i) := dv_cnpj(TO_CHAR(10000000 + MOD(i * 7654321, 89999999)) || '0001');
            END IF;
            tom_razao(i) := 'TOMADORA EXEMPLO ' || LPAD(i, 2, '0') || ' COMERCIO LTDA';
        ELSE
            IF i <= 33 THEN
                tom_doc(i) := dv_cpf('0' || LPAD(TO_CHAR(MOD(i * 3456789, 99999999)), 8, '0'));
            ELSE
                tom_doc(i) := dv_cpf(TO_CHAR(100000000 + MOD(i * 9876543, 899999999)));
            END IF;
            tom_razao(i) := 'PESSOA FISICA EXEMPLO ' || LPAD(i, 2, '0');
        END IF;
    END LOOP;

    -- contribuintes 1..30
    FOR p IN 1..30 LOOP
        v_regime := CASE WHEN p BETWEEN 13 AND 20 THEN 'S' WHEN p BETWEEN 21 AND 24 THEN 'M' WHEN p = 28 THEN 'S' ELSE 'N' END;
        IF p IN (5, 14) THEN
            v_doc := dv_cnpj('0' || LPAD(TO_CHAR(MOD(p * 2345671, 9999999)), 7, '0') || '0001');
        ELSE
            v_doc := dv_cnpj(TO_CHAR(11000000 + p * 100003) || '0001');
        END IF;
        INSERT INTO CONTRIBUINTES (ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, REGIME, ATIVO)
        VALUES (p, '1' || LPAD(p, 7, '0'), v_doc,
                CASE WHEN v_regime = 'M' THEN 'MICROEMPREENDEDOR EXEMPLO ' || LPAD(p, 2, '0')
                     ELSE 'PRESTADORA EXEMPLO ' || LPAD(p, 2, '0') || ' SERVICOS LTDA' END,
                v_regime,
                CASE WHEN p = 30 THEN 'N' ELSE 'S' END);
    END LOOP;

    -- notas de 2026-07 a 2026-10 para os prestadores 1..24
    FOR p IN 1..24 LOOP
        v_regime := CASE WHEN p BETWEEN 13 AND 20 THEN 'S' WHEN p BETWEEN 21 AND 24 THEN 'M' ELSE 'N' END;
        v_numero := 0;
        FOR m IN 0..3 LOOP
            v_mes := ADD_MONTHS(v_inicio, m);
            v_cnt := 3 + MOD(p * m + p, 4);
            FOR k IN 1..v_cnt LOOP
                v_seed := p * 1000 + m * 100 + k;
                v_dia := 1 + MOD(v_seed * 7, CASE WHEN m = 3 THEN 8 ELSE 27 END);
                v_dt := v_mes + (v_dia - 1) + (8 + MOD(v_seed, 10)) / 24 + MOD(v_seed * 13, 60) / 1440;
                v_ix := 1 + MOD(v_seed * 3, n_itens);
                v_serv := 500 + MOD(v_seed * 7919, 15000) + MOD(v_seed * 13, 100) / 100;
                v_ded := CASE WHEN MOD(v_seed, 5) = 0 THEN ROUND(v_serv * 0.1, 2) ELSE 0 END;
                v_ret := CASE WHEN MOD(v_seed, 6) = 0 THEN 'S' ELSE 'N' END;
                v_tom := 1 + MOD(v_seed * 11, 40);
                v_sit := 'N';
                v_cancel := NULL;
                v_rps_id := NULL;
                v_id := SEQ_NFSE.NEXTVAL;

                IF p = 1 AND m = 3 AND k <= 3 THEN
                    v_tom := k;
                    v_ret := 'N';
                    v_rps_id := SEQ_RPS.NEXTVAL;
                    INSERT INTO RPS (ID, PRESTADOR_ID, NUMERO, SERIE, TIPO, DT_EMISSAO, PROTOCOLO, TOMADOR_CNPJ, TOMADOR_CPF, TOMADOR_RAZAO, NFSE_ID)
                    VALUES (v_rps_id, p, k, 'A1', 1, TRUNC(v_dt), 'NC20261000000001', TO_NUMBER(tom_doc(v_tom)), NULL, tom_razao(v_tom), v_id);
                END IF;

                IF MOD(v_seed, 23) = 0 AND NOT (p = 1 AND m = 3) THEN
                    v_sit := 'C';
                    v_cancel := v_dt + 2 + 3 / 24;
                END IF;

                IF p = 3 AND m = 1 AND k = 2 THEN
                    -- nota substituida + substituidora
                    v_id2 := SEQ_NFSE.NEXTVAL;
                    inserir_nota(p, v_regime, v_dt, it_cod(v_ix), v_serv, v_ded, 'N', v_tom, 'S', NULL, NULL, v_id, v_id2, NULL);
                    inserir_nota(p, v_regime, v_dt + 5, it_cod(v_ix), v_serv + 100, v_ded, 'N', v_tom, 'N', NULL, NULL, v_id2, NULL, NULL);
                ELSE
                    inserir_nota(p, v_regime, v_dt, it_cod(v_ix), v_serv, v_ded, v_ret, v_tom, v_sit, v_cancel, NULL, v_id, NULL, v_rps_id);
                END IF;
            END LOOP;

            -- valores em que o ISS emitido pela aplicacao difere do arredondamento do banco
            IF p = 1 AND m = 2 THEN
                inserir_nota(1, 'N', v_mes + 20 + 10 / 24, '17.01', 10.50, 0, 'N', 7, 'N', NULL, 0.52, SEQ_NFSE.NEXTVAL, NULL, NULL);
                inserir_nota(1, 'N', v_mes + 20 + 11 / 24, '17.01', 250.50, 0, 'N', 8, 'N', NULL, 12.52, SEQ_NFSE.NEXTVAL, NULL, NULL);
                inserir_nota(1, 'N', v_mes + 21 + 11 / 24, '17.01', 100.10, 0, 'N', 9, 'N', NULL, 5.00, SEQ_NFSE.NEXTVAL, NULL, NULL);
                inserir_nota(1, 'N', v_mes + 22 + 14 / 24, '01.03', 1234.50, 0, 'N', 10, 'N', NULL, 37.03, SEQ_NFSE.NEXTVAL, NULL, NULL);
                inserir_nota(1, 'N', v_mes + 23 + 15 / 24, '01.03', 375.50, 0, 'N', 11, 'N', NULL, 11.26, SEQ_NFSE.NEXTVAL, NULL, NULL);
            END IF;
            IF p = 2 AND m = 3 THEN
                inserir_nota(2, 'N', v_mes + 5 + 10 / 24, '17.01', 250.50, 0, 'N', 12, 'N', NULL, 12.52, SEQ_NFSE.NEXTVAL, NULL, NULL);
                inserir_nota(2, 'N', v_mes + 6 + 10 / 24, '01.03', 1234.50, 0, 'N', 13, 'N', NULL, 37.03, SEQ_NFSE.NEXTVAL, NULL, NULL);
            END IF;
            IF p = 13 AND m = 1 THEN
                inserir_nota(13, 'S', v_mes + 12 + 9 / 24, '17.01', 10.50, 0, 'N', 14, 'N', NULL, 0.52, SEQ_NFSE.NEXTVAL, NULL, NULL);
            END IF;
        END LOOP;
    END LOOP;

    -- prestadores 25..27: notas na competencia corrente (sempre dentro do prazo de cancelamento)
    FOR p IN 25..27 LOOP
        v_numero := 0;
        FOR k IN 1..(CASE WHEN p = 25 THEN 8 WHEN p = 26 THEN 6 ELSE 5 END) LOOP
            v_seed := p * 1000 + k;
            v_dt := v_comp_rel + (k - 1) / 24;
            v_ix := 1 + MOD(v_seed * 3, n_itens);
            v_serv := 800 + MOD(v_seed * 7919, 9000) + MOD(v_seed * 13, 100) / 100;
            v_tom := 1 + MOD(v_seed * 11, 40);
            inserir_nota(p, 'N', v_dt, it_cod(v_ix), v_serv, 0, 'N', v_tom, 'N', NULL, NULL, SEQ_NFSE.NEXTVAL, NULL, NULL);
        END LOOP;
    END LOOP;

    -- lotes ja registrados (prestador 1)
    INSERT INTO LOTES_RPS (PROTOCOLO, PRESTADOR_ID, NUMERO_LOTE, QTD_RPS, SITUACAO, XML_LOTE, ERROS, DT_RECEBIMENTO, DT_PROCESSAMENTO)
    VALUES ('NC20261000000001', 1, 1, 3, 4,
            '<EnviarLoteRpsEnvio><LoteRps><NumeroLote>1</NumeroLote><Prestador><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador><QuantidadeRps>3</QuantidadeRps><ListaRps/></LoteRps></EnviarLoteRpsEnvio>',
            NULL, DATE '2026-10-05' + 9 / 24, DATE '2026-10-05' + 9 / 24 + 4 / 86400);
    INSERT INTO LOTES_RPS (PROTOCOLO, PRESTADOR_ID, NUMERO_LOTE, QTD_RPS, SITUACAO, XML_LOTE, ERROS, DT_RECEBIMENTO, DT_PROCESSAMENTO)
    VALUES ('NC20261000000002', 1, 2, 2, 3,
            '<EnviarLoteRpsEnvio><LoteRps><NumeroLote>2</NumeroLote><Prestador><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador><QuantidadeRps>2</QuantidadeRps><ListaRps/></LoteRps></EnviarLoteRpsEnvio>',
            '<ListaMensagemRetorno><MensagemRetorno><IdentificacaoRps><Numero>2</Numero><Serie>A1</Serie></IdentificacaoRps><Codigo>E060</Codigo><Mensagem>RPS já informado.</Mensagem><Correcao>Um RPS só pode originar uma NFS-e.</Correcao></MensagemRetorno><MensagemRetorno><IdentificacaoRps><Numero>9</Numero><Serie>A1</Serie></IdentificacaoRps><Codigo>E061</Codigo><Mensagem>Item de serviço inexistente.</Mensagem><Correcao>Informe um item da lista de serviços (LC 116/2003).</Correcao></MensagemRetorno></ListaMensagemRetorno>',
            DATE '2026-10-06' + 10 / 24, DATE '2026-10-06' + 10 / 24 + 3 / 86400);
    INSERT INTO LOTES_RPS (PROTOCOLO, PRESTADOR_ID, NUMERO_LOTE, QTD_RPS, SITUACAO, XML_LOTE, ERROS, DT_RECEBIMENTO, DT_PROCESSAMENTO)
    VALUES ('NC20261000000003', 1, 3, 1, 2,
            '<EnviarLoteRpsEnvio><LoteRps><NumeroLote>3</NumeroLote><Prestador><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador><QuantidadeRps>0</QuantidadeRps><ListaRps/></LoteRps></EnviarLoteRpsEnvio>',
            NULL, DATE '2026-10-07' + 11 / 24, NULL);
    COMMIT;

    -- guias de todas as competencias que tem notas
    FOR g IN (SELECT DISTINCT PRESTADOR_ID, COMPETENCIA FROM NFSE ORDER BY PRESTADOR_ID, COMPETENCIA) LOOP
        PKG_GUIA.recalcular_guia(g.PRESTADOR_ID, g.COMPETENCIA);
    END LOOP;
    COMMIT;

    -- guias ja pagas
    UPDATE GUIAS SET SITUACAO = 'P' WHERE PRESTADOR_ID = 4 AND COMPETENCIA = DATE '2026-07-01';
    UPDATE GUIAS SET SITUACAO = 'P' WHERE PRESTADOR_ID = 26 AND COMPETENCIA = v_comp_rel;
    COMMIT;
END;
/

BEGIN
    DBMS_STATS.GATHER_SCHEMA_STATS(USER);
END;
/

EXIT
