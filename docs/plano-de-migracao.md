# Plano de migração — consulta e cancelamento de NFS-e

Este plano diz **quando** cada operação avança de `legado` → `sombra` → `novo` → desligamento no monolito, **o que** monitorar em cada fase e **como voltar**. As decisões de arquitetura estão nas [ADRs](adr/README.md); aqui ficam os critérios operacionais.

Os números seguem a legenda das ADRs: **[E]** enunciado/manual, **[L]** legado, **[H]** hipótese a calibrar (o texto diz onde), **[M]** medido. Sem dados de produção, todo limite é **[H]** até o M0 produzir a linha de base; o M0 pode e deve mudar os números deste documento, por PR.

## 1. Visão geral

| Marco | O que acontece | Tráfego no novo | Sai quando |
| --- | --- | --- | --- |
| **M0** linha de base | fachada em pass-through; mede o legado | nenhum | §3 |
| **M1** pronto, sem tráfego | serviço novo e fachada em produção, flags desligadas; drills | nenhum | §4 |
| **M2** sombra | o legado responde; o novo é comparado | cópia amostrada | §5 |
| **M3** canário | o novo responde para uma fatia | 1% → 100% | §6 |
| **M4** novo 100% | o novo responde tudo da operação; legado em espera quente | 100% | §7 |
| **M5** desligamento | código da operação removido do monolito | 100% | ponto sem volta (§7) |

**Ordem por operação** ([ADR-0001](adr/0001-estrategia-strangler-fig-e-ordem-de-extracao.md)):

```
ConsultarNfse               M0 ─ M1 ─ M2 ─ M3 ─ M4 ─────────────── M5
ConsultarNfseServicoTomado  M0 ─ M1 ──── M2 ─ M3 ─ M4 ──────────── M5
CancelarNfse                M0 ─ M1 ─────────────── (Consulta em M4) M2 ─ M3 ─ M4 ─ M5
```

`ServicoTomado` pode entrar em M2 quando `ConsultarNfse` estiver em M3. `CancelarNfse` entra em M2 só com `ConsultarNfse` em M4 **[H]**. As três operações compartilham M0 e M1.

## 2. Papéis

| Papel | Responsabilidade neste plano |
| --- | --- |
| Time de modernização | executa os marcos, opera flags, faz a triagem de divergências, decide avanço dentro dos critérios |
| Plantão do time (on-call) | aciona kill switch nível 1 e 2 sem pedir permissão |
| SRE / plataforma | nível 3 e 4 do kill switch, ingress, Kubernetes |
| DBA do Nota Carioca | grants, limites de conexão, confirmação das premissas de banco |
| Fazenda / arrecadação | aprova o ADR-0005 e qualquer mudança visível na guia |
| Encarregado de dados (DPO) | aprova pseudonimização e retenção antes do M2 em produção |
| Segurança | dono dos riscos aceitos C18, C19 e K04 ([ADR-0004](adr/0004-comportamentos-legados-nao-documentados.md)) |

Avançar de fase exige **todos** os critérios da fase e o registro no changelog do plano (§10). Voltar de fase não exige aprovação de ninguém.

## 3. M0 — linha de base

A fachada entra na frente do `/NfseWS.asmx` com **todas as operações em `legado`**. Ela não muda nenhum byte; só mede. É também o primeiro teste da própria fachada em produção.

### O que medir (7 dias corridos **[H]**, incluindo um dia 10)

| Medida | Por quê | Usada em |
| --- | --- | --- |
| Volume por operação e por hora | dimensionar amostragem e N | §5, §6 |
| Distribuição de respostas por **chave** (§5.1) | saber quais chaves são raras | §5.3 |
| p50/p95/p99/p99.9 do legado por operação | referência dos limites de latência | §6, [ADR-0012](adr/0012-observabilidade-seguranca-e-lgpd.md) |
| Taxa de `soap:Fault` por operação | referência de erro | §6 |
| Tamanho do corpo (p99, máximo) | validar o limite de 1 MiB | [ADR-0008](adr/0008-fachada-yarp-roteamento-e-flags.md) |
| Overhead da fachada (span fachada − span legado) | validar o orçamento de 5/10 ms | [ADR-0008](adr/0008-fachada-yarp-roteamento-e-flags.md) |
| Requisições sem chave extraível | quantas ficariam presas no legado | [ADR-0008](adr/0008-fachada-yarp-roteamento-e-flags.md) |
| `soap_cnpj_inconsistente_total` | base para decidir C18/K04 | [ADR-0004](adr/0004-comportamentos-legados-nao-documentados.md) |
| Concentração por prestador (top 1%) | escolher prestadores piloto e evitar que um prestador domine a amostra | §5.2, §6 |

