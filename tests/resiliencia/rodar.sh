#!/usr/bin/env bash
# Resiliência com o Oracle lento ou fora do ar para o serviço novo (tests/resiliencia/README.md). CI e local.
#   tests/resiliencia/rodar.sh            # do zero: down -v, up --build --wait, testes, down -v (passe ou falhe)
#   MANTER=1 tests/resiliencia/rodar.sh   # sem derrubar no fim; rodar de novo reaproveita o ambiente de pé
# Project name e tag de imagem próprios: não encosta no compose padrão nem nas imagens dele. Portas em
# docker-compose.resiliencia.yml (variáveis NFSE_RESILIENCIA_PORTA_*).
set -euo pipefail
cd "$(dirname "$0")/../.."

PROJETO="${NFSE_RESILIENCIA_PROJETO:-nfse-resiliencia}"
export NFSE_VERSAO="${NFSE_VERSAO:-resiliencia}"
COMPOSE_ARGS="-p ${PROJETO} -f docker-compose.yml -f tests/resiliencia/docker-compose.resiliencia.yml"
# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_ARGS} "$@"; }

# Os testes reescrevem as flags da fachada nesta pasta (montada no lugar de deploy/flags) e leem os logs pelo compose.
export NFSE_RESILIENCIA=1
export NFSE_RESILIENCIA_COMPOSE="${COMPOSE_ARGS}"
export NFSE_RESILIENCIA_FLAGS="${NFSE_RESILIENCIA_FLAGS:-$PWD/tests/resiliencia/.flags}"
export NFSE_RESILIENCIA_MEDICOES="${NFSE_RESILIENCIA_MEDICOES:-$PWD/TestResults/resiliencia-medicoes.md}"
mkdir -p "${NFSE_RESILIENCIA_FLAGS}"
cp deploy/flags/flags.json "${NFSE_RESILIENCIA_FLAGS}/flags.json"

terminar() {
    status=$?
    if [ "$status" -ne 0 ]; then
        compose ps -a || true
        compose logs --no-color --tail 200 fachada nfse-novo toxiproxy || true
    fi
    if [ "${MANTER:-0}" != "1" ]; then
        compose down -v --remove-orphans || true
    fi
    exit "$status"
}
trap terminar EXIT

if [ "${MANTER:-0}" != "1" ]; then
    compose down -v --remove-orphans
fi

inicio=$(date +%s)
compose up -d --build --wait --wait-timeout 1800
echo "resiliencia: ambiente saudável em $(( $(date +%s) - inicio )) s"

dotnet test tests/Nfse.Resiliencia.Testes -c Release --logger "console;verbosity=detailed"
echo "resiliencia: medições em ${NFSE_RESILIENCIA_MEDICOES}"
cat "${NFSE_RESILIENCIA_MEDICOES}"
