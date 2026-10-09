#!/usr/bin/env bash
# Envolve um EnviarLoteRpsEnvio em um envelope SOAP e envia para RecepcionarLoteRps.
# uso: ./enviar-lote.sh lote.xml [url]
set -euo pipefail

ARQUIVO="${1:?uso: $0 lote.xml [url]}"
URL="${2:-http://127.0.0.1:8080/NfseWS.asmx}"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

{
  printf '<?xml version="1.0" encoding="utf-8"?>'
  printf '<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>'
  printf '<RecepcionarLoteRpsRequest xmlns="http://notacarioca.rio.gov.br/"><inputXML>'
  # remove a declaracao XML e escapa o conteudo
  sed -e '1{/^<?xml/d}' "$ARQUIVO" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g'
  printf '</inputXML></RecepcionarLoteRpsRequest></soap:Body></soap:Envelope>'
} > "$TMP"

curl -sS --max-time 600 -X POST "$URL" \
  -H 'Content-Type: text/xml; charset=utf-8' \
  -H 'SOAPAction: "http://notacarioca.rio.gov.br/RecepcionarLoteRps"' \
  --data-binary @"$TMP"
echo
