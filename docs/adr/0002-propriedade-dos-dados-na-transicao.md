# ADR-0002 — Propriedade dos dados na transição

- **Status:** Aceito
- **Data:** 2026-10-16
- **Relacionados:** [0001](0001-estrategia-strangler-fig-e-ordem-de-extracao.md), [0003](0003-regras-de-cancelamento-em-plsql.md), [0007](0007-arquitetura-do-servico-novo.md)

## Contexto

- O legado continua emitindo NFS-e pelo lote (`PKG_NFSE.gerar_nfse`) e mantendo guias (`PKG_GUIA`, trigger `TRG_NFSE_SITUACAO_GUIA`) durante toda a transição.
- Restrições: objetos novos ficam em schema próprio; não alterar tabelas do legado; nenhuma escrita duplicada.
- ERPs fazem sequências do tipo **cancelar → consultar** em segundos. Se o caminho de leitura do novo tiver defasagem, a nota pode aparecer como normal logo após ser cancelada — divergência visível ao contribuinte.
- `ConsultarNfseServicoTomado` força full scan na NFSE (`/*+ FULL(n) */`, `NfseRepositorio.cs:151`) sobre ~2 milhões de linhas: ler do mesmo Oracle tem custo real.
- Opções de alimentar um modelo de leitura próprio têm custo real:
  - **outbox** exigiria um trigger novo em `NFSE` gravando eventos, mais um objeto no caminho de toda emissão de lote;
  - **CDC (LogMiner/Debezium)** exige supplemental logging (no nível do banco, `ALTER DATABASE ADD SUPPLEMENTAL LOG DATA (ALL) COLUMNS`, ou por tabela), aprovação de DBA, operação do conector e validação no Oracle Free;
  - **polling** é frágil: `NFSE` não tem coluna de última atualização, a substituição (`'S'`) não tem timestamp e `ORA_ROWSCN` não é por linha (tabela sem `ROWDEPENDENCIES`);
  - qualquer uma delas introduz **defasagem** exatamente na sequência cancelar → consultar.

### Premissas e nível de verificação

| # | Premissa | Fonte | Nível | Como confirmar | Se for falsa |
| --- | --- | --- | --- | --- | --- |
| P1 | `READ` permite consultar mas não `SELECT ... FOR UPDATE`; `SELECT` permite | documentação Oracle 12c+ | **confirmada** em 23ai ([spike](../spikes/2026-10-19-premissas-oracle.md)); erro do 23ai é `ORA-41900`, do 19c `ORA-01031` | teste de grants no CI | dar `SELECT` também nas views ao dono; o runtime continua só com `READ` |
| P2 | Repassar leitura de views a terceiros exige `WITH GRANT OPTION` na origem (`ORA-01720`) | documentação Oracle | **confirmada** ([spike](../spikes/2026-10-19-premissas-oracle.md)) | script de grants aplicado no container de teste | — |
| P3 | Views e pacotes com definer rights ignoram privilégios recebidos por role | documentação Oracle | lida (grants aplicados só diretos; caso com role não testado) | teste com grant só por role falha | — |
| P4 | Sem privilégio, `UPDATE` dá `ORA-00942` e chamada de pacote dá `PLS-00201` | documentação Oracle | **confirmada** ([spike](../spikes/2026-10-19-premissas-oracle.md)) | teste de grants | ajustar o código esperado no teste |
| P5 | O ODP.NET faz autocommit fora de transação explícita (o lote grava `LOTES_RPS` sem `Commit` e funciona) | `LoteRepositorio.cs:10-27`; comportamento do provider | lida | `just lote` no ambiente local seguido de `ConsultarLoteRps` | o lote já estaria quebrado; não afeta o serviço novo, que sempre usa transação explícita |
| P6 | Ler o mesmo Oracle não degrada o processamento de lote | — | hipótese | teste de carga com lote e consultas concorrentes (M0 do plano) | antecipar read model ou réplica de leitura |

