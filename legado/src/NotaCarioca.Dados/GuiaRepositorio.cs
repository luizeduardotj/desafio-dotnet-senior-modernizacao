using System;
using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class GuiaRepositorio
    {
        public DataTable ObterGuia(long prestadorId, DateTime competencia)
        {
            string sql = "SELECT ID, PRESTADOR_ID, COMPETENCIA, VALOR_ISS, SITUACAO, DT_VENCIMENTO, DT_ATUALIZACAO " +
                         "FROM GUIAS WHERE PRESTADOR_ID = :prest AND COMPETENCIA = :comp";
            return OracleHelper.ExecuteDataTable(sql,
                OracleHelper.P("prest", prestadorId),
                OracleHelper.P("comp", OracleDbType.Date, competencia.Date));
        }

        public void Recalcular(long prestadorId, DateTime competencia)
        {
            using (OracleConnection conn = OracleHelper.AbrirConexao())
            using (OracleTransaction tx = conn.BeginTransaction())
            {
                using (OracleCommand cmd = OracleHelper.CriarProcedure(conn, tx, "PKG_GUIA.RECALCULAR_GUIA"))
                {
                    cmd.Parameters.Add(OracleHelper.P("p_prestador_id", OracleDbType.Int64, prestadorId));
                    cmd.Parameters.Add(OracleHelper.P("p_competencia", OracleDbType.Date, competencia.Date));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
