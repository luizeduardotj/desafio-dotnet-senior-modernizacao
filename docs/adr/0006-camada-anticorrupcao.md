# ADR-0006 — Camada anticorrupção (contrato SOAP do legado)

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0004](0004-comportamentos-legados-nao-documentados.md), [0007](0007-arquitetura-do-servico-novo.md), [0008](0008-fachada-yarp-roteamento-e-flags.md), [0011](0011-paridade-golden-master.md)

## Contexto

Os ERPs falam um contrato SOAP 1.1 peculiar:

- envelope com `<{Op}Request><inputXML>` contendo **XML de negócio escapado como texto**; resposta `<{Op}Response xmlns="http://notacarioca.rio.gov.br/"><outputXML>` também escapada (`Program.cs:98-104`);
- erros de negócio vêm com **HTTP 200** e `ListaMensagemRetorno` (`XmlNfseBuilder.cs:41-55`); erros técnicos como `soap:Fault` HTTP 500;
- o XML de resposta é montado por concatenação de strings (`XmlNfseBuilder.CompNfse`), com ordem de elementos, escape (`SecurityElement.Escape`), formatos de número e data próprios;
- validações em ordem própria de cada operação (C24): `ConsultarNfse` `E001` → `E010` → `E020` → `E030` → consulta → `E040`; `ServicoTomado` sem `E010`; `CancelarNfse` `E001` → `E010` → PL/SQL;
- paginação por número de página com offset (C07), com aritmética `int` sem checagem (C27) e filtro extra `COMPETENCIA <= fim` (C13) dentro do SQL paginado;
- vários comportamentos não documentados que precisam ser preservados ([ADR-0004](0004-comportamentos-legados-nao-documentados.md)).

O domínio novo **não pode conhecer** XML, SOAP, códigos `E0xx` nem convenções como `N/C/S`, `IssRetido 1/2` ou a perda de zeros à esquerda.

## Decisão

1. **A camada anticorrupção é um adaptador de entrada do serviço novo** (projeto `Nfse.Adaptadores.Soap`), exposto em `POST /NfseWS.asmx` com o mesmo algoritmo de despacho por `SOAPAction` do legado. A fachada direciona para ele sem transformar nada.
2. **Fluxo:**

```
envelope SOAP ─► LeitorEnvelope ─► inputXML ─► Tradutor de entrada ─► Query/Command de aplicação
                                                    │ (ordem E001→E010→E020→E030)
resposta SOAP ◄─ EscritorEnvelope ◄─ Serializador legado ◄─ Tradutor de saída ◄─ Resultado / Erro de domínio
                                     (CompNfse byte-compatível)  (erro de domínio → E0xx)
```

3. **Tradução nos dois sentidos, em tabelas explícitas:**
   - erros de domínio → códigos (`NotaNaoEncontrada` → `E050`, `NotaSubstituida` → `E051`, `PrazoExpirado` → `E052`, `GuiaPaga` → `E053`, `PeriodoInvalido` → `E030`, `DocumentoInvalido` → `E020`, `PrestadorNaoEncontrado` → `E010`, `SemResultado` → `E040`), com mensagens e correções copiadas de `CodigosRetorno.vb`;
   - estados (`Normal`/`Cancelada`/`Substituida` ↔ `N`/`C`/`S`) e `IssRetido` (`bool` ↔ `1`/`2`) traduzidos no adaptador de dados e no serializador, respectivamente.
4. **Compatibilidade deliberada isolada:** cada desvio intencional (C01, C04, C05, C10, ...) é uma regra pequena e nomeada no módulo `CompatibilidadeLegado`, com o ID do catálogo no nome e no teste. Exemplos:
   - C01/C02: `DataFinal` exclusiva vira período de domínio `[ini, fim − 1 dia]`; quando `ini == fim`, o adaptador responde `E040` **sem consultar**, porque o período de domínio seria vazio;
   - C04: datas com offset são convertidas explicitamente para `America/Sao_Paulo` antes de truncar (não depende do fuso do pod, que em Kubernetes é UTC).
5. **Leitura própria do adaptador SOAP.** A paginação do SOAP (offset por página, C07/C27) e o filtro `COMPETENCIA <= fim` (C13) não cabem na porta de leitura da aplicação, que usa keyset e período inclusivo. Em vez de vazar esses parâmetros para a aplicação, o adaptador SOAP tem **sua própria porta** `ILeituraCompativelSoap`, implementada em `Nfse.Infra.Oracle` com o SQL compatível com o legado (mesmo `ROWNUM`, mesma ordenação, documento comparado como string, C15). Ela devolve o mesmo modelo de leitura da aplicação. A regra de negócio (validação de documento, período) continua passando pelo domínio; só a forma de paginar e filtrar é do adaptador.
6. **Serialização:** reproduz a do legado (mesma ordem de elementos, `SecurityElement.Escape`, `InvariantCulture`, `0.00`, `0.####`, datas sem offset). A paridade é verificada em bytes nos golden masters ([ADR-0011](0011-paridade-golden-master.md)).
7. **Parsing:** mesmas regras de leitura do legado (C22, C23, C29), mas com DTD proibida (C28).
8. **Erros técnicos:** `soap:Fault` com o mesmo `faultcode` do legado, mas texto genérico + `traceId` (C20).
9. **Fronteira garantida por teste de arquitetura:** o projeto de domínio e o de aplicação não podem referenciar `System.Xml`, o projeto SOAP nem o driver Oracle.

## Alternativas consideradas

- **CoreWCF gerado a partir do WSDL.** Rejeitada: o WSDL só tipa `inputXML`/`outputXML` como string; o contrato real está no XML escapado, montado à mão. O WCF não ajuda nessa camada e reduz o controle byte a byte da resposta.
- **Tradução na fachada.** Rejeitada: a fachada precisa ser mínima, rápida e sem regra de negócio ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)); misturar tornaria o rollback dependente de lógica de negócio.
- **Serviço ACL separado (terceiro processo).** Rejeitada por ora: mais um salto de rede e deploy sem benefício; o isolamento por projeto + teste de arquitetura é suficiente. Pode ser separado depois sem mudar o domínio.
- **Domínio com "modo legado".** Rejeitada: vazamento direto do legado para o domínio.

## Consequências

- **Positivas:** o domínio é testável e evolui sem SOAP; a REST e o SOAP compartilham a mesma aplicação; toda peculiaridade tem endereço e teste.
- **Negativas:** o serializador replica código "feio" de propósito; é preciso disciplina para não "corrigir" algo preservado (protegido pelos golden masters).
- **Riscos:** diferenças sutis de escape/encoding (`charset` ignorado no request, `Encoding.UTF8` fixo) — cobertas por casos de golden master com acentos e caracteres especiais.

## Reversão

A camada só é exercitada quando a fachada direciona a operação para `novo`. Reverter = modo `legado`.

## Como verificar

- Golden masters por operação comparados em bytes (XML canônico só para diagnosticar a diferença).
- Teste de arquitetura (NetArchTest) quebra o build se o domínio depender de XML, SOAP ou Oracle.
- Teste: cada erro de domínio tem tradução para exatamente um código, e cada código usado pelo legado nas três operações tem origem conhecida.
