using System.Globalization;

namespace BGSim.Server;

/// <summary>
/// Limits and switches of the server, from <c>BGSIM_*</c> settings (environment variables, or any configuration source
/// in tests). Every value has a default for a small container; docs/server.md lists them.
/// </summary>
internal sealed record ServerLimits
{
    /// <summary>Largest request body, bytes (<c>BGSIM_MAX_BODY_BYTES</c>).</summary>
    public int MaxBodyBytes { get; init; } = 64 * 1024;

    /// <summary>Most fights one /v1/simulate request may ask for (<c>BGSIM_MAX_RUNS</c>).</summary>
    public int MaxRuns { get; init; } = 10_000;

    /// <summary>Fights when the request does not say (<c>BGSIM_DEFAULT_RUNS</c>); the precision stop usually ends sooner.</summary>
    public int DefaultRuns { get; init; } = 10_000;

    /// <summary>Longest time budget a request may ask for, ms (<c>BGSIM_MAX_TIME_BUDGET_MS</c>).</summary>
    public int MaxTimeBudgetMs { get; init; } = 1000;

    /// <summary>Time budget when the request does not say, ms (<c>BGSIM_DEFAULT_TIME_BUDGET_MS</c>).</summary>
    public int DefaultTimeBudgetMs { get; init; } = 200;

    /// <summary>
    /// Precision stop when the request does not say (<c>BGSIM_DEFAULT_TARGET_HALF_WIDTH</c>): the runs end once the 95 %
    /// interval of win, tie and loss is at most this far either side (0.01 = 1 percentage point).
    /// </summary>
    public double DefaultTargetHalfWidth { get; init; } = 0.01;

    /// <summary>Simulations that run at once (<c>BGSIM_MAX_CONCURRENT</c>), one thread each; default = logical processors.</summary>
    public int MaxConcurrent { get; init; } = Environment.ProcessorCount;

    /// <summary>Requests that may wait for a free slot (<c>BGSIM_QUEUE_LIMIT</c>); more get 503.</summary>
    public int QueueLimit { get; init; } = 2 * Environment.ProcessorCount;

    /// <summary>Longest wait in the queue, ms (<c>BGSIM_QUEUE_TIMEOUT_MS</c>); then 503.</summary>
    public int QueueTimeoutMs { get; init; } = 5000;

    /// <summary>Most log lines /v1/fight returns (<c>BGSIM_FIGHT_LOG_LINES</c>); the rest is cut.</summary>
    public int MaxFightLogLines { get; init; } = 2000;

    /// <summary>Key the /v1 endpoints require (<c>BGSIM_API_KEY</c>); null or empty = open.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Origins allowed by CORS (<c>BGSIM_CORS_ORIGINS</c>, comma-separated); empty = CORS off.</summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];

    public bool ApiKeyRequired => !string.IsNullOrEmpty(ApiKey);

    /// <summary>Reads the settings; a malformed or out-of-range value is a start-up error, not a silent default.</summary>
    public static ServerLimits From(Func<string, string?> setting)
    {
        var defaults = new ServerLimits();
        return new ServerLimits
        {
            MaxBodyBytes = Int(setting, "BGSIM_MAX_BODY_BYTES", defaults.MaxBodyBytes, 1024, 16 * 1024 * 1024),
            MaxRuns = Int(setting, "BGSIM_MAX_RUNS", defaults.MaxRuns, 1, 1_000_000),
            DefaultRuns = Int(setting, "BGSIM_DEFAULT_RUNS", defaults.DefaultRuns, 1, 1_000_000),
            MaxTimeBudgetMs = Int(setting, "BGSIM_MAX_TIME_BUDGET_MS", defaults.MaxTimeBudgetMs, 1, 60_000),
            DefaultTimeBudgetMs = Int(setting, "BGSIM_DEFAULT_TIME_BUDGET_MS", defaults.DefaultTimeBudgetMs, 1, 60_000),
            DefaultTargetHalfWidth = Double(setting, "BGSIM_DEFAULT_TARGET_HALF_WIDTH", defaults.DefaultTargetHalfWidth, 0, 0.5),
            MaxConcurrent = Int(setting, "BGSIM_MAX_CONCURRENT", defaults.MaxConcurrent, 1, 1024),
            QueueLimit = Int(setting, "BGSIM_QUEUE_LIMIT", defaults.QueueLimit, 0, 100_000),
            QueueTimeoutMs = Int(setting, "BGSIM_QUEUE_TIMEOUT_MS", defaults.QueueTimeoutMs, 1, 600_000),
            MaxFightLogLines = Int(setting, "BGSIM_FIGHT_LOG_LINES", defaults.MaxFightLogLines, 0, 1_000_000),
            ApiKey = setting("BGSIM_API_KEY") is { Length: > 0 } key ? key : null,
            CorsOrigins = (setting("BGSIM_CORS_ORIGINS") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        }.Checked();
    }

    private ServerLimits Checked()
    {
        if (DefaultRuns > MaxRuns)
        {
            throw new InvalidOperationException("BGSIM_DEFAULT_RUNS is above BGSIM_MAX_RUNS.");
        }

        if (DefaultTimeBudgetMs > MaxTimeBudgetMs)
        {
            throw new InvalidOperationException("BGSIM_DEFAULT_TIME_BUDGET_MS is above BGSIM_MAX_TIME_BUDGET_MS.");
        }

        return this;
    }

    private static int Int(Func<string, string?> setting, string name, int fallback, int min, int max)
    {
        var text = setting(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"{name} must be an integer from {min} to {max}.");
        }

        return value;
    }

    private static double Double(Func<string, string?> setting, string name, double fallback, double min, double max)
    {
        var text = setting(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !(value >= min && value <= max))
        {
            throw new InvalidOperationException($"{name} must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        }

        return value;
    }
}
