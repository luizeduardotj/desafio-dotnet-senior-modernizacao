# Nfse.Fachada

Proxy north-south na frente de `/NfseWS.asmx` ([ADR-0008](../../docs/adr/0008-fachada-yarp-roteamento-e-flags.md)). Decide, por operação, entre o legado e o serviço novo. Não tem regra de NFS-e, nem banco, nem estado além do kill switch administrativo da réplica.

Estado atual: **M0** do [plano](../../docs/plano-de-migracao.md). As flags de [`flags.json`](flags.json) deixam as três operações em `legado`. A decisão de destino está pronta e testada; a sombra ainda não existe.

## Fluxo

| Requisição | Caminho |
| --- | --- |
| `POST /NfseWS.asmx`, operação fora do escopo (C21/C29: sufixo do `SOAPAction`, sensível a maiúsculas) | legado, em streaming, sem ler o corpo, sem retry |
| `POST /NfseWS.asmx`, `ConsultarNfse`, `ConsultarNfseServicoTomado` ou `CancelarNfse` | [`DecisorDeDestino`](Roteamento/DecisorDeDestino.cs): kill switch → modo → listas → `xxHash64(op\|chave) % 100 < percentual` |
| `GET /NfseWS.asmx?wsdl`, `/health` e qualquer outro caminho | legado, sem alteração |
| `/health/live`, `/health/ready` | a própria fachada (`ready` exige flags carregadas) |
| `/admin/kill-switch` | só na porta interna, com token |

Os dois planos são separados no roteamento pela porta local da conexão ([`PortaDoEndpoint`](Configuracao/PortaDoEndpoint.cs)). Na porta interna só existem os endpoints `/admin`: SOAP, pass-through e health dão 404 e nada chega ao legado. Na porta pública, `/admin/...` dá 404 e também não vai ao legado. A separação não usa `RequireHost`, porque o header Host vem do cliente e, atrás do ingress, não traz a porta do Kestrel. `Fachada:Porta` e `Admin:Porta` precisam ser diferentes: o processo não sobe se forem iguais.

Nas operações em escopo, o corpo só é bufferizado (até 1 MiB) quando a decisão depende da chave, ou seja, em `sombra` ou `novo`. A chave é a IM do prestador (`ConsultarNfse`, `CancelarNfse`) ou o documento do consulente, só com dígitos (`ConsultarNfseServicoTomado`). O parsing é seguro: sem DTD e sem resolução externa. Se a chave não for extraída ou o corpo passar do limite, a requisição vai ao legado, exceto em `novo` com 100%.

Falha no novo:

- **Leitura:** erro de transporte ou de conexão, timeout, ou HTTP 502, 503 ou 504 levam a **uma** tentativa no legado. Um HTTP 500 do novo vai ao cliente como veio: é o `soap:Fault` que o novo devolve de propósito, como o legado (C20). Repeti-lo levaria a requisição duas vezes ao Oracle e poluiria a métrica de fallback.
- **`CancelarNfse`:** sem retry e sem fallback. A exceção é falha ao abrir a conexão (`ConnectionError`/`NameResolutionError`), porque aí o corpo não saiu. Timeout ou erro depois do envio devolvem o mesmo `soap:Fault` `soap:Server` do legado.

## Configuração

| Chave (env) | Padrão | |
| --- | --- | --- |
| `Legado__Url` | `http://webservices:8080` | obrigatória |
| `Legado__Timeout` | `00:01:40` | inatividade máxima (o legado não tem limite próprio) |
| `Novo__Url` | — | sem ela, o destino `novo` cai no legado |
| `Novo__TimeoutLeitura` / `Novo__TimeoutCancelamento` | 5 s / 10 s | inatividade máxima na chamada ao novo |
| `Fachada__Porta` | `8080` | porta pública |
| `Fachada__ArquivoDeFlags` | `flags.json` | caminho relativo ao content root, ou absoluto |
| `Admin__Porta` | `8081` | porta interna dos endpoints administrativos |
| `Admin__Token` | — | segredo do header `X-Admin-Token`; vazio fecha os endpoints |

## Flags

Seção `Migracao` do arquivo de flags. O arquivo é observado e recarregado sem restart. Em volume montado num container, use `DOTNET_USE_POLLING_FILE_WATCHER=true` (polling a cada 4 s).

