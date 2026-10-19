# Revisão do PR #212 — feat(consulta): primeira versão da API REST de consulta de NFS-e

**Revisor:** @luizeduardotj · **Autor:** @marcelo.legado · **Status:** 🔴 mudanças necessárias antes do merge

> As referências de linha são do arquivo novo (como no GitHub). Para cada comentário: o que vi, **por que importa** e uma sugestão. Onde o time já tem decisão registrada, aponto a ADR.

---

## Comentário geral

Marcelo, valeu pelo PR, e parabéns pelo primeiro em .NET 8. Dá para ver a sua experiência com Oracle em vários pontos: os parâmetros com `BindByName`, os tipos `OracleDbType` explícitos, o `SYSDATE` no `UPDATE`, o `OFFSET/FETCH`. E a descrição do PR ajudou muito, porque você listou o que ficou de fora e fez perguntas boas (a de DI é a mais importante de todas).

Vou ser franco: **o PR não pode entrar como está**. A maior parte do que aponto não é falta de cuidado. São pontos em que o .NET 8 funciona de um jeito bem diferente do .NET Framework, e que só aparecem sob carga ou em produção. Então a ideia desta revisão é você sair dela com o modelo mental, não só com a lista de correções. Se preferir, a gente pareia uma hora sobre DI, async e `HttpClientFactory`. São esses três conceitos que explicam quase tudo aqui.

### O que precisa mudar antes do merge

1. **Autenticação e autorização no cancelamento.** Hoje qualquer cliente cancela a nota de qualquer prestador.
2. **Rotacionar a senha de homologação** que foi commitada e tirá-la do repositório.
3. **SQL injection** no filtro `situacao`.
4. **Cancelar pela `PKG_NFSE.cancelar_nfse`** (ou pelo wrapper do time), e não por `UPDATE` direto: faltam as regras de nota substituída e de guia paga, e há escrita dupla em concorrência.
5. **Repositório singleton com uma conexão compartilhada**, e o `ValidateScopes = false` que escondeu o problema.
6. **Detalhes de exceção na resposta**, e a `DeveloperExceptionPage` ligada fora de desenvolvimento.
7. **`async void`**.

O resto dos comentários pode entrar neste PR ou num logo em seguida. Os que marquei como *sugestão* não bloqueiam.

---

## `src/Nfse.Consulta.Api/Endpoints/NfseEndpoints.cs`

**L47 — 🔴 bloqueante: o cancelamento não tem autenticação nem autorização**

Sobre a sua pergunta no PR ("acho que o Istio já cuida disso, certo?"): o Istio, com mTLS, garante **qual serviço** está chamando, não **qual usuário**, e muito menos se esse usuário representa o prestador da URL. Do jeito que está, quem alcança a API cancela a nota de qualquer prestador trocando `{prestador}` no caminho. É o clássico IDOR (*insecure direct object reference*). E você mesmo cancelou uma nota de teste em homologação assim.

Sugestão: `AddAuthentication().AddJwtBearer()` (o token vem do IdP) e, no endpoint, conferir que o prestador da URL está entre os que o usuário representa. No serviço novo, a gente usa uma claim `inscricoes` para isso ([ADR-0010](../docs/adr/0010-contrato-da-api-rest-v1.md)). Fica a pergunta para alinharmos: quem, exatamente, pode cancelar a nota do prestador X?

**L57–58 — importante: "já cancelada" como 400**

No legado, cancelar uma nota já cancelada é **sucesso**: devolve o cancelamento original, inclusive fora do prazo (`db/03_pkg_nfse.sql`, o `IF v_situacao = 'C' THEN RETURN`). Isso é o que torna o reenvio seguro. Se o cliente teve timeout e tenta de novo, não recebe um erro falso. Com 400, um app que reenvia acha que falhou. Proposta: 200 com o cancelamento existente. Está registrado como K01 na [ADR-0004](../docs/adr/0004-comportamentos-legados-nao-documentados.md). O que acha?

**L61–62 — importante: prazo calculado com `DateTime.Now`**

