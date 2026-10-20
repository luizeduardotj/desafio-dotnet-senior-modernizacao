# Propostas de mudança no legado

Mudanças no schema `NFSE_OWNER` que **dependem de decisão de negócio** e ficam prontas para quando ela vier. Cada pasta
corresponde a uma ADR em estado **Proposto** e não faz parte do caminho da migração.

**Nada aqui é aplicado automaticamente.** O `db/init.sh` executa só `db/01`–`06`, o `db/moderno/init.sh` só o schema
`NFSE_MODERNO`, e nenhum dos dois lê esta pasta. O compose da raiz e o CI de paridade rodam sobre o legado como ele
está em produção. Os scripts só rodam quando alguém os executa à mão, ou no teste da proposta, que sobe um Oracle
descartável próprio ([tests/propostas](../../tests/propostas/)) e só entra no CI por `workflow_dispatch`
([propostas.yml](../../.github/workflows/propostas.yml)).

| Pasta | ADR | O que muda | Teste |
| --- | --- | --- | --- |
| [0005-correcao-trigger-guia](0005-correcao-trigger-guia/) | [ADR-0005](../../docs/adr/0005-correcao-do-trigger-de-recalculo-da-guia.md) | trigger da guia recalcula na transação do cancelamento (K07) | [0005-trigger-guia.sh](../../tests/propostas/0005-trigger-guia.sh) |

Cada proposta traz três scripts:

- `aplicar.sql`: a mudança, com as pré-condições conferidas antes (ex.: premissa P4 da ADR-0005) e a validade do objeto
  conferida depois;
- `reverter.sql`: volta ao objeto original **byte a byte**, conferido pelo teste contra o DDL da linha de base;
- `relatorio-reconciliacao.sql`: só leitura (`SET TRANSACTION READ ONLY`), mostra o que a mudança afeta nos dados. Leva
  só identificadores internos, nunca CPF/CNPJ, inscrição municipal ou razão social ([ADR-0012](../../docs/adr/0012-observabilidade-seguranca-e-lgpd.md)).

## Rito de aprovação

1. **Decisão de negócio.** A Fazenda (área de arrecadação) aprova a mudança de comportamento, com o relatório de
   reconciliação rodado em produção pelo DBA como insumo: quantas guias em aberto mudam de valor e quanto. O arquivo
   do relatório circula só entre DBA e Fazenda.
2. **Pendências da ADR fechadas.** As linhas da seção "Fora deste ADR" que travam a aplicação (na 0005, a consulta de
   `COMPETENCIA` nula em produção) estão resolvidas e registradas na ADR.
3. **Teste da proposta verde** no commit que vai ser aplicado (`workflow_dispatch` de `propostas`), e o mesmo
   `aplicar.sql`/`reverter.sql` exercitado num ambiente de homologação 19c, porque o banco local é 23ai.
4. **ADR passa a Aceito**, com a data da aprovação e quem aprovou. Só então o script vira uma mudança agendada pelo DBA,
   fora das janelas de canário da fachada ([ADR-0008](../../docs/adr/0008-fachada-yarp-roteamento-e-flags.md)), com o
   `reverter.sql` pronto na mesma janela.
5. **Depois da aplicação**, o relatório roda de novo e o resultado vai para a ADR. Corrigir as guias que já estavam
   divergentes é outra decisão da Fazenda, não um efeito do script: a mudança só garante os cancelamentos daqui para a
   frente.

Os arquivos em `db/0*` continuam descrevendo o legado de hoje. Uma proposta aceita e aplicada passa a ser o estado de
produção, e só então entra no init, numa mudança separada.
