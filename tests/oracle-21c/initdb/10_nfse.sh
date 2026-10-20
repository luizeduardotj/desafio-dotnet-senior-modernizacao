#!/bin/bash
# Mesmo init do legado (db/init.sh), no PDB do XE: só troca FREEPDB1 por XEPDB1 numa cópia, sem tocar db/.
set -euo pipefail
sed 's#/FREEPDB1#/XEPDB1#g' /opt/nfse/db/init.sh > /tmp/10_nfse_xe21.sh
bash /tmp/10_nfse_xe21.sh
