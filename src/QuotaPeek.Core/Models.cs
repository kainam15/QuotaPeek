namespace QuotaPeek.Core;

public enum QuotaKind { Balance, Limit, Spend, RateWindow }
public enum SnapshotStatus { Ok, Stale, Error, Setup }
public enum ProviderType { Hone, Codex, DeepSeek, Kimi, SiliconFlow, OpenRouter, Relay, Custom, Manual, HoneWallet }

public sealed record RateWindow(string Name, decimal UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt)
{
    public decimal RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record QuotaSnapshot
{
    public required string ProviderId { get; init; }
    public QuotaKind Kind { get; init; }
    public decimal? Remaining { get; init; }
    public decimal? Used { get; init; }
    public decimal? Total { get; init; }
    public decimal? UsedToday { get; init; }
    public decimal? UsedMonth { get; init; }
    public string? Currency { get; init; }
    public string UsageLabel { get; init; } = "累计消费";
    public string Scope { get; init; } = "账户";
    public DateTimeOffset? FetchedAt { get; init; }
    public DateTimeOffset AttemptedAt { get; init; } = DateTimeOffset.UtcNow;
    public SnapshotStatus Status { get; init; } = SnapshotStatus.Ok;
    public string? Message { get; init; }
    public List<RateWindow> Windows { get; init; } = [];
    public int? AvailableResets { get; init; }
    public string? Plan { get; init; }
    public decimal? EstimatedDays { get; init; }
    public bool IsEstimate { get; init; }
    public bool HasValue => Remaining.HasValue || Used.HasValue || Windows.Count > 0;
}

public sealed record HttpMapping
{
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new() { ["Authorization"] = "Bearer {key}" };
    public string RemainingPath { get; set; } = "$.data.balance";
    public string UsedPath { get; set; } = "";
    public string TotalPath { get; set; } = "";
    public decimal Multiplier { get; set; } = 1;
    public QuotaKind Kind { get; set; } = QuotaKind.Balance;
}

public sealed record ProviderConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Hone API";
    public ProviderType Type { get; set; } = ProviderType.Hone;
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://hone.vvvv.ee";
    public string Currency { get; set; } = "USD";
    public int RefreshMinutes { get; set; } = 5;
    public decimal LowThreshold { get; set; } = 5;
    public string SecretEnvironment { get; set; } = "HONE_API_KEY";
    public string CodexExecutable { get; set; } = "";
    public decimal UsageMultiplier { get; set; } = 0.01m;
    public decimal? Budget { get; set; }
    public decimal? ManualRemaining { get; set; }
    public decimal? ManualUsed { get; set; }
    public DateTimeOffset? ManualUpdatedAt { get; set; }
    public HttpMapping Http { get; set; } = new();

    public bool NeedsSecret => Type is not (ProviderType.Codex or ProviderType.Manual);

    public static ProviderConfig Create(ProviderType type) => type switch
    {
        ProviderType.Hone => new() { Type = type },
        ProviderType.HoneWallet => new() { Type = type, Name = "Hone 钱包", SecretEnvironment = "HONE_ACCESS_TOKEN" },
        ProviderType.Codex => new() { Type = type, Name = "Codex", BaseUrl = "", Currency = "", LowThreshold = 15, SecretEnvironment = "" },
        ProviderType.DeepSeek => new() { Type = type, Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Currency = "CNY", LowThreshold = 10, SecretEnvironment = "DEEPSEEK_API_KEY" },
        ProviderType.Kimi => new() { Type = type, Name = "Kimi", BaseUrl = "https://api.moonshot.cn", Currency = "CNY", LowThreshold = 10, SecretEnvironment = "MOONSHOT_API_KEY" },
        ProviderType.SiliconFlow => new() { Type = type, Name = "硅基流动", BaseUrl = "https://api.siliconflow.cn", Currency = "CNY", LowThreshold = 10, SecretEnvironment = "SILICONFLOW_API_KEY" },
        ProviderType.OpenRouter => new() { Type = type, Name = "OpenRouter", BaseUrl = "https://openrouter.ai", SecretEnvironment = "OPENROUTER_API_KEY" },
        ProviderType.Relay => new() { Type = type, Name = "API 中转站", BaseUrl = "", SecretEnvironment = "" },
        ProviderType.Custom => new() { Type = type, Name = "自定义 API", BaseUrl = "", SecretEnvironment = "" },
        _ => new() { Type = ProviderType.Manual, Name = "手动账户", BaseUrl = "", SecretEnvironment = "", RefreshMinutes = 60 }
    };
}

