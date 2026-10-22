# Generates src/Pulse.App/Assets/pulse.ico (PNG-compressed entries, 16-256 px).
# Run with Windows PowerShell 5.1: powershell -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\Pulse.App\Assets\pulse.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded dark tile
    $r = [Math]::Max(2, [int]($s * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $s), ([System.Drawing.Color]::FromArgb(255, 22, 28, 38)), ([System.Drawing.Color]::FromArgb(255, 9, 12, 17))
    $g.FillPath($bg, $path)

    # Heartbeat line
    $pts = @(
        @(0.12, 0.58), @(0.34, 0.58), @(0.43, 0.30), @(0.55, 0.80), @(0.64, 0.46), @(0.70, 0.58), @(0.88, 0.58)
    ) | ForEach-Object { New-Object System.Drawing.PointF ([float]($_[0] * $s)), ([float]($_[1] * $s)) }
    $penWidth = [Math]::Max(1.6, $s * 0.062)
    $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 74, 222, 128)), ($penWidth * 2.2)
    $glow.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $glow.StartCap = $glow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    if ($s -ge 32) { $g.DrawLines($glow, [System.Drawing.PointF[]]$pts) }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 74, 222, 128)), $penWidth
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $images += , @($s, $ms.ToArray())
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $s = $img[0]; $data = $img[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img[1]) }
$bw.Close()
Write-Output "Wrote $out"
