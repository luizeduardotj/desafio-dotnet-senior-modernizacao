# ADR-0001 — Estratégia strangler fig e ordem de extração

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0002](0002-propriedade-dos-dados-na-transicao.md), [0003](0003-regras-de-cancelamento-em-plsql.md), [0008](0008-fachada-yarp-roteamento-e-flags.md), [plano de migração](../plano-de-migracao.md)

## Contexto

O Nota Carioca é um monolito .NET Framework 4.8 + Oracle 19c em produção, com milhares de ERPs integrados via SOAP (`/NfseWS.asmx`) que não podem mudar. Precisamos extrair `ConsultarNfse`, `ConsultarNfseServicoTomado` e `CancelarNfse` sem big bang, sem janela de manutenção e com caminho de volta a qualquer momento. `RecepcionarLoteRps`, `ConsultarSituacaoLoteRps` e `ConsultarLoteRps` ficam no legado.

Fatos que restringem a estratégia:

- O legado continua emitindo notas (lote) e mantendo guias durante toda a transição: os dados são compartilhados.
- O despacho SOAP é feito **só pelo header `SOAPAction`** (`legado/src/NotaCarioca.WebServices/Program.cs:59-60`), o que permite rotear por operação sem entender o corpo.
- As três operações não têm o mesmo risco:

| Operação | Tipo | Depende de PL/SQL | Efeito colateral | Sombra segura? | Observação |
| --- | --- | --- | --- | --- | --- |
| `ConsultarNfse` | leitura | não | nenhum | sim, executar nos dois | maior volume esperado; prestador identificado por IM |
| `ConsultarNfseServicoTomado` | leitura | não | nenhum | sim, executar nos dois | sem prestador na requisição; full scan forçado (`NfseRepositorio.cs:151`) |
| `CancelarNfse` | **escrita** | sim (`PKG_NFSE`, trigger, `PKG_GUIA`) | recálculo de guia | **não** como execução dupla | regra fiscal; prazo dependente de relógio |

## Decisão

1. **Strangler fig por operação**, com uma fachada na frente de `/NfseWS.asmx` ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)). Operações fora do escopo passam pela fachada sem leitura do corpo e sem alteração de bytes.
2. Cada operação percorre as fases **`legado` → `sombra` → `novo` (canário por prestador e percentual) → `novo` 100% → desligamento no monolito**, com critérios objetivos de avanço e retorno no [plano de migração](../plano-de-migracao.md).
3. **Ordem de extração:**
   1. `ConsultarNfse` — leitura pura, valida de ponta a ponta fachada, sombra, camada anticorrupção, paridade e observabilidade com risco mínimo.
   2. `ConsultarNfseServicoTomado` — reaproveita o mesmo caminho de leitura e a mesma serialização (`CompNfse`); pode entrar em sombra enquanto a anterior está em canário.
   3. `CancelarNfse` — só entra em sombra quando `ConsultarNfse` estiver em `novo` 100% estável, porque a sombra do cancelamento depende do caminho de leitura já provado ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)).
4. **Desligamento no monolito** é definido como: a fachada não envia tráfego da operação ao legado há pelo menos 4 semanas sem acionar rollback; só então o `case` da operação no `switch` do host e os métodos exclusivos (`ConsultaNfseServico.ConsultarNfse`, `ConsultarNfseServicoTomado`, `CancelamentoNfseServico`) são removidos em um release normal do legado. `NfseRepositorio`, `XmlNfseBuilder` e `ConsultaNfseServico` (parte de lote) permanecem, pois são usados por `ConsultarLoteRps`. O desligamento é o **único ponto sem volta** do processo: depois dele, nem o kill switch nem o *break-glass* ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)) devolvem a operação ao legado. Por isso ele exige aprovação explícita e acontece em um release do legado separado e reversível por si só (o código removido volta por um novo release).

## Alternativas consideradas

- **Big bang (reescrever e trocar tudo).** Rejeitada: contraria o requisito e não tem rollback.
- **Começar pelo cancelamento** (maior valor de negócio). Rejeitada: é a única operação em que a sombra não pode simplesmente executar duas vezes; começar por ela empilha o risco maior sobre a infraestrutura menos provada.
- **Extrair o contexto inteiro de NFS-e (incluindo lote).** Rejeitada para esta fase: o lote é o núcleo de escrita (numeração, guia, concorrência) e não é pedido; aumentaria o raio de explosão.
- **Branch by abstraction dentro do monolito.** Rejeitada: exigiria releases do legado .NET 4.8 a cada passo e não oferece kill switch sem deploy.

## Consequências

- **Positivas:** risco crescente e controlado; cada fase produz evidência (divergências, latência, erros) antes da próxima; rollback por flag em segundos.
- **Negativas:** duas implementações convivem por meses; custo de manter a sombra; o legado segue dono dos dados ([ADR-0002](0002-propriedade-dos-dados-na-transicao.md)), então o serviço novo fica acoplado ao schema legado via views.
- **Riscos:** a fachada vira ponto único de falha para todo o SOAP — mitigado em [ADR-0008](0008-fachada-yarp-roteamento-e-flags.md) (fachada mínima, sem estado, com rota de contorno direta para o legado).

## Reversão

Por operação, mudar o modo para `legado` (ou acionar o kill switch global). Nenhuma fase migra dados, então voltar não exige reconciliação. Remover a fachada inteira = apontar o ingress de volta para o legado.

## Como verificar

- Teste de integração: operações fora do escopo atravessam a fachada com corpo e resposta byte a byte idênticos.
- Critérios de avanço por fase e painéis definidos no [plano de migração](../plano-de-migracao.md) e em [ADR-0012](0012-observabilidade-seguranca-e-lgpd.md).
