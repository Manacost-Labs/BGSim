using System.Text;

namespace BGSim.Server.Tests;

/// <summary>The small parts on their own: settings, the key comparison, the simulation gate.</summary>
public sealed class PartsTests
{
    [Fact]
    public void Settings_have_defaults_and_reject_bad_values()
    {
        var defaults = ServerLimits.From(_ => null);
        Assert.Equal(64 * 1024, defaults.MaxBodyBytes);
        Assert.Equal(10_000, defaults.MaxRuns);
        Assert.Equal(200, defaults.DefaultTimeBudgetMs);
        Assert.Equal(1000, defaults.MaxTimeBudgetMs);
        Assert.Equal(Environment.ProcessorCount, defaults.MaxConcurrent);
        Assert.False(defaults.ApiKeyRequired);
        Assert.Empty(defaults.CorsOrigins);

        var set = new Dictionary<string, string> { ["BGSIM_MAX_RUNS"] = "500", ["BGSIM_DEFAULT_RUNS"] = "250", ["BGSIM_CORS_ORIGINS"] = " https://a.example ,https://b.example" };
        var limits = ServerLimits.From(set.GetValueOrDefault);
        Assert.Equal(500, limits.MaxRuns);
        Assert.Equal(["https://a.example", "https://b.example"], limits.CorsOrigins);

        Assert.Throws<InvalidOperationException>(() => ServerLimits.From(n => n == "BGSIM_MAX_RUNS" ? "lots" : null));
        Assert.Throws<InvalidOperationException>(() => ServerLimits.From(n => n == "BGSIM_MAX_CONCURRENT" ? "0" : null));
        Assert.Throws<InvalidOperationException>(() => ServerLimits.From(n => n == "BGSIM_DEFAULT_RUNS" ? "20000" : null));
    }

    [Theory]
    [InlineData("key", "key", true)]
    [InlineData("key", "kez", false)]
    [InlineData("key", "ke", false)]
    [InlineData("key", "key2", false)]
    [InlineData("key", "", false)]
    public void The_key_comparison_is_exact(string expected, string given, bool equal) =>
        Assert.Equal(equal, ApiKey.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)));

    [Fact]
    public async Task The_gate_queues_up_to_its_limit_and_turns_away_the_rest()
    {
        using var gate = new SimulationGate(slots: 1, queue: 1, queueTimeout: TimeSpan.FromMinutes(5));
        var first = await gate.EnterAsync(CancellationToken.None);
        Assert.NotNull(first);

        var waiting = gate.EnterAsync(CancellationToken.None).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Null(await gate.EnterAsync(CancellationToken.None));
        Assert.Equal(2, gate.Admitted);

        first.Dispose();
        first.Dispose();
        using var second = await waiting;
        Assert.NotNull(second);
        Assert.Equal(1, gate.Admitted);
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_gate_as_it_was()
    {
        using var gate = new SimulationGate(slots: 1, queue: 1, queueTimeout: TimeSpan.FromMinutes(5));
        using var first = await gate.EnterAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = gate.EnterAsync(cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, gate.Admitted);
    }
}
