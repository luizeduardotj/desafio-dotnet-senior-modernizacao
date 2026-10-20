using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;

namespace Nfse.Resiliencia.Testes;

/// <summary>
/// O sistema de verdade (fachada, legado, novo, Oracle) com o Oracle lento ou fora do ar só para o novo, pelo
/// Toxiproxy. Verifica o que a ADR-0008 ("Modo novo e falhas") e a ADR-0009 (sombra fora do caminho do cliente)
/// prometem, e mede os tempos [M].
/// </summary>
[Collection(ColecaoDeResiliencia.Nome)]
public sealed class OracleLentoOuForaDoArTests(Ambiente ambiente, ITestOutputHelper saida) : IAsyncLifetime
{
    // ADR-0008: timeout de inatividade de 5 s nas leituras e 10 s no cancelamento.
    private static readonly TimeSpan TimeoutLeitura = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimeoutCancelamento = TimeSpan.FromSeconds(10);

    // Folga para a única tentativa no legado depois do timeout (o caso 001 leva dezenas de ms) e para o runner do CI.
    private static readonly TimeSpan FolgaDoLegado = TimeSpan.FromSeconds(2);

    // Maior que os dois timeouts: o novo nunca responde a tempo.
    private static readonly TimeSpan AtrasoAcimaDoTimeout = TimeSpan.FromSeconds(12);

    private static readonly string RespostaDoLegado = Soap.RespostaDoLegado();

    public async Task InitializeAsync()
    {
        await ambiente.OracleNormalAsync();
        await ambiente.AguardarProntidaoAsync(HttpStatusCode.OK, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        await ambiente.OracleNormalAsync();
        await ambiente.FlagsAsync(consultarNfse: "legado", cancelarNfse: "legado");
    }

    // (a) ------------------------------------------------------------------------------------------------------------

    [ResilienciaFact]
    public async Task Modo_novo_com_oracle_lento_a_leitura_cai_no_legado_dentro_do_orcamento()
    {
        await ambiente.FlagsAsync(consultarNfse: "novo", cancelarNfse: "legado");
        Resposta normal = await ambiente.EnviarAsync(Soap.ConsultarNfse());
        Assert.Contains("-> Novo (Percentual) status 200", await ambiente.RoteamentoAsync(normal), StringComparison.Ordinal);
        Assert.Equal(RespostaDoLegado, normal.Corpo);

        await ambiente.OracleLentoAsync(AtrasoAcimaDoTimeout);
        Resposta lenta = await ambiente.EnviarAsync(Soap.ConsultarNfse());

        Assert.Equal(HttpStatusCode.OK, lenta.Status);
        Assert.Equal(RespostaDoLegado, lenta.Corpo);
        Assert.Contains("-> Legado (Fallback) status 200", await ambiente.RoteamentoAsync(lenta), StringComparison.Ordinal);
        Assert.InRange(lenta.Duracao, TimeoutLeitura - TimeSpan.FromMilliseconds(200), TimeoutLeitura + FolgaDoLegado);
        Registrar("(a) novo, Oracle +12 s", "leitura normal pelo novo", normal.Duracao);
        Registrar("(a) novo, Oracle +12 s", "leitura com fallback ao legado", lenta.Duracao);
    }

    // (b) ------------------------------------------------------------------------------------------------------------

    [ResilienciaFact]
    public async Task Modo_novo_com_oracle_fora_do_ar_a_leitura_cai_no_legado_e_o_novo_sai_do_ready()
    {
        await ambiente.FlagsAsync(consultarNfse: "novo", cancelarNfse: "legado");
        await ambiente.OracleForaDoArAsync();
        var desdeAQueda = Stopwatch.StartNew();

        Resposta r = await ambiente.EnviarAsync(Soap.ConsultarNfse());

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(RespostaDoLegado, r.Corpo);
        Assert.Contains("-> Legado (Fallback) status 200", await ambiente.RoteamentoAsync(r), StringComparison.Ordinal);
        Assert.True(r.Duracao < TimeoutLeitura + FolgaDoLegado, $"leitura levou {r.Duracao}");

        // Pronto mede a dependência (o novo não atende nada sem o Oracle); vivo não, para não reiniciar o processo.
        await ambiente.AguardarProntidaoAsync(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(10));
        TimeSpan ateSairDoReady = desdeAQueda.Elapsed;
        Assert.Equal(HttpStatusCode.OK, await ambiente.VidaDoNovoAsync());
        Registrar("(b) novo, Oracle recusando conexão", "leitura com fallback ao legado", r.Duracao);
        Registrar("(b) novo, Oracle recusando conexão", "da queda ao /health/ready 503", ateSairDoReady);
    }

    [ResilienciaFact]
    public async Task Modo_novo_com_oracle_sem_resposta_a_leitura_cai_no_legado_e_o_ready_nao_trava()
    {
        // Partição: a conexão não cai, só para de responder. É o pior caso para o pool e para o health check.
        await ambiente.FlagsAsync(consultarNfse: "novo", cancelarNfse: "legado");
        await ambiente.OracleSemRespostaAsync();

        Resposta r = await ambiente.EnviarAsync(Soap.ConsultarNfse());

        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(RespostaDoLegado, r.Corpo);
        Assert.Contains("-> Legado (Fallback) status 200", await ambiente.RoteamentoAsync(r), StringComparison.Ordinal);
        Assert.InRange(r.Duracao, TimeoutLeitura - TimeSpan.FromMilliseconds(200), TimeoutLeitura + FolgaDoLegado);

        // O probe de readiness do Kubernetes desiste em 2 s (deploy/k8s/base/nfse-novo): o check tem de responder antes.
        var cronometro = Stopwatch.StartNew();
        HttpStatusCode pronto = await ambiente.ProntidaoDoNovoAsync();
        TimeSpan duracaoDoReady = cronometro.Elapsed;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pronto);
        Assert.True(duracaoDoReady < TimeSpan.FromSeconds(2), $"/health/ready levou {duracaoDoReady}");
        Registrar("(b) novo, Oracle sem resposta", "leitura com fallback ao legado", r.Duracao);
        Registrar("(b) novo, Oracle sem resposta", "duração do /health/ready (503)", duracaoDoReady);
    }

