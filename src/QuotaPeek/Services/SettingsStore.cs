using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuotaPeek.Services;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string? Warning { get; private set; }
    public SettingsStore(string directoryPath) { DirectoryPath = directoryPath; Directory.CreateDirectory(directoryPath); }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? throw new JsonException();
            if (settings.Version != 1 || settings.Providers is null || settings.Providers.Any(p => string.IsNullOrWhiteSpace(p.Id)) || settings.Providers.Select(p => p.Id).Distinct().Count() != settings.Providers.Count)
                throw new JsonException();
            foreach (var provider in settings.Providers) provider.RefreshMinutes = Math.Clamp(provider.RefreshMinutes, 1, 1440);
            return settings;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            var backup = FilePath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            File.Copy(FilePath, backup, false);
            Warning = "设置文件无法读取，原文件已备份；当前使用默认设置。";
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        File.Move(temporary, FilePath, true);
    }
}
