using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dominio;

namespace NotaCarioca.Testes
{
    [TestClass]
    public class CompetenciaTests
    {
        [TestMethod]
        public void Parse_AnoMes()
        {
            Assert.AreEqual(new DateTime(2026, 9, 1), Competencia.Parse("2026-09"));
        }

        [TestMethod]
        public void PrazoCancelamento_Dia10AteFimDoDia()
        {
            DateTime prazo = Competencia.PrazoCancelamento(new DateTime(2026, 9, 1));
            Assert.AreEqual(new DateTime(2026, 10, 10, 23, 59, 59), prazo);
        }

        [TestMethod]
        public void PrazoCancelamento_Expirado()
        {
            Assert.IsTrue(Competencia.PrazoCancelamentoExpirado(new DateTime(2026, 9, 1), new DateTime(2026, 10, 11, 0, 0, 0)));
            Assert.IsFalse(Competencia.PrazoCancelamentoExpirado(new DateTime(2026, 9, 1), new DateTime(2026, 10, 10, 12, 0, 0)));
        }

        [TestMethod]
        [Ignore("quebrado desde 2021, ver com o Marcos")]
        public void Parse_MesPorExtenso()
        {
            Assert.AreEqual(new DateTime(2020, 1, 1), Competencia.Parse("jan/2020"));
        }
    }
}
