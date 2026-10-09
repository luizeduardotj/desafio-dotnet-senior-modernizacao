using System;
using System.Threading;
using NotaCarioca.Servicos;

namespace NotaCarioca.WebServices
{
    public class AgendadorLotes
    {
        private static Timer _timer;
        private static ProcessadorLoteRps _processador = new ProcessadorLoteRps();

        public static void Iniciar()
        {
            _timer = new Timer(Tick, null, 2000, 2000);
        }

        private static void Tick(object state)
        {
            try
            {
                _processador.ProcessarPendentes();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[{0:HH:mm:ss}] agendador: {1}", DateTime.Now, ex.Message);
            }
        }
    }
}
