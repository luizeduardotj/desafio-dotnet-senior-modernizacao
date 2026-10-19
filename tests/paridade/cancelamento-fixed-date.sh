#!/usr/bin/env bash
# Paridade do CancelarNfse (ParidadeCancelamentoTests) contra o Oracle do compose, com o relógio congelado (ADR-0011).
# CI e local, com o ambiente de pé e o banco na linha de base (recém-carregado ou restaurado, README desta pasta).
# O teste cancela notas do seed: rode depois da paridade das leituras e reponha o banco antes de rodá-las de novo
# (no CI o job termina com `down -v`).
#   1. confere que as notas que o corpus cancela (prestador 25, notas 1 e 2) ainda estão normais;
#   2. ALTER SYSTEM SET FIXED_DATE como SYS no FREEPDB1 (autenticação do SO dentro do container, sem senha);
#   3. dotnet test só do ParidadeCancelamentoTests, com NFSE_PARIDADE_CANCELAMENTO=1;
#   4. FIXED_DATE = NONE ao sair, passe ou falhe.
# Argumentos extras do compose (ex.: -p nome) vão em COMPOSE_ARGS.
set -euo pipefail
cd "$(dirname "$0")/../.."

: "${NFSE_PARIDADE_ORACLE:?defina NFSE_PARIDADE_ORACLE com a connection string do NFSE_MODERNO_APP}"
FIXED_DATE="2026-10-09-15:00:00"

# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_ARGS:-} "$@"; }

# SQL da entrada padrão como SYS no PDB; qualquer erro termina com código diferente de zero.
sys_pdb() {
    { echo "WHENEVER SQLERROR EXIT FAILURE"; echo "SET FEEDBACK OFF"; echo "ALTER SESSION SET CONTAINER = FREEPDB1;"; cat; echo "EXIT"; } |
        compose exec -T oracle sqlplus -s -L / as sysdba
}

echo "paridade-cancelamento: banco na linha de base?"
sys_pdb <<'SQL'
DECLARE
    v_alteradas NUMBER;
BEGIN
    SELECT COUNT(*) INTO v_alteradas FROM NFSE_OWNER.NFSE WHERE PRESTADOR_ID = 25 AND NUMERO IN (1, 2) AND SITUACAO <> 'N';
    IF v_alteradas > 0 THEN
        RAISE_APPLICATION_ERROR(-20001, 'banco fora da linha de base: notas 1 e 2 do prestador 25 já canceladas; restaure o banco');
    END IF;
END;
/
SQL

desligar() {
    echo "paridade-cancelamento: FIXED_DATE = NONE"
    echo "ALTER SYSTEM SET FIXED_DATE = NONE;" | sys_pdb
}
trap desligar EXIT

echo "paridade-cancelamento: FIXED_DATE = ${FIXED_DATE}"
sys_pdb <<SQL
ALTER SYSTEM SET FIXED_DATE = '${FIXED_DATE}';
DECLARE
    v_agora VARCHAR2(19);
BEGIN
    SELECT TO_CHAR(SYSDATE, 'YYYY-MM-DD-HH24:MI:SS') INTO v_agora FROM DUAL;
    IF v_agora <> '${FIXED_DATE}' THEN
        RAISE_APPLICATION_ERROR(-20002, 'FIXED_DATE sem efeito: SYSDATE = ' || v_agora);
    END IF;
END;
/
SQL

NFSE_PARIDADE_CANCELAMENTO=1 dotnet test tests/Nfse.Paridade.Testes -c Release \
    --filter "FullyQualifiedName~Nfse.Paridade.Testes.ParidadeCancelamentoTests" --logger "console;verbosity=normal"
