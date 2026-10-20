# ADR-0014 — Rollback automático pela fachada

- **Status:** Proposto
- **Data:** 2026-10-19
- **Relacionados:** [0003](0003-regras-de-cancelamento-em-plsql.md), [0008](0008-fachada-yarp-roteamento-e-flags.md), [0009](0009-sombra-escrita-unica-e-idempotencia.md), [0012](0012-observabilidade-seguranca-e-lgpd.md), [plano de migração](../plano-de-migracao.md) §6 e §9

## Contexto

O §9 do plano lista gatilhos de rollback "sem decisão humana", mas até aqui eles só alertam: o plantão recebe o alerta e aplica o nível 1 do kill switch. Entre o alerta e a flag passam minutos (avaliação da regra no Prometheus, notificação, alguém abrir o PR ou editar o CR), e nesse tempo o novo continua atendendo a fatia do canário. A ADR-0012 e o plano adiaram para o M1 a volta automática pela própria fachada.

Fatos que limitam o desenho:

- A fachada vê, por requisição, quem respondeu, o status e a duração de cada destino, e se houve fallback ([`EncaminhadorSoap`](../../src/Nfse.Fachada/Encaminhamento/EncaminhadorSoap.cs)). É o mesmo dado que vira `nfse_requisicoes_total`, `nfse_duracao` e `nfse_fallback_total`.
- O dual-run do cancelamento (`cancelamento_regra_divergente_total`, [ADR-0003](0003-regras-de-cancelamento-em-plsql.md)) é calculado dentro do serviço novo, depois do wrapper. A fachada não o vê.
- A divergência `nao_classificada` do cancelamento só existe em `sombra` (M2): em `novo` não há sombra do cancelamento, e a sombra invertida é só para leituras e fica para a fase 2 ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)). Em M2 o cliente sempre recebe o legado.
- A fachada é sem estado, com ≥ 2 réplicas, e as flags vêm de fora (flagd ou arquivo). Ela não escreve nas próprias flags, e ligar qualquer modo exige PR revisado (§8 do plano).
- Pouco tráfego engana: o degrau de prestadores piloto atende dezenas de requisições por minuto, divididas entre as réplicas.

## Decisão

### 1. O que a fachada avalia sozinha

| Gatilho (§9 do plano) | Avaliado pela fachada? | Como |
| --- | --- | --- |
| taxa de `fault` do novo > legado + 0,1 p.p. por 15 min | **sim** | janela deslizante de 15 min, Wilson (seção 2) |
| p99 do novo > 1,2 × p99 do legado por 15 min | **sim** | mesma janela, limite de confiança do quantil (seção 2) |
| `cancelamento_regra_divergente_total` > 0 | **sim**, por header | o novo marca a resposta com `X-Nfse-Dual-Run: divergente`; a fachada lê e remove o header antes do ERP |
| fallback de leitura | **sim**, com limite próprio | LI de Wilson do fallback > 5% das tentativas no novo |
| `nao_classificada` > 0 no cancelamento | **não**, fica no alerta externo | só ocorre em M2, onde o cliente já recebe o legado (seção 6) |
| fachada sem flags | já era automático | fail-safe para `legado` ([ADR-0008](0008-fachada-yarp-roteamento-e-flags.md)) |

**Dual-run por header, não pelo alerta.** É o gatilho mais grave do cancelamento (plano, §6.2) e o único sem estatística: cada evento é uma discordância determinística entre o domínio e o PL/SQL sobre o mesmo snapshot. Esperar o ciclo do Prometheus seria deixar o canário seguir por minutos com uma regra errada. O header só sai quando a classe é `nao_classificada`; `fronteira_relogio` (o `SYSDATE` do PL/SQL caiu no segundo seguinte) não dispara, como no alerta. O header não leva dado do pedido, e a fachada o remove de toda resposta do novo, então o ERP nunca o vê. O contrato SOAP com o ERP não muda. A resposta ao ERP continua correta, porque quem decide é o PL/SQL: o rollback é preventivo.

**Fallback com limite próprio.** O §6.1 usa fallback ≤ 0,5% como critério de **avanço**. Como gatilho de rollback o limite é 10× maior, porque o fallback já protege o cliente: a leitura é atendida pelo legado. O que ele não protege é a latência (o cliente espera o timeout do novo antes do legado) e a carga dobrada no Oracle. Um novo que falha rápido (conexão recusada) nunca aparece no p99, e é esse caso que o gatilho cobre.

