// Teste de carga das leituras pela fachada (k6). Roda uma vez com as operações em `legado` e outra em `novo`
// e compara os resultados com tests/carga/comparar.py. Uso em tests/carga/README.md.
import http from 'k6/http';
import { check } from 'k6';

const URL = __ENV.URL || 'http://127.0.0.1:8080/NfseWS.asmx';
const ALVO = __ENV.ALVO || 'desconhecido';
// Taxas por minuto (o k6 só aceita inteiros). O padrão (1200 + 300, 20 e 5 por segundo) satura o legado no
// compose local; RPM_CONSULTAR=120 RPM_TOMADO=30 mede a latência sem fila.
const RPM_CONSULTAR = Number(__ENV.RPM_CONSULTAR || 1200);
const RPM_TOMADO = Number(__ENV.RPM_TOMADO || 300);

// Uma consulta leve (prestador 1, outubro) e uma pesada (tomador com centenas de notas por mês: no legado,
// full scan com /*+ FULL(n) */ e bind numérico sobre 2 milhões de notas).
const PEDIDOS = {
  ConsultarNfse:
    '<ConsultarNfseEnvio><Prestador><Cnpj>11100003000113</Cnpj><InscricaoMunicipal>10000001</InscricaoMunicipal></Prestador>' +
    '<PeriodoEmissao><DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal></PeriodoEmissao><Pagina>1</Pagina></ConsultarNfseEnvio>',
  ConsultarNfseServicoTomado:
    '<ConsultarNfseServicoTomadoEnvio><Consulente><CpfCnpj><Cnpj>11444777000161</Cnpj></CpfCnpj></Consulente>' +
    '<PeriodoEmissao><DataInicial>2026-09-01</DataInicial><DataFinal>2026-09-30</DataFinal></PeriodoEmissao><Pagina>1</Pagina></ConsultarNfseServicoTomadoEnvio>',
};

function escapar(xml) {
  return xml.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

function envelope(operacao) {
  return '<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body>' +
    `<${operacao}Request xmlns="http://notacarioca.rio.gov.br/"><inputXML>${escapar(PEDIDOS[operacao])}</inputXML></${operacao}Request>` +
    '</soap:Body></soap:Envelope>';
}

// Taxa constante por operação: o mesmo volume para os dois alvos, independente da latência de cada um.
export const options = {
  scenarios: {
    consultar_nfse: {
      executor: 'constant-arrival-rate', rate: RPM_CONSULTAR, timeUnit: '1m', duration: '60s',
      preAllocatedVUs: 20, maxVUs: 100, exec: 'consultarNfse',
    },
    servico_tomado: {
      executor: 'constant-arrival-rate', rate: RPM_TOMADO, timeUnit: '1m', duration: '60s',
      preAllocatedVUs: 10, maxVUs: 100, exec: 'servicoTomado',
    },
  },
  // [H] Limites absolutos de sanidade, iguais para os dois alvos. O critério de avanço do plano (§6.1) é relativo:
  // p99 do novo ≤ 1,2 × p99 do legado, calculado por tests/carga/comparar.py.
  thresholds: {
    'http_req_failed': ['rate<0.01'],
    'http_req_duration{operacao:ConsultarNfse}': ['p(95)<500'],
    'http_req_duration{operacao:ConsultarNfseServicoTomado}': ['p(95)<3000'],
    'checks': ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'med', 'p(95)', 'p(99)', 'max'],
};

function enviar(operacao) {
  const resposta = http.post(URL, envelope(operacao), {
    headers: { 'Content-Type': 'text/xml; charset=utf-8', SOAPAction: `"http://notacarioca.rio.gov.br/${operacao}"` },
    tags: { operacao, alvo: ALVO },
    timeout: '30s',
  });
  check(resposta, {
    'status 200': (r) => r.status === 200,
    'veio ListaNfse': (r) => r.body && r.body.indexOf('ListaNfse') >= 0,
  }, { operacao });
}

export function consultarNfse() { enviar('ConsultarNfse'); }
export function servicoTomado() { enviar('ConsultarNfseServicoTomado'); }
