using System.Diagnostics;
using System.Text.Json;

namespace QuotaPeek.Core;

public sealed class CodexQuotaProvider : IQuotaProvider
{
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathFullyQualified(configured) && File.Exists(configured) && Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return configured;
            return null;
        }
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Prefer the installed desktop app, then a native CLI on PATH, npm, VS Code.
        var appRoot = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(appRoot))
        {
            var app = Directory.EnumerateDirectories(appRoot).Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (app is not null) return app;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var file = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(file)) return Path.GetFullPath(file); }
            catch (ArgumentException) { }
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe");
        if (File.Exists(npm)) return npm;
        var extensions = Path.Combine(user, ".vscode", "extensions");
        return Directory.Exists(extensions) ? Directory.EnumerateDirectories(extensions, "openai.chatgpt-*")
            .Select(d => Path.Combine(d, "bin", "windows-x86_64", "codex.exe"))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
    }

    public async Task<QuotaSnapshot> FetchAsync(ProviderConfig config, string? secret, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(config.CodexExecutable) ?? throw new ProviderException("未找到 Codex。请安装 Codex 并登录，或在设置中指定 codex.exe。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new ProviderException("无法启动 Codex 额度读取进程。");
        // Drain stderr without logging it: third-party diagnostics may contain account data.
        var drain = DrainAsync(process.StandardError, timeout.Token);
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "quotapeek", version = "0.1.0" } } });
            using (await Receive(1)) { }
            await Send(new { method = "initialized", @params = new { } });
            await Send(new { id = 2, method = "account/rateLimits/read" });
            using var response = await Receive(2);
            return Parse(config.Id, response.RootElement.GetProperty("result"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new ProviderException("Codex 额度读取超时，请检查 Codex 登录和网络。"); }
        catch (JsonException) { throw new ProviderException("Codex 返回格式不兼容，请更新 Codex。"); }
        finally
        {
            process.StandardInput.Close();
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(exitTimeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            timeout.Cancel();
            try { await drain; } catch (OperationCanceledException) { }
        }

        async Task Send(object message)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
        }
        async Task<JsonDocument> Receive(int id)
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new ProviderException("Codex 读取进程提前退出，请检查本机 Codex 配置。");
                if (line.Length > 2_000_000) throw new ProviderException("Codex 返回内容过大。");
                var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (root.TryGetProperty("id", out var identifier) && identifier.ValueKind == JsonValueKind.Number && identifier.GetInt32() == id && !root.TryGetProperty("method", out _))
                {
                    if (root.TryGetProperty("error", out _))
                    {
                        message.Dispose();
                        throw new ProviderException("无法读取 Codex 额度。请在 Codex 中用 ChatGPT 账户登录；API key 登录不提供订阅额度。");
                    }
                    return message;
                }
                message.Dispose();
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) { }
    }

    public static QuotaSnapshot Parse(string providerId, JsonElement result)
    {
        var buckets = new List<(string Id, JsonElement Data)>();
        if (JsonFields.Get(result, "rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } multiple)
            buckets.AddRange(multiple.EnumerateObject().Select(p => (p.Name, p.Value)));
        if (buckets.Count == 0 && JsonFields.Get(result, "rateLimits") is { ValueKind: JsonValueKind.Object } single)
            buckets.Add((JsonFields.Text(single, "limitId") ?? "codex", single));
        var windows = new List<RateWindow>();
        string? plan = null;
        foreach (var (id, data) in buckets.OrderBy(b => b.Id != "codex"))
        {
            plan ??= JsonFields.Text(data, "planType");
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (JsonFields.Get(data, key) is not { ValueKind: JsonValueKind.Object } window) continue;
                var used = JsonFields.Number(window, "usedPercent", true)!.Value;
                var mins = JsonFields.Number(window, "windowDurationMins");
                var reset = JsonFields.Number(window, "resetsAt");
                if (used < 0 || mins < 0) throw new ProviderException("Codex 额度字段异常。");
                var name = mins switch
                {
                    10080 => "每周", 1440 => "每日", 300 => "5 小时",
                    > 0 when mins % 60 == 0 => $"{mins / 60:0} 小时",
                    > 0 => $"{mins:0} 分钟", _ => key == "primary" ? "主要额度" : "次要额度"
                };
                if (id != "codex") name = (JsonFields.Text(data, "limitName") ?? id) + " · " + name;
                windows.Add(new(name, used, mins is null ? null : (int)mins.Value,
                    reset is null ? null : DateTimeOffset.FromUnixTimeSeconds((long)reset.Value)));
            }
        }
        if (windows.Count == 0) throw new ProviderException("当前 Codex 账户未返回额度窗口。");
        var resets = JsonFields.Number(result, "rateLimitResetCredits.availableCount");
        return new() { ProviderId = providerId, Kind = QuotaKind.RateWindow, Scope = "ChatGPT 订阅", Windows = windows,
            Plan = plan, AvailableResets = resets is null ? null : (int)resets, FetchedAt = DateTimeOffset.UtcNow };
    }
}