### 2. Janela, mínimo de amostras e estatística

- **Janela deslizante de 15 min em 15 baldes de 1 min**, por operação e por lado (`novo`, `legado`), no relógio injetado (`TimeProvider`, nunca `DateTime.Now`). A janela cobre de 14 a 15 min, conforme o instante dentro do minuto corrente. Cada balde guarda contagem, `fault` (status ≠ 2xx entregue ao cliente), fallback e um histograma de latência com baldes geométricos de 5% (1 ms a 120 s).
- **Lado novo:** requisições que o novo atendeu e tentativas que viraram fallback (contam no denominador da taxa de fallback e no p99, com a duração até a falha; não contam como `fault`, porque o cliente recebeu o legado).
- **Lado legado:** requisições da mesma operação atendidas pelo legado fora de fallback, em qualquer modo, inclusive `sombra` e o restante do canário. Assim, em M3 a comparação é na mesma janela, como pede o §6.1.
- **Sem legado na janela** (M4, 100% no novo): vale a referência do M0 configurada por operação (`RollbackAutomatico__Referencias__<Operacao>__P99` e `__TaxaDeFault`). Sem referência, o gatilho comparativo não é avaliado (dual-run e fallback continuam).
- **Mínimo de amostras:** 100 requisições do novo na janela para as taxas; 1.000 em cada lado para o p99 (pelo menos 10 observações acima do p99). Abaixo disso nada dispara, por pior que seja a taxa observada.
- **Taxas com Wilson**, como no §5.3 do plano, mas com **z = 3,29 (99,9%)**, não 1,96. A regra é reavaliada a cada requisição do novo, e reavaliar continuamente infla o falso positivo de um teste a 95%; com 99,9%, o custo de esperar mais evidência é pequeno perto do custo de um rollback espúrio. Dispara se `LI(fault do novo) > LS(fault do legado) + 0,1 p.p.`. A comparação é de pior caso contra melhor caso: com tráfego pequeno, o LS do legado fica largo e o gatilho só dispara com diferença grande.
- **p99 com limite de confiança do quantil.** O p99 de uma amostra também é estimativa. Usa-se a posição binomial `k = n·0,99 ∓ z·√(n·0,99·0,01)`: o limite inferior do p99 do novo é o valor na posição `k−` (balde inferior), e o limite superior do p99 do legado é o valor na posição `k+` (balde superior). Dispara se `LI(p99 novo) > 1,2 × LS(p99 legado)`. Com referência do M0, o lado do legado é o valor configurado.
- **Dual-run:** 1 evento basta. Não há mínimo de amostras, pelo motivo da seção 1.

### 3. Estado por réplica

Cada réplica decide sozinha, com a própria janela. Não há coordenação entre réplicas nem estado compartilhado.

- **Consistência.** O tráfego é balanceado, e uma regressão do novo afeta todas as réplicas: elas tendem a disparar na mesma janela, cada uma quando junta o mínimo de amostras. Até a última disparar, o rollback é parcial, sempre na direção do legado. Para tornar o rollback global e durável, cada disparo emite `rollback_automatico_ativo{operacao} = 1`, e o alerta sobre essa métrica chama o plantão, que aplica o nível 1 (a flag). O automático ganha os minutos entre a evidência e a flag; a flag continua sendo o estado oficial.
- **Restart.** O estado fica em memória e se perde no restart da réplica. A réplica nova volta a mandar a fatia ao novo até juntar evidência de novo e disparar outra vez. Isso é aceito porque o disparo já chamou o plantão para a flag, que sobrevive a restart. Persistir em arquivo (`emptyDir`) cobriria o restart do container, mas não o reagendamento do pod. Persistir no flagd exigiria que o plano de dados escrevesse nas próprias flags, com permissão de escrita no CR `FeatureFlag`. As duas opções estão em Alternativas.

### 4. Como desarmar: só humano, pela flag

