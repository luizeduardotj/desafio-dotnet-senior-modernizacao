#!/usr/bin/env bash
# Aplica a massa de teste da paridade (tests/paridade/massa/*.sql) no Oracle do compose, como SYS no FREEPDB1.
# Cobre o que o seed do legado não exercita (C11, C13, C15; ADR-0004) sem alterar tabela nem db/0*: só INSERT de
# linhas novas, com chaves fixas, que nenhum outro caso lê. Idempotente: cada script não faz nada se já foi aplicado.
# CI e local: depois do `compose up` e antes de qualquer captura ou paridade (a linha de base passa a ser seed + massa).
# Argumentos extras do compose (ex.: -p nome) vão em COMPOSE_ARGS.
set -euo pipefail
PDB="${NFSE_ORACLE_PDB:-FREEPDB1}"  # outro PDB (ex.: XEPDB1 no XE 21c) em NFSE_ORACLE_PDB
cd "$(dirname "$0")/../.."

# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_ARGS:-} "$@"; }

for script in tests/paridade/massa/*.sql; do
    echo "massa: ${script}"
    { echo "WHENEVER SQLERROR EXIT FAILURE"; echo "SET FEEDBACK OFF";
      echo "ALTER SESSION SET CONTAINER = ${PDB};"; echo "SET SERVEROUTPUT ON"; cat "$script"; echo "EXIT"; } |
        compose exec -T oracle sqlplus -s -L / as sysdba
done
