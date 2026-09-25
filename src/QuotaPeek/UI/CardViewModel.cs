using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace QuotaPeek.UI;

public sealed class CardViewModel
{
    public ProviderConfig Config { get; }
    public QuotaSnapshot? Snapshot { get; }
    public string Name => Config.Name;
    public string Initial => Config.Type == ProviderType.Codex ? ">_" : Name[..Math.Min(1, Name.Length)].ToUpperInvariant();
    public Brush Accent { get; }
    public Brush StatusBrush { get; }
    public string StatusText { get; }
    public string PrimaryValue { get; }
    public string PrimaryLabel { get; }
    public string SecondaryText { get; }
    public string Updated { get; }
    public string Scope { get; }
    public string? Message { get; }
    public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MoneyVisibility => Snapshot?.Kind == QuotaKind.RateWindow ? Visibility.Collapsed : Visibility.Visible;
    public List<WindowViewModel> Windows { get; } = [];
    public string Forecast { get; }
    public string ResetCredits { get; }
    public Visibility ForecastVisibility => Forecast.Length == 0 && ResetCredits.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ForecastTextVisibility => Forecast.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResetsVisibility => ResetCredits.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public PointCollection Trend { get; } = [];
    public Visibility TrendVisibility => Trend.Count < 3 ? Visibility.Collapsed : Visibility.Visible;
    public string TrendLabel => "7 日余额趋势 · 本机记录";
    public bool NeedsSetup => Snapshot?.Status == SnapshotStatus.Setup;
    public Visibility SetupVisibility => NeedsSetup ? Visibility.Visible : Visibility.Collapsed;

    public CardViewModel(ProviderConfig config, QuotaSnapshot? snapshot, IReadOnlyList<QuotaSnapshot> history)
    {
        Config = config; Snapshot = snapshot;
        Accent = Brush(config.Type == ProviderType.Codex ? "#A6EDCF" : "#9BBBF2");
        var status = snapshot?.Status;
        var low = snapshot is not null && QuotaRules.IsLow(config, snapshot);
        StatusBrush = Brush(status switch { SnapshotStatus.Stale => "#F1C985", SnapshotStatus.Error => "#FF9B9B", SnapshotStatus.Setup => "#94A8B9", _ => low ? "#FFB185" : "#A6EDCF" });
        StatusText = status switch { SnapshotStatus.Stale => "已过期", SnapshotStatus.Error => "连接失败", SnapshotStatus.Setup => "待连接", SnapshotStatus.Ok => low ? "额度偏低" : "已同步", _ => "等待同步" };
        Scope = snapshot?.Scope ?? (config.Type == ProviderType.Codex ? "本机 Codex 账户" : "API 余额");
        if (snapshot?.Plan is { Length: > 0 } plan) Scope = plan.ToUpperInvariant() + " · " + Scope;
        var minimum = snapshot?.Windows.OrderBy(w => w.RemainingPercent).FirstOrDefault();
        PrimaryValue = minimum is not null ? $"{minimum.RemainingPercent:0.#}%" : Money(snapshot?.Remaining ?? snapshot?.Used, snapshot?.Currency ?? config.Currency);
        var balanceLabel = config.Type == ProviderType.HoneWallet ? "钱包余额" : "可用余额";
        PrimaryLabel = minimum is not null ? minimum.Name + "剩余" : snapshot?.Remaining.HasValue == true ? snapshot.IsEstimate ? "预算剩余 · 估算" : balanceLabel : snapshot?.Used.HasValue == true ? snapshot.UsageLabel : balanceLabel;
        SecondaryText = snapshot?.UsedMonth is not null ? "本月已用  " + Money(snapshot.UsedMonth, snapshot.Currency)
            : snapshot?.Used is not null && snapshot.Remaining is not null ? snapshot.UsageLabel + "  " + Money(snapshot.Used, snapshot.Currency)
            : snapshot?.Total is not null ? "总额度  " + Money(snapshot.Total, snapshot.Currency) : "";
        Updated = snapshot?.FetchedAt is { } time ? "更新于 " + time.ToLocalTime().ToString("HH:mm") : "尚未读取数据";
        Message = snapshot?.Message;
        Forecast = snapshot?.EstimatedDays is { } days ? $"按近期消耗，预计可用 {days:0.#} 天" : "";
        ResetCredits = snapshot?.AvailableResets is > 0 ? $"{snapshot.AvailableResets} 次可用额度重置 · 在 Codex 中操作" : "";
        if (snapshot is not null) Windows = snapshot.Windows.Select(w => new WindowViewModel(w, Accent)).ToList();
        var points = history.Where(s => s.Status == SnapshotStatus.Ok && s.Remaining.HasValue && s.Currency == snapshot?.Currency && s.FetchedAt >= DateTimeOffset.UtcNow.AddDays(-7)).ToList();
        if (points.Count >= 3 && snapshot?.Kind != QuotaKind.RateWindow)
        {
            if (points.Count > 60) points = points.Where((_, i) => i % (int)Math.Ceiling(points.Count / 60d) == 0).Append(points[^1]).ToList();
            var min = points.Min(p => p.Remaining!.Value);
            var range = Math.Max(0.01m, points.Max(p => p.Remaining!.Value) - min);
            for (var i = 0; i < points.Count; i++) Trend.Add(new(2 + 282d * i / (points.Count - 1), 32 - (double)((points[i].Remaining!.Value - min) / range) * 28));
        }
    }

    public static string Money(decimal? value, string? currency)
        => value is null ? "—" : (currency == "CNY" ? "¥" : currency == "USD" ? "$" : "") + value.Value.ToString("N2", CultureInfo.InvariantCulture);
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}

public sealed class WindowViewModel(RateWindow window, Brush accent)
{
    public string Name => window.Name;
    public string Remaining => $"{window.RemainingPercent:0.#}% 剩余";
    public double Percentage => (double)window.RemainingPercent;
    public Brush Accent => accent;
    public string Reset => window.ResetsAt is { } date ? date > DateTimeOffset.UtcNow
        ? date.ToLocalTime().ToString("MM/dd HH:mm") + " 重置"
        : "重置时间已到，等待刷新" : "平台未提供重置时间";
}
