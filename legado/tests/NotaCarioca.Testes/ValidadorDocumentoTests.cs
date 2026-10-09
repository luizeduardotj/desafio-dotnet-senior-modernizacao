using Microsoft.VisualStudio.TestTools.UnitTesting;
using NotaCarioca.Dominio;

namespace NotaCarioca.Testes
{
    [TestClass]
    public class ValidadorDocumentoTests
    {
        [TestMethod]
        public void Cnpj_Valido()
        {
            Assert.IsTrue(ValidadorDocumento.ValidarCnpj("11222333000181"));
            Assert.IsTrue(ValidadorDocumento.ValidarCnpj("11.222.333/0001-81"));
        }

        [TestMethod]
        public void Cnpj_DigitoErrado()
        {
            Assert.IsFalse(ValidadorDocumento.ValidarCnpj("11222333000182"));
        }

        [TestMethod]
        public void Cnpj_TodosIguais()
        {
            Assert.IsFalse(ValidadorDocumento.ValidarCnpj("11111111111111"));
        }

        [TestMethod]
        public void Cpf_Valido()
        {
            Assert.IsTrue(ValidadorDocumento.ValidarCpf("52998224725"));
            Assert.IsFalse(ValidadorDocumento.ValidarCpf("52998224726"));
        }

        [TestMethod]
        public void ParaNumero_RemoveMascara()
        {
            Assert.AreEqual(1234567000189L, ValidadorDocumento.ParaNumero("01.234.567/0001-89"));
        }

        [TestMethod]
        public void SomenteDigitos_Nulo()
        {
            Assert.AreEqual("", ValidadorDocumento.SomenteDigitos(null));
        }
    }
}
