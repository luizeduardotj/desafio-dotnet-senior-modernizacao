# ADR-0007 — Arquitetura do serviço novo (clean architecture e CQRS pragmático)

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0002](0002-propriedade-dos-dados-na-transicao.md), [0003](0003-regras-de-cancelamento-em-plsql.md), [0006](0006-camada-anticorrupcao.md), [0010](0010-contrato-da-api-rest-v1.md)

## Contexto

- Requisito: .NET 8+, arquitetura limpa e CQRS **onde fizer sentido**.
- O suporte do .NET 8 (LTS) termina em novembro de 2026; o .NET 10 é o LTS corrente, com suporte até novembro de 2028.
- O serviço tem duas entradas (REST para app/portal, SOAP para ERPs) e duas naturezas de operação muito diferentes:
  - **consultas**: leitura paginada em views, sem regra de negócio além de validação de parâmetros;
  - **cancelamento**: regra fiscal, autoridade no PL/SQL ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md)).

## Decisão

### Plataforma

**.NET 10 (LTS)**, C# atual, `Nullable` habilitado, `TreatWarningsAsErrors` e analyzers no build. Escolher .NET 8 hoje significaria entregar um serviço novo já fora de suporte no mês seguinte.

### Projetos

```
src/
  Nfse.Dominio             entidades e objetos de valor: Documento (CPF/CNPJ, preserva zeros),
                           Competencia, PeriodoDeEmissao (inclusivo, ≤ 31 dias), SituacaoNota,
                           PoliticaDeCancelamento (função pura); erros de domínio tipados
  Nfse.Aplicacao           casos de uso + portas
                             Queries:  ConsultarNotasEmitidas, ConsultarNotasTomadas, ObterNota
                             Command:  CancelarNota
                             Portas:   ILeituraDeNotas, IGatewayDeCancelamento, IContribuintes
  Nfse.Infra.Oracle        adaptadores das portas: views + wrapper PL/SQL (ODP.NET async)
  Nfse.Adaptadores.Rest    Minimal APIs, ProblemDetails, OpenAPI (ADR-0010)
  Nfse.Adaptadores.Soap    camada anticorrupção (ADR-0006)
  Nfse.Host                composição, configuração, OpenTelemetry, health checks
tests/
  Nfse.Dominio.Testes, Nfse.Aplicacao.Testes, Nfse.Arquitetura.Testes,
  Nfse.Integracao.Testes (Oracle real), Nfse.Contrato.Testes, Nfse.Paridade.Testes
```

Dependências apontam para dentro: `Dominio` ← `Aplicacao` ← adaptadores ← `Host`.

### CQRS onde faz sentido

- **Lado de leitura:** query handlers chamam `ILeituraDeNotas`, que devolve **modelos de leitura** (projeções) direto das views, com SQL explícito e paginação por keyset. Não há agregado, nem repositório genérico, nem mapeamento de entidade para leitura. O adaptador SOAP usa uma porta própria (`ILeituraCompativelSoap`) com a paginação do legado, para que offset e filtros herdados não entrem na aplicação ([ADR-0006](0006-camada-anticorrupcao.md)).
- **Lado de escrita:** `CancelarNota` carrega o snapshot necessário, avalia `PoliticaDeCancelamento` (dual-run) e chama `IGatewayDeCancelamento` (wrapper PL/SQL). Uma transação, um commit.
- Leitura e escrita compartilham o mesmo banco ([ADR-0002](0002-propriedade-dos-dados-na-transicao.md)); a separação é de **modelo e de código**, não de armazenamento. Um read model próprio pode ser plugado atrás de `ILeituraDeNotas` sem mudar a aplicação.

### Acesso a dados

**ODP.NET (`Oracle.ManagedDataAccess.Core`) com Dapper**, 100% assíncrono, `CancellationToken` em toda a cadeia, binds tipados (documento sempre `Varchar2`), `CommandTimeout` explícito e pool com máximo configurado. Dois pools: `NFSE_MODERNO_APP` (REST e SOAP) e `NFSE_MODERNO_RO` (avaliação de sombra), conforme [ADR-0002](0002-propriedade-dos-dados-na-transicao.md).

O **orçamento de tempo do cancelamento** fica abaixo do timeout da fachada (10 s, [ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)): espera de lock 5 s + `CommandTimeout` 3 s + commit. Assim o serviço novo nunca commita depois que a fachada já desistiu.

### Hospedagem

**Um processo** com as duas entradas (REST e SOAP). Elas compartilham o mesmo caminho de leitura e o mesmo pool; separar em dois deployments só se escalas ou ciclos de deploy divergirem (o desenho por projetos permite isso sem mudança de código).

### O que deliberadamente **não** usamos

| Não usamos | Por quê |
| --- | --- |
| MediatR / mediator | três casos de uso; handlers injetados diretamente são mais simples de ler e depurar |
| EF Core | consultas são projeções em views e a escrita é uma chamada de procedure; o ORM não agrega e o provider Oracle adiciona peso e risco |
| Repositório genérico / Unit of Work próprio | uma transação por command; abstração sem segundo uso |
| Event sourcing / mensageria | não há evento de domínio consumido por ninguém nesta fase |
| `DateTime.Now` no domínio | o instante vem do banco ([ADR-0003](0003-regras-de-cancelamento-em-plsql.md)) |

## Alternativas consideradas

- **.NET 8.** Rejeitada: fim de suporte em novembro de 2026.
- **Vertical slices sem camadas.** Considerada; rejeitada porque o requisito pede arquitetura limpa e porque a separação domínio/adaptadores é exatamente o que protege o domínio do legado.
- **Dois serviços (REST e SOAP).** Rejeitada por ora (ver Hospedagem).

## Consequências

- **Positivas:** domínio pequeno e testável sem banco; leitura simples e performática; fronteiras verificadas por teste.
- **Negativas:** SQL escrito à mão exige testes de integração contra Oracle real (já previstos).
- **Riscos:** o legado e o CI atual usam .NET 8; o pipeline do serviço novo instala o SDK 10 ([ADR-0011](0011-paridade-golden-master.md)).

## Reversão

Decisões internas ao serviço; a reversão é refatoração, sem impacto no contrato externo.

## Como verificar

- `Nfse.Arquitetura.Testes` (NetArchTest): `Dominio` e `Aplicacao` não referenciam `System.Xml`, `Oracle.*`, `Microsoft.AspNetCore.*` nem os adaptadores.
- Build falha com warning de nulabilidade ou de async sem `CancellationToken`.
