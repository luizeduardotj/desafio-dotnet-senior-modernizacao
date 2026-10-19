set shell := ["bash", "-cu"]

default:
    @just --list

# sobe tudo do zero: Oracle (com carga e NFSE_MODERNO), legado, fachada, serviço novo e otel-lgtm
up:
    docker compose up -d --build --wait

# health e ConsultarNfse igual pela fachada e direto no legado
smoke:
    tools/smoke-compose.sh

down:
    docker compose down -v

logs:
    docker compose logs -f

# compila a solucao (net48 e net8.0)
build:
    dotnet build legado/NotaCarioca.sln

# testes unitarios (net8.0)
test:
    dotnet test legado/tests/NotaCarioca.Testes -f net8.0

# sqlplus no container do Oracle
sql:
    docker compose exec oracle sqlplus NFSE_OWNER/nfse_dev_123@//localhost:1521/FREEPDB1

# gera um lote com QTD RPS e envia
lote QTD="100":
    mkdir -p tools/saida
    dotnet run --project tools/GeradorLote -- {{QTD}} --saida tools/saida/lote.xml
    tools/enviar-lote.sh tools/saida/lote.xml