- Cada operação ganha, nas flags, `RollbackAutomatico: { Ligado, Rearme }`. Ao disparar, a réplica guarda o `Rearme` daquele momento. O disparo vale enquanto o `Rearme` das flags for o mesmo. Um humano desarma trocando o valor (por exemplo, `"2026-10-20-incidente-42"`) no mesmo PR que devolve a operação ao degrau anterior (plano, §9, "Depois de um rollback"). A janela da operação é zerada no desarme: a evidência que disparou não dispara de novo.
- Nenhum endpoint desarma. `GET /admin/rollback-automatico` (porta interna, token) só mostra o estado da réplica. O nível 2 do kill switch continua independente.
- `Ligado: false` para de observar e não cria disparo novo, mas **não desarma** um disparo existente. Assim, desligar o mecanismo nunca devolve tráfego ao novo por acidente; só o `Rearme` devolve.
- Restart também "desarma" a réplica, pelo motivo da seção 3. Por isso o runbook do alerta manda aplicar a flag antes de investigar.

### 5. Só na direção do legado

O disparo é aplicado **depois** do `DecisorDeDestino`, por uma função pura: decisão com destino `novo` e operação disparada vira `legado` com motivo `RollbackAutomatico`; qualquer outra decisão passa intacta. Por construção, o mecanismo nunca cria uma decisão `novo`. Ele também não toca o modo `sombra`: a decisão lá já é `legado`, e a sombra continua copiando para o novo (a evidência de que o plantão precisa para a triagem).

Interação com o kill switch: os níveis 1 e 2 vêm antes, no próprio decisor, e mandam tudo ao legado com motivo `KillSwitch`. Com o kill switch ligado, o rollback automático não muda nada, e desligar o kill switch não desarma o automático. O corpo continua bufferizado com o disparo ativo, para que o motivo de cada requisição continue certo e a sombra continue funcionando se o plantão voltar a operação para `sombra`.

### 6. Por que `nao_classificada` fica no alerta externo

Em M2 o cliente sempre recebe o legado, e não há dano a evitar em segundos. O §9 manda zerar a amostragem nesse caso, mas desligar a sombra automaticamente apagaria a evidência que a triagem precisa, e a divergência do cancelamento em sombra já bloqueia o avanço. O gatilho fica como alerta (`sombra_divergencias_total{classe="nao_classificada", operacao="CancelarNfse"} > 0`), e o plantão decide entre zerar a amostragem e manter para investigar. Quando a sombra invertida das leituras existir (fase 2), o mesmo contador pode alimentar este mecanismo como mais um gatilho, sem mudar o desenho.

### 7. Métrica, log e span

| Sinal | Conteúdo |
| --- | --- |
| `rollback_automatico_disparos_total` | `operacao`, `gatilho` (`fault`, `p99`, `fallback`, `regra_divergente`) |
| `rollback_automatico_ativo` (gauge) | `operacao`; 1 enquanto o disparo vale nesta réplica |
| log `Critical` (evento 30) | operação, gatilho, números que dispararam (n, taxas, limites, p99 em ms) e o `Rearme` guardado |
| log `Warning` (evento 31) | desarme pela troca do `Rearme` |
| span | `migracao.motivo_roteamento = RollbackAutomatico` |

Nenhum deles leva IM, documento ou corpo: o mecanismo só conhece operação, status e duração.

### Números desta ADR

| Valor | Origem | Onde calibrar |
| --- | --- | --- |
| Janela de 15 min em baldes de 1 min | [H] | do §9 do plano; drill do M1 |
| Mínimo de 100 requisições do novo (taxas) | [H] | volume por réplica no degrau de prestadores piloto, medido no M0 |
| Mínimo de 1.000 requisições por lado (p99) | [H] | idem; abaixo disso o p99 tem menos de 10 observações acima dele |
| z = 3,29 (99,9%) | [H] | falsos disparos num replay do tráfego do M0 com o novo sadio (alvo: zero) |
| `fault`: LI do novo > LS do legado + 0,1 p.p. | [H] | do §9; taxa de fault do legado medida no M0 |
| p99: LI do novo > 1,2 × LS do legado | [H] | do §9; variação natural do p99 do legado medida no M0 |
| Fallback: LI > 5% das tentativas no novo | [H] | 10× o critério de avanço do §6.1 |
| Dual-run: 1 evento `nao_classificada` | [H] | do §6.2 (qualquer valor > 0) |
| Histograma de latência com baldes de 5% | [H] | erro do p99 aceito; o desenho é conservador (baldes contra o disparo) |

