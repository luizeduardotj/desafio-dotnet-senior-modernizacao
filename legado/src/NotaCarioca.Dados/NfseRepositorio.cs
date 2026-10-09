using System;
using System.Data;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class NfseRepositorio
    {
        public const int TamanhoPagina = 50;

        private const string Colunas =
            "n.ID, n.PRESTADOR_ID, n.NUMERO, n.CODIGO_VERIFICACAO, n.DT_EMISSAO, n.COMPETENCIA, n.ITEM_SERVICO, " +
            "n.DISCRIMINACAO, n.VALOR_SERVICOS, n.VALOR_DEDUCOES, n.BASE_CALCULO, n.ALIQUOTA, n.VALOR_ISS, n.ISS_RETIDO, " +
            "n.TOMADOR_DOCUMENTO, n.TOMADOR_RAZAO, n.SITUACAO, n.DT_CANCELAMENTO, " +
            "(SELECT s.NUMERO FROM NFSE s WHERE s.ID = n.NFSE_SUBSTITUIDORA_ID) AS NUM_SUBSTITUIDORA, " +
            "p.DOCUMENTO AS PRESTADOR_DOC, p.INSCRICAO_MUNICIPAL AS PRESTADOR_IM, p.RAZAO_SOCIAL AS PRESTADOR_RAZAO";

        // chama PKG_NFSE.GERAR_NFSE; a transacao e controlada por quem chama
        public void GerarNfse(OracleConnection conn, OracleTransaction tx, long rpsId, long prestadorId, DateTime competencia,
                              string item, string discriminacao, decimal valorServicos, decimal valorDeducoes,
                              decimal aliquota, decimal valorIss, bool issRetido, string tomadorDoc, string tomadorRazao,
                              out long nfseId, out long numero, out string codigoVerificacao)
        {
            using (OracleCommand cmd = OracleHelper.CriarProcedure(conn, tx, "PKG_NFSE.GERAR_NFSE"))
            {
                cmd.Parameters.Add(OracleHelper.P("p_rps_id", OracleDbType.Int64, rpsId));
                cmd.Parameters.Add(OracleHelper.P("p_prestador_id", OracleDbType.Int64, prestadorId));
                cmd.Parameters.Add(OracleHelper.P("p_competencia", OracleDbType.Date, competencia));
                cmd.Parameters.Add(OracleHelper.P("p_item", OracleDbType.Varchar2, item));
                cmd.Parameters.Add(OracleHelper.P("p_discriminacao", OracleDbType.Varchar2, discriminacao));
                cmd.Parameters.Add(OracleHelper.P("p_valor_servicos", OracleDbType.Decimal, valorServicos));
                cmd.Parameters.Add(OracleHelper.P("p_valor_deducoes", OracleDbType.Decimal, valorDeducoes));
                cmd.Parameters.Add(OracleHelper.P("p_aliquota", OracleDbType.Decimal, aliquota));
                cmd.Parameters.Add(OracleHelper.P("p_valor_iss", OracleDbType.Decimal, valorIss));
                cmd.Parameters.Add(OracleHelper.P("p_iss_retido", OracleDbType.Varchar2, issRetido ? "S" : "N"));
                cmd.Parameters.Add(OracleHelper.P("p_tomador_doc", OracleDbType.Varchar2, ValidadorDocumento.SomenteDigitos(tomadorDoc)));
                cmd.Parameters.Add(OracleHelper.P("p_tomador_razao", OracleDbType.Varchar2, tomadorRazao));

                OracleParameter oId = new OracleParameter("o_nfse_id", OracleDbType.Int64);
                oId.Direction = ParameterDirection.Output;
                OracleParameter oNum = new OracleParameter("o_numero", OracleDbType.Int64);
                oNum.Direction = ParameterDirection.Output;
                OracleParameter oCod = new OracleParameter("o_codigo_verificacao", OracleDbType.Varchar2, 20);
                oCod.Direction = ParameterDirection.Output;
                cmd.Parameters.Add(oId);
                cmd.Parameters.Add(oNum);
                cmd.Parameters.Add(oCod);

                cmd.ExecuteNonQuery();

                nfseId = Convert.ToInt64(oId.Value.ToString());
                numero = Convert.ToInt64(oNum.Value.ToString());
                codigoVerificacao = oCod.Value.ToString();
            }
        }

        // devolve o codigo de erro (E050..E053) ou null quando cancelou / ja estava cancelada
        public string Cancelar(long prestadorId, long numero, string motivo)
        {
            using (OracleConnection conn = OracleHelper.AbrirConexao())
            using (OracleTransaction tx = conn.BeginTransaction())
            {
                using (OracleCommand cmd = OracleHelper.CriarProcedure(conn, tx, "PKG_NFSE.CANCELAR_NFSE"))
                {
                    cmd.Parameters.Add(OracleHelper.P("p_prestador_id", OracleDbType.Int64, prestadorId));
                    cmd.Parameters.Add(OracleHelper.P("p_numero", OracleDbType.Int64, numero));
                    cmd.Parameters.Add(OracleHelper.P("p_motivo", OracleDbType.Varchar2, motivo));
                    OracleParameter oErro = new OracleParameter("o_codigo_erro", OracleDbType.Varchar2, 10);
                    oErro.Direction = ParameterDirection.Output;
                    cmd.Parameters.Add(oErro);
                    cmd.ExecuteNonQuery();
                    tx.Commit();

                    object v = oErro.Value;
                    if (v == null || v == DBNull.Value) return null;
                    string s = v.ToString();
                    if (s == "null" || s.Length == 0) return null;
                    return s;
                }
            }
        }

        public DataTable ObterPorNumero(long prestadorId, long numero)
        {
            string sql = "SELECT " + Colunas + " FROM NFSE n JOIN CONTRIBUINTES p ON p.ID = n.PRESTADOR_ID " +
                         "WHERE n.PRESTADOR_ID = :prest AND n.NUMERO = :num";
            return OracleHelper.ExecuteDataTable(sql, OracleHelper.P("prest", prestadorId), OracleHelper.P("num", numero));
        }

        public DataTable ListarPorProtocolo(string protocolo)
        {
            string sql = "SELECT " + Colunas + " FROM RPS r JOIN NFSE n ON n.ID = r.NFSE_ID " +
                         "JOIN CONTRIBUINTES p ON p.ID = n.PRESTADOR_ID " +
                         "WHERE r.PROTOCOLO = :prot ORDER BY n.NUMERO";
            return OracleHelper.ExecuteDataTable(sql, OracleHelper.P("prot", protocolo));
        }

        // devolve ate TamanhoPagina + 1 linhas (a ultima so indica que existe proxima pagina)
        public DataTable ConsultarPorPrestador(long prestadorId, DateTime ini, DateTime fim, string tomadorDoc, int pagina)
        {
            if (pagina < 1) pagina = 1;
            string filtroTomador = "";
            if (!string.IsNullOrEmpty(tomadorDoc))
                filtroTomador = " AND n.TOMADOR_DOCUMENTO = :doc";

            string sql =
                "SELECT * FROM (" +
                " SELECT a.*, ROWNUM RN FROM (" +
                "  SELECT " + Colunas +
                "    FROM NFSE n JOIN CONTRIBUINTES p ON p.ID = n.PRESTADOR_ID" +
                "   WHERE n.PRESTADOR_ID = :prest" +
                "     AND n.COMPETENCIA <= :fim" +
                "     AND n.DT_EMISSAO >= :ini AND n.DT_EMISSAO < :fim" + filtroTomador +
                "   ORDER BY n.DT_EMISSAO, n.ID" +
                " ) a WHERE ROWNUM <= :limite" +
                ") WHERE RN > :deslocamento";

            OracleParameter[] ps;
            if (filtroTomador.Length > 0)
            {
                ps = new OracleParameter[] {
                    OracleHelper.P("prest", prestadorId),
                    OracleHelper.P("fim", OracleDbType.Date, fim.Date),
                    OracleHelper.P("ini", OracleDbType.Date, ini.Date),
                    OracleHelper.P("doc", ValidadorDocumento.SomenteDigitos(tomadorDoc)),
                    OracleHelper.P("limite", pagina * TamanhoPagina + 1),
                    OracleHelper.P("deslocamento", (pagina - 1) * TamanhoPagina)
                };
            }
            else
            {
                ps = new OracleParameter[] {
                    OracleHelper.P("prest", prestadorId),
                    OracleHelper.P("fim", OracleDbType.Date, fim.Date),
                    OracleHelper.P("ini", OracleDbType.Date, ini.Date),
                    OracleHelper.P("limite", pagina * TamanhoPagina + 1),
                    OracleHelper.P("deslocamento", (pagina - 1) * TamanhoPagina)
                };
            }
            return OracleHelper.ExecuteDataTable(sql, ps);
        }

        public DataTable ConsultarServicoTomado(string tomadorDoc, DateTime ini, DateTime fim, int pagina)
        {
            if (pagina < 1) pagina = 1;
            string sql =
                "SELECT * FROM (\n" +
                " SELECT a.*, ROWNUM RN FROM (\n" +
                "  -- hint pedido pelo DBA (2018)\n" +
                "  SELECT /*+ FULL(n) */ " + Colunas + "\n" +
                "    FROM NFSE n JOIN CONTRIBUINTES p ON p.ID = n.PRESTADOR_ID\n" +
                "   WHERE n.TOMADOR_DOCUMENTO = :doc\n" +
                "     AND TRUNC(n.DT_EMISSAO) >= :ini AND TRUNC(n.DT_EMISSAO) < :fim\n" +
                "   ORDER BY n.DT_EMISSAO, n.ID\n" +
                " ) a WHERE ROWNUM <= :limite\n" +
                ") WHERE RN > :deslocamento";

            return OracleHelper.ExecuteDataTable(sql,
                OracleHelper.P("doc", OracleDbType.Int64, ValidadorDocumento.ParaNumero(tomadorDoc)),
                OracleHelper.P("ini", OracleDbType.Date, ini.Date),
                OracleHelper.P("fim", OracleDbType.Date, fim.Date),
                OracleHelper.P("limite", pagina * TamanhoPagina + 1),
                OracleHelper.P("deslocamento", (pagina - 1) * TamanhoPagina));
        }
    }
}
