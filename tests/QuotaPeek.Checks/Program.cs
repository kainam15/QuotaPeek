using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using QuotaPeek.Core;
using QuotaPeek.Services;
using QuotaPeek.UI;

var passed = 0;
void Check(string name, bool condition) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
void Throws(string name, Action action)
{
    try { action(); } catch (ProviderException) { Check(name, true); return; }
    throw new Exception("FAIL: expected ProviderException: " + name);
}
JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
var hone = ProviderConfig.Create(ProviderType.Hone);
var wallet = ProviderConfig.Create(ProviderType.HoneWallet);
var walletRequests = new List<string>();
using (var provider = new HttpQuotaProvider(new FakeHandler(request =>
{
    walletRequests.Add(request.RequestUri!.AbsolutePath);
    var body = request.RequestUri.AbsolutePath switch
    {
        "/api/status" => """{"success":true,"data":{"quota_display_type":"USD","quota_per_unit":500000,"usd_exchange_rate":7.3}}""",
        "/api/user/self" => """{"success":true,"data":{"quota":13410000,"used_quota":141590000}}""",
        _ => throw new Exception("Wallet requested a key billing endpoint")
    };
    if (request.RequestUri.AbsolutePath == "/api/user/self")
        Check("wallet account token stays in Authorization", request.Headers.Authorization?.Parameter == "test-only-wallet-token");
    else Check("public status receives no credential", request.Headers.Authorization is null);
    return new(HttpStatusCode.OK) { Content = new StringContent(body) };
})))
{
    var value = await provider.FetchAsync(wallet, "test-only-wallet-token", default);
    Check("wallet reads account balance, not unlimited key quota", value.Remaining == 26.82m && value.Used == 283.18m && value.Kind == QuotaKind.Balance);
    Check("wallet uses account endpoint only", walletRequests.SequenceEqual(new[] { "/api/status", "/api/user/self" }));
    var card = new CardViewModel(wallet, value, []);
    Check("wallet card prioritizes wallet balance and account spend", card.PrimaryValue == "$26.82" && card.PrimaryLabel == "钱包余额" && card.SecondaryText == "账户累计消费  $283.18");
}
var usdStatus = Json("""{"success":true,"data":{"quota_display_type":"USD","quota_per_unit":500000}}""");
var walletAccount = Json("""{"success":true,"data":{"quota":13410123,"used_quota":141590000}}""");
var walletResult = HttpQuotaProvider.ParseWallet(wallet, walletAccount, usdStatus);
Check("wallet raw quota uses site divisor with full precision", walletResult.Remaining == 26.820246m && walletResult.Total is null && !walletResult.IsEstimate);
var cnyWallet = HttpQuotaProvider.ParseWallet(wallet, walletAccount, Json("""{"success":true,"data":{"quota_display_type":"CNY","quota_per_unit":500000,"usd_exchange_rate":7.3}}"""));
Check("wallet CNY applies the site exchange rate", cnyWallet.Currency == "CNY" && cnyWallet.Remaining == 26.820246m * 7.3m);
Check("wallet debt preserved and absent spend not invented", HttpQuotaProvider.ParseWallet(wallet, Json("""{"success":true,"data":{"quota":-500000}}"""), usdStatus) is { Remaining: -1m, Used: null });
Throws("wallet missing balance is not zero", () => HttpQuotaProvider.ParseWallet(wallet, Json("""{"success":true,"data":{"used_quota":1}}"""), usdStatus));
Throws("wallet rejects zero divisor", () => HttpQuotaProvider.ParseWallet(wallet, walletAccount, Json("""{"data":{"quota_display_type":"USD","quota_per_unit":0}}""")));
Throws("wallet CNY requires a site exchange rate", () => HttpQuotaProvider.ParseWallet(wallet, walletAccount, Json("""{"data":{"quota_display_type":"CNY","quota_per_unit":500000}}""")));
Throws("wallet tokens not mislabeled as dollars", () => HttpQuotaProvider.ParseWallet(wallet, walletAccount, Json("""{"data":{"quota_display_type":"TOKENS","quota_per_unit":500000}}""")));
Throws("wallet rejects account business errors", () => HttpQuotaProvider.ParseWallet(wallet, Json("""{"success":false,"message":"test-only-wallet-token","data":{"quota":1}}"""), usdStatus));
Throws("wallet rejects unsuccessful status", () => HttpQuotaProvider.ParseWallet(wallet, walletAccount, Json("""{"success":false,"data":{"quota_display_type":"USD","quota_per_unit":500000}}""")));
foreach (var httpStatus in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
{
    using var provider = new HttpQuotaProvider(new FakeHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
        ? new(HttpStatusCode.OK) { Content = new StringContent(usdStatus.GetRawText()) }
        : new(httpStatus) { Content = new StringContent("test-only-wallet-token") }));
    try { await provider.FetchAsync(wallet, "test-only-wallet-token", default); throw new Exception("Wallet accepted an unauthorized account"); }
    catch (ProviderException error) { Check("wallet " + (int)httpStatus + " explains account credential without exposing it", error.Message.Contains("账户访问令牌") && !error.Message.Contains("test-only-wallet-token")); }
}
using (var provider = new HttpQuotaProvider(new FakeHandler(_ => throw new Exception("Unconnected wallet accessed network"))))
{
    var missingWallet = await provider.FetchAsync(wallet, null, default);
    Check("wallet waits for its own credential", missingWallet.Status == SnapshotStatus.Setup && !missingWallet.HasValue && missingWallet.Message!.Contains("账户访问令牌"));
    var missingCard = new CardViewModel(wallet, missingWallet, []);
    Check("unconnected wallet does not invent displayed balance", missingCard.PrimaryValue == "—" && missingCard.PrimaryLabel == "钱包余额");
}
var relay = HttpQuotaProvider.ParseRelay(hone, Json("""{"hard_limit_usd":100}"""), Json("""{"total_usage":1234}"""), "USD");
Check("relay cents and decimal precision", relay.Remaining == 87.66m && relay.Used == 12.34m);
Check("relay cumulative label", relay.UsageLabel == "累计消费" && relay.UsedMonth is null);
var unlimited = HttpQuotaProvider.ParseRelay(hone, Json("""{"hard_limit_usd":100000000}"""), Json("""{"total_usage":100}"""), "USD");
Check("unlimited is not fake cash", unlimited.Remaining is null && unlimited.Total is null && unlimited.Used == 1);
Check("unlimited billing is explicitly key scoped", unlimited.Scope == "当前 API key" && unlimited.UsageLabel == "key 累计消费");
var groupWalletSnapshot = new QuotaSnapshot { ProviderId = wallet.Id, Kind = QuotaKind.Balance, Currency = "USD",
    Remaining = 10.45m, Used = 299.55m, UsageLabel = "账户累计消费", Scope = "账户钱包", FetchedAt = DateTimeOffset.UtcNow };
