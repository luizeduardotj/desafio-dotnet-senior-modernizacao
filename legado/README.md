# Nota Carioca - código legado

Solução .NET (C# 7.3 / VB.NET) multi-target `net48;net8.0`, exceto o host `NotaCarioca.WebServices` (somente `net8.0`).

| Projeto | Descrição |
|---------|-----------|
| `NotaCarioca.Dominio` | VB.NET: cálculo de ISS, validação de CPF/CNPJ, competência, códigos de retorno |
| `NotaCarioca.Dados` | C#: acesso ao Oracle (ODP.NET gerenciado) |
| `NotaCarioca.Servicos` | C#: orquestração, leitura/geração de XML |
| `NotaCarioca.WebServices` | host ASP.NET Core que emula o endpoint ASMX (`/NfseWS.asmx`) |
| `tests/NotaCarioca.Testes` | testes unitários (MSTest) |

## Compilar e testar

```bash
dotnet build legado/NotaCarioca.sln
dotnet test legado/tests/NotaCarioca.Testes -f net8.0
```

O target `net48` compila em Linux usando `Microsoft.NETFramework.ReferenceAssemblies`; os testes rodam em `net8.0`.

## Executar

Na raiz do repositório:

```bash
docker compose up -d --build   # Oracle + carga de dados + host dos webservices em 127.0.0.1:8080
curl http://127.0.0.1:8080/health
```

A string de conexão é lida da variável de ambiente `NFSE_CONNECTION_STRING`.
