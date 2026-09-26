using System.Windows;
using System.Windows.Media;

namespace QuotaPeek.UI;

// A presentation group only: credentials, snapshots, histories and refreshes stay source-owned.
public sealed class ProviderCardViewModel
{
    public string Name { get; }
    public string Scope { get; }
    public string Initial { get; }
    public Brush Accent { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public string AutomationId { get; }
    public IReadOnlyList<ServiceSectionViewModel> Services { get; }

    private ProviderCardViewModel(IReadOnlyList<CardViewModel> cards)
    {
        // Always give the account wallet priority, including when it needs attention.
        var ordered = cards.OrderBy(c => c.Config.Type == ProviderType.HoneWallet ? 0 : 1).ToList();
        var first = ordered[0];
        var grouped = ordered.Count > 1;
        Name = grouped ? ProviderName(first.Config) : first.Name;
        Scope = grouped
            ? string.Join(" · ", ordered.GroupBy(c => ServiceKind(c.Config.Type))
                .Select(g => g.Count() == 1 ? g.Key : $"{g.Count()} 个{g.Key}"))
            : first.Scope;
        Initial = first.Config.Type == ProviderType.Codex ? ">_" : Name[..Math.Min(1, Name.Length)].ToUpperInvariant();
        Accent = first.Accent;
        AutomationId = "ProviderCard_" + first.Config.Id;

        var attention = ordered.OrderByDescending(Attention).First();
        var mixed = ordered.Select(c => c.Snapshot?.Status).Distinct().Count() > 1;
        StatusText = mixed ? attention.Snapshot?.Status switch
        {
            SnapshotStatus.Error => "部分连接失败",
            SnapshotStatus.Stale => "部分数据过期",
            SnapshotStatus.Setup => "部分待连接",
            _ => "等待同步"
        } : attention.StatusText;
        StatusBrush = attention.StatusBrush;

        var hasWallet = ordered.Any(c => c.Config.Type == ProviderType.HoneWallet);
        Services = ordered.Select((card, index) => new ServiceSectionViewModel(card,
            SectionName(card, ordered), compact: index > 0,
            showHeader: grouped && (index > 0 || mixed || ordered.Count(c => c.Config.Type == card.Config.Type) > 1),
            hasWallet)).ToList();
    }

    public static IReadOnlyList<ProviderCardViewModel> Group(IEnumerable<CardViewModel> cards)
        => cards.Where(c => c.Config.Enabled).GroupBy(c => GroupKey(c.Config))
            .Select(g => new ProviderCardViewModel(g.ToList())).ToList();

    private static (ProviderType Family, string Site) GroupKey(ProviderConfig config)
    {
        var family = config.Type == ProviderType.HoneWallet ? ProviderType.Hone : config.Type;
        if (family == ProviderType.Codex) return (family, "local");
        if (family == ProviderType.Manual) return (family, config.Id);
        // Match the actual fetcher's /v1 normalization; keep tenant paths and ports distinct.
        var address = family == ProviderType.Custom ? config.Http.Url : HttpQuotaProvider.Endpoint(config.BaseUrl, "");
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            return (family, config.Id);
        return (family, uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            + uri.AbsolutePath.TrimEnd('/') + uri.Query);
    }

    private static string ProviderName(ProviderConfig config) => config.Type switch
    {
        ProviderType.Hone or ProviderType.HoneWallet => "Hone",
        ProviderType.Relay or ProviderType.Custom => new Uri(config.Type == ProviderType.Custom ? config.Http.Url : config.BaseUrl).Host,
        _ => ProviderConfig.Create(config.Type).Name
    };

    private static string ServiceKind(ProviderType type) => type switch
    {
        ProviderType.HoneWallet => "钱包",
        ProviderType.Hone or ProviderType.OpenRouter => "API key",
        ProviderType.Codex => "订阅",
        _ => "账户"
    };

    private static string SectionName(CardViewModel card, IReadOnlyList<CardViewModel> cards)
    {
        var name = card.Name;
        if (cards.Count(c => c.Config.Type == card.Config.Type) == 1)
            name = card.Config.Type switch
            {
                ProviderType.HoneWallet when name == "Hone 钱包" => "账户钱包",
                ProviderType.Hone when name == "Hone API" => "API key",
                _ => name
            };
        var duplicates = cards.Where(c => c.Name == card.Name).ToList();
        return duplicates.Count > 1 ? name + " " + (duplicates.IndexOf(card) + 1) : name;
    }

    private static int Attention(CardViewModel card) => card.Snapshot?.Status switch
    {
        SnapshotStatus.Error => 5,
        SnapshotStatus.Stale => 4,
        SnapshotStatus.Setup => 3,
        null => 2,
        _ => QuotaRules.IsLow(card.Config, card.Snapshot) ? 1 : 0
    };
}

public sealed class ServiceSectionViewModel
{
    public CardViewModel Card { get; }
    public string Name { get; }
    public string AutomationId => "Service_" + Card.Config.Id;
    public double ValueFontSize { get; }
    public Visibility HeaderVisibility { get; }
    public Thickness SeparatorThickness { get; }
    public Thickness Padding { get; }
    public Thickness Margin { get; }
    public string? Message { get; }
    public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;

    internal ServiceSectionViewModel(CardViewModel card, string name, bool compact, bool showHeader, bool hasWallet)
    {
        Card = card;
        Name = name;
        ValueFontSize = compact ? 22 : 32;
        HeaderVisibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
        SeparatorThickness = new Thickness(0, compact ? 1 : 0, 0, 0);
        Padding = new Thickness(0, compact ? 10 : 0, 0, 0);
        Margin = new Thickness(0, compact ? 11 : 0, 0, 0);
        // Only replace the healthy unlimited-key hint. Never suppress an error or stale warning.
        Message = hasWallet && card.Config.Type == ProviderType.Hone
            && card.Snapshot is { Status: SnapshotStatus.Ok, Kind: QuotaKind.Limit, Remaining: null, Total: null, Used: not null }
            ? "当前 API key 未设限。" : card.Message;
    }
}
