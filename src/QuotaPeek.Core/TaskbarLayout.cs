namespace QuotaPeek.Core;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class TaskbarLayout
{
    // Use only free space in the left half. Never move or resize Explorer's controls.
    public static PixelRect? FindSpace(PixelRect taskbar, double scale, IEnumerable<PixelRect> occupied)
    {
        if (scale <= 0 || taskbar.Width <= taskbar.Height || taskbar.Height < 24 * scale) return null;
        var gap = (int)Math.Ceiling(8 * scale);
        var minimum = (int)Math.Ceiling(144 * scale);
        var preferred = (int)Math.Ceiling(232 * scale);
        var height = Math.Min((int)Math.Round(32 * scale), taskbar.Height - 2 * (int)Math.Ceiling(4 * scale));
        var start = taskbar.Left + gap;
        var end = taskbar.Left + taskbar.Width / 2;
        var blocks = occupied.Where(r => r.Width > 0 && r.Height > 0 && r.Bottom > taskbar.Top && r.Top < taskbar.Bottom && r.Right > start && r.Left < end)
            .OrderBy(r => r.Left);
        foreach (var block in blocks)
        {
            if (Math.Min(end, block.Left - gap) - start >= minimum)
                return At(start, Math.Min(preferred, Math.Min(end, block.Left - gap) - start));
            start = Math.Max(start, block.Right + gap);
        }
        return end - start >= minimum ? At(start, Math.Min(preferred, end - start)) : null;

        PixelRect At(int left, int width)
        {
            var top = taskbar.Top + (taskbar.Height - height) / 2;
            return new(left, top, left + width, top + height);
        }
    }
}
