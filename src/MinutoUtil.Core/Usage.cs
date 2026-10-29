namespace MinutoUtil.Core;

/// <summary>
/// What a run spent on paid-for or quota-limited services, written to run.log at the end. The model budget is shared by
/// every provider: once it is spent, no client sends another request, whatever failed before.
/// </summary>
public sealed class RunUsage(int maxModelRequests = 3)
{
    private int _gemini;
    private int _openRouter;
    private int _smallestRequests;
    private int _smallestCharacters;

    public int MaxModelRequests => maxModelRequests;
    public int GeminiRequests => _gemini;
    public int OpenRouterRequests => _openRouter;
    public int ModelRequests => _gemini + _openRouter;
    public int SmallestRequests => _smallestRequests;
    public int SmallestCharacters => _smallestCharacters;

    /// <summary>The voice that actually spoke ("smallest:thiago", "kokoro:pm_santa").</summary>
    public string? Voice { get; set; }

    /// <summary>Counts one model request, or throws when the run's budget is spent.</summary>
    public void TakeModelRequest(string provider)
    {
        lock (this)
        {
            if (ModelRequests >= maxModelRequests)
                throw new Llm.LlmException($"The run's budget of {maxModelRequests} model requests is spent; not asking {provider}.");
            if (provider == "gemini") _gemini++;
            else _openRouter++;
        }
    }

    private int _coverImages;

    /// <summary>Painted thumbnail pictures asked of Cloudflare Workers AI (not part of the writer's budget).</summary>
    public int CoverImages => _coverImages;

    public void AddCoverImage() => Interlocked.Increment(ref _coverImages);

    public void AddSmallest(int characters)
    {
        Interlocked.Increment(ref _smallestRequests);
        Interlocked.Add(ref _smallestCharacters, characters);
    }

    public string Summary() =>
        $"usage: gemini requests {GeminiRequests}, openrouter requests {OpenRouterRequests} (budget {maxModelRequests}), " +
        $"smallest requests {SmallestRequests}, smallest characters {SmallestCharacters}, cover images {CoverImages}, voice {Voice ?? "none"}";
}
