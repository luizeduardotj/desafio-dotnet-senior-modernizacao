# CLAUDE.md — regras para agentes neste repositório

Desafio técnico de modernização do Nota Carioca (NFS-e do Rio): extrair `ConsultarNfse`, `ConsultarNfseServicoTomado` e `CancelarNfse` do monolito por strangler fig. O enunciado está no [README.md](README.md); as decisões estão em [docs/adr/](docs/adr/README.md) e os critérios operacionais em [docs/plano-de-migracao.md](docs/plano-de-migracao.md). **Leia a ADR da área antes de mexer nela.** Código que contradiz uma ADR aceita exige uma ADR nova que a substitua.

## Mapa

| Pasta | O que é | Regras |
| --- | --- | --- |
| `legado/` | réplica do monolito (C# 7.3, VB.NET, `net48;net8.0`) | **produção**: só mudança mínima, justificada em ADR, compatível com .NET Framework 4.8 / C# 7.3 |
| `db/` | schema, PL/SQL e seeds do legado (Oracle 19c em produção, 23ai Free local) | **não alterar tabelas**; objeto novo só no schema `NFSE_MODERNO` ([ADR-0002](docs/adr/0002-propriedade-dos-dados-na-transicao.md)) |
| `src/` | serviço novo e fachada (.NET 10) | [ADR-0007](docs/adr/0007-arquitetura-do-servico-novo.md), [ADR-0008](docs/adr/0008-fachada-yarp-roteamento-e-flags.md) |
| `tests/` | testes do código novo e de paridade | [ADR-0011](docs/adr/0011-paridade-golden-master.md) |
| `tools/` | gerador de lotes e exemplos SOAP do legado | não alterar os exemplos: são evidência de comportamento |
| `revisao/` | PR-212 a revisar | `REVIEW-PR-212.md` é escrito pelo autor humano |

## Regras de arquitetura (verificadas por teste)

- `Nfse.Dominio` e `Nfse.Aplicacao` não referenciam XML, SOAP, Oracle nem ASP.NET (`tests/Nfse.Arquitetura.Testes`).
- O domínio não conhece códigos `E0xx`, `N/C/S`, `IssRetido 1/2` nem nenhuma convenção do legado: isso é da camada anticorrupção ([ADR-0006](docs/adr/0006-camada-anticorrupcao.md)).
- Todo desvio intencional de compatibilidade com o legado cita o ID do catálogo ([ADR-0004](docs/adr/0004-comportamentos-legados-nao-documentados.md)) no nome da regra **e** no nome do teste (ex.: `C01_DataFinalExclusiva`).
- Prazo e "agora" vêm do `SYSDATE` do banco; nunca `DateTime.Now`/`UtcNow` no serviço novo ([ADR-0003](docs/adr/0003-regras-de-cancelamento-em-plsql.md)).
- Escrita no Oracle só pelo wrapper `NFSE_MODERNO.PKG_CANCELAMENTO_API`; a sombra usa o usuário só leitura.
- Nenhum CPF/CNPJ/IM em claro em log, span, métrica ou URL ([ADR-0012](docs/adr/0012-observabilidade-seguranca-e-lgpd.md)).
- Async de ponta a ponta com `CancellationToken`; sem `.Result`, `.Wait()` ou `async void`.

## Commits

- Conventional Commits em português: `feat(fachada): ...`, `test(paridade): ...`, `docs(adr): ...`.
- Commits pequenos que contam a evolução; cada commit compila e passa nos testes.
- **Sem** `Co-Authored-By` de IA e sem rodapé "Generated with Claude Code".
- Testes de caracterização do legado entram **antes** do código novo que os satisfaz.
- Não há remote configurado de propósito: **não** adicionar remote nem fazer push.

## Comandos

```bash
dotnet build legado/NotaCarioca.sln
dotnet test legado/tests/NotaCarioca.Testes -f net8.0
dotnet build Nfse.Moderno.slnx
dotnet test Nfse.Moderno.slnx
```

`docker compose up` sobe Oracle + legado (a primeira carga leva minutos e ~2–3 GB de RAM). Só suba o ambiente quando a tarefa pedir.
