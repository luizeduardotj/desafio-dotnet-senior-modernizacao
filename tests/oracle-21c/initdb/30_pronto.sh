#!/bin/bash
# O healthcheck.sh do gvenzl/oracle-xe só olha se o PDB está aberto, e fica saudável no meio do init (a imagem
# oracle-free espera os scripts). Este marcador, no volume, é o que o healthcheck do override exige além disso.
set -euo pipefail
touch /opt/oracle/oradata/.nfse-init-ok
