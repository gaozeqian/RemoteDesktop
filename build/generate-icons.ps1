Param(
    [string]$SourceSvg = "src/Hosts/RemoteDesktop.Client/Assets/icon.svg",
    [string]$OutputDir = "src/Hosts/RemoteDesktop.Client/Assets"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$edgePath = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
$resolvedSvg = (Resolve-Path $SourceSvg).Path
$outFull = [System.IO.Path]::GetFullPath($OutputDir)
$tempMaster = Join-Path $outFull "icon-512.png"
$svgUri = "file:///" + ($resolvedSvg -replace '\\', '/')

$edgeArgs = "--headless --disable-gpu --default-background-color=00000000 --force-device-scale-factor=1 --hide-scrollbars --screenshot=`"$tempMaster`" --window-size=512,512 `"$svgUri`""

cmd.exe /c "`"$edgePath`" $edgeArgs" | Out-Null

if (-not (Test-Path $tempMaster)) {
    throw "Failed to render master PNG from SVG"
}

$src = [System.Drawing.Bitmap]::FromFile($tempMaster)
$sizes = 256, 128, 64, 48, 32, 24, 16
$entries = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()

    [System.IO.File]::WriteAllBytes((Join-Path $outFull "icon-$s.png"), $bytes)
    $entries += ,@($s, $bytes)
}
$src.Dispose()

# 组装 ICO 文件
$icoPath = Join-Path $outFull "icon.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs
$count = $entries.Count
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$count)
$offset = 6 + (16 * $count)

for ($i = 0; $i -lt $count; $i++) {
    $s = $entries[$i][0]
    $data = $entries[$i][1]
    $dim = if ($s -ge 256) { [byte]0 } else { [byte]$s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}

for ($i = 0; $i -lt $count; $i++) {
    $bw.Write($entries[$i][1])
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()

Copy-Item (Join-Path $outFull "icon-256.png") (Join-Path $outFull "icon.png") -Force
Write-Output "Successfully generated: $icoPath and all platform PNGs"
