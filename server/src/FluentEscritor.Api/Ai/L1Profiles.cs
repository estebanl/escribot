namespace FluentEscritor.Api.Ai;

public sealed record L1LanguageProfile(string Label, string CoachingFocus);

/// <summary>Coaching notes for the writer's first language, keyed by language code.</summary>
public static class L1Profiles
{
    public static readonly IReadOnlyDictionary<string, L1LanguageProfile> All = new Dictionary<string, L1LanguageProfile>
    {
        ["es"] = new(
            "Spanish (Español → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): Romance false cognates ('actually' used for 'actualmente' = 'currently / at present / in recent years' — never keep 'actually' or swap it with an intensifier like 'extremely'; 'sensible' vs 'sensitive'; 'assist' vs 'attend'; 'realize' vs 'carry out'), comma splices joining independent clauses with ', however,' or ', therefore,' (classify comma splices under GRA, not CC), and definite articles before plural/uncountable general nouns ('the society', 'the technology'). Do NOT claim a 'missing subject' when a sentence uses a valid compound predicate sharing a subject (e.g., 'X rose and then fell')."),
        ["pt"] = new(
            "Portuguese (Português → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): Romance false cognates ('actually' used for 'atualmente' = 'currently / at present', 'pretend', 'push'), definite articles with abstract nouns, comma-spliced independent clauses (classify under GRA), and preposition calques ('depend of', 'consist in')."),
        ["fr"] = new(
            "French (Français → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): false cognates ('actual' / 'actually' used for 'actuel / actuellement' = 'current / currently', 'eventually', 'assist', 'demand'), article generalizations, and delayed thesis positioning in IELTS Task 2."),
        ["zh"] = new(
            "Mandarin Chinese (中文 → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): singular/plural countability & article accuracy, tense consistency in historical vs present data, topic-comment comma splices ('Although..., but...' — classify under GRA), and mechanical memorized idioms."),
        ["ar"] = new(
            "Arabic (العربية → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): syndetic 'and' clause chaining, relative pronoun resumption ('the policy which governments implement it'), definite article patterns, and rhetorical repetition."),
        ["hi"] = new(
            "Hindi (हिन्दी → English)",
            "ONLY flag these patterns if they literally appear in the candidate's text (never invent or assume them): progressive aspect overuse for habitual states, article placement, preposition idioms ('discuss about', 'cope up with'), and formal bureaucratic phrasing ('do the needful')."),
        ["neutral"] = new(
            "Language-Neutral Academic English",
            "Focus strictly on international Academic IELTS Band 7.5–8.5 standards: natural cohesion without mechanical linkers, precise academic collocations, and balanced complex syntax."),
    };

    public static L1LanguageProfile Get(string? code) =>
        code is not null && All.TryGetValue(code, out var profile) ? profile : All["neutral"];
}
