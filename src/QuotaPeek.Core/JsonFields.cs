using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuotaPeek.Core;

public static partial class JsonFields
{
    [GeneratedRegex(@"(?:\.?([A-Za-z_][A-Za-z0-9_-]*)|\[(\d+)\])")]
    private static partial Regex PathParts();

    public static JsonElement? Get(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var clean = path.StartsWith('$') ? path[1..] : path;
        if (clean.Length == 0) return root;
        var parts = PathParts().Matches(clean);
        if (string.Concat(parts.Select(m => m.Value)) != clean)
            throw new ProviderException("字段路径仅支持 $.data.balance 和 $.items[0].value 形式。");
        var position = 0;
        foreach (Match part in parts)
        {
            if (part.Index != position) throw new ProviderException("字段路径仅支持 $.data.balance 和 $.items[0].value 形式。");
            position = part.Index + part.Length;
            if (part.Groups[1].Success)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(part.Groups[1].Value, out root)) return null;
            }
            else
            {
                if (!int.TryParse(part.Groups[2].Value, out var index) || root.ValueKind != JsonValueKind.Array || index >= root.GetArrayLength()) return null;
                root = root[index];
            }
        }
        if (position != clean.Length) throw new ProviderException("字段路径格式不受支持。");
        return root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : root;
    }

    public static decimal? Number(JsonElement root, string path, bool required = false)
    {
        var value = Get(root, path);
        if (value is null && !required) return null;
        if (value is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d)) return d;
        if (value is { ValueKind: JsonValueKind.String } s && decimal.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        throw new ProviderException($"接口字段 {path} 缺失或不是有效金额。");
    }

    public static string? Text(JsonElement root, string path) => Get(root, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
    public static bool? Bool(JsonElement root, string path) => Get(root, path)?.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };

    public static void RequireSuccess(JsonElement root)
    {
        if (Get(root, "error") is not null || Bool(root, "success") == false || Bool(root, "status") == false)
            throw new ProviderException("平台返回业务错误，请核对 key 权限或查看平台控制台。");
    }
}