### Premissas a confirmar no M0

| Premissa | ADR | Como |
| --- | --- | --- |
| Fuso do `SYSDATE` em produção | 0003 P8 | DBA: `DBTIMEZONE`, fuso do SO |
| Nenhuma nota com `COMPETENCIA` nula ou fora do dia 1 | 0003 P7, 0005 P4 | consulta de contagem, somente leitura |
| Grants e usuários `NFSE_MODERNO*` | 0002 P1–P4 | script aplicado pelo DBA, teste de grants |
| Carga das consultas não degrada o lote | 0002 P6 | teste de carga em homologação com volume do M0 |

### Saída do M0

- 7 dias de dados, com um dia 10 dentro **[H]**;
- overhead da fachada dentro do orçamento; se não estiver, a fachada volta para o ingress direto (nível 3) e o plano para;
- premissas acima confirmadas ou com plano B registrado na ADR;
- números deste plano revisados com os dados (PR no plano).

## 4. M1 — pronto, sem tráfego

O serviço novo sobe em produção com todas as flags em `legado`. Nada do tráfego real chega a ele.

**Saída do M1:**

- CI verde: unitários, integração com Oracle real, contrato, **golden master cobrindo todos os IDs do catálogo** ([ADR-0011](adr/0011-paridade-golden-master.md));
- smoke test em produção pelo endpoint interno, com requisições sintéticas de leitura (nenhuma escrita);
- **drill dos quatro níveis do kill switch** (§8), cronometrado e registrado como **[M]**;
- dashboards da [ADR-0012](adr/0012-observabilidade-seguranca-e-lgpd.md) provisionados e com dados do M0;
- aprovação do DPO para a pseudonimização (trava o M2).

## 5. M2 — sombra

O legado responde ao ERP; o novo processa uma cópia e o resultado é comparado ([ADR-0009](adr/0009-sombra-escrita-unica-e-idempotencia.md)). Degraus de amostragem **[H]**: **1% → 5% → 25% → 100%**, no mínimo 2 dias por degrau, sem regressão de latência do cliente.

### 5.1 Chaves de comparação

A coincidência é medida por **chave**: o tipo de resposta do legado.

| Operação | Chaves |
| --- | --- |
| `ConsultarNfse` | `sucesso` (com notas), `E040`, `E010`, `E020`, `E030`, `E001` |
| `ConsultarNfseServicoTomado` | `sucesso`, `E040`, `E020`, `E030`, `E001` |
| `CancelarNfse` | `cancelou` (estado anterior `N`), `ja_cancelada` (K01), `E050`, `E051`, `E052`, `E053`, `E010`, `E001` |

### 5.2 Unidade de amostra

Uma amostra por **(chave de roteamento, operação, chave de resposta, dia)**. A chave de roteamento é a IM (consultas e cancelamento) ou o hash do documento do consulente (`ServicoTomado`). Isso impede que um prestador com milhares de requisições iguais domine a estatística. Se houver mais de uma requisição na mesma unidade, vale a pior (uma divergência marca a unidade como divergente).

### 5.3 Critério de coincidência

**Coincidência** = amostras iguais ÷ (iguais + `nao_classificada` + `bug_novo`).

Ficam fora do denominador as classes **explicadas** (`decisao_consciente`, `corrida`, `fronteira_relogio`, `indeterminado`), desde que somem **≤ 2% [H]** das amostras da chave. Acima disso, a "explicação" vira suspeita e a chave volta a contar tudo.

A aprovação usa o **limite inferior de Wilson a 95%** (z = 1,96), não a taxa observada:

```
LI = (p + z²/2n − z·√(p(1−p)/n + z²/4n²)) / (1 + z²/n)
```

| Alvo do LI | N mínimo com 0 divergências | com 1 | com 2 |
| --- | --- | --- | --- |
| 99% | 381 | 563 | 726 |
| 99,5% | 765 | — | — |
| 99,9% | 3.838 | 5.662 | 7.289 |

(Com zero divergências, LI = n / (n + z²).)

**Critérios de saída do M2 [H]:**

