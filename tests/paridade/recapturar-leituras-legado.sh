#!/usr/bin/env bash
# Recaptura as LEITURAS do corpus (casos 0xx e 1xx) direto do legado do compose e falha se o golden master mudou
# (ADR-0011): prova que legado.xml/legado.status ainda descrevem o legado, não só que o novo os reproduz.
# CI e local, com o ambiente de pé e o banco na linha de base para as leituras (nenhum cancelamento ainda).
#   - fala com o legado (webservices:8080), não com a fachada, por um curl num container na rede do compose;
#   - não há o que mascarar: as leituras não dependem do relógio (seed com datas fixas, nenhuma data de
#     processamento na resposta), e o FIXED_DATE não entra; qualquer diferença é real e falha o step;
#   - os casos 2xx ficam de fora: mudam o banco e são conferidos pelo cancelamento-fixed-date.sh.
# Argumentos extras do compose (ex.: -p nome) vão em COMPOSE_ARGS. Ao falhar, o diff fica na árvore para inspeção.
set -euo pipefail
cd "$(dirname "$0")/../.."

CURL_IMAGE="curlimages/curl:8.23.0"
# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_ARGS:-} "$@"; }

fachada="$(compose ps -q fachada)"
[ -n "$fachada" ] || { echo "recaptura: a fachada não está de pé (o curl usa a rede dela)" >&2; exit 1; }

if ! git diff --quiet -- tests/paridade/casos; then
    echo "recaptura: tests/paridade/casos já tem alterações locais; faça commit ou descarte antes" >&2
    exit 1
fi

echo "recaptura: leituras do corpus direto no legado"
CASOS='0 1' CURL="docker run --rm -i --network container:${fachada} ${CURL_IMAGE}" \
    tests/paridade/capturar.sh legado http://webservices:8080/NfseWS.asmx

if ! git diff --exit-code --stat -- tests/paridade/casos; then
    git diff -- tests/paridade/casos | head -200
    echo "recaptura: o legado respondeu diferente do golden master (acima). Investigue antes de regravar o corpus." >&2
    exit 1
fi
echo "recaptura: golden master das leituras igual ao legado"
