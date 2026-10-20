-- Massa de teste da paridade para C11, C13 e C15 (ADR-0004), que o seed do legado não exercita.
-- Só no ambiente de paridade, aplicada por tests/paridade/massa-de-teste.sh depois da carga; não muda tabela nem
-- db/0*. Linhas novas, com chaves fixas, num prestador e em tomadores que nenhum outro caso consulta: os casos
-- existentes (contagens, paginação, guias) continuam iguais. Sem relógio: datas fixas, nenhum SYSDATE, e o INSERT não
-- dispara o trigger da guia (só UPDATE OF SITUACAO), então GUIAS não muda.
--   prestador 31 (IM 10000031): pessoa física, CPF 52819374646 -> C11 (sai na tag <Cnpj>)
--   notas 1-3, tomador CNPJ 47291836000156, agosto/2026; a nota 2 tem competência 09/2026 -> C13
--   notas 4-5, tomador CNPJ 00074579869109, julho/2026; como número é igual ao CPF válido 74579869109,
--   que não tem nota nenhuma -> C15 (o legado devolve ao CPF as notas do CNPJ)
-- Idempotente: se o prestador 31 já existe, não faz nada.
DECLARE
    v_existe NUMBER;
    v_colisao NUMBER;

    PROCEDURE nota(p_id NUMBER, p_numero NUMBER, p_emissao DATE, p_competencia DATE, p_servicos NUMBER,
                   p_tomador VARCHAR2, p_razao VARCHAR2) IS
    BEGIN
        INSERT INTO NFSE_OWNER.NFSE (ID, PRESTADOR_ID, NUMERO, CODIGO_VERIFICACAO, DT_EMISSAO, COMPETENCIA, ITEM_SERVICO,
                                     DISCRIMINACAO, VALOR_SERVICOS, VALOR_DEDUCOES, BASE_CALCULO, ALIQUOTA, VALOR_ISS,
                                     ISS_RETIDO, TOMADOR_DOCUMENTO, TOMADOR_RAZAO, SITUACAO)
        VALUES (p_id, 31, p_numero, 'VT' || LPAD(p_numero, 7, '0'), p_emissao, p_competencia, '17.01',
                'Consultoria de teste da paridade - nota ' || p_numero, p_servicos, 0, p_servicos, 0.05,
                ROUND(p_servicos * 0.05, 2), 'N', p_tomador, p_razao, 'N');
    END;
BEGIN
    SELECT COUNT(*) INTO v_existe FROM NFSE_OWNER.CONTRIBUINTES WHERE ID = 31 OR INSCRICAO_MUNICIPAL = '10000031';
    IF v_existe > 0 THEN
        DBMS_OUTPUT.PUT_LINE('massa: prestador 31 ja existe, nada a fazer');
        RETURN;
    END IF;

    -- A colisão só prova C15 se nenhum outro tomador tiver esses documentos (como string ou como número).
    SELECT COUNT(*) INTO v_colisao FROM NFSE_OWNER.NFSE
     WHERE TOMADOR_DOCUMENTO IN ('47291836000156', '00074579869109', '74579869109')
        OR TO_NUMBER(TOMADOR_DOCUMENTO) IN (47291836000156, 74579869109);
    IF v_colisao > 0 THEN
        RAISE_APPLICATION_ERROR(-20010, 'massa: documentos de teste ja usados por ' || v_colisao || ' nota(s)');
    END IF;

    INSERT INTO NFSE_OWNER.CONTRIBUINTES (ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, REGIME, ATIVO)
    VALUES (31, '10000031', '52819374646', 'PROFISSIONAL AUTONOMO EXEMPLO 31', 'N', 'S');

    nota(900000001, 1, TIMESTAMP '2026-08-10 10:00:00', DATE '2026-08-01', 1000.00, '47291836000156', 'TOMADORA C13 EXEMPLO LTDA');
    nota(900000002, 2, TIMESTAMP '2026-08-12 10:00:00', DATE '2026-09-01', 2000.00, '47291836000156', 'TOMADORA C13 EXEMPLO LTDA');
    nota(900000003, 3, TIMESTAMP '2026-08-20 11:00:00', DATE '2026-08-01', 1500.00, '47291836000156', 'TOMADORA C13 EXEMPLO LTDA');
    nota(900000004, 4, TIMESTAMP '2026-07-15 09:00:00', DATE '2026-07-01',  800.00, '00074579869109', 'TOMADORA C15 ZEROS LTDA');
    nota(900000005, 5, TIMESTAMP '2026-07-16 09:30:00', DATE '2026-07-01',  900.00, '00074579869109', 'TOMADORA C15 ZEROS LTDA');
    COMMIT;
    DBMS_OUTPUT.PUT_LINE('massa: prestador 31 e 5 notas inseridos');
END;
/