| Operação | Global | Por chave frequente | Outros |
| --- | --- | --- | --- |
| `ConsultarNfse` | LI ≥ 99,9% (N ≥ 3.838) | LI ≥ 99% (N ≥ 381) | `nao_classificada` pendente de triagem = 0 |
| `ConsultarNfseServicoTomado` | LI ≥ 99,9% | LI ≥ 99% | idem; custo de reexecução dentro do limite |
| `CancelarNfse` | LI ≥ 99,5% (N ≥ 765) | LI ≥ 99% | §5.4 para as raras; um ciclo com dia 10 dentro |

Os alvos de leitura são mais altos porque o volume é maior e o erro é silencioso para o ERP (uma nota a menos numa página). O cancelamento tem alvo global menor porque seu volume é menor, mas a regra continua no PL/SQL também no modo `novo` (§6.2).

Comum a todas: p99 do cliente em sombra igual ao do M0 (o novo fora do caminho), `sombra_descartadas_total` ≤ 1% **[H]**, e todas as divergências triadas como `bug_novo` corrigidas, com caso novo no golden master e a contagem reiniciada para a chave afetada.

### 5.4 Chaves raras

Pelo seed e pela regra, algumas chaves serão raras ou ausentes em produção: `E051` (nada no código do legado grava `SITUACAO = 'S'`), `E053`, `E050`, e qualquer resposta a menos de 1 minuto da virada do dia 10 para o dia 11.

1. **Taxa 1.** A decisão de amostragem é tomada **depois** da resposta do legado. Respostas em chave rara (configurável; padrão: `E050`, `E051`, `E053`, `E001`, `E030` e janela do prazo) vão **sempre** para a sombra, fora da rampa de amostragem.
2. **Acúmulo sobre todos os elegíveis.** Para chave rara, o N é contado sobre todas as amostras desde o início do M2, não por degrau.
3. **Prazo.** Se a chave não chegar a N ≥ 381 em **21 dias [H]**:
   - ela não bloqueia o avanço **se** tiver cobertura completa no golden master (todas as combinações do catálogo) **e** zero divergências nas amostras que existirem;
   - o risco aceito fica registrado no changelog (§10), com o N alcançado;
   - no canário, a chave continua sob vigilância: no cancelamento, o dual-run mede a regra em cada requisição (§6.2); nas leituras, a sombra invertida da fase 2 cobre.
4. **Por que isso é aceitável no cancelamento:** em modo `novo`, quem decide a regra continua sendo o PL/SQL ([ADR-0003](adr/0003-regras-de-cancelamento-em-plsql.md)). Um erro do domínio numa chave rara aparece no dual-run e não muda o resultado para o ERP. O que o canário expõe é a serialização da camada anticorrupção, que é igual para todas as chaves e já está coberta pelo golden master.

## 6. M3 — canário

O novo responde para uma fatia. Degraus **[H]**: **prestadores piloto** (lista de inclusão, 10 a 20 IMs escolhidas no M0 com volume médio e sem concentração) → **1% → 5% → 25% → 50% → 100%**, no mínimo **7 dias por degrau [H]**. No cancelamento, o degrau 25% precisa atravessar um dia 10.

### 6.1 Critérios para subir de degrau

| Medida (por operação, novo × legado na mesma janela) | Limite **[H]** |
| --- | --- |
| p99 do novo | ≤ 1,2 × p99 do legado medido no M0 |
| Taxa de `soap:Fault` do novo | ≤ taxa do legado + 0,1 p.p. |
| Fallback de leitura para o legado | ≤ 0,5% das requisições do novo |
| Sombra invertida (se ativa) | mesmos critérios do M2 |
| Reclamações de contribuintes atribuídas à migração | 0 abertas |

### 6.2 Específico do cancelamento

- `cancelamento_regra_divergente_total` (dual-run) **= 0** em cada degrau. Qualquer valor > 0 dispara rollback automático (§9).
- Espera de lock no wrapper: p99 < 1 s **[H]**; estouro do `WAIT 5` = 0.
- Contagem de `UPDATE` por requisição de cancelamento = 1 (via `DT_CANCELAMENTO` inalterado em reenvio).
- Verificação diária por amostragem: para 50 notas canceladas pelo novo, o estado em `NFSE` e `GUIAS` é igual ao que o legado teria produzido (inclusive o K07 enquanto o ADR-0005 não for aplicado).

## 7. M4 — novo 100% e M5 — desligamento

**M4:** a operação inteira vai para o novo, **inclusive** requisições sem chave extraível ou com corpo acima do limite ([ADR-0008](adr/0008-fachada-yarp-roteamento-e-flags.md)). O legado continua implantado e atendendo as outras operações, em espera quente para esta: o kill switch ainda devolve a operação a ele em segundos.

**Saída do M4 para o M5 [H]:**

