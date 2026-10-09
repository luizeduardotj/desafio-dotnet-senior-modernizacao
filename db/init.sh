#!/bin/bash
# Executado pelo entrypoint da imagem gvenzl/oracle-free (initdb.d) na primeira subida.
set -e
export NLS_LANG=.AL32UTF8
CONN="NFSE_OWNER/${APP_USER_PASSWORD}@//localhost:1521/FREEPDB1"
INICIO=$(date +%s)
for f in 01_schema 02_pkg_guia 03_pkg_nfse 04_triggers 05_seed_base 06_seed_volume; do
    echo "[init] $f.sql"
    sqlplus -s -L "$CONN" @/opt/nfse/db/$f.sql
done
echo "[init] concluido em $(( $(date +%s) - INICIO ))s"