## Decisão

1. **O legado é o sistema de registro** de `NFSE`, `GUIAS`, `CONTRIBUINTES`, `RPS` e `LOTES_RPS` durante toda a extração das três operações.
2. **Leitura:** o serviço novo lê o **mesmo Oracle**, por **views finas** em um schema próprio. As views apenas projetam colunas e juntam `NFSE` com `CONTRIBUINTES`; **nenhuma regra ou tradução** vive nelas (a tradução de `N/C/S`, documentos e datas fica no adaptador .NET, [ADR-0006](0006-camada-anticorrupcao.md)).
3. **Escrita:** apenas cancelamento, e apenas pelo pacote wrapper descrito em [ADR-0003](0003-regras-de-cancelamento-em-plsql.md). O serviço novo nunca faz `INSERT/UPDATE/DELETE` em tabela do legado.
4. **Três usuários no schema próprio (menor privilégio).** Todos os grants são **diretos ao usuário, nunca por role**: views e pacotes com definer rights ignoram privilégios recebidos por role.

| Usuário | Papel | Privilégios |
| --- | --- | --- |
| `NFSE_MODERNO` | dono das views e do wrapper; **sem login em runtime** | `READ ... WITH GRANT OPTION` em `NFSE_OWNER.NFSE`, `CONTRIBUINTES`, `GUIAS` (para criar views e repassar leitura); `SELECT` direto, sem grant option, **só** em `NFSE_OWNER.NFSE` (o `FOR UPDATE` do wrapper exige `SELECT`, não `READ`); `EXECUTE` direto em `NFSE_OWNER.PKG_NFSE` |
| `NFSE_MODERNO_APP` | conexão do serviço novo (REST, SOAP em modo `novo`) | `READ` nas views; `EXECUTE` no wrapper |
| `NFSE_MODERNO_RO` | conexão da avaliação de sombra | `READ` nas views; **nada mais** |

   Notas:
   - `READ` (12c+) permite consultar mas não `SELECT ... FOR UPDATE` nem `LOCK TABLE`: nenhum usuário de runtime consegue travar linhas do legado.
   - Sem `WITH GRANT OPTION` na origem, o `GRANT` das views a terceiros falha com `ORA-01720`.
   - O `EXECUTE` em `PKG_NFSE` libera também `gerar_nfse` (o Oracle não concede por procedure); por isso fica só com o dono, encapsulado pelo wrapper.
   - A sombra usa um pool separado com `NFSE_MODERNO_RO`: a garantia de "sombra não escreve" vem do banco, não do código ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)).
5. **Nenhuma tabela própria é necessária no MVP.** O cancelamento é naturalmente idempotente (K01), então a API REST não precisa de tabela de idempotência ([ADR-0009](0009-sombra-escrita-unica-e-idempotencia.md)). Divergências da sombra **não** vão para o Oracle (vão para o backend de observabilidade).
6. **Modelo de leitura próprio (CDC/outbox) fica como evolução documentada**, adotada apenas se uma destas condições for medida:
   - a carga das consultas no Oracle degradar a emissão de lotes (p99 de processamento de lote acima do baseline em mais de 20%);
   - a extração da emissão/guia mudar a propriedade dos dados.

   As views são a costura: um read model futuro substitui a fonte das views sem mudar o serviço. Qualquer read model terá a defasagem medida e exposta como métrica antes de servir tráfego.

### Matriz de propriedade

| Tabela | Fase `legado`/`sombra` | Fase `novo` | Quem escreve sempre |
| --- | --- | --- | --- |
| `NFSE` | legado lê/escreve; novo lê (sombra) | novo lê; novo cancela via wrapper | lote do legado (emissão) |
| `GUIAS` | legado (trigger) | legado (trigger, disparado pelo cancelamento do novo) | só PL/SQL do legado |
| `CONTRIBUINTES` | legado | novo só lê | legado |
| `RPS`, `LOTES_RPS` | legado | não usadas pelo novo | legado |
| `NFSE_MODERNO.*` (views, wrapper) | — | novo (só leitura e execução) | — |