- um ciclo de competência completo (passando pelo dia 10) e no mínimo 4 semanas em 100% sem rollback;
- todos os critérios do §6.1 válidos durante o período;
- nenhuma divergência `nao_classificada` aberta;
- aprovação explícita registrada no changelog.

**M5 — ponto sem volta.** O `case` da operação no host do legado e os métodos exclusivos são removidos num release do legado (C# 7.3, .NET Framework 4.8). A partir daí, kill switch e *break-glass* não cobrem mais a operação. Voltar significa um novo release do legado com o código restaurado (horas, não segundos), por isso o M5 de cada operação é independente e pode esperar indefinidamente sem custo além da manutenção.

## 8. Níveis do kill switch

| Nível | Mecanismo | Alcance | Tempo alvo **[H]** | Quem | Aprovação | Quando usar |
| --- | --- | --- | --- | --- | --- | --- |
| **1** | flag `kill-switch` (global ou por operação) no flagd, sincronizado por watch | todas as réplicas | < 10 s | plantão do time | nenhuma; registro no incidente | qualquer suspeita |
| **2** | `POST /admin/kill-switch` na porta interna da fachada, aplicado a cada réplica por script | por réplica | < 5 s por réplica | plantão do time | nenhuma | flagd indisponível ou nível 1 sem efeito |
| **3** | *break-glass*: o ingress aponta direto para o legado, sem a fachada | todo o SOAP | < 5 min | SRE / plataforma | líder do incidente | defeito na própria fachada |
| **4** | `kubectl rollout undo` (ou revert no GitOps) da fachada ou do serviço novo | versão | < 15 min | SRE / plataforma | líder do incidente | regressão de uma versão nova |

- O arquivo de flags tem *code owner* do time de modernização: **ligar** qualquer modo exige PR revisado; **desligar** (nível 1) não exige.
- Os tempos são medidos no drill do M1 e repetidos antes de cada degrau do M3; o resultado vira **[M]** neste documento.
- Nenhum nível exige migração ou reconciliação de dados: nas três operações, quem escreve é sempre o mesmo PL/SQL ([ADR-0003](adr/0003-regras-de-cancelamento-em-plsql.md)).

## 9. Rollback

### Gatilhos automáticos (a fachada volta a operação ao modo anterior sem intervenção)

| Gatilho | Fase |
| --- | --- |
| `cancelamento_regra_divergente_total` > 0 | M3, M4 (cancelamento) |
| divergência `nao_classificada` > 0 no cancelamento | M2 → amostragem 0; M3 → `legado` |
| p99 do novo > 1,2 × p99 do legado por 15 min | M3, M4 |
| taxa de `fault` do novo > legado + 0,1 p.p. por 15 min | M3, M4 |
| fachada sem conseguir avaliar flags | qualquer (fail-safe para `legado`) |

### Gatilhos manuais (plantão decide)

Divergência `nao_classificada` nas leituras acima de 0,1% das amostras em 1 h, reclamação de contribuinte atribuível, alerta de pool de conexões ou de carga no Oracle, qualquer incidente do Nota Carioca em andamento.

### Depois de um rollback

1. A operação volta ao modo anterior (M3 → M2, M4 → M3 no último degrau estável, M2 → amostragem 0).
2. Causa raiz registrada; se for `bug_novo`, caso novo no golden master antes da correção.
3. A contagem estatística da chave afetada reinicia; as outras chaves mantêm seu N.
4. Retomada no **degrau anterior** ao que falhou, nunca direto no degrau da falha.

## 10. Changelog do plano

Toda mudança de fase, degrau, limite ou risco aceito entra aqui por PR, com data, operação, dados que justificam e quem aprovou.

| Data | Operação | Mudança | Evidência | Aprovado por |
| --- | --- | --- | --- | --- |
| 2026-10-16 | todas | versão inicial do plano, todos os limites **[H]** | — | time de modernização |
| 2026-10-19 | todas | kill switch nível 1 pelo flagd medido no compose: 0,4 s até 100% no legado; mudança de modo em 1,6 s **[M]** (alvo < 10 s) | `src/Nfse.Fachada/README.md` | time de modernização |
| 2026-10-19 | leituras | k6 no compose local, legado × novo pela fachada: sem fila, p99 do novo 0,36× (ConsultarNfse) e 0,58× (ServicoTomado) o do legado; a 20 + 5 req/s o legado satura e o novo não **[M]**. Não substitui a medida do M0 em produção | `tests/carga/README.md` | time de modernização |
