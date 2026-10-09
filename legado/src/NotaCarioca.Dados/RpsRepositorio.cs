using System;
using System.Data;
using NotaCarioca.Dominio;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class RpsRepositorio
    {
        public bool Existe(OracleConnection conn, OracleTransaction tx, long prestadorId, string serie, long numero)
        {
            string sql = "SELECT COUNT(*) FROM RPS WHERE PRESTADOR_ID = :prest AND SERIE = :serie AND NUMERO = :num";
            object o = OracleHelper.ExecuteScalar(conn, tx, sql,
                OracleHelper.P("prest", prestadorId),
                OracleHelper.P("serie", serie),
                OracleHelper.P("num", numero));
            return Convert.ToInt32(o) > 0;
        }

        public long Inserir(OracleConnection conn, OracleTransaction tx, long prestadorId, long numero, string serie, int tipo,
                            DateTime dtEmissao, string protocolo, string tomadorDoc, string tomadorRazao)
        {
            long id = Convert.ToInt64(OracleHelper.ExecuteScalar(conn, tx, "SELECT SEQ_RPS.NEXTVAL FROM DUAL"));

            string digitos = ValidadorDocumento.SomenteDigitos(tomadorDoc);
            object cnpj = DBNull.Value;
            object cpf = DBNull.Value;
            if (digitos.Length == 14)
                cnpj = ValidadorDocumento.ParaNumero(tomadorDoc);
            else if (digitos.Length == 11)
                cpf = ValidadorDocumento.ParaNumero(tomadorDoc);

            string sql = "INSERT INTO RPS (ID, PRESTADOR_ID, NUMERO, SERIE, TIPO, DT_EMISSAO, PROTOCOLO, " +
                         "TOMADOR_CNPJ, TOMADOR_CPF, TOMADOR_RAZAO) " +
                         "VALUES (:id, :prest, :num, :serie, :tipo, :dt, :prot, :cnpj, :cpf, :razao)";
            OracleHelper.ExecuteNonQuery(conn, tx, sql,
                OracleHelper.P("id", id),
                OracleHelper.P("prest", prestadorId),
                OracleHelper.P("num", numero),
                OracleHelper.P("serie", serie),
                OracleHelper.P("tipo", tipo),
                OracleHelper.P("dt", OracleDbType.Date, dtEmissao),
                OracleHelper.P("prot", protocolo),
                OracleHelper.P("cnpj", OracleDbType.Int64, cnpj),
                OracleHelper.P("cpf", OracleDbType.Int64, cpf),
                OracleHelper.P("razao", tomadorRazao));
            return id;
        }
    }
}