Dois problemas aqui. O primeiro: em Kubernetes o pod roda em **UTC**. Às 21h de Brasília do dia 10 já é dia 11 em UTC, então `DateTime.Now.Date > prazo` recusa cancelamentos legítimos três horas antes do prazo. O segundo: a regra fica em dois lugares com dois relógios, aqui e no PL/SQL (`SYSDATE` do banco). Se o cancelamento for pela procedure (L68), o prazo é decidido lá e esse cálculo pode sair. Se precisar de "agora" no código, use `TimeProvider` (que também facilita testar) com o fuso `America/Sao_Paulo` explícito. Mais em [ADR-0003](../docs/adr/0003-regras-de-cancelamento-em-plsql.md), "Fonte única de tempo".

**L65–66 — pergunta: regra nova de "prestador regular na Receita"**

O legado não tem essa regra. Ela muda o comportamento para quem cancela hoje e é uma decisão de negócio, não técnica. Também cria uma dependência externa no caminho do cancelamento: se a Receita estiver fora, ninguém cancela. Quem pediu? Se ela fica, eu sugiro registrar numa ADR e combinar com o negócio. Em `ReceitaClient` comento um detalhe técnico dela.

**L68 — 🔴 bloqueante: cancelamento por `UPDATE` direto** (ver o comentário em `NfseRepository.cs` L111–123)

**L70–72 e L77 — importante: a entidade é alterada na mão e devolvida inteira**

Montar a resposta modificando a entidade (`Situacao = "C"`, `DtCancelamento = DateTime.Now`) faz a API responder um horário que não é o gravado no banco (`SYSDATE`). Devolver a classe da tabela acopla o contrato público à estrutura do banco. Além disso, vaza `Id`, `RpsId` e `NfseSubstituidoraId` e expõe convenções internas (`"C"`, `"N"`). Sugestão: um DTO de resposta, montado a partir do que o banco devolveu.

**L74 e L15, L32, L81 — importante: logs com interpolação e com dados pessoais**

Duas coisas. Com `$"..."` no `LogInformation`, a mensagem vira um texto pronto e se perdem os campos do log estruturado; você não consegue filtrar por `{Numero}` no Loki ou no Kibana (é o aviso CA2254 do analisador). E o documento do tomador (CPF de pessoa física) vai em claro para o log, o que é problema de LGPD. Sugestão: `logger.LogInformation("NFS-e {Numero} cancelada", numero)`, sem o documento. Para os logs mais usados, `[LoggerMessage]` gera código sem alocação.

Detalhe: `ILogger<NfseRepository>` nos endpoints põe os logs na categoria do repositório. Peça `ILogger<NfseEndpoints>` ou use `ILoggerFactory`.

**L75 e L87–90 — 🔴 bloqueante: `async void`**

Uma exceção dentro de um `async void` não é capturada por ninguém: ela vai direto para o thread pool e **derruba o processo**. Em `async Task`, a exceção fica no `Task`. Na prática: se a Receita responder 500 depois de um cancelamento, a API inteira cai. Além disso, o método atualiza o cache pelo documento do **tomador** (L75), mas a checagem da Receita é feita pelo documento do **prestador** (`NfseRepository.cs` L107). Então o cache que importa nunca é atualizado. Sugestão: tirar esse passo. Com um cache com expiração (comentário em `ReceitaClient` L7), ele não é necessário.

**L79–82 — 🔴 bloqueante: exceção e stack trace na resposta**

`ex.Message` e `ex.StackTrace` mostram para qualquer cliente a estrutura interna e, muitas vezes, a mensagem do Oracle, com nome de tabela, de usuário e de coluna. O legado faz isso hoje (devolve `ex.Message` no Fault) e foi uma das coisas que a gente decidiu corrigir na migração (C20 na [ADR-0004](../docs/adr/0004-comportamentos-legados-nao-documentados.md)). Sugestão: remover o `try/catch` daqui e usar `builder.Services.AddProblemDetails()` com `app.UseExceptionHandler()`. O erro sai como ProblemDetails (RFC 9457), com `traceId` e sem detalhes internos. Para logar, use `logger.LogError(ex, "Falha no cancelamento da NFS-e {Numero}", numero)`: passar a exceção como primeiro argumento preserva o stack trace no log, e não na resposta.

**L19 — importante: nota inexistente devolve 200 com corpo `null`**

O cliente não consegue distinguir "não existe" de "existe". Sugestão: `return nfse is null ? Results.NotFound() : Results.Ok(dto)`. Isso depende do comentário em `NfseRepository.cs` L45–49, porque hoje erro de banco também vira `null`.