Exemplo, com z = 3,29 e a referência do M0 em 0,1% de fault (limite de 0,2%): com 100 requisições do novo, dispara a partir de 2 faults; com 1.000, a partir de 7; com 10.000, a partir de 35. Com 99 requisições, não dispara nem com todas em fault.

## Alternativas consideradas

- **Alertmanager chamando um webhook que liga a flag.** Automatiza sem mexer na fachada e já é global. Rejeitada como mecanismo principal: soma o intervalo de avaliação do Prometheus, o `for:` da regra e a notificação (minutos), e dá a um serviço externo permissão de escrita no CR das flags. Continua sendo a evolução natural para tornar o disparo global.
- **A fachada escreve o kill switch no `FeatureFlag`.** Global e durável. Rejeitada: o plano de dados ganharia escrita nas próprias flags (RBAC no CR) e um caminho de falha novo na hora do incidente; uma fachada com defeito poderia desligar a migração inteira.
- **Persistir o disparo em arquivo (`emptyDir`).** Sobrevive a restart do container, não a reagendamento do pod. Rejeitada por enquanto: o restart já leva o plantão à flag, e o arquivo seria mais um estado a limpar no desarme.
- **Estado compartilhado (Redis) entre réplicas.** Rejeitada: a fachada é sem estado de propósito (ADR-0008), e o Redis viraria dependência do caminho do cliente.
- **Limite simples sobre a taxa observada (sem Wilson).** Rejeitada: com 20 requisições, 1 fault já é 5%. O mínimo de amostras sozinho não resolve: no limite, o que decide é o tamanho da diferença comparado com a incerteza.
- **Disparar pelo `nao_classificada` da sombra lendo a métrica.** Rejeitada pelo motivo da seção 6.

## Consequências

- **Positivas:** os gatilhos do §9 que dependem do tráfego viram rollback em segundos, por réplica, sem esperar o plantão; o disparo nunca vai na direção do novo; o desarme é explícito e revisado.
- **Negativas:** um disparo parcial (uma réplica disparou, as outras não) é um estado novo para o plantão; a fachada passa a ter estado em memória além do kill switch nível 2; o serviço novo ganha um header interno no `CancelarNfse`.
- **Riscos:** falso disparo em pouco tráfego, mitigado por mínimo de amostras, z de 99,9% e o drill; disparo que some no restart, mitigado pelo alerta que manda à flag; header do dual-run vazando ao ERP, mitigado por teste que confere que a fachada o remove.

## Reversão

`RollbackAutomatico.Ligado: false` nas flags para de criar disparos (segundos). Os disparos existentes se desfazem com a troca do `Rearme` ou com o restart das réplicas. Remover o código volta ao comportamento anterior: alerta e plantão.

## Como verificar

**Conferido no compose em 2026-10-20 [M]:** `ConsultarNfse` em `novo` 100% com o rollback ligado e o `nfse-novo` parado. O gatilho `fallback` disparou no 23º fallback, com 233 tentativas na janela (LI de Wilson 0,0512 > 0,05). As 107 requisições seguintes foram direto ao legado (motivo `RollbackAutomatico`), sem tentar o novo, e o cliente recebeu 200 em 130 de 130. Com o novo de volta, a operação continuou no legado; trocar o `Rearme` de `r1` para `r2` na flag a devolveu ao novo e zerou o estado. `rollback_automatico_disparos_total{gatilho="fallback"}` = 1.


- Testes unitários da janela e da estatística, com relógio injetado: não dispara abaixo do mínimo de amostras; dispara no mínimo; a janela esquece depois de 15 min; Wilson confere com a tabela do §5.3 do plano.
- Teste de propriedade da função de aplicação: para toda decisão, a saída só difere da entrada quando a entrada é `novo`, e a saída é sempre `legado`.
- Testes de integração na fachada: faults do novo em canário disparam e a próxima requisição vai ao legado; header de dual-run dispara na primeira e não chega ao cliente; troca do `Rearme` desarma; `Ligado: false` não dispara; a sombra de uma operação continua copiando com outra operação disparada.
- Drill do M1: novo com falha injetada (fault e latência) em canário; medir o tempo até o disparo de cada réplica e o tempo até o plantão aplicar a flag. O resultado vira **[M]** nesta ADR.
