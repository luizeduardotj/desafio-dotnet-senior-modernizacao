FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY legado/ ./legado/
RUN dotnet publish legado/src/NotaCarioca.WebServices/NotaCarioca.WebServices.csproj -c Release -o /app/publish

# Auto-instrumentação OpenTelemetry .NET (ADR-0012): só empacotamento, nenhuma linha do legado muda. A imagem leva o
# agente dormente; quem liga é o ambiente (CORECLR_* e DOTNET_STARTUP_HOOKS, no docker-compose.yml). Versão e
# checksums fixados: são os digests publicados no release. O link `native` dá ao profiler o mesmo caminho em x64 e arm64.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS otel
ARG TARGETARCH
ARG OTEL_DOTNET_AUTO_VERSION=1.17.0
ARG OTEL_DOTNET_AUTO_SHA256_X64=cc56878f0155e28b6554924fbd420fcb8bec7108cd9d41836dc530764ac923f8
ARG OTEL_DOTNET_AUTO_SHA256_ARM64=d7dc0d8f6562eb12108eaf896c277e536133259d49545e3e2545be3ea7a14144
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
      amd64) arq=x64; sha="$OTEL_DOTNET_AUTO_SHA256_X64" ;; \
      arm64) arq=arm64; sha="$OTEL_DOTNET_AUTO_SHA256_ARM64" ;; \
      *) echo "arquitetura sem agente: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    zip="opentelemetry-dotnet-instrumentation-linux-glibc-$arq.zip"; \
    curl -fsSLo "/tmp/$zip" "https://github.com/open-telemetry/opentelemetry-dotnet-instrumentation/releases/download/v$OTEL_DOTNET_AUTO_VERSION/$zip"; \
    echo "$sha  /tmp/$zip" | sha256sum -c -; \
    apt-get update -qq && apt-get install -y -qq --no-install-recommends unzip > /dev/null; \
    unzip -q "/tmp/$zip" -d /otel-dotnet-auto; \
    rm -f /otel-dotnet-auto/linux-*/*.debug; \
    ln -s "linux-$arq" /otel-dotnet-auto/native

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=otel /otel-dotnet-auto /otel-dotnet-auto
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "NotaCarioca.WebServices.dll"]
