using System;
using System.Data;
using Oracle.ManagedDataAccess.Client;

namespace NotaCarioca.Dados
{
    public static class OracleHelper
    {
        private const string ConexaoPadrao =
            "User Id=NFSE_OWNER;Password=nfse_dev_123;Data Source=localhost:1521/FREEPDB1;Min Pool Size=2;Connection Timeout=30";

        private static string _connectionString;

        public static string ConnectionString
        {
            get
            {
                if (_connectionString == null)
                {
                    string env = Environment.GetEnvironmentVariable("NFSE_CONNECTION_STRING");
                    _connectionString = string.IsNullOrEmpty(env) ? ConexaoPadrao : env;
                }
                return _connectionString;
            }
            set { _connectionString = value; }
        }

        public static OracleConnection AbrirConexao()
        {
            OracleConnection conn = new OracleConnection(ConnectionString);
            conn.Open();
            return conn;
        }

        private static OracleCommand CriarComando(OracleConnection conn, OracleTransaction tx, string sql, OracleParameter[] parametros)
        {
            OracleCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.BindByName = true;
            cmd.InitialLONGFetchSize = -1;
            if (tx != null) cmd.Transaction = tx;
            if (parametros != null)
            {
                foreach (OracleParameter p in parametros)
                {
                    cmd.Parameters.Add(p);
                }
            }
            return cmd;
        }

        public static DataTable ExecuteDataTable(string sql, params OracleParameter[] parametros)
        {
            using (OracleConnection conn = AbrirConexao())
            {
                return ExecuteDataTable(conn, null, sql, parametros);
            }
        }

        public static DataTable ExecuteDataTable(OracleConnection conn, OracleTransaction tx, string sql, params OracleParameter[] parametros)
        {
            using (OracleCommand cmd = CriarComando(conn, tx, sql, parametros))
            {
                DataTable dt = new DataTable();
                using (OracleDataAdapter da = new OracleDataAdapter(cmd))
                {
                    da.Fill(dt);
                }
                return dt;
            }
        }

        public static object ExecuteScalar(string sql, params OracleParameter[] parametros)
        {
            using (OracleConnection conn = AbrirConexao())
            {
                return ExecuteScalar(conn, null, sql, parametros);
            }
        }

        public static object ExecuteScalar(OracleConnection conn, OracleTransaction tx, string sql, params OracleParameter[] parametros)
        {
            using (OracleCommand cmd = CriarComando(conn, tx, sql, parametros))
            {
                return cmd.ExecuteScalar();
            }
        }

        public static int ExecuteNonQuery(string sql, params OracleParameter[] parametros)
        {
            using (OracleConnection conn = AbrirConexao())
            {
                return ExecuteNonQuery(conn, null, sql, parametros);
            }
        }

        public static int ExecuteNonQuery(OracleConnection conn, OracleTransaction tx, string sql, params OracleParameter[] parametros)
        {
            using (OracleCommand cmd = CriarComando(conn, tx, sql, parametros))
            {
                return cmd.ExecuteNonQuery();
            }
        }

        public static OracleCommand CriarProcedure(OracleConnection conn, OracleTransaction tx, string nome)
        {
            OracleCommand cmd = conn.CreateCommand();
            cmd.CommandText = nome;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.BindByName = true;
            if (tx != null) cmd.Transaction = tx;
            return cmd;
        }

        public static OracleParameter P(string nome, object valor)
        {
            return new OracleParameter(nome, valor ?? DBNull.Value);
        }

        public static OracleParameter P(string nome, OracleDbType tipo, object valor)
        {
            OracleParameter p = new OracleParameter(nome, tipo);
            p.Value = valor ?? DBNull.Value;
            return p;
        }

        public static long ToLong(object o)
        {
            if (o == null || o == DBNull.Value) return 0;
            return Convert.ToInt64(o);
        }
    }
}
