using System;
using System.Collections.Generic;
using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class LoteRepositorio
    {
        public string Inserir(long prestadorId, long numeroLote, int qtdRps, string xmlLote)
        {
            using (OracleConnection conn = OracleHelper.AbrirConexao())
            {
                long seq = Convert.ToInt64(OracleHelper.ExecuteScalar(conn, null, "SELECT SEQ_PROTOCOLO.NEXTVAL FROM DUAL"));
                string protocolo = "NC" + DateTime.Now.ToString("yyyyMM") + seq.ToString("D8");

                string sql = "INSERT INTO LOTES_RPS (PROTOCOLO, PRESTADOR_ID, NUMERO_LOTE, QTD_RPS, SITUACAO, XML_LOTE, DT_RECEBIMENTO) " +
                             "VALUES (:prot, :prest, :numlote, :qtd, 1, :xml, SYSDATE)";
                OracleHelper.ExecuteNonQuery(conn, null, sql,
                    OracleHelper.P("prot", protocolo),
                    OracleHelper.P("prest", prestadorId),
                    OracleHelper.P("numlote", numeroLote),
                    OracleHelper.P("qtd", qtdRps),
                    OracleHelper.P("xml", OracleDbType.Clob, xmlLote));
                return protocolo;
            }
        }

        public DataTable Obter(string protocolo)
        {
            string sql = "SELECT PROTOCOLO, PRESTADOR_ID, NUMERO_LOTE, QTD_RPS, SITUACAO, ERROS, DT_RECEBIMENTO, DT_PROCESSAMENTO " +
                         "FROM LOTES_RPS WHERE PROTOCOLO = :prot";
            return OracleHelper.ExecuteDataTable(sql, OracleHelper.P("prot", protocolo));
        }

        public List<string> ObterPendentes()
        {
            List<string> lista = new List<string>();
            DataTable dt = OracleHelper.ExecuteDataTable(
                "SELECT PROTOCOLO FROM LOTES_RPS WHERE SITUACAO = 1 ORDER BY DT_RECEBIMENTO, PROTOCOLO");
            foreach (DataRow r in dt.Rows)
            {
                lista.Add(Convert.ToString(r["PROTOCOLO"]));
            }
            return lista;
        }

        public string ObterXml(string protocolo)
        {
            object o = OracleHelper.ExecuteScalar("SELECT XML_LOTE FROM LOTES_RPS WHERE PROTOCOLO = :prot",
                OracleHelper.P("prot", protocolo));
            if (o == null || o == DBNull.Value) return null;
            return Convert.ToString(o);
        }

        public DataTable ObterProcessamento(string protocolo)
        {
            return OracleHelper.ExecuteDataTable(
                "SELECT l.PRESTADOR_ID, l.NUMERO_LOTE, c.INSCRICAO_MUNICIPAL, c.REGIME " +
                "FROM LOTES_RPS l JOIN CONTRIBUINTES c ON c.ID = l.PRESTADOR_ID WHERE l.PROTOCOLO = :prot",
                OracleHelper.P("prot", protocolo));
        }

        public void AtualizarSituacao(string protocolo, int situacao)
        {
            string sql = "UPDATE LOTES_RPS SET SITUACAO = :sit WHERE PROTOCOLO = :prot";
            if (situacao >= 3)
                sql = "UPDATE LOTES_RPS SET SITUACAO = :sit, DT_PROCESSAMENTO = SYSDATE WHERE PROTOCOLO = :prot";
            OracleHelper.ExecuteNonQuery(sql,
                OracleHelper.P("sit", situacao),
                OracleHelper.P("prot", protocolo));
        }

        public void SalvarErros(string protocolo, string xmlErros)
        {
            OracleHelper.ExecuteNonQuery("UPDATE LOTES_RPS SET ERROS = :erros WHERE PROTOCOLO = :prot",
                OracleHelper.P("erros", OracleDbType.Clob, xmlErros),
                OracleHelper.P("prot", protocolo));
        }
    }
}