### Números desta ADR

| Valor | Origem | Onde calibrar |
| --- | --- | --- |
| Read model só se o p99 do processamento de lote subir mais de 20% sobre a linha de base | [H] | linha de base no M0; teste de carga com consultas concorrentes ao lote |

## Fora deste ADR

| Pendência | Dono | Trava |
| --- | --- | --- |
| Aprovação do DBA para os usuários `NFSE_MODERNO*` e os grants | DBA do Nota Carioca | a sombra de qualquer operação |
| Limite de conexões do `NFSE_MODERNO_APP` e `NFSE_MODERNO_RO` acordado com o DBA | DBA + time de modernização | o canário de `ConsultarNfse` |
| Read model próprio (CDC/outbox) | time de modernização | nada; reaberto se a premissa P6 falhar |

## Alternativas consideradas

- **Banco próprio do serviço novo (ex.: PostgreSQL) sincronizado.** Rejeitada nesta fase: cancelar exige escrever na fonte do legado (a guia é do legado), e manter duas fontes coerentes sem dual write exigiria CDC, cujo custo está descrito acima. A FAQ permite outro banco, mas o ganho não paga o risco agora.
- **Ler as tabelas do legado diretamente, sem views.** Rejeitada: acopla o serviço a nomes de coluna do legado e exige grants mais amplos; as views são o ponto de troca e de contrato testável.
- **Read model com CDC desde o início.** Rejeitada para a primeira entrega: custo de infraestrutura, de aprovação (supplemental logging) e de operação, e cria defasagem justamente na sequência cancelar → consultar. O ganho (tirar carga do Oracle) ainda não foi medido como necessário.
- **Serviço novo chamar o SOAP do legado para ler.** Rejeitada: não reduz acoplamento e herda a latência do legado.

## Consequências

- **Positivas:** consistência imediata (ler após cancelar sempre vê o cancelamento); nenhum dado migrado, então rollback trivial; menor superfície de mudança no Oracle.
- **Negativas:** o serviço novo compartilha a carga e o pool do Oracle com o legado; mudanças no schema legado podem quebrar as views.
- **Riscos e mitigação:**
  - carga: o novo pode usar índices que o legado ignora (ex.: comparar `TOMADOR_DOCUMENTO` como string, usando `IX_NFSE_TOMADOR_DT`), ficando mais barato que o legado; medir no teste de carga;
  - schema: testes de contrato das views no CI contra Oracle real ([ADR-0011](0011-paridade-golden-master.md));
  - pool: limite máximo de conexões do `NFSE_MODERNO_APP` configurado e monitorado.

## Reversão

Nenhum dado é movido. Desfazer = voltar as operações para `legado` e, se necessário, `DROP USER NFSE_MODERNO_RO`, `NFSE_MODERNO_APP` e `NFSE_MODERNO CASCADE`. Não há impacto em objetos do legado.

## Como verificar

- Teste de integração (grants), automático desde 2026-10-19 em [GarantiasDoBancoTests.cs](../../tests/Nfse.Paridade.Testes/GarantiasDoBancoTests.cs), no job `compose`: `NFSE_MODERNO_APP` e `NFSE_MODERNO_RO` recebem `ORA-00942` ao tentar `UPDATE NFSE_OWNER.NFSE` (sem privilégio algum na tabela) e `PLS-00201` ao chamar `NFSE_OWNER.PKG_NFSE.gerar_nfse`; `NFSE_MODERNO_RO` recebe `PLS-00201` ao chamar o wrapper; `SELECT ... FOR UPDATE` numa view falha para os dois.
- Teste de integração: cancelar pelo novo e consultar em seguida pelo novo e pelo legado retorna a nota cancelada nos dois.
- Métrica de conexões ativas e tempo de espera de pool do `NFSE_MODERNO_APP`.
