using System.ComponentModel;
using Microsoft.Data.Sqlite;

namespace QuotaPeek.Services;

public sealed class QuotaMonitor : IDisposable
{
    private readonly CredentialStore credentials;
    private readonly HistoryStore history;
    private readonly HttpQuotaProvider http = new();
    private readonly CodexQuotaProvider codex = new();
    private readonly SemaphoreSlim slots = new(2);
    private CancellationTokenSource pause = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> busy = [];
    private readonly Dictionary<string, DateTimeOffset> due = [];
    private readonly Dictionary<string, DateTimeOffset> attempted = [];
    private readonly Dictionary<string, DateTimeOffset> rateLimitedUntil = [];
    private readonly Dictionary<string, int> failures = [];
    private int generation;
    public AppSettings Settings { get; private set; }
    public Dictionary<string, QuotaSnapshot> Snapshots { get; } = [];
    public bool Paused { get; private set; }
    public bool IsRefreshing => busy.Count > 0;
    public bool Demo { get; }
    public string? Warning { get; private set; }
    public event Action? Changed;
    public event Action<ProviderConfig, QuotaSnapshot>? LowQuota;

    public QuotaMonitor(AppSettings settings, CredentialStore credentials, HistoryStore history, bool demo)
    {
        Settings = settings;
        this.credentials = credentials;
        this.history = history;
        Demo = demo;
        Restore();
        if (demo) SetDemo();
    }

    public void Apply(AppSettings settings)
    {
        generation++;
        Settings = settings;
        due.Clear(); attempted.Clear(); failures.Clear(); rateLimitedUntil.Clear();
        Snapshots.Clear();
        Restore();
        if (Demo) SetDemo();
        Changed?.Invoke();
    }

    private void Restore()
    {
        foreach (var config in Settings.Providers)
        {
            try
            {
                if (history.Read(config.Id, 1).LastOrDefault() is { HasValue: true } latest)
                    Snapshots[config.Id] = latest with { Status = SnapshotStatus.Stale, Message = "上次记录，等待更新" };
            }
            catch (SqliteException) { Warning = "无法读取本地历史，仍可刷新实时数据。"; }
        }
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        if (paused) pause.Cancel();
        else { pause.Dispose(); pause = new(); due.Clear(); }
        Changed?.Invoke();
    }

    public Task RefreshAsync(bool force = false)
    {
        if (Paused || lifetime.IsCancellationRequested || Demo) return Task.CompletedTask;
        foreach (var config in Settings.Providers)
        {
            if (!Snapshots.TryGetValue(config.Id, out var current) || current.Status != SnapshotStatus.Ok || config.Type == ProviderType.Manual) continue;
            var resetDue = current.Windows.Any(w => w.ResetsAt <= DateTimeOffset.UtcNow && current.FetchedAt < w.ResetsAt);
            if (resetDue) due[config.Id] = DateTimeOffset.UtcNow;
            if (current.FetchedAt < DateTimeOffset.UtcNow.AddMinutes(-Math.Max(2, config.RefreshMinutes * 2)) || resetDue)
                Snapshots[config.Id] = current with { Status = SnapshotStatus.Stale, Message = "数据已过期，等待重新同步。" };
        }
        return Task.WhenAll(Settings.Providers.Where(p => p.Enabled).Select(p => FetchOne(p, force)));
    }

    private async Task FetchOne(ProviderConfig config, bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (busy.Contains(config.Id) || (!force && due.GetValueOrDefault(config.Id) > now)
            || rateLimitedUntil.GetValueOrDefault(config.Id) > now
            || (force && attempted.TryGetValue(config.Id, out var last) && now - last < TimeSpan.FromSeconds(15))) return;
        busy.Add(config.Id);
        Changed?.Invoke();
        var version = generation;
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, pause.Token);
        var acquired = false;
        try
        {
            await slots.WaitAsync(token.Token);
            acquired = true;
            attempted[config.Id] = now;
            var secret = config.NeedsSecret ? credentials.Read(config.Id) : null;
            if (secret is null && !string.IsNullOrWhiteSpace(config.SecretEnvironment)) secret = Environment.GetEnvironmentVariable(config.SecretEnvironment);
            IQuotaProvider provider = config.Type == ProviderType.Codex ? codex : http;
            var snapshot = await provider.FetchAsync(config, secret, token.Token);
            if (version != generation || token.IsCancellationRequested) return;
            failures[config.Id] = 0;
            due[config.Id] = DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(config.RefreshMinutes, 1, 1440));
            if (snapshot.Kind is QuotaKind.Balance or QuotaKind.Limit && snapshot.Remaining.HasValue && config.Type != ProviderType.Manual)
            {
                var points = History(config.Id);
                points.Add(snapshot);
                snapshot = snapshot with { EstimatedDays = QuotaRules.EstimateDays(points) };
            }
            Snapshots[config.Id] = snapshot;
            try
            {
                history.Save(snapshot);
                if (snapshot.Status == SnapshotStatus.Ok && history.CrossedLowThreshold(config.Id, QuotaRules.IsLow(config, snapshot)) && Settings.Notifications)
                    LowQuota?.Invoke(config, snapshot);
            }
            catch (SqliteException) { Warning = "本地历史写入失败，实时数据仍可用。"; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (version != generation) return;
            var message = error switch
            {
                ProviderException => error.Message,
                Win32Exception => "无法读取系统凭据或启动 Codex，请检查本机设置。",
                _ => "本次读取失败，将自动重试。"
            };
            var count = failures.GetValueOrDefault(config.Id) + 1;
            failures[config.Id] = count;
            var retryAfter = (error as ProviderException)?.RetryAfter;
            due[config.Id] = DateTimeOffset.UtcNow + QuotaRules.RetryDelay(count, TimeSpan.FromMinutes(config.RefreshMinutes), retryAfter);
            if (retryAfter.HasValue) rateLimitedUntil[config.Id] = DateTimeOffset.UtcNow + retryAfter.Value;
            Snapshots[config.Id] = QuotaRules.Failure(config, Snapshots.GetValueOrDefault(config.Id), message, DateTimeOffset.UtcNow);
            try { history.Save(Snapshots[config.Id]); }
            catch (SqliteException) { Warning = "本地历史写入失败，实时数据仍可用。"; }
        }
        finally
        {
            if (acquired) slots.Release();
            busy.Remove(config.Id);
            Changed?.Invoke();
        }
    }

    public List<QuotaSnapshot> History(string id)
    {
        try { return history.Read(id); }
        catch (SqliteException) { Warning = "本地历史暂不可用。"; return []; }
    }

    private void SetDemo()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var config in Settings.Providers)
            Snapshots[config.Id] = config.Type == ProviderType.Codex
                ? new() { ProviderId = config.Id, Kind = QuotaKind.RateWindow, Scope = "演示数据", Plan = "Pro",
                    FetchedAt = now, Windows = [new("5 小时", 28, 300, now.AddHours(2.3)), new("每周", 46, 10080, now.AddDays(3.7))] }
                : new() { ProviderId = config.Id, Kind = config.Type == ProviderType.HoneWallet ? QuotaKind.Balance : QuotaKind.Limit,
                    Currency = config.Currency, Remaining = 42.86m, Total = config.Type == ProviderType.HoneWallet ? null : 100,
                    Used = 57.14m, UsageLabel = config.Type == ProviderType.HoneWallet ? "账户累计消费" : "累计消费",
                    Scope = "演示数据", FetchedAt = now, EstimatedDays = 18.4m };
    }

    public void Dispose() { lifetime.Cancel(); pause.Cancel(); http.Dispose(); }
}
