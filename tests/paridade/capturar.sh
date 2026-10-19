#!/usr/bin/env bash
# Captura as respostas de um alvo (legado ou novo) para cada caso do corpus de paridade (ADR-0011).
# uso: tests/paridade/capturar.sh <alvo> [url]     ex.: tests/paridade/capturar.sh legado http://127.0.0.1:8080/NfseWS.asmx
# Para cada casos/<id>/ grava <alvo>.xml (corpo HTTP) e <alvo>.status. Os casos 2xx (cancelamento) mudam o estado do
# banco: rode sempre sobre um banco recém-restaurado e na ordem dos nomes.
# CASOS limita os casos por prefixo (ex.: CASOS='0 1' só as leituras). CURL troca o curl do host por outro comando
# compatível, ex.: um container na rede do compose para falar direto com o legado (recapturar-leituras-legado.sh).
set -euo pipefail
cd "$(dirname "$0")"
ALVO="${1:?alvo: legado ou novo}"
URL="${2:-http://127.0.0.1:8080/NfseWS.asmx}"
CASOS="${CASOS:-}"
# shellcheck disable=SC2206
CURL=(${CURL:-curl})

escapar() { sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'; }

for caso in casos/*/; do
  if [ -n "$CASOS" ]; then
    nome="$(basename "$caso")"
    incluir=0
    for prefixo in $CASOS; do
      case "$nome" in "$prefixo"*) incluir=1 ;; esac
    done
    [ "$incluir" = 1 ] || continue
  fi
  op="$(tr -d '\n' < "$caso/operacao")"
  entrada="$(escapar < "$caso/entrada.xml" | tr -d '\n')"
  envelope="<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><${op}Request xmlns=\"http://notacarioca.rio.gov.br/\"><inputXML>${entrada}</inputXML></${op}Request></soap:Body></soap:Envelope>"
  # Corpo e status pela saída padrão (o status na última linha), para funcionar também com o curl num container.
  resposta="$(printf '%s' "$envelope" | "${CURL[@]}" -sS --max-time 120 -w '\n%{http_code}' -X POST "$URL" \
    -H 'Content-Type: text/xml; charset=utf-8' \
    -H "SOAPAction: \"http://notacarioca.rio.gov.br/${op}\"" \
    --data-binary @-)"
  status="${resposta##*$'\n'}"
  printf '%s' "${resposta%$'\n'*}" > "$caso/$ALVO.xml"
  echo "$status" > "$caso/$ALVO.status"
  printf '%-55s %s\n' "$(basename "$caso")" "$status"
done
