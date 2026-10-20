-- ADR-0005, relatório de reconciliação: guias EM ABERTO ('A') cujo VALOR_ISS difere do valor que
-- PKG_GUIA.recalcular_guia calcularia agora (ISS das notas normais e não retidas da competência).
-- Só leitura: a transação é READ ONLY, então nenhum comando abaixo consegue escrever. Guias pagas ('P') ficam de fora
-- de propósito: o que fazer com guia paga a maior é decisão da Fazenda (ADR-0005, "Fora deste ADR").
-- Executar como NFSE_OWNER ou como um usuário com SELECT em NFSE e GUIAS. Saída em CSV no stdout; redirecione para
-- um arquivo de acesso restrito.
--
-- Dados pessoais (ADR-0012): o relatório leva só o ID interno do prestador e o ID da guia, que bastam para a Fazenda
-- localizar o contribuinte no próprio sistema. Ficam fora CNPJ/CPF, inscrição municipal, razão social e qualquer dado
-- do tomador. O ID interno ainda identifica o contribuinte para quem tem acesso à base: o arquivo é dado pessoal
-- pseudonimizado, circula só entre DBA e Fazenda e não vai para chamado, e-mail aberto ou repositório.
SET DEFINE OFF
SET FEEDBACK OFF
SET VERIFY OFF
SET TERMOUT ON
SET PAGESIZE 0
SET LINESIZE 400
SET TRIMSPOOL ON
WHENEVER SQLERROR EXIT FAILURE

SET TRANSACTION READ ONLY;

SET MARKUP CSV ON QUOTE OFF
SET HEADING ON
SET PAGESIZE 50000

WITH abertas AS (
    SELECT ID, PRESTADOR_ID, COMPETENCIA, DT_VENCIMENTO, VALOR_ISS
      FROM GUIAS
     WHERE SITUACAO = 'A'
), recalculo AS (
    SELECT n.PRESTADOR_ID, n.COMPETENCIA,
           NVL(SUM(CASE WHEN n.SITUACAO = 'N' AND n.ISS_RETIDO = 'N'
                        THEN ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2) END), 0) AS VALOR,
           COUNT(CASE WHEN n.SITUACAO = 'C' THEN 1 END) AS NOTAS_CANCELADAS
      FROM NFSE n
     WHERE (n.PRESTADOR_ID, n.COMPETENCIA) IN (SELECT PRESTADOR_ID, COMPETENCIA FROM abertas)
     GROUP BY n.PRESTADOR_ID, n.COMPETENCIA
)
SELECT g.ID                                   AS GUIA_ID,
       g.PRESTADOR_ID,
       TO_CHAR(g.COMPETENCIA, 'YYYY-MM')      AS COMPETENCIA,
       TO_CHAR(g.DT_VENCIMENTO, 'YYYY-MM-DD') AS VENCIMENTO,
       TO_CHAR(g.VALOR_ISS, 'FM999999999990.00')                          AS VALOR_GUIA,
       TO_CHAR(NVL(r.VALOR, 0), 'FM999999999990.00')                      AS VALOR_RECALCULADO,
       TO_CHAR(NVL(g.VALOR_ISS, 0) - NVL(r.VALOR, 0), 'FM999999999990.00') AS COBRADO_A_MAIS,
       NVL(r.NOTAS_CANCELADAS, 0)             AS NOTAS_CANCELADAS
  FROM abertas g
  LEFT JOIN recalculo r
    ON r.PRESTADOR_ID = g.PRESTADOR_ID AND r.COMPETENCIA = g.COMPETENCIA
 WHERE g.VALOR_ISS IS NULL OR g.VALOR_ISS <> NVL(r.VALOR, 0)
 ORDER BY g.PRESTADOR_ID, g.COMPETENCIA;

-- Totais, para a decisão da Fazenda e para comparar execuções (antes e depois da correção).
WITH abertas AS (
    SELECT ID, PRESTADOR_ID, COMPETENCIA, VALOR_ISS FROM GUIAS WHERE SITUACAO = 'A'
), recalculo AS (
    SELECT n.PRESTADOR_ID, n.COMPETENCIA,
           NVL(SUM(CASE WHEN n.SITUACAO = 'N' AND n.ISS_RETIDO = 'N'
                        THEN ROUND((n.VALOR_SERVICOS - n.VALOR_DEDUCOES) * n.ALIQUOTA, 2) END), 0) AS VALOR
      FROM NFSE n
     WHERE (n.PRESTADOR_ID, n.COMPETENCIA) IN (SELECT PRESTADOR_ID, COMPETENCIA FROM abertas)
     GROUP BY n.PRESTADOR_ID, n.COMPETENCIA
)
SELECT COUNT(*)                                                                     AS GUIAS_ABERTAS,
       COUNT(CASE WHEN g.VALOR_ISS IS NULL OR g.VALOR_ISS <> NVL(r.VALOR, 0) THEN 1 END) AS GUIAS_DIVERGENTES,
       TO_CHAR(NVL(SUM(NVL(g.VALOR_ISS, 0) - NVL(r.VALOR, 0)), 0), 'FM999999999990.00') AS COBRADO_A_MAIS_TOTAL
  FROM abertas g
  LEFT JOIN recalculo r
    ON r.PRESTADOR_ID = g.PRESTADOR_ID AND r.COMPETENCIA = g.COMPETENCIA;

COMMIT;
EXIT
