-- Carga de volume: ~2.000.000 NFS-e, ~5.000 prestadores, ~200.000 tomadores, 24 meses
SET DEFINE OFF
SET TIMING ON
SET SERVEROUTPUT ON
WHENEVER SQLERROR EXIT FAILURE

-- prestadores de volume (ids 1001..6000); 5% MEI
INSERT INTO CONTRIBUINTES (ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, REGIME, ATIVO)
SELECT 1000 + LEVEL,
       '2' || LPAD(TO_CHAR(1000 + LEVEL), 7, '0'),
       LPAD(TO_CHAR((1000 + LEVEL) * 7919), 8, '0') || '000100',
       'PRESTADORA VOLUME ' || LPAD(TO_CHAR(1000 + LEVEL), 5, '0') || ' LTDA',
       CASE WHEN MOD(LEVEL, 20) = 0 THEN 'M' WHEN MOD(LEVEL, 3) = 0 THEN 'S' ELSE 'N' END,
       'S'
  FROM DUAL
CONNECT BY LEVEL <= 5000;
COMMIT;

-- notas
INSERT /*+ APPEND */ INTO NFSE
       (ID, PRESTADOR_ID, NUMERO, CODIGO_VERIFICACAO, DT_EMISSAO, COMPETENCIA, ITEM_SERVICO, DISCRIMINACAO,
        VALOR_SERVICOS, VALOR_DEDUCOES, BASE_CALCULO, ALIQUOTA, VALOR_ISS, ISS_RETIDO, TOMADOR_DOCUMENTO,
        TOMADOR_RAZAO, SITUACAO, DT_CANCELAMENTO, MOTIVO_CANCELAMENTO)
SELECT SEQ_NFSE.NEXTVAL, v.PRESTADOR_ID, v.NUMERO, v.COD, v.DT, TRUNC(v.DT, 'MM'), v.ITEM, v.DISCR,
       v.SERV, v.DED, v.SERV - v.DED, v.ALIQ, ROUND((v.SERV - v.DED) * v.ALIQ, 2), v.RETIDO,
       v.TDOC, 'TOMADOR ' || v.TIDX, v.SIT,
       CASE WHEN v.SIT = 'C' THEN v.DT + 1 END,
       CASE WHEN v.SIT = 'C' THEN 'Cancelamento a pedido do tomador' END
  FROM (
        SELECT b.PRESTADOR_ID, b.NUMERO, b.TIDX, b.DT, b.ITEM, b.SERV,
               'V' || LPAD(TO_CHAR(MOD(b.H, 99999999)), 8, '0') AS COD,
               'Serviços prestados: ' || b.DESCRICAO || ' - ref. contrato ' || LPAD(TO_CHAR(MOD(b.H, 9999)), 4, '0') ||
               ' - competência ' || TO_CHAR(b.DT, 'MM/YYYY') || ' - documento fiscal eletrônico emitido por meio do sistema. ' ||
               'Serviços executados conforme ordem de serviço, incluindo mão de obra, deslocamento da equipe técnica, ' ||
               'acompanhamento presencial, emissão de relatórios de atividades e demais custos previstos em contrato. ' ||
               'Valores já consideram as deduções legais aplicáveis quando houver.' AS DISCR,
               CASE WHEN MOD(TRUNC(b.H / 7), 10) = 0 THEN ROUND(b.SERV * 0.1, 2) ELSE 0 END AS DED,
               CASE WHEN b.REGIME = 'M' THEN 0 ELSE b.ALIQUOTA END AS ALIQ,
               CASE WHEN MOD(TRUNC(b.H / 11), 7) = 0 THEN 'S' ELSE 'N' END AS RETIDO,
               CASE WHEN MOD(TRUNC(b.H / 13), 50) = 0 THEN 'C' ELSE 'N' END AS SIT,
               CASE
                   WHEN b.TIDX = 0 THEN '11444777000161'
                   WHEN MOD(b.TIDX, 2) = 0 THEN LPAD(TO_CHAR(MOD(b.TIDX * 982451653, 100000000000000)), 14, '0')
                   ELSE LPAD(TO_CHAR(MOD(b.TIDX * 48271, 100000000000)), 11, '0')
               END AS TDOC
          FROM (
                SELECT x.PRESTADOR_ID, x.REGIME, x.NUMERO, x.H,
                       CASE WHEN MOD(TRUNC(x.H / 17), 100) = 0 THEN 0
                            ELSE 1 + MOD(TRUNC(x.H / 19), 199999) END AS TIDX,
                       DATE '2024-10-01'
                         + FLOOR((x.NUMERO - 1) * 728 / 400 + MOD(ORA_HASH(x.PRESTADOR_ID, 99, 3), 100) / 100 * 1.8)
                         + MOD(ORA_HASH(x.PRESTADOR_ID * 1000 + x.NUMERO, 86399, 7), 86400) / 86400 AS DT,
                       100 + MOD(x.H, 2000000) / 100 AS SERV,
                       i.CODIGO AS ITEM, i.DESCRICAO, i.ALIQUOTA
                  FROM (SELECT p.ID AS PRESTADOR_ID, p.REGIME, n.NUMERO,
                               ORA_HASH(p.ID * 1000 + n.NUMERO) AS H
                          FROM (SELECT ID, REGIME FROM CONTRIBUINTES WHERE ID > 1000) p
                         CROSS JOIN (SELECT LEVEL AS NUMERO FROM DUAL CONNECT BY LEVEL <= 400) n) x
                  JOIN (SELECT CODIGO, DESCRICAO, ALIQUOTA, ROW_NUMBER() OVER (ORDER BY CODIGO) - 1 AS RN
                          FROM ITENS_SERVICO) i
                    ON i.RN = MOD(x.H, 20)
               ) b
       ) v;
COMMIT;

-- guias consistentes com as notas carregadas
INSERT INTO GUIAS (ID, PRESTADOR_ID, COMPETENCIA, VALOR_ISS, SITUACAO, DT_VENCIMENTO, DT_ATUALIZACAO)
SELECT SEQ_GUIAS.NEXTVAL, g.PRESTADOR_ID, g.COMPETENCIA, g.VALOR,
       CASE WHEN g.COMPETENCIA < DATE '2026-08-01' THEN 'P' ELSE 'A' END,
       ADD_MONTHS(g.COMPETENCIA, 1) + 9, ADD_MONTHS(g.COMPETENCIA, 1)
  FROM (SELECT PRESTADOR_ID, COMPETENCIA,
               SUM(ROUND((VALOR_SERVICOS - VALOR_DEDUCOES) * ALIQUOTA, 2)) AS VALOR
          FROM NFSE
         WHERE PRESTADOR_ID > 1000 AND SITUACAO = 'N' AND ISS_RETIDO = 'N'
         GROUP BY PRESTADOR_ID, COMPETENCIA) g;
COMMIT;

BEGIN
    DBMS_STATS.GATHER_SCHEMA_STATS(USER, DEGREE => 2, CASCADE => TRUE);
END;
/

SELECT COUNT(*) AS TOTAL_NFSE FROM NFSE;
EXIT
