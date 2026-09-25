using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QuotaPeek.Core;

public sealed class HttpQuotaProvider : IQuotaProvider, IDisposable
{
    private readonly HttpClient client;
    public HttpQuotaProvider(HttpMessageHandler? handler = null)
    {
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.Timeout = TimeSpan.FromSeconds(25);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuotaPeek/0.1");
    }

    public async Task<QuotaSnapshot> FetchAsync(ProviderConfig config, string? secret, CancellationToken cancellationToken)
    {
        if (config.Type == ProviderType.Manual)
        {
            if (config.ManualRemaining is null && config.ManualUsed is null) throw new ProviderException("请在设置中录入余额或已用金额。");
            return new() { ProviderId = config.Id, Kind = config.ManualUsed.HasValue ? QuotaKind.Spend : QuotaKind.Balance,
                Remaining = config.ManualRemaining ?? (config.Budget - config.ManualUsed), Used = config.ManualUsed,
                Total = config.Budget, Currency = config.Currency, FetchedAt = config.ManualUpdatedAt,
                Scope = "手动录入", UsageLabel = "录入消费", IsEstimate = config.ManualRemaining is null && config.Budget.HasValue };
        }
        if (string.IsNullOrWhiteSpace(secret))
            return new() { ProviderId = config.Id, Currency = config.Currency, Status = SnapshotStatus.Setup,
                Scope = config.Type == ProviderType.HoneWallet ? "账户钱包" : "账户",
                Message = config.Type == ProviderType.HoneWallet ? "在设置中添加 Hone 账户访问令牌以读取钱包余额。" : "在设置中添加 API key" };

        try
        {
            if (config.Type == ProviderType.HoneWallet)
            {
                using var status = await GetAsync(Endpoint(config.BaseUrl, "/api/status"), null, null, cancellationToken);
                using var account = await GetAsync(Endpoint(config.BaseUrl, "/api/user/self"), secret, null, cancellationToken, accountCredential: true);
                return ParseWallet(config, account.RootElement, status.RootElement);
            }
            if (config.Type is ProviderType.Hone or ProviderType.Relay)
            {
                string? currency = config.Currency;
                if (config.Type == ProviderType.Hone)
                {
                    using var status = await GetAsync(Endpoint(config.BaseUrl, "/api/status"), null, null, cancellationToken);
                    var display = JsonFields.Text(status.RootElement, "data.quota_display_type");
                    if (display is not ("USD" or "CNY")) throw new ProviderException("Hone 的计费展示单位发生变化，请核对站点设置。");
                    currency = display;
                }
                var subscriptionTask = GetAsync(Endpoint(config.BaseUrl, "/v1/dashboard/billing/subscription"), secret, null, cancellationToken);
                var usageTask = GetAsync(Endpoint(config.BaseUrl, "/v1/dashboard/billing/usage"), secret, null, cancellationToken);
                // Await both tasks and dispose every successful response even if its peer fails.
                try
                {
                    await Task.WhenAll(subscriptionTask, usageTask);
                    return ParseRelay(config, subscriptionTask.Result.RootElement, usageTask.Result.RootElement, currency!);
                }
                finally
                {
                    if (subscriptionTask.IsCompletedSuccessfully) subscriptionTask.Result.Dispose();
                    if (usageTask.IsCompletedSuccessfully) usageTask.Result.Dispose();
                }
            }
            var url = config.Type switch
            {
                ProviderType.DeepSeek => Endpoint(config.BaseUrl, "/user/balance"),
                ProviderType.Kimi => Endpoint(config.BaseUrl, "/v1/users/me/balance"),
                ProviderType.SiliconFlow => Endpoint(config.BaseUrl, "/v1/user/info"),
                ProviderType.OpenRouter => Endpoint(config.BaseUrl, "/api/v1/key"),
                ProviderType.Custom => config.Http.Url,
                _ => throw new ProviderException("不支持的数据源类型。")
            };
            using var document = await GetAsync(url, secret, config.Type == ProviderType.Custom ? config.Http.Headers : null, cancellationToken);
            return Parse(config, document.RootElement);
        }
        catch (JsonException) { throw new ProviderException("接口未返回有效 JSON，请核对地址和平台状态。"); }
        catch (HttpRequestException) { throw new ProviderException("网络连接失败，请检查网络或代理。"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProviderException("请求超时，将自动重试。"); }
    }

    public static string Endpoint(string baseUrl, string path)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
        return root + path;
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ProviderException("请使用不含用户名、密码或片段的 HTTPS 地址。");
        return uri;
    }