var groupWalletCard = new CardViewModel(wallet, groupWalletSnapshot, []);
var groupApiCard = new CardViewModel(hone, unlimited with { Used = 21.13m }, []);
var groupCodexConfig = ProviderConfig.Create(ProviderType.Codex);
var groupCodexCard = new CardViewModel(groupCodexConfig, new QuotaSnapshot { ProviderId = groupCodexConfig.Id,
    Kind = QuotaKind.RateWindow, Windows = [new("每周", 6, 10080, DateTimeOffset.UtcNow.AddDays(1))] }, []);
var providerGroups = ProviderCardViewModel.Group([groupApiCard, groupCodexCard, groupWalletCard]);
Check("Hone wallet and API share one card while Codex stays separate", providerGroups.Count == 2 && providerGroups[0].Name == "Hone" && providerGroups[1].Name == "Codex");
var honeGroup = providerGroups[0];
Check("wallet leads its group even when configured after API", ReferenceEquals(honeGroup.Services[0].Card, groupWalletCard) && ReferenceEquals(honeGroup.Services[1].Card, groupApiCard));
Check("group preserves wallet balance, account spend and key spend separately", honeGroup.Services[0].Card.PrimaryValue == "$10.45"
    && honeGroup.Services[0].Card.SecondaryText == "账户累计消费  $299.55" && honeGroup.Services[1].Card.PrimaryValue == "$21.13"
    && honeGroup.Services[1].Card.PrimaryLabel == "key 累计消费");
