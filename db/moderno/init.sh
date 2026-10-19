#!/bin/bash
# Executado pelo entrypoint da imagem gvenzl/oracle-free (initdb.d, como 20_moderno.sh) depois de 10_nfse.sh, só na
# primeira subida. Aplica o schema NFSE_MODERNO (README.md desta pasta) com as senhas vindas do ambiente.
# Qualquer erro, inclusive pacote ou view inválidos, termina com código diferente de zero: o entrypoint para e o
# container não fica saudável. Como o init não roda de novo num volume já criado, corrija e suba com `down -v`.
set -euo pipefail
export NLS_LANG=.AL32UTF8

DIR="${NFSE_MODERNO_SCRIPTS:-/opt/nfse/db/moderno}"
PDB="//localhost:1521/FREEPDB1"
: "${ORACLE_PASSWORD:?}" "${APP_USER_PASSWORD:?}"
: "${NFSE_MODERNO_PASSWORD:?defina NFSE_MODERNO_PASSWORD}"
: "${NFSE_MODERNO_APP_PASSWORD:?defina NFSE_MODERNO_APP_PASSWORD}"
: "${NFSE_MODERNO_RO_PASSWORD:?defina NFSE_MODERNO_RO_PASSWORD}"

# Credenciais e DEFINEs vão pela entrada padrão (CONNECT dentro da sessão), não na linha de comando do processo.
# SQLERROR cobre falha de login e de DDL; arquivo ausente (SP2-0310) não é SQLERROR, por isso o teste antes.
executar() {
    local usuario="$1" senha="$2" script="$3"
    shift 3
    echo "[moderno] ${script} como ${usuario}"
    {
        echo "WHENEVER SQLERROR EXIT FAILURE"
        echo "WHENEVER OSERROR EXIT FAILURE"
        echo "SET VERIFY OFF"
        echo "CONNECT ${usuario}/\"${senha}\"@${PDB}"
        for definicao in "$@"; do
            echo "DEFINE ${definicao}"
        done
        cat
    } | sqlplus -s -L /nolog
}

aplicar() {
    local usuario="$1" senha="$2" script="$3"
    shift 3
    [ -f "${DIR}/${script}" ] || { echo "[moderno] ${DIR}/${script} não encontrado" >&2; exit 1; }
    echo "@${DIR}/${script}" | executar "${usuario}" "${senha}" "${script}" "$@"
}

INICIO=$(date +%s)
aplicar SYSTEM "${ORACLE_PASSWORD}" 01_usuarios.sql \
    "senha_moderno = \"${NFSE_MODERNO_PASSWORD}\"" \
    "senha_app = \"${NFSE_MODERNO_APP_PASSWORD}\"" \
    "senha_ro = \"${NFSE_MODERNO_RO_PASSWORD}\""
aplicar NFSE_OWNER "${APP_USER_PASSWORD}" 02_grants_legado.sql
aplicar NFSE_MODERNO "${NFSE_MODERNO_PASSWORD}" 03_views.sql
aplicar NFSE_MODERNO "${NFSE_MODERNO_PASSWORD}" 04_pkg_cancelamento_api.sql

# Conferência final: nenhum objeto do schema pode ficar inválido (views e pacote).
executar NFSE_MODERNO "${NFSE_MODERNO_PASSWORD}" "conferência de objetos" <<'SQL'
DECLARE
    v_invalidos NUMBER;
BEGIN
    SELECT COUNT(*) INTO v_invalidos FROM USER_OBJECTS WHERE STATUS <> 'VALID';
    IF v_invalidos > 0 THEN
        RAISE_APPLICATION_ERROR(-20002, v_invalidos || ' objeto(s) inválido(s) em NFSE_MODERNO');
    END IF;
END;
/
EXIT
SQL
echo "[moderno] concluido em $(( $(date +%s) - INICIO ))s"
