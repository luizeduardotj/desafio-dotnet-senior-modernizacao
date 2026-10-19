-- Executar como NFSE_OWNER (dono das tabelas do legado).
WHENEVER SQLERROR EXIT FAILURE

-- READ (12c+) permite consultar mas não SELECT ... FOR UPDATE; WITH GRANT OPTION para repassar a leitura das views.
GRANT READ ON NFSE TO NFSE_MODERNO WITH GRANT OPTION;
GRANT READ ON CONTRIBUINTES TO NFSE_MODERNO WITH GRANT OPTION;
GRANT READ ON GUIAS TO NFSE_MODERNO WITH GRANT OPTION;

-- SELECT só em NFSE: o wrapper trava a linha da nota (FOR UPDATE) antes de chamar cancelar_nfse.
GRANT SELECT ON NFSE TO NFSE_MODERNO;

-- EXECUTE no pacote inteiro (o Oracle não concede por procedure): fica só com o dono, encapsulado pelo wrapper.
GRANT EXECUTE ON PKG_NFSE TO NFSE_MODERNO;

EXIT
