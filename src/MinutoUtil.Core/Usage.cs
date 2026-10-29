namespace MinutoUtil.Core;

/// <summary>
/// What a run spent on paid-for or quota-limited services, written to run.log at the end. Gemini has its own model
/// budget, since its free quota is shared with the live Flopômetro channel; the spare writers (Cloudflare Workers AI and
/// OpenRouter, quotas of their own) share a second one. Once a budget is spent, no client sends another request to
/// those providers, whatever failed before, so a bad Gemini morning never uses up the spares' turn.
/// </summary>
public sealed class RunUsage(int maxGeminiRequests = 3, int maxSpareRequests = 4)
{
    private int _gemini;
    private int _spare;
    private int _smallestRequests;
    private int _smallestCharacters;

    public int MaxGeminiRequests => maxGeminiRequests;
    public int MaxSpareRequests => maxSpareRequests;
    public int GeminiRequests => _gemini;
    /// <summary>Requests to the spare writers, Cloudflare Workers AI and OpenRouter.</summary>
    public int SpareRequests => _spare;
    public int SmallestRequests => _smallestRequests;
    public int SmallestCharacters => _smallestCharacters;

    /// <summary>The voice that actually spoke ("smallest:thiago", "kokoro:pm_santa").</summary>
    public string? Voice { get; set; }

    /// <summary>Counts one model request, or throws when the provider's budget for the run is spent.</summary>
    public void TakeModelRequest(string provider)
    {
        lock (this)
        {
            if (provider == "gemini")
            {
                if (_gemini >= maxGeminiRequests)
                    throw new Llm.LlmException($"The run's budget of {maxGeminiRequests} Gemini requests is spent; not asking gemini.");
                _gemini++;
            }
            else
            {
                if (_spare >= maxSpareRequests)
                    throw new Llm.LlmException($"The run's budget of {maxSpareRequests} spare writer requests is spent; not asking {provider}.");
                _spare++;
            }
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
        $"usage: gemini requests {GeminiRequests} (budget {maxGeminiRequests}), spare writer requests {SpareRequests} (budget {maxSpareRequests}), " +
        $"smallest requests {SmallestRequests}, smallest characters {SmallestCharacters}, cover images {CoverImages}, voice {Voice ?? "none"}";
}
