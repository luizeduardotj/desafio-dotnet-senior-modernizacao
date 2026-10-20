#!/usr/bin/env bash
# Imprime o snapshot de NFSE/GUIAS da competência da nota <im>/<numero> (estado-depois-do-cancelamento.sql), lido
# como SYS no FREEPDB1 do compose. Usado pelo capturar.sh com ESTADO=1 depois de cada caso 2xx; numero não numérico
# (ex.: caso E001) imprime nada. Argumentos extras do compose (ex.: -p nome) vão em COMPOSE_ARGS.
# uso: tests/paridade/estado-do-banco.sh <inscricao-municipal> <numero>
set -euo pipefail
cd "$(dirname "$0")/../.."
IM="${1:?inscrição municipal}"
NUMERO="${2:-}"
[[ "$IM" =~ ^[0-9]+$ ]] || IM=""
[[ "$NUMERO" =~ ^[0-9]+$ ]] || NUMERO="NULL"

# shellcheck disable=SC2086
{
    echo "WHENEVER SQLERROR EXIT FAILURE"
    echo "SET FEEDBACK OFF HEADING OFF PAGESIZE 0 LINESIZE 32767 TRIMOUT ON TAB OFF VERIFY OFF"
    echo "ALTER SESSION SET CONTAINER = FREEPDB1;"
    echo "VARIABLE im VARCHAR2(20)"
    echo "VARIABLE numero NUMBER"
    echo "EXEC :im := '${IM}'"
    echo "EXEC :numero := ${NUMERO}"
    cat tests/paridade/estado-depois-do-cancelamento.sql
    echo "/"
    echo "EXIT"
} | docker compose ${COMPOSE_ARGS:-} exec -T -e NLS_LANG=.AL32UTF8 oracle sqlplus -s -L / as sysdba
