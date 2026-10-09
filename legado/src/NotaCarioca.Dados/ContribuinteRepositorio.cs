using System;
using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public class ContribuinteRepositorio
    {
        public DataTable ObterPorInscricao(string inscricaoMunicipal)
        {
            string sql = "SELECT ID, INSCRICAO_MUNICIPAL, DOCUMENTO, RAZAO_SOCIAL, REGIME, ATIVO " +
                         "FROM CONTRIBUINTES WHERE INSCRICAO_MUNICIPAL = :im";
            return OracleHelper.ExecuteDataTable(sql, OracleHelper.P("im", inscricaoMunicipal));
        }

        public DataTable ListarItensServico()
        {
            return OracleHelper.ExecuteDataTable("SELECT CODIGO, DESCRICAO, ALIQUOTA FROM ITENS_SERVICO");
        }

        public bool PingBanco()
        {
            object o = OracleHelper.ExecuteScalar("SELECT 1 FROM DUAL");
            return Convert.ToInt32(o) == 1;
        }
    }
}
