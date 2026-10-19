#!/usr/bin/env bash
# Smoke test do compose (CI e local), com o ambiente já de pé (`docker compose up -d --build --wait`):
#   1. a fachada e o serviço novo respondem /health/ready;
#   2. tools/exemplos/04-consultar-nfse.xml enviado à fachada volta byte a byte igual ao enviado direto ao legado.
# O curl roda num container que compartilha a rede da fachada: 127.0.0.1:8080 é a fachada e webservices:8080 é o
# legado, que não publica porta no host. Argumentos extras do compose (ex.: -p nome) vão em COMPOSE_ARGS.
set -euo pipefail
cd "$(dirname "$0")/.."

CURL_IMAGE="curlimages/curl:8.23.0"
EXEMPLO="04-consultar-nfse.xml"
# shellcheck disable=SC2086
compose() { docker compose ${COMPOSE_ARGS:-} "$@"; }

fachada="$(compose ps -q fachada)"
novo="$(compose ps -q nfse-novo)"
[ -n "$fachada" ] && [ -n "$novo" ] || { echo "smoke: fachada ou nfse-novo não estão de pé" >&2; exit 1; }

curl_em() {
    local container="$1"
    shift
    docker run --rm --network "container:${container}" -v "$PWD/tools/exemplos:/exemplos:ro" "$CURL_IMAGE" \
        -sS --max-time 60 "$@"
}

soap() {
    curl_em "$fachada" -X POST "$1" \
        -H 'Content-Type: text/xml; charset=utf-8' \
        -H 'SOAPAction: "http://notacarioca.rio.gov.br/ConsultarNfse"' \
        --data-binary "@/exemplos/${EXEMPLO}" -w '\n%{http_code}'
}

echo "smoke: /health/ready da fachada e do serviço novo"
curl_em "$fachada" -f http://127.0.0.1:8080/health/ready && echo
curl_em "$novo" -f http://127.0.0.1:8080/health/ready && echo

echo "smoke: ${EXEMPLO} pela fachada e direto no legado"
pela_fachada="$(soap http://127.0.0.1:8080/NfseWS.asmx)"
direto="$(soap http://webservices:8080/NfseWS.asmx)"

status="${pela_fachada##*$'\n'}"
if [ "$status" != "200" ] || ! grep -q "CompNfse" <<<"$pela_fachada"; then
    echo "smoke: resposta inesperada da fachada (HTTP ${status}):" >&2
    head -c 2000 <<<"$pela_fachada" >&2
    exit 1
fi
if [ "$pela_fachada" != "$direto" ]; then
    echo "smoke: a fachada alterou a resposta do legado" >&2
    diff <(echo "$direto") <(echo "$pela_fachada") | head -40 >&2 || true
    exit 1
fi
echo "smoke: ok (${#pela_fachada} bytes, iguais)"
