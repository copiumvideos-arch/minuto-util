using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MinutoUtil.Core;
using MinutoUtil.Core.Llm;
using MinutoUtil.Core.Scripts;
using MinutoUtil.Core.Sources;

namespace MinutoUtil.Tests;

public sealed class ScriptAndLlmTests
{
    private static readonly string[] Models = ["gemini-a", "gemini-b", "gemini-c"];

    [Fact]
    public void The_comment_question_is_cleaned_and_closes_the_descriptions_first_paragraph()
    {
        var section = new ScriptSection("Em 1996 o Brasil adotou a urna eletrônica.", ["voting machine"], ["urna"], "q");
        var script = new VideoScript("Urna", "Por que só o Brasil?", "1996", "Brasil", [section], "A urna chegou em 1996.", ["urna"])
        {
            Question = "Você prefere a urna eletrônica — ou o voto em papel?",
        };
        var sources = new List<SourceArticle> { new("pt", "Urna eletrônica (Brasil)", "https://pt.wikipedia.org/wiki/Urna", "texto", 1) };

        var polished = ScriptWriter.Polish(script, sources);
        Assert.DoesNotContain("—", polished.Question);
        Assert.Contains("ou o voto em papel?", polished.Question);

        var metadata = Pipeline.Metadata(polished, sources, [], 50, "voz", "gemini", Path.GetTempPath());
        Assert.StartsWith($"A urna chegou em 1996. {polished.Question}", metadata.Description);
        // The TikTok caption takes that first paragraph, so the question reaches TikTok too.
        Assert.Contains(polished.Question, MinutoUtil.Core.Publishing.PublishCopy.TikTokCaption(metadata, new TikTokOptions()));
    }

    private static string GeminiAnswer => File.ReadAllText(TestPaths.Fixture("gemini-answer.json"));

    private static IReadOnlyList<SourceArticle> Sources()
    {
        var (article, _) = WikipediaClient.Parse("en", File.ReadAllText(TestPaths.Fixture("wikipedia-en.json")));
        return [article];
    }

    /// <summary>Word limits that the stored answer (about 100 words) meets, so no rewrite is asked for.</summary>
    private static readonly WriterOptions Lenient = new() { MinWords = 90, MaxWords = 170 };

