#!/usr/bin/env bash
# Captura as respostas de um alvo (legado ou novo) para cada caso do corpus de paridade (ADR-0011).
# uso: tests/paridade/capturar.sh <alvo> [url]     ex.: tests/paridade/capturar.sh legado http://127.0.0.1:8080/NfseWS.asmx
# Para cada casos/<id>/ grava <alvo>.xml (corpo HTTP) e <alvo>.status. Os casos 2xx (cancelamento) mudam o estado do
# banco: rode sempre sobre um banco recém-restaurado e na ordem dos nomes.
set -euo pipefail
cd "$(dirname "$0")"
ALVO="${1:?alvo: legado ou novo}"
URL="${2:-http://127.0.0.1:8080/NfseWS.asmx}"

escapar() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

for caso in casos/*/; do
  op="$(tr -d '\n' < "$caso/operacao")"
  entrada="$(escapar < "$caso/entrada.xml" | tr -d '\n')"
  envelope="<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><${op}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>${entrada}</inputXML></${op}Request></soap:Body></soap:Envelope>"
  status="$(printf '%s' "$envelope" | curl -sS --max-time 120 -o "$caso/$ALVO.xml" -w '%{http_code}' -X POST "$URL" \
    -H 'Content-Type: text/xml; charset=utf-8' \
    -H "SOAPAction: \"http://notacarioca.rio.gov.br/${op}\"" \
    --data-binary @-)"
  echo "$status" > "$caso/$ALVO.status"
  printf '%-55s %s\n' "$(basename "$caso")" "$status"
done
