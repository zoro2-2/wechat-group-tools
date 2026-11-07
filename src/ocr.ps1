# ocr.ps1 —— Windows 自带 OCR（中文），2 倍放大 + 内存流（避免 WinRT 文件权限限制）
# 用法: powershell -File ocr.ps1 <输入png> <输出tsv>
param(
    [Parameter(Mandatory=$true)][string]$InPath,
    [Parameter(Mandatory=$true)][string]$OutPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Runtime.WindowsRuntime

$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType=WindowsRuntime]

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]

function Await($op, $type) {
    $asTask = $asTaskGeneric.MakeGenericMethod($type)
    $task = $asTask.Invoke($null, @($op))
    $task.Wait(-1) | Out-Null
    $task.Result
}

# 1) 读入并放大 2 倍（小字号识别准确率的关键），全程内存
$scale = 2
$src = [System.Drawing.Image]::FromFile($InPath)
try {
    $w = [int]($src.Width * $scale)
    $h = [int]($src.Height * $scale)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($src, 0, 0, $w, $h)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
} finally { $src.Dispose() }

# 2) .NET 流 -> WinRT 流（不碰文件系统，绕过 StorageFile 权限限制）
$bytes = $ms.ToArray()
$ms.Dispose()
$mem = New-Object System.IO.MemoryStream(,$bytes)
$ras = [System.IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($mem)

$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($ras)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$lang = New-Object Windows.Globalization.Language 'zh-Hans-CN'
$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
if (-not $engine) { Write-Error "OCR engine unavailable"; exit 2 }

$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])

# 3) 输出 y / x / 文本（坐标换算回原图尺度）
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine("y`tx`tw`th`ttext")
foreach ($line in @($result.Lines)) {
    $wds = @($line.Words)
    if ($wds.Count -eq 0) { continue }
    $minX = 1e9; $minY = 1e9; $maxX = 0; $maxY = 0
    foreach ($wd in $wds) {
        $rr = $wd.BoundingRect
        if ($rr.X -lt $minX) { $minX = $rr.X }
        if ($rr.Y -lt $minY) { $minY = $rr.Y }
        if (($rr.X + $rr.Width) -gt $maxX) { $maxX = $rr.X + $rr.Width }
        if (($rr.Y + $rr.Height) -gt $maxY) { $maxY = $rr.Y + $rr.Height }
    }
    $x = [int]($minX / $scale); $y = [int]($minY / $scale)
    $w = [int](($maxX - $minX) / $scale); $h = [int](($maxY - $minY) / $scale)
    $txt = ($line.Text -replace ' ', '')
    $txt = $txt.Replace("`t", ' ')
    $null = $sb.AppendLine("$y`t$x`t$w`t$h`t$txt")
}
[System.IO.File]::WriteAllText($OutPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))