#!/bin/bash
# Mesmo init do NFSE_MODERNO (db/moderno/init.sh), no PDB do XE: só troca FREEPDB1 por XEPDB1 numa cópia.
set -euo pipefail
sed 's#/FREEPDB1#/XEPDB1#g' /opt/nfse/db/moderno/init.sh > /tmp/20_moderno_xe21.sh
bash /tmp/20_moderno_xe21.sh
