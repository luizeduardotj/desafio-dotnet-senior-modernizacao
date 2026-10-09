set shell := ["bash", "-cu"]

default:
    @just --list

# sobe Oracle (com carga) e o host dos webservices
up:
    docker compose up -d --build

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
