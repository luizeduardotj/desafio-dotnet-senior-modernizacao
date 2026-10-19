# Fachada YARP (ADR-0008). Contexto de build: raiz do repositório.
#   docker build -f src/Nfse.Fachada/Dockerfile -t nfse-fachada:0.1.0 .
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-alpine3.24
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.13-alpine3.24

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY global.json .editorconfig ./
COPY src/Directory.Build.props src/Directory.Packages.props src/
COPY src/Nfse.Observabilidade/Nfse.Observabilidade.csproj src/Nfse.Observabilidade/
COPY src/Nfse.Fachada/Nfse.Fachada.csproj src/Nfse.Fachada/
RUN dotnet restore src/Nfse.Fachada/Nfse.Fachada.csproj
COPY src/Nfse.Observabilidade/ src/Nfse.Observabilidade/
COPY src/Nfse.Fachada/ src/Nfse.Fachada/
RUN dotnet publish src/Nfse.Fachada/Nfse.Fachada.csproj -c Release --no-restore -o /app

FROM ${RUNTIME_IMAGE}
# tzdata: o fuso America/Sao_Paulo é aplicado explicitamente no código, nunca o do container (ADR-0012).
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app .
# As portas vêm de Fachada__Porta e Admin__Porta (Kestrel configurado no código), não da imagem base.
ENV ASPNETCORE_HTTP_PORTS=
USER $APP_UID
# 8080: plano de dados (SOAP, health); 8081: administração (kill switch nível 2), só na rede interna.
EXPOSE 8080 8081
HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/health/ready || exit 1
ENTRYPOINT ["dotnet", "Nfse.Fachada.dll"]