    // (c) ------------------------------------------------------------------------------------------------------------

    [ResilienciaFact]
    public async Task Modo_sombra_com_oracle_lento_o_cliente_nao_percebe_e_a_sombra_registra_erro_novo_e_descarte()
    {
        const int Requisicoes = 10;
        await ambiente.FlagsAsync(consultarNfse: "legado", cancelarNfse: "legado");
        List<Resposta> semSombra = await EmSequenciaAsync(Requisicoes);

        await ambiente.FlagsAsync(consultarNfse: "sombra", cancelarNfse: "legado", amostragem: 100);
        await ambiente.OracleLentoAsync(AtrasoAcimaDoTimeout);
        DateTimeOffset inicio = DateTimeOffset.UtcNow;
        List<Resposta> comSombra = await EmSequenciaAsync(Requisicoes);

        Assert.All(comSombra, r =>
        {
            Assert.Equal(HttpStatusCode.OK, r.Status);
            Assert.Equal(RespostaDoLegado, r.Corpo);
        });

        TimeSpan medianaSem = Mediana(semSombra);
        TimeSpan medianaCom = Mediana(comSombra);
        TimeSpan maximoCom = comSombra.Max(r => r.Duracao);
        Assert.True(medianaCom <= medianaSem + TimeSpan.FromMilliseconds(100), $"mediana {medianaCom} com sombra × {medianaSem} sem");
        Assert.True(maximoCom < TimeSpan.FromSeconds(1), $"máximo {maximoCom} com a sombra lenta");

        // Um worker e canal de 2 (override do compose): a 1ª cópia espera o timeout da sombra (5 s) e vira erro_novo;
        // com o canal cheio, as seguintes são descartadas sem segurar o cliente.
        IReadOnlyList<string> logs = await AguardarLogAsync(inicio, "divergência erro_novo", TimeSpan.FromSeconds(20));
        Assert.Contains(logs, l => l.Contains("Sombra ConsultarNfse: cópia descartada (canal_cheio)", StringComparison.Ordinal));
        Registrar("(c) sombra 100%, Oracle +12 s", $"mediana do cliente sem sombra ({Requisicoes} req.)", medianaSem);
        Registrar("(c) sombra 100%, Oracle +12 s", $"mediana do cliente com sombra ({Requisicoes} req.)", medianaCom);
        Registrar("(c) sombra 100%, Oracle +12 s", "máximo do cliente com sombra", maximoCom);
        Registrar("(c) sombra 100%, Oracle +12 s", "cópias descartadas (canal_cheio)",
            logs.Count(l => l.Contains("descartada (canal_cheio)", StringComparison.Ordinal)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // (d) ------------------------------------------------------------------------------------------------------------

    [ResilienciaFact]
    public async Task Modo_novo_cancelamento_com_lock_alem_do_wait_5_responde_fault_sem_escrever()
    {
        await ambiente.FlagsAsync(consultarNfse: "legado", cancelarNfse: "novo");
        long numero = await BancoDoLegado.NotaParaCancelarAsync(ambiente.OracleDoLegado);
        Soap cancelar = Soap.Cancelar(numero);

        Resposta travada;
        await using (await BancoDoLegado.TravarNotaAsync(ambiente.OracleDoLegado, Soap.Prestador, numero))
        {
            travada = await ambiente.EnviarAsync(cancelar);

            // FOR UPDATE WAIT 5 no wrapper: ORA-30006, o novo devolve o Fault de erro interno e a fachada não tenta o legado.
            Assert.Equal(HttpStatusCode.InternalServerError, travada.Status);
            Assert.Contains("<faultcode>soap:Server</faultcode>", travada.Corpo, StringComparison.Ordinal);
            Assert.Contains("-> Novo (Percentual) status 500", await ambiente.RoteamentoAsync(travada), StringComparison.Ordinal);
            // O WAIT 5 é contado pelo Oracle; o cronômetro do cliente pode fechar um pouco abaixo (4,9997 s no CI).
            Assert.InRange(travada.Duracao, TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(200), TimeoutCancelamento);
        }

        // Lock solto: ninguém escreve depois (nem retry da fachada, nem o legado, nem o novo atrasado).
        await Task.Delay(TimeSpan.FromSeconds(2));
        BancoDoLegado.EstadoDaNota depois = await BancoDoLegado.NotaAsync(ambiente.OracleDoLegado, Soap.Prestador, numero);
        Assert.Equal("N", depois.Situacao);
        Assert.Null(depois.DataCancelamento);

        // O ERP reenvia (ADR-0008): cancela uma vez, e o segundo reenvio devolve o mesmo DataHora (K01) sem nova escrita.
        (Resposta reenvio, _) = await ReenviarDuasVezesAsync(cancelar, numero, gravadoAntes: null);
        Registrar("(d) novo, lock da nota além do WAIT 5", "Fault do cancelamento travado", travada.Duracao);
        Registrar("(d) novo, lock da nota além do WAIT 5", "reenvio depois do lock", reenvio.Duracao);
    }

    [ResilienciaFact]
    public async Task Modo_novo_cancelamento_com_oracle_lento_alem_do_timeout_da_fachada_e_coerente_no_reenvio()
    {
        // Atraso só na volta: o banco recebe na hora, o novo só sabe do resultado depois. A fachada desiste em 10 s com
        // Fault. O novo segue sem o CancellationToken da requisição (EndpointSoap) e termina sozinho: commita, ou
        // desfaz se o execute passar do CommandTimeout (GatewayDeCancelamento). Nos dois desfechos o reenvio tem de
        // terminar com uma única escrita e o DataHora gravado (K01).
        await ambiente.FlagsAsync(consultarNfse: "legado", cancelarNfse: "novo");
        long numero = await BancoDoLegado.NotaParaCancelarAsync(ambiente.OracleDoLegado);
        Soap cancelar = Soap.Cancelar(numero);

        await ambiente.OracleLentoAsync(TimeSpan.FromSeconds(6));
        Resposta lenta = await ambiente.EnviarAsync(cancelar);
        Assert.Equal(HttpStatusCode.InternalServerError, lenta.Status);
        Assert.Contains("<faultcode>soap:Server</faultcode>", lenta.Corpo, StringComparison.Ordinal);
        Assert.InRange(lenta.Duracao, TimeoutCancelamento - TimeSpan.FromMilliseconds(200), TimeoutCancelamento + TimeSpan.FromSeconds(1));
        Assert.Contains("-> Novo (Percentual)", await ambiente.RoteamentoAsync(lenta), StringComparison.Ordinal);

        // Orçamento do novo: espera do lock 5 s + CommandTimeout 8 s + commit; com a volta atrasada, até ~20 s do envio.
        await ambiente.OracleNormalAsync();
        await Task.Delay(TimeSpan.FromSeconds(10));
        BancoDoLegado.EstadoDaNota desfecho = await BancoDoLegado.NotaAsync(ambiente.OracleDoLegado, Soap.Prestador, numero);
        Assert.True(desfecho.Situacao is "N" or "C", $"situação {desfecho.Situacao} depois do Fault");

        (Resposta reenvio, _) = await ReenviarDuasVezesAsync(cancelar, numero, gravadoAntes: desfecho.Situacao == "C" ? desfecho : null);
        Registrar("(d) novo, Oracle +6 s na volta", "Fault da fachada no cancelamento", lenta.Duracao);
        Registrar("(d) novo, Oracle +6 s na volta", "desfecho no novo depois do Fault",
            desfecho.Situacao == "C" ? "gravado (commit depois da desistência da fachada)" : "não gravado (rollback)");
        Registrar("(d) novo, Oracle +6 s na volta", "reenvio (K01)", reenvio.Duracao);
    }

    /// <summary>
    /// Reenvio do ERP depois de um Fault: sucesso, nota cancelada uma vez só (a mesma gravação de antes, se já havia), e
    /// um segundo reenvio devolve os mesmos bytes sem mexer na nota.
    /// </summary>
    private async Task<(Resposta Reenvio, BancoDoLegado.EstadoDaNota Cancelada)> ReenviarDuasVezesAsync(
        Soap cancelar, long numero, BancoDoLegado.EstadoDaNota? gravadoAntes)
    {
        Resposta reenvio = await ambiente.EnviarAsync(cancelar);
        Assert.Equal(HttpStatusCode.OK, reenvio.Status);
        BancoDoLegado.EstadoDaNota cancelada = await BancoDoLegado.NotaAsync(ambiente.OracleDoLegado, Soap.Prestador, numero);
        Assert.Equal("C", cancelada.Situacao);
        if (gravadoAntes is not null)
        {
            Assert.Equal(gravadoAntes, cancelada);
        }

        Resposta repetido = await ambiente.EnviarAsync(cancelar);
        Assert.Equal(reenvio.Corpo, repetido.Corpo);
        Assert.Equal(cancelada, await BancoDoLegado.NotaAsync(ambiente.OracleDoLegado, Soap.Prestador, numero));
        return (reenvio, cancelada);
    }

    // (e) ------------------------------------------------------------------------------------------------------------

    [ResilienciaFact]
    public async Task Removido_o_toxic_o_novo_volta_a_atender_sem_restart()
    {
        await ambiente.FlagsAsync(consultarNfse: "novo", cancelarNfse: "legado");
        string containerAntes = await ambiente.EstadoDoContainerDoNovoAsync();

        // Lento: uma leitura cancelada pela fachada no meio da query deixa a conexão no pool.
        await ambiente.OracleLentoAsync(AtrasoAcimaDoTimeout);
        Assert.Contains("(Fallback)", await ambiente.RoteamentoAsync(await ambiente.EnviarAsync(Soap.ConsultarNfse())), StringComparison.Ordinal);
        await ambiente.OracleNormalAsync();
        TimeSpan aposLentidao = await AteONovoAtenderAsync();

        // Fora do ar: as conexões do pool morrem com o proxy.
        await ambiente.OracleForaDoArAsync();
        Assert.Contains("(Fallback)", await ambiente.RoteamentoAsync(await ambiente.EnviarAsync(Soap.ConsultarNfse())), StringComparison.Ordinal);
        await ambiente.AguardarProntidaoAsync(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(10));
        await ambiente.OracleNormalAsync();
        TimeSpan aposQueda = await AteONovoAtenderAsync();
        TimeSpan ateOReady = await ambiente.AguardarProntidaoAsync(HttpStatusCode.OK, TimeSpan.FromSeconds(10));

        Assert.Equal(containerAntes, await ambiente.EstadoDoContainerDoNovoAsync());
        Registrar("(e) recuperação", "do fim da lentidão à 1ª leitura pelo novo", aposLentidao);
        Registrar("(e) recuperação", "da volta do Oracle à 1ª leitura pelo novo", aposQueda);
        Registrar("(e) recuperação", "e daí ao /health/ready 200", ateOReady);
    }

    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>Lê até uma resposta vir do novo, sem fallback, e igual ao legado; devolve quanto levou.</summary>
    private async Task<TimeSpan> AteONovoAtenderAsync()
    {
        var cronometro = Stopwatch.StartNew();
        int tentativas = 0;
        while (true)
        {
            tentativas++;
            Resposta r = await ambiente.EnviarAsync(Soap.ConsultarNfse());
            Assert.Equal(RespostaDoLegado, r.Corpo);
            if ((await ambiente.RoteamentoAsync(r)).Contains("-> Novo (Percentual) status 200", StringComparison.Ordinal))
            {
                saida.WriteLine($"novo voltou a atender em {cronometro.Elapsed} ({tentativas} leituras)");
                return cronometro.Elapsed;
            }

            Assert.True(cronometro.Elapsed < TimeSpan.FromSeconds(30), "o novo não voltou a atender em 30 s sem restart");
            await Task.Delay(250);
        }
    }

    private async Task<List<Resposta>> EmSequenciaAsync(int quantidade)
    {
        var respostas = new List<Resposta>(quantidade);
        for (int i = 0; i < quantidade; i++)
        {
            respostas.Add(await ambiente.EnviarAsync(Soap.ConsultarNfse()));
        }

        return respostas;
    }

    private async Task<IReadOnlyList<string>> AguardarLogAsync(DateTimeOffset desde, string trecho, TimeSpan limite)
    {
        var cronometro = Stopwatch.StartNew();
        while (true)
        {
            IReadOnlyList<string> logs = await ambiente.LogsDaFachadaAsync(desde);
            if (logs.Any(l => l.Contains(trecho, StringComparison.Ordinal)))
            {
                return logs;
            }

            Assert.True(cronometro.Elapsed < limite, $"\"{trecho}\" não apareceu no log da fachada em {limite.TotalSeconds} s");
            await Task.Delay(500);
        }
    }

    private static TimeSpan Mediana(List<Resposta> respostas)
    {
        List<TimeSpan> ordenadas = [.. respostas.Select(r => r.Duracao).Order()];
        return ordenadas[ordenadas.Count / 2];
    }

    private void Registrar(string cenario, string medida, TimeSpan valor)
    {
        saida.WriteLine($"[M] {cenario} — {medida}: {valor.TotalMilliseconds:0} ms");
        ambiente.Medicoes.Registrar(cenario, medida, valor);
    }

    private void Registrar(string cenario, string medida, string valor)
    {
        saida.WriteLine($"[M] {cenario} — {medida}: {valor}");
        ambiente.Medicoes.Registrar(cenario, medida, valor);
    }
}