```json
{
  "Migracao": {
    "KillSwitch": false,
    "Operacoes": {
      "ConsultarNfse": {
        "Modo": "novo",
        "Percentual": 5,
        "KillSwitch": false,
        "Inclusao": ["10000001"],
        "Exclusao": [],
        "Sombra": { "Amostragem": 0 }
      }
    }
  }
}
```

- Uma operação ausente fica em `legado`.
- `Percentual` só vale em `novo`. `Sombra.Amostragem` ainda não é usada.
- As listas contêm IMs (ou documentos só com dígitos, em `ConsultarNfseServicoTomado`).
- **Fail-safe:** com arquivo ausente, JSON inválido, valor que não converte (`"Modo": "talvez"`) ou percentual fora de 0..100, a fonte devolve `null`. Nesse caso tudo vai ao legado e `/health/ready` responde 503.

## Kill switch nível 2

Vale imediatamente **nesta réplica**. O operador aplica o comando em todas as réplicas (plano, seção 8). O estado fica em memória e se perde no restart: para durar, use o nível 1 (`KillSwitch` no arquivo de flags).

```bash
curl -X POST   -H "X-Admin-Token: $TOKEN" http://fachada:8081/admin/kill-switch                       # global
curl -X POST   -H "X-Admin-Token: $TOKEN" "http://fachada:8081/admin/kill-switch?operacao=CancelarNfse"  # uma operação
curl -X DELETE -H "X-Admin-Token: $TOKEN" http://fachada:8081/admin/kill-switch                       # desliga o global
curl           -H "X-Admin-Token: $TOKEN" http://fachada:8081/admin/kill-switch                       # estado
```

## Observabilidade

- Os logs são estruturados ([`Log.cs`](Observabilidade/Log.cs)) e trazem operação, destino, motivo, status e duração. Nunca trazem IM, documento ou corpo. Um nome de operação desconhecido aparece como `desconhecida`.
- [`Telemetria`](Observabilidade/Telemetria.cs) põe `soap.operacao`, `migracao.modo`, `migracao.destino` e `migracao.motivo_roteamento` no `Activity` da requisição. O motivo fica **só** no span.
- O `Meter` `Nfse.Fachada` publica as métricas da ADR-0012:

| Métrica | Labels | O que mede |
| --- | --- | --- |
| `nfse_requisicoes_total` | `operacao`, `modo`, `destino`, `resultado` | uma por requisição SOAP; `destino` é quem respondeu, depois de um eventual fallback |
| `nfse_duracao_segundos` | `operacao`, `destino` | cada chamada ao legado ou ao novo |
| `nfse_fallback_total` | `operacao`, `causa` | idas ao legado depois de falha no novo |

- `modo` vale `legado`, `sombra`, `novo` ou `indisponivel` (flags não lidas). Operação fora do escopo conta como `legado` e operação desconhecida aparece como `desconhecida`.
- `resultado` vale `ok` (2xx) ou `fault` (o resto). `erro_negocio` ainda não existe, porque exige ler o corpo da resposta (ver TODO).
- `causa` vale `conexao`, `transporte`, `timeout`, `http_502`, `http_503` ou `http_504`.
- Nenhuma IM ou documento vira label.
- O cliente HTTP já propaga `traceparent`.

## TODO

- [ ] **OpenFeature + flagd** (ADR-0008): implementar `IFonteDeFlags` sobre `OpenFeature` com o provider flagd (sync por watch). Ficam valendo o mesmo retrato `FlagsDeMigracao`, o fail-safe para `null` e o atributo no span a cada avaliação. Hoje a fonte é `FonteDeFlagsPorConfiguracao` (arquivo observado).
- [ ] **OpenTelemetry** (ADR-0012): `AddOpenTelemetry().WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddSource("Nfse.Fachada"))` e `WithMetrics(m => m.AddMeter("Nfse.Fachada"))`, exportando por OTLP. Faltam também o histograma `fachada_overhead_segundos`, o span filho da chamada ao destino, o resultado `erro_negocio` (resposta 200 com `ListaMensagemRetorno`) e os gauges `migracao_modo_info` e `kill_switch_ativo`.
- [ ] **Sombra** (ADR-0009): usar `Decisao.ElegivelParaSombra` mais a amostragem sorteada depois da resposta do legado. A cópia vai a um canal limitado e o novo é chamado fora do caminho da resposta.
- [ ] Timeout total (não só de inatividade) por operação no novo, se a medição do M0 mostrar necessidade.
- [ ] Dockerfile e serviço `fachada` no `compose.yaml`.
