-- ADR-0005, reversão: recria o TRG_NFSE_SITUACAO_GUIA original. O bloco CREATE ... END; abaixo é cópia byte a byte
-- de db/04_triggers.sql (o teste de tests/propostas compara o DDL antes da aplicação e depois da reversão).
-- Executar como NFSE_OWNER. Nenhum dado é revertido: guias recalculadas durante a vigência da correção continuam
-- corretas; a partir daqui o defeito K07 volta.
SET DEFINE OFF
WHENEVER SQLERROR EXIT FAILURE

-- 2019: recalcula guia em cancelamentos (inclusive cancelamento administrativo em massa)
CREATE OR REPLACE TRIGGER TRG_NFSE_SITUACAO_GUIA
AFTER UPDATE OF SITUACAO ON NFSE
FOR EACH ROW
DECLARE
    PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
    PKG_GUIA.recalcular_guia(:NEW.PRESTADOR_ID, :NEW.COMPETENCIA);
    COMMIT;
END;
/

SHOW ERRORS

DECLARE
    v_status VARCHAR2(10);
BEGIN
    SELECT STATUS INTO v_status FROM USER_OBJECTS WHERE OBJECT_NAME = 'TRG_NFSE_SITUACAO_GUIA' AND OBJECT_TYPE = 'TRIGGER';
    IF v_status <> 'VALID' THEN
        RAISE_APPLICATION_ERROR(-20001, 'TRG_NFSE_SITUACAO_GUIA original inválido');
    END IF;
END;
/

EXIT