Check("group removes redundant connect-wallet hint without mutating source", honeGroup.Services[1].Message == "当前 API key 未设限。"
    && groupApiCard.Message!.Contains("连接") && honeGroup.StatusText == "已同步");
Check("secondary service is compact and keeps its own timestamp", honeGroup.Services[1].ValueFontSize < honeGroup.Services[0].ValueFontSize
    && honeGroup.Services[1].Card.Updated == groupApiCard.Updated);
var normalizedWallet = new CardViewModel(wallet with { BaseUrl = "https://HONE.vvvv.ee:443/v1/" }, groupWalletSnapshot, []);
Check("grouping normalizes host case, default port and fetcher v1 suffix", ProviderCardViewModel.Group([groupApiCard, normalizedWallet]).Count == 1);
Check("different provider sites remain separate", ProviderCardViewModel.Group([groupApiCard,
    new(wallet with { BaseUrl = "https://other.example.test" }, groupWalletSnapshot, [])]).Count == 2);
Check("different tenant paths remain separate", ProviderCardViewModel.Group([
    new(hone with { BaseUrl = "https://example.test/team-a" }, unlimited, []),
    new(wallet with { BaseUrl = "https://example.test/team-b" }, groupWalletSnapshot, [])]).Count == 2);
Check("different ports remain separate", ProviderCardViewModel.Group([groupApiCard,
    new(wallet with { BaseUrl = "https://hone.vvvv.ee:8443" }, groupWalletSnapshot, [])]).Count == 2);
Check("grouping uses provider identity rather than display name", ProviderCardViewModel.Group([
    new(hone with { Name = "工作 key" }, unlimited, []), new(wallet with { Name = "余额账户" }, groupWalletSnapshot, [])]).Single().Name == "Hone");
var disabledWalletGroup = ProviderCardViewModel.Group([groupApiCard, new(wallet with { Enabled = false }, groupWalletSnapshot, [])]).Single();
Check("disabled source is excluded and lone service retains its name", disabledWalletGroup.Name == "Hone API" && disabledWalletGroup.Services.Count == 1
    && disabledWalletGroup.Services[0].Message == groupApiCard.Message);
Check("empty provider list creates no cards", ProviderCardViewModel.Group([]).Count == 0);
var staleWallet = new CardViewModel(wallet, QuotaRules.Failure(wallet, groupWalletSnapshot, "钱包暂时离线", DateTimeOffset.UtcNow), []);
var staleGroup = ProviderCardViewModel.Group([groupApiCard, staleWallet]).Single();
Check("partial stale status does not hide wallet failure or old balance", staleGroup.StatusText == "部分数据过期"
    && staleGroup.Services[0].Message == "钱包暂时离线" && staleGroup.Services[0].Card.PrimaryValue == "$10.45"
    && staleGroup.Services[0].HeaderVisibility == System.Windows.Visibility.Visible);
var failedApi = new CardViewModel(hone, QuotaRules.Failure(hone, null, "API key 无效", DateTimeOffset.UtcNow), []);
var failedGroup = ProviderCardViewModel.Group([groupWalletCard, failedApi]).Single();
Check("healthy wallet does not mask API failure", failedGroup.StatusText == "部分连接失败" && failedGroup.Services[1].Message == "API key 无效");
var setupWallet = new CardViewModel(wallet, new QuotaSnapshot { ProviderId = wallet.Id, Status = SnapshotStatus.Setup }, []);
var setupGroup = ProviderCardViewModel.Group([groupApiCard, setupWallet]).Single();
Check("missing wallet never promotes key spend to wallet balance", setupGroup.StatusText == "部分待连接"
    && setupGroup.Services[0].Card.PrimaryValue == "—" && setupGroup.Services[0].Card.PrimaryLabel == "钱包余额" && setupGroup.Services[0].Card.NeedsSetup);
var lowGroup = ProviderCardViewModel.Group([new(wallet, groupWalletSnapshot with { Remaining = 1 }, []), groupApiCard]).Single();
Check("group retains a service low-quota warning", lowGroup.StatusText == "额度偏低");
var duplicateApi = new CardViewModel(hone with { Id = "other-key" }, unlimited with { ProviderId = "other-key", Currency = "CNY" }, []);
var multipleKeys = ProviderCardViewModel.Group([groupWalletCard, groupApiCard, duplicateApi]).Single();
Check("multiple API keys remain identifiable without currency totals", multipleKeys.Services.Count == 3
    && multipleKeys.Services[1].Name != multipleKeys.Services[2].Name && multipleKeys.Services[2].Card.PrimaryValue == "¥1.00");
