-- Snapshot de NFSE e GUIAS da competência da nota pedida, depois de um caso 2xx (ADR-0011): cobre K07 (a guia não
-- desconta a nota cancelada) e escrita fora do lugar. Binds :im (InscricaoMunicipal) e :numero (NULL = nada).
-- Mesmo texto para os dois lados: estado-do-banco.sh (sqlplus, captura do legado) e ParidadeCancelamentoTests
-- (ODP.NET pelo NFSE_MODERNO_APP). Só lê as views do NFSE_MODERNO. Sem linha em branco: o sqlplus a leria como fim.
-- Independente do mês da carga do seed: datas relativas ao início da competência (C+dias hh:mi:ss) e, para o que o
-- próprio caso gravou, 'agora' (= SYSDATE, congelado pelo FIXED_DATE). DT_ATUALIZACAO da guia vira agora/antes.
WITH alvo AS (
    SELECT n.PRESTADOR_ID, n.COMPETENCIA
      FROM NFSE_MODERNO.V_NFSE n
     WHERE n.PRESTADOR_IM = :im AND n.NUMERO = :numero
)
SELECT linha FROM (
    SELECT 1 AS ordem, n.NUMERO AS chave,
           'nfse ' || TO_CHAR(n.NUMERO) || ' situacao=' || n.SITUACAO
           || ' emissao=C+' || TO_CHAR(TRUNC(n.DT_EMISSAO) - a.COMPETENCIA) || 'd ' || TO_CHAR(n.DT_EMISSAO, 'HH24:MI:SS')
           || ' servicos=' || TO_CHAR(n.VALOR_SERVICOS, 'FM9999999999990.00') || ' iss=' || TO_CHAR(n.VALOR_ISS, 'FM9999999999990.00')
           || ' retido=' || n.ISS_RETIDO
           || ' cancelamento=' || CASE
                  WHEN n.DT_CANCELAMENTO IS NULL THEN '-'
                  WHEN n.DT_CANCELAMENTO = SYSDATE THEN 'agora'
                  ELSE 'C+' || TO_CHAR(TRUNC(n.DT_CANCELAMENTO) - a.COMPETENCIA) || 'd ' || TO_CHAR(n.DT_CANCELAMENTO, 'HH24:MI:SS')
              END
           || ' motivo=' || NVL(n.MOTIVO_CANCELAMENTO, '-')
           || ' substituidora=' || NVL(TO_CHAR(n.NUMERO_SUBSTITUIDORA), '-') AS linha
      FROM NFSE_MODERNO.V_NFSE n
      JOIN alvo a ON a.PRESTADOR_ID = n.PRESTADOR_ID AND a.COMPETENCIA = n.COMPETENCIA
    UNION ALL
    SELECT 2, 0,
           'guia valor_iss=' || TO_CHAR(g.VALOR_ISS, 'FM9999999999990.00') || ' situacao=' || g.SITUACAO
           || ' atualizada=' || CASE WHEN g.DT_ATUALIZACAO = SYSDATE THEN 'agora' ELSE 'antes' END
      FROM NFSE_MODERNO.V_GUIA g
      JOIN alvo a ON a.PRESTADOR_ID = g.PRESTADOR_ID AND a.COMPETENCIA = g.COMPETENCIA
)
ORDER BY ordem, chave
