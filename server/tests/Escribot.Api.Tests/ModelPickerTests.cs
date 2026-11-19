using Escribot.Api.Ai;

namespace Escribot.Api.Tests;

/// <summary>Role models and starting lists picked from what a provider offers, by price.</summary>
public class ModelPickerTests
{
    // Prices per million tokens (prompt, completion), as OpenRouter listed them in November 2026.
    private static OpenRouterModelInfo M(string id, double prompt, double completion, bool vision = true) =>
        new(id, id, 1_000_000, vision, true, prompt, completion);

    private static readonly IReadOnlyList<OpenRouterModelInfo> Catalog =
    [
        M("anthropic/claude-fable-5.2", 8, 40),
        M("anthropic/claude-haiku-5.5", 0.1, 0.5),
        M("anthropic/claude-sonnet-5.5", 2, 10),
        M("anthropic/claude-opus-5.5", 4, 20),
        M("anthropic/claude-fable-5.1", 10, 50),
        M("anthropic/claude-opus-5", 5, 25),
        M("anthropic/claude-haiku-4.5", 1, 5),
        M("openai/gpt-6.2-sol", 2, 10),
        M("openai/gpt-6.2-astra", 8, 40),
        M("openai/gpt-6.1-terra", 1, 5),
        M("openai/gpt-6.1-sol", 2, 10),
        M("openai/gpt-6-luna", 0.1, 0.5),
        M("openai/gpt-6-sol", 2, 10),
        M("openai/gpt-6-astra", 10, 50),
        M("openai/gpt-5.6-luna", 0.2, 1.2),
        M("google/gemini-4-argon", 2, 10),
        M("google/gemini-4-carbon", 2, 10),
        M("google/gemini-4-hydrogen", 1.5, 7.5),
        M("google/gemini-4-neon", 3, 15),
        M("google/gemini-3.8-flash", 0.825, 4.125),
        M("google/gemini-3.7-flash", 0.75, 3.75),
        M("google/gemini-3.6-flash", 0.75, 3.75),
        M("deepseek/deepseek-v4-pro-0813", 0.111, 4.44, vision: false),
        M("deepseek/deepseek-v4.1-flash-1017", 0.15, 0.6),
    ];

    [Fact]
    public void Anthropic_gets_sonnet_to_examine_and_haiku_for_speed()
    {
        string[] live = ["claude-fable-5-2", "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-5", "claude-sonnet-5", "claude-fable-5", "claude-haiku-4-5-20251001"];
        var picks = ModelPicker.Resolve("anthropic", live, Catalog);
        Assert.Equal(("claude-sonnet-5-5", "claude-haiku-5-5", "claude-haiku-5-5"), (picks.Grader, picks.Fast, picks.Vision));
        Assert.Equal(["claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-2", "claude-opus-5-5", "claude-fable-5-1"], picks.Starting);
    }

    [Fact]
    public void OpenAI_gets_the_newest_mid_priced_and_cheapest_models()
    {
        string[] live = ["gpt-6.2-sol", "gpt-6.2-astra", "gpt-6.1-terra", "gpt-6.1-sol", "gpt-6-luna", "gpt-6-sol", "gpt-6-astra", "gpt-5.6-luna", "gpt-4o"];
        var picks = ModelPicker.Resolve("openai", live, Catalog);
        Assert.Equal(("gpt-6.2-sol", "gpt-6-luna", "gpt-6-luna"), (picks.Grader, picks.Fast, picks.Vision));
    }

    [Fact]
    public void Gemini_gets_the_newest_flash_for_speed_not_an_older_cheaper_one()
    {
        string[] live = ["gemini-4-argon", "gemini-4-carbon", "gemini-4-hydrogen", "gemini-4-neon", "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash"];
        var picks = ModelPicker.Resolve("gemini", live, Catalog);
        Assert.Equal("gemini-3.8-flash", picks.Fast);
        Assert.Equal("gemini-4-hydrogen", picks.Grader);
    }

    [Fact]
    public void Without_enough_prices_the_configured_defaults_it_still_offers_are_used()
    {
        // OpenRouter has no model named deepseek-flash, so only one of these has a price.
        var picks = ModelPicker.Resolve("deepseek", ["deepseek-flash", "deepseek-v4-pro"], Catalog);
        Assert.Equal(("deepseek-v4-pro", "deepseek-flash", "deepseek-flash"), (picks.Grader, picks.Fast, picks.Vision));

        // Offline, a default the provider no longer offers gives way to its newest model.
        var offline = ModelPicker.Resolve("openai", ["gpt-7", "gpt-6-luna"], catalog: null);
        Assert.Equal(("gpt-7", "gpt-6-luna", "gpt-7"), (offline.Grader, offline.Fast, offline.Vision));
    }

    [Fact]
    public void Names_match_the_catalogue_across_dots_dates_and_version_stamps_only()
    {
        // claude-haiku-4-5-20251001 is OpenRouter's claude-haiku-4.5; claude-sonnet-5 is not claude-sonnet-5.5.
        var picks = ModelPicker.Pick("anthropic", ["claude-sonnet-5", "claude-haiku-4-5-20251001", "claude-opus-5"], Catalog);
        Assert.NotNull(picks);
        Assert.Equal("claude-haiku-4-5-20251001", picks.Fast);
        Assert.Equal("claude-opus-5", picks.Grader);
    }

    [Fact]
    public void OpenRouter_keeps_its_configured_defaults()
    {
        var picks = ModelPicker.Resolve("openrouter", ["anthropic/claude-sonnet-5.5"], Catalog);
        Assert.Equal("anthropic/claude-sonnet-5.5", picks.Grader);
        Assert.Equal("deepseek/deepseek-v4.1-flash-1017", picks.Fast);
    }
}
