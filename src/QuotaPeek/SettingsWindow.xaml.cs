using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuotaPeek.Services;
using QuotaPeek.UI;

namespace QuotaPeek;

public partial class SettingsWindow : Window
{
    private readonly App app;
    private AppSettings working;
    private ProviderConfig? selected;
    private readonly HashSet<string> drafts = [];
    private readonly CancellationTokenSource lifetime = new();
    private static readonly (ProviderType Type, string Name)[] Types = [(ProviderType.Hone, "Hone API"), (ProviderType.HoneWallet, "Hone 钱包"), (ProviderType.Codex, "Codex"),
        (ProviderType.DeepSeek, "DeepSeek"), (ProviderType.Kimi, "Kimi"), (ProviderType.SiliconFlow, "硅基流动"),
        (ProviderType.OpenRouter, "OpenRouter"), (ProviderType.Relay, "New API 中转站"), (ProviderType.Custom, "自定义 HTTP"), (ProviderType.Manual, "手动录入")];
    public SettingsWindow(App app)
    {
        InitializeComponent();
        this.app = app;
        working = Clone(app.Monitor.Settings);
        AddType.ItemsSource = Types.Select(t => t.Name); AddType.SelectedIndex = 0;
        NotifyCheck.IsChecked = working.Notifications;
        FullscreenCheck.IsChecked = working.AutoHideFullscreen;
        StartupCheck.IsChecked = working.StartWithWindows;
        ReloadList(working.Providers.FirstOrDefault()?.Id);
        Closed += (_, _) => lifetime.Cancel();
    }
    private static AppSettings Clone(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, SettingsStore.Json), SettingsStore.Json)!;
    private void ReloadList(string? id)
    {
        ProviderList.ItemsSource = null; ProviderList.ItemsSource = working.Providers;
        ProviderList.SelectedItem = working.Providers.FirstOrDefault(p => p.Id == id) ?? working.Providers.FirstOrDefault();
        Editor.IsEnabled = ProviderList.SelectedItem is not null;
        Actions.IsEnabled = Editor.IsEnabled;
    }
    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderConfig p) return;
        selected = p; KeyInput.Clear();
        TypeTitle.Text = Types.First(t => t.Type == p.Type).Name;
        NameInput.Text = p.Name; EnabledCheck.IsChecked = p.Enabled;
        BaseUrlInput.Text = p.Type == ProviderType.Custom ? p.Http.Url : p.BaseUrl;
        EnvironmentInput.Text = p.SecretEnvironment; CodexPathInput.Text = p.CodexExecutable;
        IntervalInput.Text = p.RefreshMinutes.ToString(); ThresholdInput.Text = p.LowThreshold.ToString(CultureInfo.InvariantCulture);
        ThresholdLabel.Text = p.Type == ProviderType.Codex ? "剩余百分比提醒 / %" : "剩余金额提醒";
        CurrencyInput.SelectedIndex = p.Currency == "CNY" ? 1 : 0;
        CurrencyInput.IsEnabled = p.Type is not (ProviderType.Hone or ProviderType.HoneWallet or ProviderType.Kimi or ProviderType.OpenRouter);
        UsageMultiplierInput.Text = p.UsageMultiplier.ToString(CultureInfo.InvariantCulture);
        ManualRemainingInput.Text = p.ManualRemaining?.ToString(CultureInfo.InvariantCulture) ?? "";
        ManualUsedInput.Text = p.ManualUsed?.ToString(CultureInfo.InvariantCulture) ?? "";
        BudgetInput.Text = p.Budget?.ToString(CultureInfo.InvariantCulture) ?? "";
        KindInput.SelectedIndex = (int)p.Http.Kind;
        RemainingPathInput.Text = p.Http.RemainingPath; UsedPathInput.Text = p.Http.UsedPath; TotalPathInput.Text = p.Http.TotalPath;
        MultiplierInput.Text = p.Http.Multiplier.ToString(CultureInfo.InvariantCulture);
        HeadersInput.Text = JsonSerializer.Serialize(p.Http.Headers, SettingsStore.Json);
        ApiFields.Visibility = p.NeedsSecret ? Visibility.Visible : Visibility.Collapsed;
        KeyLabel.Text = p.Type == ProviderType.HoneWallet ? "账户访问令牌" : "API key";
        HoneWalletFields.Visibility = p.Type == ProviderType.HoneWallet ? Visibility.Visible : Visibility.Collapsed;
        ConnectWalletFields.Visibility = p.Type == ProviderType.Hone ? Visibility.Visible : Visibility.Collapsed;
        DeleteKeyButton.Visibility = ApiFields.Visibility;
        CodexFields.Visibility = p.Type == ProviderType.Codex ? Visibility.Visible : Visibility.Collapsed;
        CurrencyFields.Visibility = p.Type == ProviderType.Codex ? Visibility.Collapsed : Visibility.Visible;
        RelayFields.Visibility = p.Type is ProviderType.Hone or ProviderType.Relay ? Visibility.Visible : Visibility.Collapsed;
        ManualFields.Visibility = p.Type == ProviderType.Manual ? Visibility.Visible : Visibility.Collapsed;
        CustomFields.Visibility = p.Type == ProviderType.Custom ? Visibility.Visible : Visibility.Collapsed;
        BudgetFields.Visibility = p.Type is ProviderType.Manual or ProviderType.Custom ? Visibility.Visible : Visibility.Collapsed;
        try { KeyHint.Text = app.Credentials.Read(p.Id) is null ? p.Type == ProviderType.HoneWallet
            ? "尚未连接钱包；填写账户访问令牌或使用环境变量。" : "尚未保存密钥；可填写 key 或使用环境变量。"
            : "密钥已保存在 Windows 凭据管理器；留空保留。"; }
        catch { KeyHint.Text = "Windows 凭据管理器暂不可用。"; }
        EditorScroll.ScrollToTop();
    }
    private ProviderConfig ReadForm()
    {
        if (selected is null) throw new ProviderException("请先选择一个数据源。");
        var config = selected with { Http = selected.Http with { } };
        config.Name = NameInput.Text.Trim();
        if (config.Name.Length == 0) throw new ProviderException("请填写显示名称。");
        config.Enabled = EnabledCheck.IsChecked == true;
        if (!int.TryParse(IntervalInput.Text, out var interval) || interval < 1 || interval > 1440) throw new ProviderException("刷新间隔须在 1–1440 分钟之间。");
        config.RefreshMinutes = interval;
        config.LowThreshold = Number(ThresholdInput.Text, "阈值", false)!.Value;
        if (config.LowThreshold < 0 || config.Type == ProviderType.Codex && config.LowThreshold > 100) throw new ProviderException("阈值无效；Codex 应为 0–100%。");
        config.Currency = CurrencyInput.SelectedIndex == 1 ? "CNY" : "USD";
        config.SecretEnvironment = EnvironmentInput.Text.Trim();
        config.CodexExecutable = CodexPathInput.Text.Trim();
        if (config.Type == ProviderType.Codex) config.Currency = "";
        if (config.NeedsSecret)
        {
            var url = BaseUrlInput.Text.Trim();
            HttpQuotaProvider.ValidateUrl(url);
            if (url.Contains("{key}", StringComparison.OrdinalIgnoreCase) || url.Contains("key=", StringComparison.OrdinalIgnoreCase) || url.Contains("token=", StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("密钥请放入请求头模板，不要填在 URL 中。");
            if (config.Type == ProviderType.Custom) config.Http.Url = url; else config.BaseUrl = url;
        }
        if (config.Type is ProviderType.Hone or ProviderType.Relay)
        {
            config.UsageMultiplier = Number(UsageMultiplierInput.Text, "换算倍数", false)!.Value;
            if (config.UsageMultiplier <= 0) throw new ProviderException("换算倍数必须大于 0。");
        }
        if (config.Type is ProviderType.Manual or ProviderType.Custom)
        {
            config.Budget = Number(BudgetInput.Text, "预算", true);
            if (config.Budget < 0) throw new ProviderException("预算不能为负数。");
        }
        if (config.Type == ProviderType.Manual)
        {
            config.ManualRemaining = Number(ManualRemainingInput.Text, "余额", true);
            config.ManualUsed = Number(ManualUsedInput.Text, "已用", true);
            if (config.ManualUsed < 0) throw new ProviderException("已用金额不能为负数。");
            if (config.ManualRemaining is null && config.ManualUsed is null) throw new ProviderException("至少录入一个金额。");
            config.ManualUpdatedAt = DateTimeOffset.UtcNow;
        }
        if (config.Type == ProviderType.Custom)
        {
            config.Http.Kind = (QuotaKind)Math.Clamp(KindInput.SelectedIndex, 0, 2);
            config.Http.RemainingPath = RemainingPathInput.Text.Trim(); config.Http.UsedPath = UsedPathInput.Text.Trim(); config.Http.TotalPath = TotalPathInput.Text.Trim();
            config.Http.Multiplier = Number(MultiplierInput.Text, "换算倍数", false)!.Value;
            if (config.Http.Multiplier <= 0) throw new ProviderException("换算倍数必须大于 0。");
            using var empty = JsonDocument.Parse("{}");
            foreach (var path in new[] { config.Http.RemainingPath, config.Http.UsedPath, config.Http.TotalPath })
                if (path.Length > 0) JsonFields.Get(empty.RootElement, path);
            if (new[] { config.Http.RemainingPath, config.Http.UsedPath, config.Http.TotalPath }.All(string.IsNullOrWhiteSpace)) throw new ProviderException("请填写至少一个字段路径。");
            try { config.Http.Headers = JsonSerializer.Deserialize<Dictionary<string, string>>(HeadersInput.Text) ?? throw new JsonException(); }
            catch (JsonException) { throw new ProviderException("请求头必须是 JSON 字符串字典。"); }
            foreach (var pair in config.Http.Headers)
                if (pair.Value is null || pair.Value.Contains("sk-") || (new[] { "authorization", "key", "token", "secret" }.Any(word => pair.Key.Contains(word, StringComparison.OrdinalIgnoreCase)) && !pair.Value.Contains("{key}")))
                    throw new ProviderException("请求头中请用 {key} 引用密钥，不要写入实际 key。");
        }
        var endpointChanged = config.BaseUrl != selected.BaseUrl || config.Http.Url != selected.Http.Url;
        if (endpointChanged && config.NeedsSecret && KeyInput.Password.Length == 0 && app.Credentials.Read(selected.Id) is not null)
            throw new ProviderException("接口地址已改变，请重新输入该地址对应的 key。");
        return config;
    }
    private static decimal? Number(string value, string label, bool optional)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return null;
        if (decimal.TryParse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return number;
        throw new ProviderException(label + "应填写数字，小数点使用 .。");
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var config = ProviderConfig.Create(Types[Math.Max(0, AddType.SelectedIndex)].Type);
        drafts.Add(config.Id);
        working.Providers.Add(config); ReloadList(config.Id); StatusText.Text = "填写信息后点击保存账户。";
    }
    private void ConnectWallet_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = BaseUrlInput.Text.Trim();
            HttpQuotaProvider.ValidateUrl(baseUrl);
            var existing = working.Providers.FirstOrDefault(p => p.Type == ProviderType.HoneWallet && p.BaseUrl == baseUrl);
            if (existing is not null) { ReloadList(existing.Id); return; }
            var wallet = ProviderConfig.Create(ProviderType.HoneWallet) with { BaseUrl = baseUrl };
            drafts.Add(wallet.Id); working.Providers.Insert(0, wallet); ReloadList(wallet.Id);
            StatusText.Text = "填写 Hone 账户访问令牌后测试并保存。现有 API key 单独保留。";
        }
        catch (Exception error) { ShowError(error); }
    }
    private void WalletWebsite_Click(object sender, RoutedEventArgs e)
    {
        try { App.OpenWebsite(HttpQuotaProvider.Endpoint(ReadForm().BaseUrl, "/security")); }
        catch (Exception error) { ShowError(error); }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        try
        {
            var config = ReadForm();
            var key = KeyInput.Password.Length > 0 ? KeyInput.Password.Trim() : app.Credentials.Read(config.Id);
            key ??= string.IsNullOrEmpty(config.SecretEnvironment) ? null : Environment.GetEnvironmentVariable(config.SecretEnvironment);
            if (app.Monitor.Demo) { StatusText.Text = "演示模式不访问网络；请在正式实例中测试。"; return; }
            StatusText.Text = "正在读取额度…";
            using var http = new HttpQuotaProvider();
            IQuotaProvider provider = config.Type == ProviderType.Codex ? new CodexQuotaProvider() : http;
            var snapshot = await provider.FetchAsync(config, key, lifetime.Token);
            var card = new CardViewModel(config, snapshot, []);
            StatusText.Text = snapshot.Status == SnapshotStatus.Setup ? snapshot.Message : "连接成功 · " + card.PrimaryLabel + " " + card.PrimaryValue + "。点击保存后开始自动刷新。";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error); }
        finally { TestButton.IsEnabled = true; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ReadForm(); var old = selected!;
            var newKey = KeyInput.Password.Trim();
            // Start a fresh history stream when account identity or units change.
            var identityChanged = newKey.Length > 0 || config.BaseUrl != old.BaseUrl || config.Http.Url != old.Http.Url || config.Currency != old.Currency
                || config.CodexExecutable != old.CodexExecutable || config.UsageMultiplier != old.UsageMultiplier || config.SecretEnvironment != old.SecretEnvironment
                || JsonSerializer.Serialize(config.Http) != JsonSerializer.Serialize(old.Http);
            if (identityChanged) config.Id = Guid.NewGuid().ToString("N");
            var retainedKey = newKey.Length > 0 ? newKey : identityChanged ? app.Credentials.Read(old.Id) : null;
            if (config.NeedsSecret && retainedKey is { Length: > 0 }) app.Credentials.Write(config.Id, retainedKey);
            var index = working.Providers.IndexOf(old); working.Providers[index] = config;
            try { Commit(config.Id); }
            catch { working.Providers[index] = old; if (identityChanged && retainedKey is { Length: > 0 }) app.Credentials.Delete(config.Id); throw; }
            if (identityChanged) app.Credentials.Delete(old.Id);
            drafts.Remove(old.Id);
            KeyInput.Clear(); ReloadList(config.Id); StatusText.Text = "账户已保存，正在同步。";
        }
        catch (Exception error) { ShowError(error); }
    }
    private void Commit(string? savingId = null)
    {
        // Window coordinates can change while settings are open.
        working.LeftPixels = app.Monitor.Settings.LeftPixels; working.TopPixels = app.Monitor.Settings.TopPixels;
        working.StartExpanded = app.Monitor.Settings.StartExpanded;
        var committed = Clone(working);
        committed.Providers = committed.Providers.Where(p => !drafts.Contains(p.Id) || p.Id == savingId).ToList();
        app.SaveSettings(committed);
    }
    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startup = StartupCheck.IsChecked == true;
            if (startup != working.StartWithWindows)
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (startup) key.SetValue("QuotaPeek", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("QuotaPeek", false);
            }
            working.StartWithWindows = startup;
            working.Notifications = NotifyCheck.IsChecked == true; working.AutoHideFullscreen = FullscreenCheck.IsChecked == true;
            Commit(); StatusText.Text = "桌面偏好已保存。";
        }
        catch (Exception error) { ShowError(error); }
    }
    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        try { app.Credentials.Delete(selected.Id); KeyInput.Clear(); Commit(); KeyHint.Text = "已清除保存的 key；环境变量仍可提供密钥。"; }
        catch (Exception error) { ShowError(error); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        try
        {
            var old = selected; working.Providers.Remove(old); Commit(); app.Credentials.Delete(old.Id); ReloadList(null);
            StatusText.Text = "数据源已移除，历史记录保留至正常过期。";
        }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error) => StatusText.Text = error is ProviderException ? error.Message : "操作失败（" + error.GetType().Name + "），请检查输入、网络或目录权限。";
}
