#!/usr/bin/env bash
# Teste da proposta ADR-0005 (correção do K07) num Oracle descartável (tests/propostas/docker-compose.yml, projeto
# próprio, sem porta publicada). Nada daqui roda no compose da raiz nem no CI de paridade.
#   0. linha de base: notas 1 e 2 do prestador 25 normais; DDL do trigger original guardado; relatório de reconciliação;
#   1. aplicar.sql;
#   2. mesma transação: cancela 25/1 e 25/2 pelo PKG_NFSE e lê a guia antes do commit (1557,01 e depois 1267,86);
#      ROLLBACK devolve a guia ao valor anterior;
#   3. vários cancelamentos na mesma competência: três comandos numa transação (prestador 27) e um UPDATE em massa de
#      todas as notas de um prestador do seed de volume (um MERGE por competência, contado no V$SQL);
#   4. concorrência pelo wrapper (NFSE_MODERNO_APP): duas sessões cancelam notas diferentes da mesma competência; a
#      primeira segura a transação por 3 s. Com a guia existente e com a guia ainda inexistente;
#   5. relatório de reconciliação com a correção;
#   6. reverter.sql: o DDL do trigger volta igual ao da linha de base;
#   7. comportamento antigo de volta: cancela 25/1 e 25/2 em commits separados e a guia fica em 1557,01 (K07);
#      UPDATE em massa com o trigger antigo, para comparar tempo e número de MERGE;
#   8. relatório de reconciliação depois da reversão: a guia do prestador 25 aparece divergente.
# Uso: tests/propostas/0005-trigger-guia.sh   (MANTER=1 não derruba o banco no fim)
# Saída: os números medidos vão para o stdout com o prefixo [M] e para tests/propostas/saida/ (fora do git).
set -euo pipefail
cd "$(dirname "$0")/../.."

COMPOSE_FILE=tests/propostas/docker-compose.yml
PROJETO="${PROJETO:-nfse-proposta-0005}"
PROPOSTA=/opt/nfse/db/propostas/0005-correcao-trigger-guia
SAIDA=tests/propostas/saida
mkdir -p "$SAIDA"

compose() { docker compose -f "$COMPOSE_FILE" -p "$PROJETO" "$@"; }

# SQL da entrada padrão; qualquer erro termina com código diferente de zero. Credenciais dentro da sessão (CONNECT),
# fora da linha de comando, como em db/moderno/init.sh.
sessao() {
    local conexao="$1"
    { echo "WHENEVER SQLERROR EXIT FAILURE"; echo "SET SERVEROUTPUT ON SIZE UNLIMITED"; echo "SET FEEDBACK OFF";
      echo "SET VERIFY OFF"; echo "SET DEFINE OFF"; echo "SET LINESIZE 200"; echo "CONNECT ${conexao}"; cat; echo "EXIT"; } |
        compose exec -T oracle sqlplus -s -L /nolog
}
owner() { sessao "NFSE_OWNER/nfse_dev_123@//localhost:1521/FREEPDB1"; }
app()   { sessao "NFSE_MODERNO_APP/moderno_app_dev_123@//localhost:1521/FREEPDB1"; }
sys()   { { echo "ALTER SESSION SET CONTAINER = FREEPDB1;"; cat; } | sessao "/ as sysdba"; }

merges() { echo "SELECT 'MERGES=' || NVL(SUM(EXECUTIONS), 0) FROM V\$SQL WHERE SQL_TEXT LIKE 'MERGE INTO GUIAS%';" |
    sys | sed -n 's/^MERGES=//p'; }

ddl_trigger() {
    owner <<'SQL'
SET LONG 100000 LONGCHUNKSIZE 100000 PAGESIZE 0 LINESIZE 32767 TRIMSPOOL ON HEADING OFF
SELECT DBMS_METADATA.GET_DDL('TRIGGER', 'TRG_NFSE_SITUACAO_GUIA') FROM DUAL;
SQL
}

relatorio() {
    local rotulo="$1"
    echo "@${PROPOSTA}/relatorio-reconciliacao.sql" | owner > "$SAIDA/relatorio-${rotulo}.csv"
    echo "[M] relatório de reconciliação (${rotulo}):"
    sed 's/^/    /' "$SAIDA/relatorio-${rotulo}.csv"
}