var manualOne = ProviderConfig.Create(ProviderType.Manual);
var manualTwo = ProviderConfig.Create(ProviderType.Manual);
Check("manual accounts with the same label are not assumed to share a supplier", ProviderCardViewModel.Group([new(manualOne, null, []), new(manualTwo, null, [])]).Count == 2);
Check("invalid or empty sites do not merge unrelated sources", ProviderCardViewModel.Group([
    new(hone with { BaseUrl = "" }, null, []), new(wallet with { BaseUrl = "" }, null, [])]).Count == 2);
var groupDeepSeek = ProviderConfig.Create(ProviderType.DeepSeek);
Check("other built-in provider accounts also group", ProviderCardViewModel.Group([
    new(groupDeepSeek, null, []), new(groupDeepSeek with { Id = "second-deepseek" }, null, [])]).Single().Services.Count == 2);
var groupCustom = ProviderConfig.Create(ProviderType.Custom);
Check("custom sources group by their actual HTTP endpoint", ProviderCardViewModel.Group([
    new(groupCustom with { Http = new() { Url = "https://example.test/balance" } }, null, []),
    new(groupCustom with { Id = "second-custom", Http = new() { Url = "https://example.test/balance" } }, null, [])]).Single().Name == "example.test");
Throws("HTTP 200 business errors", () => HttpQuotaProvider.ParseRelay(hone, Json("""{"error":{"message":"secret"}}"""), Json("{}"), "USD"));
var ds = ProviderConfig.Create(ProviderType.DeepSeek);
var dsResult = HttpQuotaProvider.Parse(ds, Json("""{"is_available":true,"balance_infos":[{"currency":"USD","total_balance":"2.30"},{"currency":"CNY","total_balance":"12.3401"}]}"""));
Check("DeepSeek selects matching currency", dsResult.Currency == "CNY" && dsResult.Remaining == 12.3401m);
Throws("missing currency not silently relabeled", () => HttpQuotaProvider.Parse(ds, Json("""{"balance_infos":[{"currency":"USD","total_balance":"2"}]}""")));
var kimi = ProviderConfig.Create(ProviderType.Kimi);
var debt = HttpQuotaProvider.Parse(kimi, Json("""{"data":{"available_balance":-1.25,"cash_balance":-2}}"""));
Check("negative balance is preserved", debt.Remaining == -1.25m);
var international = HttpQuotaProvider.Parse(kimi with { BaseUrl = "https://api.moonshot.ai" }, Json("""{"data":{"available_balance":1}}"""));
Check("Kimi international currency", international.Currency == "USD");
var silicon = HttpQuotaProvider.Parse(ProviderConfig.Create(ProviderType.SiliconFlow), Json("""{"status":true,"data":{"totalBalance":"88.88","balance":"0.88"}}"""));
Check("SiliconFlow uses total balance", silicon.Remaining == 88.88m);
var router = HttpQuotaProvider.Parse(ProviderConfig.Create(ProviderType.OpenRouter), Json("""{"data":{"limit":null,"limit_remaining":null,"usage":4.56,"usage_monthly":1.2,"usage_daily":0.2}}"""));
Check("OpenRouter unlimited remains unknown", router.Remaining is null && router.UsedMonth == 1.2m && router.Scope == "当前 API key");
Throws("missing money is not zero", () => HttpQuotaProvider.Parse(ProviderConfig.Create(ProviderType.SiliconFlow), Json("""{"data":{}}""")));
Check("nested array field", JsonFields.Number(Json("""{"data":[{"balance":"3.45"}]}"""), "$.data[0].balance") == 3.45m);
Throws("reject unsupported paths even if root missing", () => JsonFields.Get(Json("{}"), "$.items[*].balance"));
Throws("reject insecure HTTP", () => HttpQuotaProvider.ValidateUrl("http://example.test"));
Throws("reject URL credentials", () => HttpQuotaProvider.ValidateUrl("https://user:password@example.test"));
var codex = CodexQuotaProvider.Parse("codex", Json("""{"rateLimits":{"primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"planType":"pro","primary":{"usedPercent":6,"windowDurationMins":10080,"resetsAt":1790929328},"secondary":null},"fast":{"limitName":"Fast","primary":{"usedPercent":105,"windowDurationMins":300,"resetsAt":1790929328}}},"rateLimitResetCredits":{"availableCount":1}}"""));
Check("Codex multi-bucket precedence, no duplicate", codex.Windows.Count == 2 && codex.Windows[0].RemainingPercent == 94);
Check("Codex window durations are dynamic", codex.Windows[0].Name == "每周" && codex.Windows[1].Name == "Fast · 5 小时");
Check("Codex over-limit safely clamped", codex.Windows[1].RemainingPercent == 0);
Check("Codex Unix seconds, not milliseconds", codex.Windows[0].ResetsAt?.Year == 2026);
Check("Codex quota not money", codex.Currency is null && codex.Remaining is null && codex.AvailableResets == 1);
Throws("Codex empty windows is not a healthy zero", () => CodexQuotaProvider.Parse("codex", Json("""{"rateLimits":{"primary":null,"secondary":null}}""")));
var failed = QuotaRules.Failure(hone, relay, "offline", DateTimeOffset.UtcNow.AddMinutes(5));
Check("failure retains value and original fetched time", failed.Status == SnapshotStatus.Stale && failed.Remaining == relay.Remaining && failed.FetchedAt == relay.FetchedAt);
Check("first failure does not invent balance", !QuotaRules.Failure(hone, null, "offline", DateTimeOffset.UtcNow).HasValue);
Check("stale values do not alert", !QuotaRules.IsLow(hone, failed with { Remaining = 0 }));
Check("absolute money threshold", QuotaRules.IsLow(hone, relay with { Remaining = 4 }));
Check("exponential backoff capped", QuotaRules.RetryDelay(20, TimeSpan.FromMinutes(5)).TotalHours == 1);
Check("Retry-After respected", QuotaRules.RetryDelay(1, TimeSpan.FromMinutes(5), TimeSpan.FromHours(2)).TotalHours == 2);
var date = DateTimeOffset.UtcNow.AddDays(-2);
QuotaSnapshot Point(int hour, decimal balance) => new() { ProviderId = "history", Kind = QuotaKind.Balance, Remaining = balance, Currency = "USD", FetchedAt = date.AddHours(hour) };
Check("refill excluded from burn estimate", QuotaRules.EstimateDays([Point(0,100),Point(12,90),Point(24,200),Point(36,190)]) == 9.5m);
Check("insufficient history stays unknown", QuotaRules.EstimateDays([Point(0,100),Point(1,90)]) is null);
Check("zero burn stays unknown", QuotaRules.EstimateDays([Point(0,100),Point(12,100),Point(24,100)]) is null);
var custom = ProviderConfig.Create(ProviderType.Custom);
custom.Http = new() { RemainingPath = "", UsedPath = "$.data.cost", Kind = QuotaKind.Spend, Multiplier = 0.01m }; custom.Budget = 20;
var customResult = HttpQuotaProvider.Parse(custom, Json("""{"data":{"cost":"350"}}"""));
Check("spend budget estimate marked", customResult.Remaining == 16.5m && customResult.IsEstimate);
using (var provider = new HttpQuotaProvider(new FakeHandler(request => new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(25)) } })))
{
    try { await provider.FetchAsync(ds, "test-only-key", default); throw new Exception("missing error"); }
    catch (ProviderException error) { Check("429 retry hint survives transport", error.RetryAfter?.TotalMinutes == 25 && !error.Message.Contains("test-only-key")); }
}
var authSeen = false;
using (var provider = new HttpQuotaProvider(new FakeHandler(request => { authSeen = request.Headers.Authorization?.Parameter == "test-only-key"; return new(HttpStatusCode.OK) { Content = new StringContent("""{"balance_infos":[{"currency":"CNY","total_balance":"9.50"}]}""") }; })))
{
    var value = await provider.FetchAsync(ds, "test-only-key", default);
    Check("authenticated GET uses backend header", authSeen && value.Remaining == 9.5m);
    var setup = await provider.FetchAsync(ds, null, default);
    Check("missing key becomes setup state", setup.Status == SnapshotStatus.Setup);
}
var directory = Path.Combine(Path.GetTempPath(), "QuotaPeek-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var store = new SettingsStore(directory);
var settings = new AppSettings(); store.Save(settings);
Check("settings round trip", store.Load().Providers.Count == 2);
Check("old settings keep floating mode", !store.Load().TaskbarDocked);
store.Save(settings with { TaskbarDocked = true, LeftPixels = 240, TopPixels = 620, StartExpanded = false });
var dockSettings = store.Load();
Check("taskbar preference preserves independent floating position", dockSettings.TaskbarDocked && dockSettings.LeftPixels == 240 && dockSettings.TopPixels == 620 && !dockSettings.StartExpanded);
var taskbar = new PixelRect(0, 1504, 2560, 1600);
var dock = TaskbarLayout.FindSpace(taskbar, 2, [new(526, 1504, 616, 1600), new(620, 1520, 1768, 1584), new(1884, 1504, 2560, 1600)]);
Check("200 percent taskbar capsule fits before Start", dock is { Left: 16, Top: 1520, Width: 464, Height: 64 } && dock.Value.Right < 526);
var compact = TaskbarLayout.FindSpace(taskbar, 2, [new(360, 1504, 1900, 1600)]);
Check("narrow taskbar gap shrinks capsule without covering buttons", compact is { Left: 16, Width: 328 } && compact.Value.Right == 344);
Check("widgets and centered icons with no room use floating fallback", TaskbarLayout.FindSpace(taskbar, 2, [new(0, 1504, 300, 1600), new(526, 1504, 2560, 1600)]) is null);
var offsetDock = TaskbarLayout.FindSpace(new(-1920, 1040, 0, 1080), 1, [new(-1560, 1040, -600, 1080)]);
Check("negative monitor coordinates keep placement inside taskbar", offsetDock is { Left: -1912, Top: 1044, Width: 232, Height: 32 });
Check("vertical or too thin taskbar falls back", TaskbarLayout.FindSpace(new(0, 0, 48, 1080), 1, []) is null && TaskbarLayout.FindSpace(new(0, 1070, 1920, 1080), 1, []) is null);
Check("config contains no key", !File.ReadAllText(store.FilePath).Contains("test-only-key"));
store.Save(new AppSettings { Providers = [hone, wallet] });
Check("wallet configuration and key credential identities remain separate", store.Load().Providers[1].Type == ProviderType.HoneWallet && wallet.Id != hone.Id && wallet.SecretEnvironment != hone.SecretEnvironment);
File.WriteAllText(store.FilePath, "{broken");
Check("corrupt settings backed up", store.Load().Providers.Count == 2 && Directory.GetFiles(directory, "*.invalid-*").Length == 1);
var history = new HistoryStore(directory); history.Save(relay);
Check("SQLite amount precision round trip", history.Read(hone.Id).Single().Remaining == 87.66m);
Check("threshold first crossing", history.CrossedLowThreshold(hone.Id, true));
Check("threshold does not repeat after restart", !new HistoryStore(directory).CrossedLowThreshold(hone.Id, true));
Check("threshold rearms above boundary", !history.CrossedLowThreshold(hone.Id, false) && history.CrossedLowThreshold(hone.Id, true));
var credentials = new CredentialStore("checks-" + Guid.NewGuid().ToString("N"));
try { credentials.Write("fixture", "test-only-key"); Check("Windows credential round trip", credentials.Read("fixture") == "test-only-key"); }
finally { credentials.Delete("fixture"); }
Check("Windows credential deletion", credentials.Read("fixture") is null);
if (args.Contains("--live-codex"))
{
    var actual = await new CodexQuotaProvider().FetchAsync(ProviderConfig.Create(ProviderType.Codex), null, default);
    Check("LIVE Codex app-server quota", actual.Windows.Count > 0 && actual.Status == SnapshotStatus.Ok);
    foreach (var window in actual.Windows) Console.WriteLine($"  {window.Name}: {window.RemainingPercent}% remaining; resets {window.ResetsAt:O}");
}
Console.WriteLine($"{passed} checks passed.");
Console.WriteLine("Isolated data: " + directory);

sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
}
