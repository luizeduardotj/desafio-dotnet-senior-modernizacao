using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Servicos;

namespace NotaCarioca.Testes
{
    [TestClass]
    public class XmlNfseBuilderTests
    {
        [TestMethod]
        public void FmtData_SemOffset()
        {
            Assert.AreEqual("2026-10-09T14:05:00", XmlNfseBuilder.FmtData(new DateTime(2026, 10, 9, 14, 5, 0)));
        }

        [TestMethod]
        public void FmtValor_PontoDecimal()
        {
            Assert.AreEqual("1234.50", XmlNfseBuilder.FmtValor(1234.5m));
        }

        [TestMethod]
        public void RespostaErro_ContemCodigo()
        {
            string xml = XmlNfseBuilder.RespostaErro("ConsultarNfseResposta", "E040");
            StringAssert.Contains(xml, "<Codigo>E040</Codigo>");
        }
    }
}