# Concorrência: A cancela pelo wrapper e segura a transação por 3 s; B começa 1 s depois e cancela outra nota da
# mesma competência. Cada sessão imprime o código de erro e quanto tempo esperou.
cancelar_pelo_wrapper() {
    local numero="$1" segurar="$2" rotulo="$3"
    app <<SQL
DECLARE
    v_erro VARCHAR2(10); v_sit VARCHAR2(1); v_comp DATE; v_paga NUMBER; v_agora DATE; v_dt DATE;
    v_t0 NUMBER := DBMS_UTILITY.GET_TIME;
BEGIN
    NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar(27, ${numero}, 'proposta 0005 ${rotulo}', v_erro, v_sit, v_comp, v_paga, v_agora, v_dt);
    IF v_erro IS NOT NULL OR v_sit <> 'N' THEN
        RAISE_APPLICATION_ERROR(-20100, '${rotulo}: erro=' || v_erro || ' situação antes=' || v_sit);
    END IF;
    DBMS_OUTPUT.PUT_LINE('[M] ${rotulo}: nota 27/${numero} cancelada pelo wrapper em ' || (DBMS_UTILITY.GET_TIME - v_t0) * 10 || ' ms');
    DBMS_SESSION.SLEEP(${segurar});
    COMMIT;
END;
/
SQL
}

concorrencia() {
    local nota_a="$1" nota_b="$2" rotulo="$3"
    cancelar_pelo_wrapper "$nota_a" 3 "${rotulo}/A" > "$SAIDA/concorrencia-${rotulo}-a.log" 2>&1 &
    local pid_a=$!
    sleep 1
    local status_b=0 status_a=0
    cancelar_pelo_wrapper "$nota_b" 0 "${rotulo}/B" > "$SAIDA/concorrencia-${rotulo}-b.log" 2>&1 || status_b=$?
    wait "$pid_a" || status_a=$?
    cat "$SAIDA/concorrencia-${rotulo}-a.log" "$SAIDA/concorrencia-${rotulo}-b.log"
    [ "$status_a" -eq 0 ] && [ "$status_b" -eq 0 ] || { echo "concorrência ${rotulo}: A=${status_a} B=${status_b}" >&2; exit 1; }
}

# UPDATE em massa de todas as notas normais de um prestador do seed de volume, com ROLLBACK. Confere as guias abertas
# dentro da transação (só com a correção) e conta os MERGE executados.
massa() {
    local rotulo="$1" conferir="$2" antes depois
    antes=$(merges)
    owner <<SQL
DECLARE
    v_p NUMBER; v_notas NUMBER; v_comps NUMBER; v_t0 NUMBER; v_erradas NUMBER;
    v_pagas_antes NUMBER; v_pagas_depois NUMBER; v_abertas_antes NUMBER; v_abertas_depois NUMBER;
BEGIN
    SELECT MIN(PRESTADOR_ID) INTO v_p FROM GUIAS WHERE PRESTADOR_ID > 1000 AND SITUACAO = 'A';
    SELECT COUNT(*), COUNT(DISTINCT COMPETENCIA) INTO v_notas, v_comps FROM NFSE WHERE PRESTADOR_ID = v_p AND SITUACAO = 'N';
    SELECT NVL(SUM(VALOR_ISS), 0) INTO v_pagas_antes FROM GUIAS WHERE PRESTADOR_ID = v_p AND SITUACAO = 'P';
    SELECT NVL(SUM(VALOR_ISS), 0) INTO v_abertas_antes FROM GUIAS WHERE PRESTADOR_ID = v_p AND SITUACAO = 'A';
    v_t0 := DBMS_UTILITY.GET_TIME;
    UPDATE NFSE SET SITUACAO = 'C', DT_CANCELAMENTO = SYSDATE, MOTIVO_CANCELAMENTO = 'proposta 0005 massa'
     WHERE PRESTADOR_ID = v_p AND SITUACAO = 'N';
    DBMS_OUTPUT.PUT_LINE('[M] massa ${rotulo}: prestador do seed de volume, ' || SQL%ROWCOUNT || ' notas em ' || v_comps
        || ' competências, UPDATE em ' || (DBMS_UTILITY.GET_TIME - v_t0) * 10 || ' ms');
    SELECT NVL(SUM(VALOR_ISS), 0) INTO v_pagas_depois FROM GUIAS WHERE PRESTADOR_ID = v_p AND SITUACAO = 'P';
    IF v_pagas_depois <> v_pagas_antes THEN
        RAISE_APPLICATION_ERROR(-20030, 'massa: guia paga alterada');
    END IF;
    IF ${conferir} = 1 THEN
        SELECT COUNT(*) INTO v_erradas FROM GUIAS
         WHERE PRESTADOR_ID = v_p AND SITUACAO = 'A' AND VALOR_ISS <> 0;
        IF v_erradas > 0 THEN
            RAISE_APPLICATION_ERROR(-20031, 'massa: ' || v_erradas || ' guia(s) aberta(s) não zeradas na transação');
        END IF;
        DBMS_OUTPUT.PUT_LINE('[M] massa ${rotulo}: guias abertas somavam ' || v_abertas_antes || ' e ficaram em 0 antes do commit; pagas inalteradas');
    END IF;
    ROLLBACK;
    SELECT NVL(SUM(VALOR_ISS), 0) INTO v_abertas_depois FROM GUIAS WHERE PRESTADOR_ID = v_p AND SITUACAO = 'A';
    IF v_abertas_depois <> v_abertas_antes THEN
        RAISE_APPLICATION_ERROR(-20032, 'massa: ROLLBACK não devolveu as guias (' || v_abertas_depois || ')');
    END IF;
END;
/
SQL
    depois=$(merges)
    echo "[M] massa ${rotulo}: MERGE INTO GUIAS executados = $(( depois - antes ))"
}

