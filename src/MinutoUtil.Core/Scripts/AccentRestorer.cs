using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MinutoUtil.Core.Scripts;

/// <summary>
/// Puts back accents a model left out ("invasao" becomes "invasão"), without asking a model again. On 2026-10-27
/// gemini-3.5-flash wrote a whole script without tildes and acute accents; the voice then reads "decisao" wrong and the
/// captions look broken. A word is only changed when the Portuguese source text never writes it without accents and
/// does write one accented form of it (the most frequent wins), plus a short list of common words a source may lack.
/// "e" and "é", "esta" and "está", "mares" and "marés" stay as written whenever the source uses both.
/// </summary>
public static partial class AccentRestorer
{
    /// <summary>Common words that are never right without their accent, for when the source text lacks them.</summary>
    private static readonly string[] CommonWords =
    [
        "até", "não", "são", "então", "também", "já", "após", "só", "três", "mês", "país", "além", "porém", "através",
        "lá", "você", "vocês", "nós", "história", "histórias", "decisão", "operação", "invasão", "libertação", "manhã",
        "início", "último", "última", "única", "único", "época", "possível", "impossível", "difícil", "fácil", "rápido",
        "ninguém", "alguém", "milhões", "aliás", "ação", "missão", "previsão", "condições", "alemães", "alemão",
        "ausência", "líder", "líderes", "trégua", "exército", "exércitos", "império", "república", "político", "política",
        "família", "países", "histórico", "histórica", "número", "números", "público", "pública", "período", "século",
        "séculos", "estratégia", "estratégico", "atlântico", "pacífico", "américa", "áfrica", "ásia", "japonês",
        "inglês", "francês", "português", "alemã", "alemãs", "soviético", "soviética", "aéreo", "aérea", "aéreos",
        "marítimo", "marítima", "atrás", "há", "porém", "refém", "reféns", "irmão", "irmãos", "mãe", "mão", "mãos",
        "chão", "verão", "coração", "nação", "nações", "missões", "batalhão", "canhão", "canhões", "avião", "aviões",
        "tripulação", "população", "situação", "decisões", "informação", "informações", "comunicação", "rádio",
        "código", "códigos", "máquina", "máquinas", "espião", "espiões", "vitória", "vitórias", "começou", "começo",
        "começa", "força", "forças", "frança", "ameaça", "cabeça", "presença", "diferença", "esperança", "segurança",
        "aliança", "avanço", "lançou", "invasões", "operações", "explosão", "prisão", "tensão", "pressão", "multidão",
    ];

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    public static (string Text, int Changed) Restore(string text, Vocabulary vocabulary)
    {
        var changed = 0;
        var result = Word().Replace(text, m =>
        {
            var word = m.Value;
            var lower = word.ToLowerInvariant();
            if (!vocabulary.Forms.TryGetValue(StripDiacritics(lower), out var forms) || forms.ContainsKey(lower)) return word;
            changed++;
            return MatchCase(word, forms.MaxBy(f => f.Value).Key);
        });
        return (result, changed);
    }

    /// <summary>Every spelling the source text uses for each word, keyed by the word without accents, with counts.</summary>
    public sealed record Vocabulary(IReadOnlyDictionary<string, Dictionary<string, int>> Forms);

    /// <summary>
    /// The source text's spellings. A script word is changed only when the source never spells it that way but does
    /// spell the same letters with accents; the common word list fills in words the source does not use at all.
    /// </summary>
    public static Vocabulary Learn(string sourceText)
    {
        var forms = new Dictionary<string, Dictionary<string, int>>();
        foreach (Match m in Word().Matches(sourceText))
        {
            var lower = m.Value.ToLowerInvariant();
            var key = StripDiacritics(lower);
            if (!forms.TryGetValue(key, out var spellings)) forms[key] = spellings = [];
            spellings[lower] = spellings.GetValueOrDefault(lower) + 1;
        }
        foreach (var word in CommonWords)
            forms.TryAdd(StripDiacritics(word), new Dictionary<string, int> { [word] = 1 });
        return new Vocabulary(forms);
    }

    public static VideoScript Restore(VideoScript script, string sourceText, out int changed)
    {
        var vocabulary = Learn(sourceText);
        var total = 0;
        string Fix(string text)
        {
            var (fixedText, count) = Restore(text, vocabulary);
            total += count;
            return fixedText;
        }
        var result = script with
        {
            Title = Fix(script.Title),
            Hook = Fix(script.Hook),
            PlaceStamp = Fix(script.PlaceStamp),
            EraStamp = Fix(script.EraStamp),
            Description = Fix(script.Description),
            Question = Fix(script.Question ?? ""),
            Sections = script.Sections.Select(s => s with { Text = Fix(s.Text), SearchPt = s.SearchPt.Select(Fix).ToList() }).ToList(),
        };
        changed = total;
        return result;
    }

    private static string StripDiacritics(string word) =>
        new(word.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    private static string MatchCase(string original, string replacement)
    {
        if (original.All(char.IsUpper) && original.Length > 1) return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0])) return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }
}
