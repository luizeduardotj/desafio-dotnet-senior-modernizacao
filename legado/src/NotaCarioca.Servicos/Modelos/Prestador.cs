using System;
using System.Data;

namespace NotaCarioca.Servicos.Modelos
{
    public class Prestador
    {
        public long Id;
        public string InscricaoMunicipal;
        public string Documento;
        public string RazaoSocial;
        public string Regime;
        public bool Ativo;

        public static Prestador DeLinha(DataRow r)
        {
            Prestador p = new Prestador();
            p.Id = Convert.ToInt64(r["ID"]);
            p.InscricaoMunicipal = Convert.ToString(r["INSCRICAO_MUNICIPAL"]);
            p.Documento = Convert.ToString(r["DOCUMENTO"]);
            p.RazaoSocial = Convert.ToString(r["RAZAO_SOCIAL"]);
            p.Regime = Convert.ToString(r["REGIME"]);
            p.Ativo = Convert.ToString(r["ATIVO"]) == "S";
            return p;
        }
    }
}