# Guia do prestador/competência da nota informada e o valor correto (soma das notas normais e não retidas).
GUIA_E_CORRETO="
    FUNCTION guia(p NUMBER, n NUMBER) RETURN NUMBER IS v NUMBER; BEGIN
        SELECT g.VALOR_ISS INTO v FROM GUIAS g JOIN NFSE x ON x.PRESTADOR_ID = g.PRESTADOR_ID AND x.COMPETENCIA = g.COMPETENCIA
         WHERE x.PRESTADOR_ID = p AND x.NUMERO = n;
        RETURN v; END;
    FUNCTION correto(p NUMBER, n NUMBER) RETURN NUMBER IS v NUMBER; BEGIN
        SELECT NVL(SUM(ROUND((y.VALOR_SERVICOS - y.VALOR_DEDUCOES) * y.ALIQUOTA, 2)), 0) INTO v
          FROM NFSE x JOIN NFSE y ON y.PRESTADOR_ID = x.PRESTADOR_ID AND y.COMPETENCIA = x.COMPETENCIA
         WHERE x.PRESTADOR_ID = p AND x.NUMERO = n AND y.SITUACAO = 'N' AND y.ISS_RETIDO = 'N';
        RETURN v; END;
    PROCEDURE esperar(rotulo VARCHAR2, obtido NUMBER, esperado NUMBER) IS BEGIN
        DBMS_OUTPUT.PUT_LINE('[M] ' || rotulo || ': guia ' || TO_CHAR(obtido, 'FM999990.00') || ' (esperado ' || TO_CHAR(esperado, 'FM999990.00') || ')');
        IF obtido IS NULL OR obtido <> esperado THEN RAISE_APPLICATION_ERROR(-20000, rotulo || ': divergiu'); END IF;
    END;"

if [ "${MANTER:-0}" != 1 ]; then
    trap 'echo "derrubando ${PROJETO}"; compose down -v --remove-orphans >/dev/null 2>&1 || true' EXIT
fi

echo "== subindo Oracle descartável (${PROJETO}); a primeira carga leva alguns minutos"
T0=$(date +%s)
compose up -d --wait --wait-timeout 1800
echo "[M] Oracle saudável em $(( $(date +%s) - T0 )) s"
echo "SELECT '[M] ' || BANNER_FULL FROM V\$VERSION;" | sys | grep '^\[M\]' | tr '\n' ' '; echo

echo "== 0. linha de base"
owner <<SQL
DECLARE
    v_alteradas NUMBER;
${GUIA_E_CORRETO}
BEGIN
    SELECT COUNT(*) INTO v_alteradas FROM NFSE WHERE PRESTADOR_ID IN (25, 27) AND SITUACAO <> 'N';
    IF v_alteradas > 0 THEN RAISE_APPLICATION_ERROR(-20001, 'banco fora da linha de base'); END IF;
    esperar('linha de base 25', guia(25, 1), 1608.58);
END;
/
SQL
ddl_trigger > "$SAIDA/ddl-original.sql"
relatorio linha-de-base

echo "== 1. aplicar.sql"
echo "@${PROPOSTA}/aplicar.sql" | owner