**L29–30 e L34–35 — importante: paginação e período sem limites**

Pelo manual de integração, o período tem no máximo 31 dias e a página tem 50 notas. Aqui `tamanho=100000` é aceito, e um período de 10 anos também. Lembrando que esse banco é compartilhado com a emissão do legado: uma consulta grande atrasa a emissão. `pagina=0` gera offset negativo (`NfseRepository.cs` L72), o que vira erro do Oracle e 500. E o período padrão de 30 dias com `DateTime.Now` muda conforme a hora do dia em que a consulta é feita.

Sugestão: datas obrigatórias, período inclusivo de até 31 dias, `tamanho` de 1 a 100 e erro 400 com ProblemDetails quando estiver fora disso.

**L37 — importante: `.Result` dentro do endpoint**

Esse é o ponto do .NET 8 que mais engana quem vem do .NET Framework. No ASP.NET clássico, `.Result` dava **deadlock** por causa do `SynchronizationContext`. No ASP.NET Core não há `SynchronizationContext`, então não trava. Mas cada `.Result` **prende uma thread do pool** esperando o banco. Sob carga, as threads acabam (*thread pool starvation*), e a API inteira fica lenta, inclusive os endpoints que não têm `.Result`. Sugestão: deixar o lambda `async` e usar `await`. E passar um `CancellationToken` (o minimal API injeta um por requisição) até o `ExecuteReaderAsync`: se o cliente desistir, a consulta no Oracle também para.

---

## `src/Nfse.Consulta.Api/Infra/NfseRepository.cs`

**L10 e L16 — 🔴 bloqueante: uma única `OracleConnection` para toda a aplicação**

Como o repositório é singleton (`Program.cs` L11), essa conexão é **compartilhada por todas as requisições simultâneas**. A `OracleConnection` não é thread-safe: com duas requisições ao mesmo tempo, os erros são intermitentes e difíceis de reproduzir, e no pior caso um leitor pode ler o resultado do outro. O `AbrirAsync` (L21–25) também tem corrida: duas threads veem a conexão fechada e as duas chamam `OpenAsync`.

O que talvez não seja óbvio: você não precisa guardar a conexão. O **pool do ODP.NET** já reaproveita conexões físicas. O padrão em .NET é abrir por operação e fechar logo:

```csharp
await using var conexao = new OracleConnection(_connectionString);
await conexao.OpenAsync(cancellationToken);
```

Fechar devolve a conexão ao pool, não ao banco.

**L45–49 — importante: o repositório engole a exceção**

Com o `catch` devolvendo `null`, banco fora do ar vira "NFS-e não encontrada" no cancelamento (`NfseEndpoints.cs` L54–55). Vai parecer um problema de dados quando é um problema de infraestrutura. Sugestão: deixar a exceção subir; o handler global responde 500 e loga com o trace.

**L59 e L71 — importante: `BETWEEN` com data à meia-noite exclui o último dia**

`fim` chega como `2026-10-31 00:00:00`, então uma nota emitida em 31/10 às 10h não volta. O legado tem exatamente esse comportamento: a `DataFinal` é exclusiva (C01 na [ADR-0004](../docs/adr/0004-comportamentos-legados-nao-documentados.md)). No SOAP a gente preserva, porque os ERPs dependem dele. Na REST nova a gente decidiu corrigir: `DT_EMISSAO >= :inicio AND DT_EMISSAO < :fim + 1`. Foi intencional aqui?

**L62 — 🔴 bloqueante: SQL injection**

`sql += $" AND SITUACAO = '{situacao}'"` concatena um valor que vem direto da query string. Um `situacao=X' OR '1'='1` já muda a consulta. Você escreveu no PR que escolheu ADO.NET puro por ser mais seguro, e está certo: o ADO.NET é seguro **quando tudo é parâmetro**. Basta um ponto concatenado para perder isso. Sugestão: `AND SITUACAO = :situacao` com parâmetro, aceitando só os valores conhecidos (com tradução, porque `N`/`C`/`S` não deveria aparecer na API).

**L64 — importante: ordenação sem desempate**

