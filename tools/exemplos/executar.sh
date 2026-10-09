#!/usr/bin/env bash
# Executa os exemplos nesta pasta contra o host local e grava NN-nome.resposta.xml ao lado de cada pedido.
# uso: ./executar.sh [url] [arquivo.xml ...]
set -uo pipefail
cd "$(dirname "$0")"
URL="${1:-http://127.0.0.1:8080/NfseWS.asmx}"
shift || true
ARQUIVOS=("$@")
if [ ${#ARQUIVOS[@]} -eq 0 ]; then ARQUIVOS=( [0-9]*.xml ); fi

enviar() {
  local arq="$1"
  local op
  op="$(grep -o '<[A-Za-z]*Request' "$arq" | head -1 | sed 's/<//; s/Request$//')"
  curl -sS --max-time 120 -X POST "$URL" \
    -H 'Content-Type: text/xml; charset=utf-8' \
    -H "SOAPAction: \"http://notacarioca.rio.gov.br/${op}\"" \
    --data-binary @"$arq"
}

for arq in "${ARQUIVOS[@]}"; do
  case "$arq" in *.resposta.xml) continue;; esac
  saida="${arq%.xml}.resposta.xml"
  echo ">> $arq"
  enviar "$arq" > "$saida"
  echo "   -> $saida"
  if [ "$arq" = "01-recepcionar-lote-rps.xml" ]; then
    protocolo="$(grep -o 'NC[0-9]\{14\}' "$saida" | head -1)"
    if [ -n "$protocolo" ]; then
      sleep 6
      sed -e "s/NC20261000000001/${protocolo}/" -e "s/10000001/10000025/" 03-consultar-lote-rps.xml > /tmp/consulta-recem-enviado.xml
      enviar /tmp/consulta-recem-enviado.xml > 01b-consultar-lote-rps-recem-enviado.resposta.xml
      echo "   -> 01b-consultar-lote-rps-recem-enviado.resposta.xml"
    fi
  fi
done