    private async Task<JsonDocument> GetAsync(string url, string? secret, Dictionary<string, string>? headers, CancellationToken token, bool accountCredential = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateUrl(url));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (headers is not null)
        {
            foreach (var (name, template) in headers)
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                    throw new ProviderException("自定义请求不支持 Host 或 Cookie 请求头。");
                var value = template.Replace("{key}", secret ?? "");
                if (value.Contains('\r') || value.Contains('\n') || !request.Headers.TryAddWithoutValidation(name, value))
                    throw new ProviderException("请求头模板格式无效。");
            }
        }
        else if (secret is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
            throw new ProviderException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => accountCredential ? "账户访问令牌无效或已过期；请使用 Hone「安全与访问」中的访问令牌，普通 API key 无法读取钱包。" : "API key 无效或已过期，请在设置中更新。",
                HttpStatusCode.Forbidden => accountCredential ? "账户访问令牌没有钱包查询权限，请检查 Hone 控制台。" : "当前 key 没有额度查询权限。",
                HttpStatusCode.NotFound => "站点没有提供该计费接口，请核对 API 地址。",
                HttpStatusCode.TooManyRequests => "请求被限流，已延后自动重试。",
                >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "接口发生重定向，请更新为最终 HTTPS 地址。",
                _ => $"平台暂时不可用（HTTP {(int)response.StatusCode}）。"
            }, retry);
        }
        // A balance response should be small. Bound the streamed body before JSON parsing.
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > 1_048_576) throw new ProviderException("计费接口返回内容过大。");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    public static QuotaSnapshot ParseRelay(ProviderConfig config, JsonElement subscription, JsonElement usage, string currency)
    {
        JsonFields.RequireSuccess(subscription);
        JsonFields.RequireSuccess(usage);
        var total = JsonFields.Number(subscription, "hard_limit_usd", true)!.Value;
        var used = JsonFields.Number(usage, "total_usage", true)!.Value * config.UsageMultiplier;
        if (total < 0 || used < 0) throw new ProviderException("计费接口金额异常，请核对单位。");
        var unlimited = total >= 100_000_000m;
        return new() { ProviderId = config.Id, Kind = QuotaKind.Limit, Currency = currency,
            Total = unlimited ? null : total, Used = used, Remaining = unlimited ? null : total - used,
            Scope = unlimited ? "当前 API key" : "站点账单", UsageLabel = unlimited ? "key 累计消费" : "累计消费", FetchedAt = DateTimeOffset.UtcNow,
            Message = unlimited ? config.Type == ProviderType.Hone
                ? "当前 API key 未设限；在设置中连接「Hone 钱包」可显示账户余额。"
                : "当前 API key 未设限；此接口未返回账户钱包余额。"
                : "站点可按账户或 key 统计，以控制台为准。" };
    }

    public static QuotaSnapshot ParseWallet(ProviderConfig config, JsonElement account, JsonElement status)
    {
        JsonFields.RequireSuccess(status);
        if (JsonFields.Bool(account, "success") != true || JsonFields.Get(account, "error") is not null)
            throw new ProviderException("钱包查询失败，请检查账户访问令牌和 Hone 控制台。");
        var currency = JsonFields.Text(status, "data.quota_display_type");
        if (currency is not ("USD" or "CNY")) throw new ProviderException("钱包计费展示单位不受支持，请核对 Hone 站点设置。");
        var quotaPerUnit = JsonFields.Number(status, "data.quota_per_unit", true)!.Value;
        if (quotaPerUnit <= 0) throw new ProviderException("钱包计费单位无效，无法换算余额。");
        var exchangeRate = currency == "CNY" ? JsonFields.Number(status, "data.usd_exchange_rate", true)!.Value : 1m;
        if (exchangeRate <= 0) throw new ProviderException("钱包汇率无效，无法换算余额。");
        var remaining = JsonFields.Number(account, "data.quota", true)!.Value;
        var used = JsonFields.Number(account, "data.used_quota");
        if (used < 0) throw new ProviderException("钱包累计消费异常，请核对 Hone 控制台。");
        try
        {
            return new() { ProviderId = config.Id, Kind = QuotaKind.Balance, Currency = currency,
                Remaining = remaining / quotaPerUnit * exchangeRate, Used = used / quotaPerUnit * exchangeRate,
                Scope = "账户钱包", UsageLabel = "账户累计消费", FetchedAt = DateTimeOffset.UtcNow };
        }
        catch (OverflowException) { throw new ProviderException("钱包金额超出可显示范围，请核对 Hone 站点单位。"); }
    }

    public static QuotaSnapshot Parse(ProviderConfig config, JsonElement root)
    {
        JsonFields.RequireSuccess(root);
        var snapshot = new QuotaSnapshot { ProviderId = config.Id, Currency = config.Currency, FetchedAt = DateTimeOffset.UtcNow };
        switch (config.Type)
        {
            case ProviderType.DeepSeek:
                var balances = JsonFields.Get(root, "balance_infos");
                if (balances is not { ValueKind: JsonValueKind.Array }) throw new ProviderException("平台没有返回余额列表。");
                foreach (var balance in balances.Value.EnumerateArray())
                    if (JsonFields.Text(balance, "currency") == config.Currency)
                        return snapshot with { Remaining = JsonFields.Number(balance, "total_balance", true),
                            Message = JsonFields.Bool(root, "is_available") == false ? "平台提示当前余额不可用。" : null };
                throw new ProviderException("接口没有返回所选币种的余额，请切换币种。");
            case ProviderType.Kimi:
                var expected = new Uri(config.BaseUrl).Host.EndsWith("moonshot.ai", StringComparison.OrdinalIgnoreCase) ? "USD" : "CNY";
                return snapshot with { Currency = expected, Remaining = JsonFields.Number(root, "data.available_balance", true) };
            case ProviderType.SiliconFlow:
                return snapshot with { Remaining = JsonFields.Number(root, "data.totalBalance", true),
                    Message = JsonFields.Text(root, "data.status") is string status && status != "normal" ? "请检查平台账户状态。" : null };
            case ProviderType.OpenRouter:
                return snapshot with { Kind = QuotaKind.Limit, Currency = "USD", Scope = "当前 API key",
                    Total = JsonFields.Number(root, "data.limit"), Remaining = JsonFields.Number(root, "data.limit_remaining"),
                    Used = JsonFields.Number(root, "data.usage", true), UsedToday = JsonFields.Number(root, "data.usage_daily"),
                    UsedMonth = JsonFields.Number(root, "data.usage_monthly"),
                    Message = JsonFields.Number(root, "data.limit") is null ? "此 key 未设限；不能据此推算账户余额。" : null };
            case ProviderType.Custom:
                decimal? Read(string path) => string.IsNullOrWhiteSpace(path) ? null : JsonFields.Number(root, path, true) * config.Http.Multiplier;
                var remaining = Read(config.Http.RemainingPath);
                var used = Read(config.Http.UsedPath);
                var total = Read(config.Http.TotalPath);
                if (remaining is null && used is null && total is null) throw new ProviderException("至少配置一个金额字段路径。");
                var budget = config.Http.Kind == QuotaKind.Spend ? config.Budget : null;
                return snapshot with { Kind = config.Http.Kind, Remaining = remaining ?? ((total ?? budget) - used), Used = used,
                    Total = total ?? budget, Scope = "自定义接口", IsEstimate = remaining is null && budget.HasValue, UsageLabel = "接口已用" };
            default: throw new ProviderException("无法解析此数据源。");
        }
    }

    public void Dispose() => client.Dispose();
}