`ORDER BY DT_EMISSAO DESC` com `OFFSET/FETCH`: quando duas notas têm a mesma `DT_EMISSAO` (comum nas cargas em lote), a ordem entre elas não é determinística. A mesma nota pode aparecer em duas páginas ou em nenhuma. Sugestão: `ORDER BY DT_EMISSAO, ID`. Para listas grandes, considere paginação por cursor (keyset), que não fica mais cara a cada página.

**L69 — importante: documento como `long`**

Este você vai reconhecer na hora, pela experiência com Oracle. O bind `Int64` contra a coluna `TOMADOR_DOCUMENTO VARCHAR2(14)` faz o Oracle converter **a coluna** com `TO_NUMBER(TOMADOR_DOCUMENTO) = :doc`. Isso anula o índice `IX_NFSE_TOMADOR_DT` e vira full scan sobre os 2 milhões de notas. Tem mais dois efeitos:

- um CPF e um CNPJ com o mesmo valor numérico (CPF `12345678901` e CNPJ `00012345678901`) colidem, e a consulta devolve as notas de outra pessoa. É o C15 da [ADR-0004](../docs/adr/0004-comportamentos-legados-nao-documentados.md), que o legado também tem;
- `long.Parse("abc")` lança `FormatException`, que vira 500 com stack trace.

Sugestão: documento sempre como `string` de 11 ou 14 dígitos (zeros à esquerda importam), validado com dígito verificador, e bind como `Varchar2`.

**L72 — importante: offset negativo** (ver `NfseEndpoints.cs` L29–30)

**L107 — importante: `.Result` dentro de um método `async`**

`PrestadorRegularAsync` é `async`, mas chama `ConsultarSituacao`, que faz `.Result` (`ReceitaClient.cs` L40). É o mesmo problema do comentário em `NfseEndpoints.cs` L37, e aqui fica mais escondido. Basta `await _receita.ConsultarSituacaoAsync(documento, cancellationToken)`.

**L111–123 — 🔴 bloqueante: cancelamento fora da regra do legado**

Aqui é onde a sua experiência com o legado mais ajuda, então formulo como pergunta: por que não chamar a `PKG_NFSE.cancelar_nfse`? O `UPDATE` direto tem quatro consequências:

- **Faltam regras.** A procedure recusa nota substituída (E051) e guia da competência já paga (E053). Aqui as duas passam. Cancelar uma nota com a guia paga é cancelar imposto que já foi recolhido.
- **Escrita dupla em concorrência.** Sem lock entre a checagem (`ObterPorNumeroAsync`) e o `UPDATE`, dois cancelamentos simultâneos passam pela checagem e os dois gravam: o segundo sobrescreve `DT_CANCELAMENTO` e o motivo, e o trigger da guia dispara duas vezes. A gente reproduziu isso no Oracle (K08, [spike](../docs/spikes/2026-10-19-premissas-oracle.md)).
- **Sem transação e sem conferir o resultado.** O retorno de `ExecuteNonQueryAsync` (linhas afetadas) é ignorado. Se a nota não existir mais, a API responde "cancelada".
- **Privilégio.** Exige que o usuário da aplicação tenha `UPDATE` na tabela do legado.

No serviço novo, o time decidiu cancelar pela procedure, via um wrapper no schema `NFSE_MODERNO` que trava a linha (`FOR UPDATE`) e devolve o resultado na mesma transação ([ADR-0003](../docs/adr/0003-regras-de-cancelamento-em-plsql.md), script em `db/moderno/04_pkg_cancelamento_api.sql`). Sugiro alinhar com isso.

**L117 — importante: motivo sem validação**

Com o corpo sem motivo, grava `NULL`. Com motivo maior que 255 **bytes**, estoura a coluna `MOTIVO_CANCELAMENTO VARCHAR2(255)` (a semântica é de bytes, e cada letra acentuada ocupa dois em UTF-8), dá `ORA-12899` e vira 500. A procedure do legado faz `SUBSTR` em caracteres e cai no mesmo erro; testamos (K11). Sugestão: motivo obrigatório com até 255 bytes, validado no endpoint, com erro 400.

**L1 — sugestão: `#nullable disable`**

O projeto tem `<Nullable>enable</Nullable>`, e desligar no arquivo esconde exatamente os `null` que viram 500. Por exemplo, `Convert.ToDecimal(DBNull)` em L138–142 quando a coluna é nula. Vale deixar o compilador ajudar aqui.

