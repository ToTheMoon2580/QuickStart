# Generates a multi-size .ico (PNG-compressed entries, supported since Vista).
# ASCII-only on purpose: Windows PowerShell 5.1 decodes BOM-less scripts as ANSI.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\app.ico'))

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 32.0
    $accent = [System.Drawing.Color]::FromArgb(255, 96, 158, 255)
    $light = [System.Drawing.Color]::FromArgb(255, 175, 216, 255)

    $penRing = New-Object System.Drawing.Pen($accent, [float](3.4 * $s))
    $g.DrawEllipse($penRing, [float](4.6 * $s), [float](4.6 * $s), [float](16.8 * $s), [float](16.8 * $s))
    $penRing.Dispose()

    $penHandle = New-Object System.Drawing.Pen($accent, [float](3.8 * $s))
    $penHandle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $penHandle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($penHandle, [float](18.6 * $s), [float](18.6 * $s), [float](27.2 * $s), [float](27.2 * $s))
    $penHandle.Dispose()

    $brush = New-Object System.Drawing.SolidBrush($light)
    $g.FillEllipse($brush, [float](9.4 * $s), [float](9.4 * $s), [float](7.2 * $s), [float](7.2 * $s))
    $brush.Dispose()

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs.Add($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = 0
    if ($s -lt 256) { $dim = $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

$len = (Get-Item -LiteralPath $Out).Length
Write-Output ("icon: {0} ({1:N0} bytes, {2} sizes)" -f $Out, $len, $sizes.Count)
