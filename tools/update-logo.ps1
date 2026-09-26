$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'logo.png'
$iconPath = Join-Path $projectRoot 'src\QuotaPeek\Assets\QuotaPeek.ico'
$previewPath = Join-Path $projectRoot 'docs\images\logo.png'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[object]]::new()
$source = [System.Drawing.Image]::FromFile($sourcePath)

try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(166, 237, 207))
        $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $buffer = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            # Match the widget's mint badge; preserve the original artwork and aspect ratio.
            $diameter = [single]($size * 0.6)
            $edge = [single]($size - $diameter)
            $shape.AddArc(0, 0, $diameter, $diameter, 180, 90)
            $shape.AddArc($edge, 0, $diameter, $diameter, 270, 90)
            $shape.AddArc($edge, $edge, $diameter, $diameter, 0, 90)
            $shape.AddArc(0, $edge, $diameter, $diameter, 90, 90)
            $shape.CloseFigure()
            $graphics.FillPath($background, $shape)
            $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
            $width = [single]($source.Width * $scale)
            $height = [single]($source.Height * $scale)
            $bounds = [System.Drawing.RectangleF]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height)
            $graphics.DrawImage($source, $bounds)
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add([pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() })
        } finally {
            $buffer.Dispose(); $shape.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
        }
    }
} finally {
    $source.Dispose()
}

[System.IO.Directory]::CreateDirectory((Split-Path -Parent $iconPath)) | Out-Null
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $previewPath)) | Out-Null
$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($iconPath))
try {
    # ICO directory followed by one PNG frame per Windows icon size.
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = [byte]($frame.Size % 256)
        $writer.Write($dimension); $writer.Write($dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally {
    $writer.Dispose()
}
[System.IO.File]::WriteAllBytes($previewPath, $frames[$frames.Count - 1].Bytes)
Get-Item -LiteralPath $iconPath, $previewPath | Select-Object FullName, Length