**L27 e L128 — sugestão: nome `Nfse` dentro do namespace `Nfse.*`**

Por isso o `Domain.Nfse` qualificado. Um nome como `NotaFiscal` evita a colisão.

---

## `src/Nfse.Consulta.Api/Infra/ReceitaClient.cs`

**L25 — importante: `new HttpClient()` a cada chamada**

Esse é um clássico do .NET. Cada `HttpClient` descartado deixa o socket em `TIME_WAIT` por alguns minutos. Sob carga, os sockets do pod acabam (*socket exhaustion*). Também não há timeout: o padrão é 100 s, o que prende o cancelamento se a Receita estiver lenta. Sugestão: typed client com `IHttpClientFactory`:

```csharp
builder.Services.AddHttpClient<ReceitaClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Receita:BaseUrl"]!);
    c.Timeout = TimeSpan.FromSeconds(3);
});
```

O `ReceitaClient` recebe o `HttpClient` pronto no construtor. Se quiser retry e circuit breaker, `Microsoft.Extensions.Http.Resilience` acrescenta com uma linha.

**L7, L20 e L34 — importante: cache estático com `Dictionary`**

Três problemas:

- `Dictionary` não é thread-safe. Escritas simultâneas podem corromper a estrutura interna, e aí até leituras entram em loop.
- Não há expiração: uma empresa que ficar irregular continua "ATIVA" para sempre.
- Não há limite: a memória cresce a cada documento novo.

Sugestão: `IMemoryCache` (`builder.Services.AddMemoryCache()`) com `AbsoluteExpirationRelativeToNow`. No .NET 9 em diante, o `HybridCache` resolve também o *cache stampede*.

**L28 — importante: `cnpj/{documento}` para qualquer documento**

O prestador pode ser pessoa física com CPF (MEI, autônomo). Vale confirmar com o time da Receita se existe a rota para CPF ou se a regra só se aplica a CNPJ.

**L23 — importante: documento no log** (ver o comentário de logs em `NfseEndpoints.cs`)

**L38–41 — importante: wrapper síncrono com `.Result`**

Não precisa existir: todos os chamadores podem ser `async`. Ver o comentário em `NfseEndpoints.cs` L37.

**L32 — sugestão:** `dto!.Situacao` lança `NullReferenceException` se a Receita devolver um corpo vazio. Melhor tratar e devolver um erro claro.

---

## `src/Nfse.Consulta.Api/Program.cs`

**L6 — 🔴 bloqueante: `ValidateScopes = false`**

Este é o "erro de scope" que você comentou no PR, e a sua pergunta foi a certa. O erro era o .NET avisando que um **singleton** (`NfseRepository`, L11) recebia um serviço **scoped** (`ReceitaClient`, L10). Um serviço scoped deveria nascer e morrer com cada requisição, mas, preso dentro de um singleton, vive para sempre. Isso tem nome: *captive dependency*. Desligar a validação esconde o aviso e mantém o problema.

Uma forma de pensar, a partir do .NET Framework:

| Lifetime | Quando a instância é criada | Equivalente no .NET Framework |
| --- | --- | --- |
| `Singleton` | uma vez no processo | um campo `static` |
| `Scoped` | uma por requisição | o `new Repositorio()` que você fazia no começo de cada chamada |
| `Transient` | a cada injeção | criar um novo sempre que alguém pedir |

A regra prática é que um serviço só pode depender de outros com lifetime **igual ou maior** que o dele. Aqui, o repositório sem estado (abrindo a conexão por operação, comentário em `NfseRepository.cs` L10) pode ser singleton, e o `ReceitaClient` vira typed client (comentário em `ReceitaClient.cs` L25). Com isso, dá para voltar o `ValidateScopes` para o padrão.

**L8 — importante: CORS totalmente aberto**

`AllowAnyOrigin` com `AllowAnyMethod` numa API que cancela nota permite que qualquer site chame o `POST` a partir do navegador de um usuário autenticado. Sugestão: listar as origens do portal e do app.

**L15 — 🔴 bloqueante: `UseDeveloperExceptionPage()` sem condição**

