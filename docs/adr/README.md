# Architecture Decision Records

Formato: [MADR](https://adr.github.io/madr/) adaptado, em português. Toda ADR segue o mesmo esqueleto:

**Contexto → Decisão → Alternativas consideradas → Consequências → Reversão → Como verificar**

As duas últimas seções são obrigatórias. Uma decisão sem caminho de volta ou sem teste/métrica que a comprove não é aceita. Quando a decisão deixa algo em aberto, a ADR tem também **Fora deste ADR**, dizendo o que cada pendência bloqueia.

## Origem dos números

Não temos dados de produção do Nota Carioca. Para não confundir hipótese com medição, todo número de limite, prazo ou meta traz a origem:

| Marca | Significa | Exemplo |
| --- | --- | --- |
| **[E]** | exigido pelo enunciado ou pelo manual de integração | 50 notas por página, 31 dias |
| **[L]** | comportamento do legado, lido no código | `ROWNUM`, `SUBSTR(...,255)` |
| **[H]** | hipótese do time; o texto diz onde será calibrada (M0, teste de carga, drill) | orçamento de 10 ms da fachada |
| **[M]** | medido (no ambiente local, no teste de carga ou em produção), com data e fonte | — ainda nenhum |

## Nível de verificação das premissas

Até aqui, **todas as premissas vêm de leitura estática** do código e do PL/SQL; nada foi executado contra o Oracle. As ADRs 0002, 0003, 0005 e 0011 listam suas premissas com o nível atual (**lida no código**, **confirmada por teste**, **refutada**) e o teste que a confirma. Uma premissa só sobe de nível com teste no CI.

## Índice

| ADR | Decisão | Status |
| --- | --- | --- |
| [0001](0001-estrategia-strangler-fig-e-ordem-de-extracao.md) | Strangler fig por operação; ordem `ConsultarNfse` → `ConsultarNfseServicoTomado` → `CancelarNfse` | Aceito |
| [0002](0002-propriedade-dos-dados-na-transicao.md) | Legado é dono dos dados; novo lê o mesmo Oracle por views em schema próprio; read model como evolução | Aceito |
| [0003](0003-regras-de-cancelamento-em-plsql.md) | PL/SQL encapsulado como autoridade (wrapper com lock); regra espelhada no domínio como função pura | Aceito |
| [0004](0004-comportamentos-legados-nao-documentados.md) | Catálogo de comportamentos legados: preservar no SOAP, corrigir na REST, decisões de segurança | Aceito (vivo) |
| [0005](0005-correcao-do-trigger-de-recalculo-da-guia.md) | Trocar o trigger autônomo por compound trigger para a guia refletir o cancelamento | **Proposto** |
| [0006](0006-camada-anticorrupcao.md) | Camada anticorrupção como adaptador SOAP do serviço novo, compatibilidade isolada e testada | Aceito |
| [0007](0007-arquitetura-do-servico-novo.md) | .NET 10, clean architecture, CQRS pragmático, Dapper; sem MediatR/EF | Aceito |
| [0008](0008-fachada-yarp-roteamento-e-flags.md) | Fachada YARP north-south, OpenFeature/flagd, kill switch, sem service mesh | Aceito |
| [0009](0009-sombra-escrita-unica-e-idempotencia.md) | Sombra assíncrona; cancelamento avaliado sem escrita (usuário só leitura) sobre estado reconstruído; escrita única; idempotência natural | Aceito |
| [0010](0010-contrato-da-api-rest-v1.md) | REST `/api/v1`, cursor, ProblemDetails, JWT | Aceito |
| [0011](0011-paridade-golden-master.md) | Golden master com `FIXED_DATE` e containers isolados; contrato via OpenAPI/oasdiff | Aceito |
| [0012](0012-observabilidade-seguranca-e-lgpd.md) | OpenTelemetry, métricas que decidem fase, `otel-lgtm`; dados pessoais fora da telemetria | Aceito |

## Mapa das decisões

```
0001 estratégia ──┬─► 0008 fachada ──► 0009 sombra / escrita única
                  │
                  ├─► 0002 dados ──► 0003 PL/SQL ──► 0005 trigger (proposto)
                  │
                  └─► 0007 serviço novo ──┬─► 0006 ACL (SOAP) ◄── 0004 catálogo
                                          └─► 0010 REST
0011 paridade e 0012 observabilidade verificam todas as anteriores
```

## Escopo da entrega: MVP e fase 2

"Profundidade vale mais do que cobertura." As ADRs descrevem o destino completo; a entrega prioriza o que prova o strangler de ponta a ponta.

| MVP (entregue e testado) | Fase 2 (decidido e documentado, implementação posterior) |
| --- | --- |
| Fachada YARP com roteamento por operação, flags flagd, kill switch (flag + endpoint admin) | Sombra invertida em modo `novo` |
| Sombra de leitura com comparação em bytes e reexecução limitada | Payload cifrado para reprodução de divergências (MVP: pseudonimização + seed) |
| ACL SOAP + REST v1 para `ConsultarNfse` e `ConsultarNfseServicoTomado` | Read model próprio com CDC (ADR-0002) |
| `CancelarNfse` via wrapper com lock, dual-run e sombra por reconstrução | Correção do trigger (ADR-0005, depende de aprovação) |
| Golden master por ID do catálogo, contrato OpenAPI + oasdiff, integração com Oracle real | Testes de propriedade além de período e cursor |
| OpenTelemetry + `otel-lgtm` com os 3 dashboards | Rate limiting no SOAP (após medição) |
| Compose, manifests Kubernetes, CI | Separar REST e SOAP em deployments distintos |

## Como propor uma nova ADR

1. Copie o esqueleto de uma ADR existente com o próximo número.
2. Abra com status **Proposto**; mude para **Aceito** no merge.
3. Para substituir uma decisão, crie uma ADR nova e marque a antiga como **Substituída por NNNN**. ADR aceita não é reescrita.
