using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Cosmos;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Tests;

[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class CountersAndErrorsTests(AppHostFixture fixture)
{
    // Each test gets its own point in time, far from the others, so counters never overlap.
    private readonly ManualTimeProvider _time = new(
        new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(Random.Shared.Next(0, 300_000)));

    [Fact]
    public async Task Rate_limit_allows_exactly_the_limit_under_concurrent_requests()
    {
        var limiter = new CosmosRateLimiter(fixture.Get<CosmosStore>(), _time);
        var key = $"ai:{AppHostFixture.NewUserId()}";

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => limiter.CheckAsync(key, 15, TimeSpan.FromMinutes(1))));

        Assert.Equal(15, results.Count(r => r.Allowed));
        Assert.Equal(25, results.Count(r => !r.Allowed));
    }

    [Fact]
    public async Task Rate_limit_starts_again_in_the_next_window_and_reports_the_wait()
    {
        var limiter = new CosmosRateLimiter(fixture.Get<CosmosStore>(), _time);
        // An IP address with characters that a document id cannot hold.
        var key = $"sign-in:2001:db8::{Random.Shared.Next():x}/64?#";
        var window = TimeSpan.FromMinutes(1);
        _time.Now = _time.Now.AddSeconds(20);

        Assert.True((await limiter.CheckAsync(key, 2, window)).Allowed);
        Assert.True((await limiter.CheckAsync(key, 2, window)).Allowed);
        var blocked = await limiter.CheckAsync(key, 2, window);
        Assert.False(blocked.Allowed);
        Assert.Equal(40, blocked.RetryAfterSeconds);
        Assert.True((await limiter.CheckAsync($"{key}-other", 2, window)).Allowed);

        _time.Now = _time.Now.AddSeconds(40);
        var next = await limiter.CheckAsync(key, 2, window);
        Assert.True(next.Allowed);
        Assert.Equal(60, next.RetryAfterSeconds);
    }

    [Fact]
    public async Task Free_ai_daily_counter_counts_every_concurrent_request_and_resets_each_day()
    {
        var counter = new CosmosFreeAiDailyCounter(fixture.Get<CosmosStore>(), _time);
        Assert.Equal(0, await counter.GetTodayAsync());

        var totals = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => counter.IncrementTodayAsync()));

        Assert.Equal(Enumerable.Range(1, 30).Select(i => (long)i), totals.Order());
        Assert.Equal(30, await counter.GetTodayAsync());

        _time.Now = _time.Now.AddDays(1);
        Assert.Equal(0, await counter.GetTodayAsync());
        Assert.Equal(1, await counter.IncrementTodayAsync());
    }

    [Fact]
    public async Task Error_events_are_stored_newest_first_shortened_and_without_credentials()
    {
        var errors = fixture.Get<IErrorEventRepository>();
        var marker = AppHostFixture.NewId("marker");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        // Made up at run time: nothing shaped like a credential is written in the repository.
        var bearer = TestSecrets.NewSecret()[..22];
        var providerKey = TestSecrets.NewSecret()[..20];

        await errors.RecordAsync(new ErrorEvent
        {
            Source = "server",
            Message = $"{marker} first: provider rejected Authorization: Bearer {bearer} for sk-or-v1-{providerKey}",
            Stack = new string('x', 30) + " " + string.Join(' ', Enumerable.Repeat("at Frame()", 800)),
            Path = $"/api/ai/evaluate?token={bearer}",
            Id = "caller-supplied",
            CreatedAt = TestData.Start,
        });
        await Task.Delay(20);
        await errors.RecordAsync(new ErrorEvent { Source = "client", Message = $"{marker} second", Digest = "d1" });

        var stored = (await errors.ListRecentAsync(500)).Where(e => e.Message.StartsWith(marker, StringComparison.Ordinal)).ToList();

        Assert.Equal(["second", "first"], stored.Select(e => e.Message.Split(' ')[1].TrimEnd(':')));
        var first = stored[1];
        Assert.DoesNotContain(bearer, first.Message);
        Assert.DoesNotContain(providerKey, first.Message);
        Assert.Equal(ErrorSanitizer.MaxStack, first.Stack!.Length);
        Assert.Equal("/api/ai/evaluate", first.Path);
        Assert.NotEqual("caller-supplied", first.Id);
        Assert.True(first.CreatedAt >= before);
        Assert.True(await errors.CountSinceAsync(before) >= 2);
        Assert.Equal(0, await errors.CountSinceAsync(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Single(await errors.ListRecentAsync(1));
    }

    [Fact]
    public async Task Admin_figures_cover_all_users()
    {
        var admin = fixture.Get<IAdminRepository>();
        var tag = Guid.NewGuid().ToString("N");
        var free = $"admin-{tag}-a";
        var pro = $"admin-{tag}-b";
        var since = DateTimeOffset.UtcNow.AddYears(50);
        var feature = $"feature-{tag}";

        await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(free, $"first.{tag}@example.test"));
        await fixture.Get<IUserAccountRepository>().CreateAsync(TestData.Account(pro, $"second.{tag}@example.test"));
        await fixture.Get<IUserAccountRepository>().UpdateAsync(pro, a => a with { EmailVerified = true });
        await fixture.Get<ISettingsRepository>().UpdateAsync(pro, s => s with { Plan = "pro" });
        await fixture.Get<ISubscriptionRepository>().CreateAsync(pro, $"cus_{tag}");
        await fixture.Get<ISubscriptionRepository>().UpdateAsync(pro, s => s with { Status = $"status-{tag}" });

        var usage = fixture.Get<IAiUsageRepository>();
        await usage.RecordAsync(free, TestData.Usage("u1", feature, plan: "free", cost: 0.5) with { CreatedAt = since.AddDays(1) });
        await usage.RecordAsync(free, TestData.Usage("u2", "other", plan: "free", cost: 0.25) with { CreatedAt = since.AddDays(1) });
        await usage.RecordAsync(pro, TestData.Usage("u1", feature, plan: "pro", cost: 2) with { CreatedAt = since.AddDays(1) });
        await usage.RecordAsync(pro, TestData.Usage("u2", feature, keySource: "user", plan: "pro", cost: 9) with { CreatedAt = since.AddDays(1) });

        var stats = await admin.GetAccountStatsAsync();
        Assert.True(stats.Total >= 2);
        Assert.InRange(stats.Verified, 1, stats.Total);
        Assert.True(stats.CreatedLast7Days >= 2);
        Assert.True(stats.CreatedLast30Days >= stats.CreatedLast7Days);

        var rows = await admin.ListAccountsAsync($".{tag.ToUpperInvariant()}@", 10);
        Assert.Equal([pro, free], rows.Select(r => r.Id));
        Assert.Equal("pro", rows[0].StoredPlan);
        Assert.Equal($"status-{tag}", rows[0].SubscriptionStatus);
        Assert.True(rows[0].EmailVerified);
        Assert.Null(rows[1].StoredPlan);
        Assert.Null(rows[1].SubscriptionStatus);
        Assert.Single(await admin.ListAccountsAsync($".{tag}@", 1));
        Assert.Single(await admin.ListAccountsAsync(null, 1));

        Assert.Equal(1, (await admin.GetSubscriptionStatusCountsAsync())[$"status-{tag}"]);

        var totals = await admin.GetAiUsageTotalsAsync(since);
        Assert.Equal(3, totals.Count);
        Assert.Equal(0.75, totals.Single(t => t.Plan == "free").CostUsd, 6);
        Assert.Equal(2, totals.Single(t => t.Plan == "free").Calls);
        Assert.Equal(2, totals.Single(t => t is { Plan: "pro", KeySource: "platform" }).CostUsd, 6);
        Assert.Equal(9, totals.Single(t => t is { Plan: "pro", KeySource: "user" }).CostUsd, 6);

        Assert.Equal(3, await admin.CountAiCallsAsync(since, [feature]));

        var perUser = await admin.GetAiUsageByUserAsync([free, pro], since, [feature]);
        Assert.Equal(1, perUser.Single(u => u.UserId == free).CountedCalls);
        Assert.Equal(0.75, perUser.Single(u => u.UserId == free).PlatformCostUsd, 6);
        Assert.Equal(2, perUser.Single(u => u.UserId == pro).CountedCalls);
        Assert.Equal(2, perUser.Single(u => u.UserId == pro).PlatformCostUsd, 6);
    }
}
