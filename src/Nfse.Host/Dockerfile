# Serviço novo (ADR-0007). Contexto de build: raiz do repositório.
#   docker build -f src/Nfse.Host/Dockerfile -t nfse-novo:0.1.0 .
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json .editorconfig ./
# O Host referencia quase todos os projetos de src/: copiar a pasta inteira é mais simples que listar os .csproj.
COPY src/ src/
RUN dotnet publish src/Nfse.Host/Nfse.Host.csproj -c Release -o /app

FROM ${RUNTIME_IMAGE}
# tzdata: o fuso America/Sao_Paulo é aplicado explicitamente no código, nunca o do container (ADR-0012).
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Nfse.Host.dll"]