echo "== 2. mesma transação (PKG_NFSE.cancelar_nfse, caminho do legado)"
owner <<SQL
DECLARE
    v_erro VARCHAR2(10);
${GUIA_E_CORRETO}
BEGIN
    PKG_NFSE.cancelar_nfse(25, 1, 'proposta 0005', v_erro);
    IF v_erro IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20002, 'cancelar 25/1: ' || v_erro); END IF;
    esperar('com a correção, 25/1 cancelada, antes do commit', guia(25, 1), 1557.01);
    PKG_NFSE.cancelar_nfse(25, 2, 'proposta 0005', v_erro);
    IF v_erro IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20002, 'cancelar 25/2: ' || v_erro); END IF;
    esperar('com a correção, 25/1 e 25/2 canceladas, antes do commit', guia(25, 1), 1267.86);
    ROLLBACK;
    esperar('com a correção, depois do ROLLBACK', guia(25, 1), 1608.58);
END;
/
SQL

echo "== 3. vários cancelamentos na mesma competência"
owner <<SQL
DECLARE
    v_erro VARCHAR2(10); v_inicial NUMBER;
${GUIA_E_CORRETO}
BEGIN
    v_inicial := guia(27, 1);
    FOR n IN 1..3 LOOP
        PKG_NFSE.cancelar_nfse(27, n, 'proposta 0005', v_erro);
        IF v_erro IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20003, 'cancelar 27/' || n || ': ' || v_erro); END IF;
        esperar('três comandos, 27/1..' || n || ' canceladas na transação', guia(27, 1), correto(27, 1));
    END LOOP;
    ROLLBACK;
    esperar('três comandos, depois do ROLLBACK', guia(27, 1), v_inicial);
END;
/
SQL
massa "com a correção" 1

echo "== 4. concorrência pelo wrapper"
concorrencia 1 2 "guia existente"
owner <<SQL
DECLARE
${GUIA_E_CORRETO}
BEGIN
    esperar('concorrência com guia existente, 27/1 e 27/2 confirmadas', guia(27, 1), correto(27, 1));
END;
/
SQL
# Guia ainda inexistente: as duas sessões caem no INSERT do MERGE. Só neste banco descartável a guia é apagada.
printf "DELETE FROM GUIAS WHERE PRESTADOR_ID = 27;\nCOMMIT;\n" | owner
concorrencia 3 4 "guia inexistente"
owner <<SQL
DECLARE
    v_guias NUMBER;
${GUIA_E_CORRETO}
BEGIN
    SELECT COUNT(*) INTO v_guias FROM GUIAS WHERE PRESTADOR_ID = 27;
    IF v_guias <> 1 THEN RAISE_APPLICATION_ERROR(-20004, 'guias do prestador 27: ' || v_guias); END IF;
    esperar('concorrência com guia criada pelo cancelamento, 27/1..4 confirmadas', guia(27, 1), correto(27, 1));
END;
/
SQL

echo "== 5. relatório com a correção"
relatorio com-correcao

echo "== 6. reverter.sql"
echo "@${PROPOSTA}/reverter.sql" | owner
ddl_trigger > "$SAIDA/ddl-revertido.sql"
if diff -u "$SAIDA/ddl-original.sql" "$SAIDA/ddl-revertido.sql"; then
    echo "[M] DDL do trigger depois da reversão idêntico ao da linha de base"
else
    echo "DDL do trigger mudou depois da reversão" >&2; exit 1
fi

echo "== 7. comportamento antigo de volta (K07)"
for n in 1 2; do
    owner <<SQL
DECLARE v_erro VARCHAR2(10);
BEGIN
    PKG_NFSE.cancelar_nfse(25, ${n}, 'proposta 0005 revertida', v_erro);
    IF v_erro IS NOT NULL THEN RAISE_APPLICATION_ERROR(-20005, 'cancelar 25/${n}: ' || v_erro); END IF;
    COMMIT;
END;
/
SQL
done
owner <<SQL
DECLARE
${GUIA_E_CORRETO}
BEGIN
    esperar('trigger original, 25/1 e 25/2 confirmadas em commits separados', guia(25, 1), 1557.01);
    DBMS_OUTPUT.PUT_LINE('[M] valor correto seria ' || TO_CHAR(correto(25, 1), 'FM999990.00'));
END;
/
SQL
massa "trigger original" 0

echo "== 8. relatório depois da reversão"
relatorio revertido

echo "== proposta 0005: todos os passos conferidos"
