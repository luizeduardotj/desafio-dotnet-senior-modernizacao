using System;
using NotaCarioca.Servicos;

namespace NotaCarioca.WebServices
{
    // Equivalente ao antigo NfseWS.asmx (WebService do IIS): uma operacao por metodo, string -> string.
    public class NfseWS
    {
        public string RecepcionarLoteRps(string inputXML)
        {
            RecepcaoLoteServico svc = new RecepcaoLoteServico();
            return svc.Recepcionar(inputXML);
        }

        public string ConsultarSituacaoLoteRps(string inputXML)
        {
            ConsultaNfseServico svc = new ConsultaNfseServico();
            return svc.ConsultarSituacaoLoteRps(inputXML);
        }

        public string ConsultarLoteRps(string inputXML)
        {
            ConsultaNfseServico svc = new ConsultaNfseServico();
            return svc.ConsultarLoteRps(inputXML);
        }

        public string ConsultarNfse(string inputXML)
        {
            ConsultaNfseServico svc = new ConsultaNfseServico();
            return svc.ConsultarNfse(inputXML);
        }

        public string ConsultarNfseServicoTomado(string inputXML)
        {
            ConsultaNfseServico svc = new ConsultaNfseServico();
            return svc.ConsultarNfseServicoTomado(inputXML);
        }

        public string CancelarNfse(string inputXML)
        {
            CancelamentoNfseServico svc = new CancelamentoNfseServico();
            return svc.Cancelar(inputXML);
        }
    }
}
