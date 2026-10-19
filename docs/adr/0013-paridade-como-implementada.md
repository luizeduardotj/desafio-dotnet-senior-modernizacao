# ADR-0013 — Paridade e contrato: o que mudou na implementação

- **Status:** Aceito. Substitui em parte a [ADR-0011](0011-paridade-golden-master.md): o isolamento por alvo, a aprovação com Verify, o oasdiff e o desenho do pipeline.
- **Data:** 2026-10-19
- **Relacionados:** [0010](0010-contrato-da-api-rest-v1.md), [0011](0011-paridade-golden-master.md), [0004](0004-comportamentos-legados-nao-documentados.md)

## Contexto

A ADR-0011 foi escrita antes do código. Na implementação, quatro pontos saíram diferentes, por custo ou porque uma alternativa mais simples dava a mesma garantia. Uma revisão final da entrega encontrou o texto antigo ainda descrevendo jobs e ferramentas que não existem. Esta ADR registra o que vale, e a 0011 continua valendo no resto: relógio congelado com `FIXED_DATE`, corpus por ID do catálogo, comparação em bytes sem normalização e divergências só por exceção explicada.

## Decisão

### 1. Um Oracle por job, com ordem de execução, em vez de um container por alvo

Cada Oracle do compose leva ~2–3 GB e minutos para carregar o seed. Subir um por alvo dobraria o tempo do CI sem ganho: as leituras não escrevem. O job `compose` sobe **um** Oracle do zero e roda nesta ordem:

1. paridade das leituras e da sombra do cancelamento (sem escrita);
2. paridade do cancelamento com `FIXED_DATE`, num step próprio, que confere a linha de base antes e devolve `FIXED_DATE = NONE` ao sair;
3. as leituras de novo, para provar que o cancelamento não afetou o que elas comparam.

Antes de tudo, `tests/paridade/recapturar-leituras-legado.sh` recaptura as leituras direto no legado e falha se `git diff` em `tests/paridade/casos` não estiver vazio: o golden master ainda descreve o legado, não só o que o novo reproduz. Entre as leituras e o `FIXED_DATE` entram as garantias do banco: grants dos usuários do `NFSE_MODERNO` e K08 pelo wrapper, numa nota que nenhum caso do corpus usa.

Localmente, a linha de base é o volume `nfse-oracle-baseline` restaurado sobre o volume do compose ([tests/paridade/README.md](../../tests/paridade/README.md)).

### 2. Corpus em arquivos simples, sem a biblioteca Verify

Cada caso em `tests/paridade/casos/NNN-.../` tem `operacao`, `entrada.xml`, `legado.xml`, `legado.status` e `descricao` (com o ID do catálogo). As respostas do legado são capturadas por `tests/paridade/capturar.sh`. Um `git diff` mostra a mudança, e o Verify não acrescentava nada que justificasse a dependência.

### 3. Contrato REST: snapshot do OpenAPI, não oasdiff

`tests/Nfse.Host.Testes/Contrato/ContratoOpenApiTests.cs` compara o `/openapi/v1.json` gerado com o arquivo versionado `openapi-v1.json` e falha em **qualquer** diferença, quebra ou não. A mudança intencional regenera o arquivo (`NFSE_ATUALIZAR_CONTRATO=1`), e o diff do snapshot entra no PR, onde o revisor decide se quebra a v1. O oasdiff classificaria a quebra sozinho, mas exige ferramenta externa e uma `main` publicada para comparar. Com uma v1 e um time, o snapshot dá a mesma proteção. As respostas reais da REST (sucesso e ProblemDetails) são validadas contra esse mesmo snapshot com NJsonSchema; foi essa validação que achou o 401 fora do spec e um `limite` não inteiro que não virava `parametros-invalidos`. O contrato SOAP é testado contra a forma dos exemplos de `tools/exemplos` (Categoria `Contrato`).

### 4. Pipeline real

| Job | Conteúdo | Banco |
| --- | --- | --- |
| `legado` | build `net48;net8.0` e testes do legado | — |
| `moderno` | build com analyzers (warnings como erro), unitários, arquitetura, fachada, host; step próprio para os testes de contrato | — |
| `compose` | build das imagens, `compose up --wait`, smoke, recaptura das leituras pelo legado, paridade das leituras e da sombra com os grants, K08 pelo wrapper, cancelamento com `FIXED_DATE`, leituras de novo | Oracle real do compose |
| `manifests` | `kubeconform -strict` dos overlays `local` e `producao`, com os CRDs pelo catálogo datreeio | — |

## O que a ADR-0011 previa e ainda não existe

A validação das respostas REST, a recaptura pelo legado e os testes de grants e de K08 entraram depois da primeira versão desta ADR, no mesmo dia. Falta:

| Previsto na 0011 | Situação |
| --- | --- |
| Snapshot de `NFSE`/`GUIAS` depois de cada cancelamento | não implementado; K08 tem teste automático, K07 só o spike |
| Teste de guarda contra `DateTime.Now` no serviço novo | não implementado; a regra está no CLAUDE.md e na revisão |
| "ID do catálogo sem caso falha o build" | não implementado; C13 e C15 não têm caso por falta de massa no seed |
| Testes de propriedade (período, cursor, documentos) | não implementados; período e cursor têm testes de exemplo nos limites |

Cada item está em "O que eu faria com mais tempo" no [README](../../README.md).

## Alternativas consideradas

- **Manter um container por alvo:** isolamento mais forte, ao custo de dobrar o tempo e a memória do CI. A ordem fixa mais as leituras repetidas no fim dão a mesma garantia para as operações extraídas, que não emitem notas.
- **oasdiff agora:** fica para quando houver mais de uma versão da API publicada ou mais de um time consumindo o spec.

## Consequências

- O CI cabe num runner padrão. O custo é a ordem: um teste de leitura que passasse a escrever quebraria o passo 3, e é exatamente isso que o passo 3 detecta.
- O snapshot obriga a regenerar o arquivo em toda mudança da API, inclusive nas compatíveis. Esse atrito é proposital: nenhuma mudança de contrato passa sem aparecer no diff.

## Reversão

Ferramental de teste, sem efeito em produção. Voltar ao desenho da 0011 é acrescentar jobs, sem mudar código.

## Como verificar

- `.github/workflows/ci.yml` tem os quatro jobs da tabela, e o último run do fork passou nos quatro.
- `dotnet test tests/Nfse.Host.Testes --filter Categoria=Contrato` falha quando um endpoint muda sem regenerar o snapshot.