    [Fact]
    public async Task ParsesTheStoredModelAnswerAndCleansIt()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, GeminiAnswer));
        var usage = new RunUsage(3);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage);
        var writer = new ScriptWriter(gemini, "{CHANNEL} {MIN_WORDS}-{MAX_WORDS}", Lenient, "Minuto Útil");

        var written = await writer.WriteAsync("Segunda Guerra Mundial", Sources(), 16000, CancellationToken.None);

        var script = written.Script;
        Assert.Single(handler.Requests);
        Assert.Equal(1, usage.GeminiRequests);
        Assert.Equal("key", handler.Requests[0].Request.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("key", handler.Requests[0].Request.RequestUri!.ToString());
        Assert.Equal(5, script.Sections.Count);
        Assert.Equal(6, script.SpokenSections.Count);
        Assert.Equal("6 de junho de 1944", script.EraStamp);
        // The em dash the model wrote became a comma.
        Assert.DoesNotContain(script.SpokenSections, s => s.Contains('\u2014') || s.Contains('\u2013'));
        Assert.Contains("Mas o tempo não ajudou, e a operação", script.Sections[1].Text);
        Assert.Equal(["historia", "segundaguerra", "diad", "normandia", "shorts"], script.Hashtags);
        // Section 4 says "6 de junho" in digits: flagged, and spelled out for the voice.
        Assert.Contains(written.Warnings, w => w.Contains("section 4 has digits"));
        Assert.Equal("Então, na madrugada de seis de junho, começou o ataque, com bombardeios pelo ar e pelo mar.",
            SpokenText.SpellNumbers(script.Sections[3].Text));
        // Every quote is in the article.
        Assert.DoesNotContain(written.Warnings, w => w.Contains("source quote"));
        Assert.Contains("Minuto Útil 90-170", writer.SystemPrompt());
    }

    [Fact]
    public void FlagsAQuoteThatIsNotInTheSource()
    {
        var script = JsonSerializer.Deserialize<VideoScript>(File.ReadAllText(TestPaths.Fixture("script-answer.json")), Json.Options)!;
        script = script with { Sections = [.. script.Sections.Take(4), script.Sections[4] with { SourceQuote = "Napoleon crossed the Alps with twelve elephants in 1944." }] };
        var writer = new ScriptWriter(new UnavailableClient(), "", Lenient, "x");
        var warnings = writer.Check(ScriptWriter.Clean(script), Sources());
        Assert.Contains(warnings, w => w.StartsWith("section 5: source quote not found"));
    }

    // Years split the English way ("fifteen ninety"), as the Palmares video said "quinze noventa" for 1590.
    [Theory]
    [InlineData("No fim da década de quinze noventa, um grupo", "No fim da década de mil quinhentos e noventa, um grupo")]
    [InlineData("em dezenove quarenta e quatro", "em mil novecentos e quarenta e quatro")]
    [InlineData("Dezenove dezoito acabou a guerra", "Mil novecentos e dezoito acabou a guerra")]
    [InlineData("em dezenove zero cinco", "em mil novecentos e cinco")]
    [InlineData("em quatorze noventa e dois", "em mil quatrocentos e noventa e dois")]
    [InlineData("em mil quinhentos e noventa, quinze mil pessoas", "em mil quinhentos e noventa, quinze mil pessoas")]
    [InlineData("quinze anos e noventa dias", "quinze anos e noventa dias")]
    [InlineData("entre onze mil e vinte mil habitantes", "entre onze mil e vinte mil habitantes")]
    public void SaysSplitYearsInFull(string text, string expected) => Assert.Equal(expected, SpokenText.WithFullYears(text));

    [Fact]
    public void TheScriptCleanupSaysSplitYearsInFull()
    {
        var script = JsonSerializer.Deserialize<VideoScript>(File.ReadAllText(TestPaths.Fixture("script-answer.json")), Json.Options)!;
        script = script with
        {
            Hook = "Em quinze noventa, tudo começou.",
            Sections = [script.Sections[0] with { Text = "Na década de dezesseis noventa, cresceu." }, .. script.Sections.Skip(1)],
        };
        var cleaned = ScriptWriter.Clean(script);
        Assert.Equal("Em mil quinhentos e noventa, tudo começou.", cleaned.Hook);
        Assert.Equal("Na década de mil seiscentos e noventa, cresceu.", cleaned.Sections[0].Text);
    }

    // The ROB RUN video (8/11/2026) said "chegou ao PC por gratuito" in the title and "na Steam por gratuito" aloud.
    [Theory]
    [InlineData("ROB RUN chegou ao PC por gratuito", "ROB RUN chegou ao PC de graça")]
    [InlineData("RobRun está disponível na Steam por gratuito.", "RobRun está disponível na Steam de graça.")]
    [InlineData("Sai por grátis no PC", "Sai de graça no PC")]
    [InlineData("chegou por R$ 0,00 na Steam", "chegou de graça na Steam")]
    [InlineData("O jogo é gratuito na Steam.", "O jogo é gratuito na Steam.")]
    [InlineData("custa por gratuitos motivos", "custa por gratuitos motivos")]
    public void A_free_game_is_never_said_like_a_price(string text, string expected) => Assert.Equal(expected, SpokenText.WithFreeSaidPlainly(text));

    [Fact]
    public void The_cleanup_says_free_plainly_in_the_title_hook_sections_and_description()
    {
        var section = new ScriptSection("RobRun está disponível na Steam por gratuito.", ["crime"], ["crime"], "q");
        var script = new VideoScript("ROB RUN chegou ao PC por gratuito", "ROB RUN chegou por gratuito.", "", "", [section],
            "Disponível por gratuito na Steam.", ["robrun"]);
        var cleaned = ScriptWriter.Clean(script);
        Assert.Equal("ROB RUN chegou ao PC de graça", cleaned.Title);
        Assert.Equal("ROB RUN chegou de graça.", cleaned.Hook);
        Assert.Equal("RobRun está disponível na Steam de graça.", cleaned.Sections[0].Text);
        Assert.Equal("Disponível de graça na Steam.", cleaned.Description);
    }

    [Theory]
    // Shrek 2 Remastered (7/11/2026): the hook and a section both said the co-op player count.
    [InlineData("Shrek 2 Remastered chega dia dez de novembro ao PC, trazendo cooperativo para até quatro pessoas.|" +
                "O jogo permite jogar sozinho ou em até quatro pessoas no modo cooperativo local, online ou misto.", "ate quatro pessoas")]
    // ROB RUN (8/11/2026): where and how it was out, said at the start and again at the end.
    [InlineData("O jogo está disponível gratuitamente na Steam para Windows.|No jogo, você pode criar sua própria família do crime.|" +
                "O jogo tem um sistema de progressão.|O jogo tem um sistema de economia.|RobRun está disponível na Steam de graça.", "o jogo tem um")]
    // One price is one figure, not a repeat.
    [InlineData("Custa noventa e nove reais e noventa e nove centavos na Steam.|O jogo tem legendas em português.", null)]
    [InlineData("ArcheAge Chronicles acabou de chegar ao PC em acesso antecipado.|Custa noventa e nove reais e noventa e nove centavos na Steam.|" +
                "Você explora o continente de Auroria como membro de uma companhia mercante.", null)]
    public void A_phrase_said_twice_is_found(string sections, string? expected) =>
        Assert.Equal(expected, SpokenText.RepeatedPhrase(sections.Split('|')));

    [Fact]
    public void A_games_script_that_says_a_fact_twice_costs_a_rewrite_but_a_history_script_does_not()
    {
        var sections = new[] { "O jogo permite jogar sozinho ou em até quatro pessoas no modo cooperativo local." }
            .Select(t => new ScriptSection(t, ["coop"], ["coop"], "q")).ToList();
        var script = new VideoScript("t", "Shrek 2 chega dia dez ao PC, com cooperativo para até quatro pessoas.", "", "", sections, "d", ["shrek"]);
        var options = new WriterOptions { MinWords = 10, MaxWords = 200, MinSections = 1, MaxSections = 6 };

        var games = new ScriptWriter(new UnavailableClient(), "", options with { NoRepeats = true }, "Profissão Spoiler");
        Assert.Contains("\"ate quatro pessoas\" is said twice", games.HardProblem(script));
        Assert.Null(new ScriptWriter(new UnavailableClient(), "", options, "Minuto Útil").HardProblem(script));
    }

    [Fact]
    public async Task AScriptFarTooShortCostsExactlyOneMoreRequest()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.OK, GeminiAnswer));
        var usage = new RunUsage(3);
        var writer = new ScriptWriter(new GeminiClient(new HttpClient(handler), "key", Models, usage), "",
            new WriterOptions { MinWords = 140, MaxWords = 170, HardWordMargin = 20 }, "x");

        var written = await writer.WriteAsync("t", Sources(), 16000, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("previous answer was rejected", handler.Requests[1].Body);
        Assert.Contains(written.Warnings, w => w.Contains("words, outside 140-170"));
    }

    [Fact]
    public async Task ABusyModelIsNotRetriedTheNextModelGetsTheOneRequest()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.ToString().Contains("gemini-a")
            ? FakeHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"The model is overloaded."}}""")
            : FakeHandler.Json(HttpStatusCode.OK, GeminiAnswer));
        var usage = new RunUsage(3);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage);

        var script = await gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None);

        Assert.Equal(5, script.Sections.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("gemini-b", handler.Requests[1].Request.RequestUri!.ToString());
        Assert.Equal(2, usage.GeminiRequests);
    }

    private static readonly GeminiBusyRetry AnHourEveryFiveMinutes = new(TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(5));

    private static HttpResponseMessage Busy() =>
        FakeHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"This model is currently experiencing high demand.","status":"UNAVAILABLE"}}""");

    /// <summary>Moves the fake clock a minute at a time until the request is done, so the client's waits run out.</summary>
    private static async Task<T> WithClock<T>(Task<T> task, FakeTimeProvider time)
    {
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            await Task.Delay(5);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        return await task;
    }

    [Fact]
    public async Task ABusyGeminiIsAskedAgainEveryFiveMinutesUntilItAnswers()
    {
        // The Profissão Spoiler runs of 2026-11-09 to 11: every model 503 at first, then one answers.
        var count = 0;
        var handler = new FakeHandler((_, _) => Interlocked.Increment(ref count) <= 6 ? Busy() : FakeHandler.Json(HttpStatusCode.OK, GeminiAnswer));
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var usage = new RunUsage(5);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage, busyRetry: AnHourEveryFiveMinutes, time: time);

        var script = await WithClock(gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None), time);

        Assert.Equal(5, script.Sections.Count);
        // Two busy passes of three models, then gemini-a answered on the third pass, ten minutes later.
        Assert.Equal(7, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromMinutes(10), time.GetUtcNow() - start);
        Assert.Equal(1, usage.GeminiRequests);
        Assert.Equal(6, usage.GeminiBusyRequests);
        Assert.Contains("plus 6 busy", usage.Summary());
    }

    [Fact]
    public async Task ABusyGeminiIsGivenUpAfterAnHour()
    {
        var handler = new FakeHandler((_, _) => Busy());
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var usage = new RunUsage(5);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage, busyRetry: AnHourEveryFiveMinutes, time: time);

        var error = await Assert.ThrowsAsync<LlmException>(() => WithClock(gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None), time));

        // Passes at 0, 5, ..., 60 minutes: thirteen of three requests, none of them taken from the run's budget of 5.
        Assert.Contains("60 minutes", error.Message);
        Assert.Equal(39, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromMinutes(60), time.GetUtcNow() - start);
        Assert.Equal(0, usage.GeminiRequests);
    }

    [Fact]
    public async Task ARejectedKeyOrADailyQuotaIsNotWaitedFor()
    {
        var unauthorized = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.Unauthorized, """{"error":{"message":"API key not valid"}}"""));
        var time = new FakeTimeProvider();
        var gemini = new GeminiClient(new HttpClient(unauthorized), "key", Models, new RunUsage(5), busyRetry: AnHourEveryFiveMinutes, time: time);
        await Assert.ThrowsAsync<LlmException>(() => gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None));
        Assert.Single(unauthorized.Requests);

        using var temp = new TempFolder();
        var outOfQuota = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota exceeded PerDay"}}"""));
        var quota = new QuotaTracker(Path.Combine(temp.Path, "quota.json"));
        gemini = new GeminiClient(new HttpClient(outOfQuota), "key", Models, new RunUsage(5), quota, AnHourEveryFiveMinutes, time);
        await Assert.ThrowsAsync<LlmException>(() => gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None));
        Assert.Equal(3, outOfQuota.Requests.Count);
    }

    [Fact]
    public async Task WithoutBusyRetryABusyGeminiFailsAfterOnePass()
    {
        // Minuto Útil: its spare writers take over at once, so nothing changes for it.
        var handler = new FakeHandler((_, _) => Busy());
        var usage = new RunUsage(5);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage);

        await Assert.ThrowsAsync<LlmException>(() => gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(3, usage.GeminiRequests);
        Assert.Equal(0, usage.GeminiBusyRequests);
    }

    [Fact]
    public async Task ADailyQuotaIsRememberedAndTheSharedQuotaFileIsRespected()
    {
        using var temp = new TempFolder();
        var shared = Path.Combine(temp.Path, "canal-dark-quota.json");
        // The live channel used up gemini-a today.
        await File.WriteAllTextAsync(shared, JsonSerializer.Serialize(new Dictionary<string, DateTimeOffset>
        {
            ["gemini:gemini-a"] = DateTimeOffset.UtcNow.AddMinutes(30),
        }));
        var handler = new FakeHandler((request, _) => request.RequestUri!.ToString().Contains("gemini-b")
            ? FakeHandler.Json(HttpStatusCode.TooManyRequests,
                """{"error":{"code":429,"message":"You exceeded your current quota. Quota: GenerateRequestsPerDayPerProjectPerModel-FreeTier"}}""")
            : FakeHandler.Json(HttpStatusCode.OK, GeminiAnswer));
        var own = Path.Combine(temp.Path, "quota-state.json");
        var quota = new QuotaTracker(own, sharedPaths: [shared]);
        var usage = new RunUsage(5);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage, quota);

        await gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None);
        // gemini-a skipped without a request, gemini-b refused for the day, gemini-c answered.
        Assert.Equal(["gemini-b", "gemini-c"], handler.Requests.Select(r => r.Request.RequestUri!.Segments[^1].Split(':')[0]));

        await gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("gemini:gemini-b", await File.ReadAllTextAsync(own));
        // The shared file is only read, never written.
        Assert.DoesNotContain("gemini-b", await File.ReadAllTextAsync(shared));
    }

    [Fact]
    public async Task FallsBackToOpenRouterWhenEveryGeminiModelIsOut()
    {
        var answer = File.ReadAllText(TestPaths.Fixture("script-answer.json"));
        var handler = new FakeHandler((request, _) => request.RequestUri!.Host.Contains("openrouter")
            ? FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }))
            : FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"error":{"message":"quota exceeded PerDay"}}"""));
        var usage = new RunUsage(3);
        var http = new HttpClient(handler);
        var chain = new ChainedLlmClient([
            new GeminiClient(http, "g", ["gemini-a", "gemini-b"], usage),
            new OpenRouterClient(http, "o", ["some/model:free"], usage),
        ]);

        var script = await chain.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None);

        Assert.Equal(5, script.Sections.Count);
        Assert.Equal(2, usage.GeminiRequests);
        Assert.Equal(1, usage.SpareRequests);
        Assert.Equal("Bearer o", handler.Requests[^1].Request.Headers.Authorization!.ToString());
    }

    /// <summary>
    /// The morning of 2026-10-29, three renders in a row: Gemini timed out and answered 503, Workers AI cut both answers
    /// at its default 256 tokens (gpt-oss spent them thinking and gave no text, Llama stopped mid JSON), and the
    /// budget of 5 was spent before OpenRouter was asked.
    /// </summary>
    [Fact]
    public async Task A_bad_gemini_morning_and_cut_off_spare_answers_still_reach_openrouter()
    {
        var answer = File.ReadAllText(TestPaths.Fixture("script-answer.json"));
        var handler = new FakeHandler((request, body) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("gemini-a")) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 180 seconds elapsing.");
            if (uri.Contains("generativelanguage")) return FakeHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"The model is overloaded."}}""");
            if (uri.Contains("cloudflare") && body.Contains("gpt-oss"))
                return FakeHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":null,"reasoning":"We need to produce"},"finish_reason":"length"}]}""");
            if (uri.Contains("cloudflare"))
                return FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer[..200] }, finish_reason = "length" } } }));
            return FakeHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
        });
        var usage = new RunUsage(5, new WriterOptions().MaxSpareRequests);
        var http = new HttpClient(handler);
        var chain = new ChainedLlmClient([
            new GeminiClient(http, "g", Models, usage),
            OpenRouterClient.ForCloudflare(http, "cf", "b77e134015ef6359c129cf8cfe3bb38a", ["@cf/openai/gpt-oss-120b", "@cf/meta/llama-3.3-70b-instruct-fp8-fast"], usage),
            new OpenRouterClient(http, "o", ["some/model:free"], usage),
        ]);
        var writer = new ScriptWriter(chain, "", Lenient, "x");

        var written = await writer.WriteAsync("Guerra do Paraguai", Sources(), 16000, CancellationToken.None);

        Assert.Equal(5, written.Script.Sections.Count);
        Assert.Contains("openrouter.ai", handler.Requests[^1].Request.RequestUri!.Host);
        Assert.Equal((3, 3), (usage.GeminiRequests, usage.SpareRequests));
        // Workers AI is asked for a reply long enough for a script; its default of 256 tokens cut both answers.
        Assert.All(handler.Requests.Where(r => r.Request.RequestUri!.Host.Contains("cloudflare")),
            r => Assert.Contains($"\"max_tokens\":{OpenRouterClient.MaxReplyTokens}", r.Body));
    }

    [Fact]
    public async Task TheRunBudgetStopsRequestsWhateverFailed()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.InternalServerError, "{}"));
        var usage = new RunUsage(2);
        var gemini = new GeminiClient(new HttpClient(handler), "key", Models, usage);

        var error = await Assert.ThrowsAsync<LlmException>(() => gemini.CompleteJsonAsync<VideoScript>("s", "u", CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("budget of 2 Gemini requests", error.Message);
    }

    [Theory]
    [InlineData("1944", "mil novecentos e quarenta e quatro")]
    [InlineData("24.000 soldados", "vinte e quatro mil soldados")]
    [InlineData("156000", "cento e cinquenta e seis mil")]
    [InlineData("100", "cem")]
    [InlineData("1500", "mil e quinhentos")]
    [InlineData("2.500.000", "dois milhões e quinhentos mil")]
    [InlineData("2,5%", "dois vírgula cinco por cento")]
    [InlineData("em 6 de junho", "em seis de junho")]
    // Money: the currency is named after the sum, never read as a code ("AU dólares", Hollow Knight's first cloud run).
    [InlineData("arrecadou cerca de AU$57 mil, acima da meta de AU$ 35 mil.", "arrecadou cerca de cinquenta e sete mil dólares australianos, acima da meta de trinta e cinco mil dólares australianos.")]
    [InlineData("A$ 57.000", "cinquenta e sete mil dólares australianos")]
    [InlineData("35 mil AUD", "trinta e cinco mil dólares australianos")]
    [InlineData("US$ 1,5 milhão", "um vírgula cinco milhão de dólares")]
    [InlineData("R$ 1", "um real")]
    [InlineData("vendeu por $ 10 mil", "vendeu por dez mil dólares")]
    [InlineData("€ 2 bilhões", "dois bilhões de euros")]
    public void SpellsNumbersInBrazilianPortuguese(string text, string expected) =>
        Assert.Equal(expected, SpokenText.SpellNumbers(text));

    [Theory]
    [InlineData("O tempo \u2014 ruim \u2014 atrasou tudo.", "O tempo, ruim, atrasou tudo.")]
    [InlineData("Era 1944 \u2013 e chovia.", "Era 1944, e chovia.")]
    [InlineData("Fim \u2014.", "Fim.")]
    public void RemovesDashes(string text, string expected) => Assert.Equal(expected, SpokenText.WithoutDashes(text));

    [Fact]
    public void ParsesTheWikipediaAnswerWithItsPortugueseLink()
    {
        var (article, linked) = WikipediaClient.Parse("en", File.ReadAllText(TestPaths.Fixture("wikipedia-en.json")));
        Assert.Equal("Normandy landings", article.Title);
        Assert.Equal("Desembarques na Normandia", linked);
        Assert.Equal("https://en.wikipedia.org/wiki/Normandy_landings", article.Url);
        Assert.StartsWith("The Normandy landings were", article.Text);
        Assert.True(article.RevisionId > 0);
    }

    private sealed class UnavailableClient : ILlmClient
    {
        public string Name => "none";

        public Task<T> CompleteJsonAsync<T>(string system, string user, CancellationToken ct) => throw new LlmException("none");
    }
}

public sealed class AccentRestorerTests
{
    private const string Source = """
        A invasão começou na manhã de 6 de junho. A decisão final de Eisenhower foi tomada após a previsão do tempo.
        As marés e a lua limitavam os dias possíveis; os mares estavam agitados. Esta operação mudou a história, e está
        documentada. Os comandantes alemães deixaram seus postos.
        """;

    [Theory]
    [InlineData("A invasao comecou na manha de seis de junho.", "A invasão começou na manhã de seis de junho.")]
    [InlineData("A decisao final veio ate a operaçao.", "A decisão final veio até a operação.")]
    [InlineData("Os comandantes alemaes deixaram os postos.", "Os comandantes alemães deixaram os postos.")]
    [InlineData("INVASAO", "INVASÃO")]
    // Words the source writes both ways, or plain, stay as written.
    [InlineData("Os mares e as mares.", "Os mares e as mares.")]
    [InlineData("Esta operação e esta.", "Esta operação e esta.")]
    [InlineData("Rommel viajou.", "Rommel viajou.")]
    public void PutsBackAccentsOnlyWhereTheSourceIsUnambiguous(string text, string expected) =>
        Assert.Equal(expected, AccentRestorer.Restore(text, AccentRestorer.Learn(Source)).Text);
}