Ela mostra o stack trace e o código em qualquer ambiente. Sugestão: `if (app.Environment.IsDevelopment())`. Fora disso, o `UseExceptionHandler()` do comentário em `NfseEndpoints.cs` L79–82.

---

## `src/Nfse.Consulta.Api/appsettings.json`

**L3 — 🔴 bloqueante: senha de homologação no repositório**

Mesmo que o próximo commit apague a linha, a senha **fica no histórico do git**. Por isso a primeira ação é **rotacionar a senha do `NFSE_APP` em homologação**, antes de qualquer outra coisa. Depois: `appsettings.json` sem connection string; em desenvolvimento, `dotnet user-secrets`; nos ambientes, variável de ambiente ou secret do Kubernetes (`ConnectionStrings__Nfse`).

---

## `src/Nfse.Consulta.Api/Nfse.Consulta.Api.csproj`

**L11 — sugestão:** a `Oracle.ManagedDataAccess.Core` 3.21 está bem atrás da linha 23.x que o time usa. Vale alinhar e rodar `dotnet list package --vulnerable`: no serviço novo, uma dependência transitiva do ODP.NET tinha vulnerabilidade conhecida e precisou ser fixada.

---

## `src/Nfse.Consulta.Api/Domain/Nfse.cs`

**L18 e L21 — sugestão:** `IssRetido = "N"` e `Situacao = "N"` são convenções do banco legado. No modelo da API, `bool IssRetido` e um `enum` de situação deixam o código mais claro, e a tradução fica num lugar só, no repositório.

**L37 — importante:** `CancelamentoRequest(string Motivo)` com `Nullable` habilitado diz que o motivo nunca é nulo, mas um JSON sem o campo chega `null`. Ver o comentário em `NfseRepository.cs` L117.

---

## `tests/Nfse.Consulta.Tests/NfseEndpointsTests.cs`

**L24 — importante: o teste não testa**

`Assert.NotNull(resp)` é sempre verdadeiro: `GetAsync` nunca devolve `null`, nem com 404 ou 500. O teste passa com a API quebrada.

**L34 — importante: o teste aceita o erro que diz evitar**

`StatusCode == OK || StatusCode == InternalServerError` num teste chamado `NaoDeveDarErroDeServidor` garante exatamente o contrário do nome. Imagino que ele falhava por causa do banco e o `||` foi a saída. Faz sentido, mas aí o problema é o banco do teste, não a asserção.

**L22 — sugestão:** `Thread.Sleep(500)` depois do `await` não espera nada útil e bloqueia a thread. Pode sair.

**Geral — importante: os testes rodam contra o banco de homologação**

A `WebApplicationFactory` usa o `appsettings.json`, então os testes falam com o Oracle de homologação. Hoje só consultam; no dia em que alguém escrever um teste de cancelamento, ele cancela notas reais de homologação. Sugestões:

- para testar os endpoints e as regras: substituir o repositório por um falso em `ConfigureTestServices`;
- para testar o SQL: Oracle num container (a imagem `gvenzl/oracle-free` que o time usa no compose);
- testes que valem mais: as regras de cancelamento (prazo no dia 10 às 23:59:59 e no dia 11 às 00:00, nota substituída, guia paga, já cancelada), paginação (sem repetir nem perder), período inválido e autorização (prestador A não cancela nota do B).

Sobre o "testei localmente, funcionando" do PR: é exatamente isso que um bom teste automatizado substitui. Ele prova que funciona, e continua provando depois que alguém mudar o código.

---

## Fora do escopo do PR (só para registrar o caminho)

Você listou OpenAPI e observabilidade como pendentes, e concordo que não precisam entrar aqui. Quando for fazer, no serviço novo usamos:

- `builder.Services.AddOpenApi()` + `app.MapOpenApi()` (nativo no .NET 9 em diante);
- `AddProblemDetails()` para os erros;
- OpenTelemetry com traces, métricas e logs correlacionados ([ADR-0012](../docs/adr/0012-observabilidade-seguranca-e-lgpd.md)).

Posso mostrar como ficou lá.

---

Valeu de novo, Marcelo. Muita coisa aqui é o tipo de detalhe que só aparece em produção; melhor descobrir no PR. Quando quiser, a gente pareia sobre os itens de DI e async e eu revejo a próxima versão com prioridade. 👊