public sealed record AppSettings
{
    public int Version { get; set; } = 1;
    public List<ProviderConfig> Providers { get; set; } = [ProviderConfig.Create(ProviderType.Hone), ProviderConfig.Create(ProviderType.Codex)];
    public bool Notifications { get; set; } = true;
    public bool AutoHideFullscreen { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public double? LeftPixels { get; set; }
    public double? TopPixels { get; set; }
    public bool StartExpanded { get; set; } = true;
}

public sealed class ProviderException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IQuotaProvider
{
    Task<QuotaSnapshot> FetchAsync(ProviderConfig config, string? secret, CancellationToken cancellationToken);
}

public static class QuotaRules
{
    public static QuotaSnapshot Failure(ProviderConfig config, QuotaSnapshot? previous, string message, DateTimeOffset now)
        => previous is { HasValue: true }
            ? previous with { Status = SnapshotStatus.Stale, Message = message, AttemptedAt = now }
            : new() { ProviderId = config.Id, Kind = config.Type == ProviderType.Codex ? QuotaKind.RateWindow : QuotaKind.Balance,
                Currency = config.Currency, Status = SnapshotStatus.Error, Message = message, AttemptedAt = now };

    public static bool IsLow(ProviderConfig config, QuotaSnapshot snapshot)
        => snapshot.Status == SnapshotStatus.Ok && (snapshot.Kind == QuotaKind.RateWindow
            ? snapshot.Windows.Any(w => w.RemainingPercent <= config.LowThreshold)
            : snapshot.Remaining <= config.LowThreshold);

    public static decimal? EstimateDays(IReadOnlyList<QuotaSnapshot> history)
    {
        var points = history.Where(s => s.Status == SnapshotStatus.Ok && s.Remaining.HasValue && s.FetchedAt.HasValue)
            .OrderBy(s => s.FetchedAt).ToList();
        if (points.Count < 3 || points[^1].Kind == QuotaKind.RateWindow) return null;
        var currency = points[^1].Currency;
        var end = points[^1].FetchedAt!.Value;
        points = points.Where(s => s.Currency == currency && end - s.FetchedAt!.Value <= TimeSpan.FromDays(7)).ToList();
        decimal spent = 0;
        double days = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var delta = points[i - 1].Remaining!.Value - points[i].Remaining!.Value;
            var duration = (points[i].FetchedAt!.Value - points[i - 1].FetchedAt!.Value).TotalDays;
            // Exclude refill intervals and long offline gaps entirely.
            if (delta < 0 || duration <= 0 || duration > 1.5) continue;
            spent += delta;
            days += duration;
        }
        if (days < 0.5 || spent <= 0) return null;
        return Math.Round(Math.Max(0, points[^1].Remaining!.Value) / (spent / (decimal)days), 1);
    }

    public static TimeSpan RetryDelay(int failures, TimeSpan interval, TimeSpan? retryAfter = null)
    {
        var seconds = Math.Min(3600, Math.Max(60, interval.TotalSeconds) * Math.Pow(2, Math.Clamp(failures - 1, 0, 8)));
        return TimeSpan.FromSeconds(Math.Max(seconds, Math.Clamp(retryAfter?.TotalSeconds ?? 0, 0, 86400)));
    }
}
