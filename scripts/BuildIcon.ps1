param([string]$Preview)
# 由 src/ScreenTranslator.App/Themes/AppIcon.xaml 的矢量图生成多尺寸 Assets/AppIcon.ico。
# 只用 Windows 自带的 WPF 渲染，不下载任何工具；修改图标时改 XAML 后重新运行本脚本。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$iconRoot = Split-Path -Parent $PSScriptRoot
$iconSource = Join-Path $iconRoot 'src\ScreenTranslator.App\Themes\AppIcon.xaml'
$iconTarget = Join-Path $iconRoot 'src\ScreenTranslator.App\Assets\AppIcon.ico'
$iconStream = [IO.File]::OpenRead($iconSource)
try { $iconArt = [Windows.Markup.XamlReader]::Load($iconStream) } finally { $iconStream.Close() }

function Get-IconArt([int]$size) {
    if ($size -le 24) { return $iconArt['AppIconSmall'] }
    if ($size -le 48) { return $iconArt['AppIconMedium'] }
    return $iconArt['AppIconLarge']
}
function Get-IconBitmap([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage((Get-IconArt $size), (New-Object Windows.Rect(0, 0, $size, $size)))
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}
function Get-PngBytes($bitmap) {
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object IO.MemoryStream
    $encoder.Save($memory)
    return , $memory.ToArray()
}
# 256 像素用 PNG 帧；更小尺寸用 32 位 BMP 帧，兼容只认传统格式的读取方。
function Get-DibBytes($bitmap, [int]$size) {
    $straight = New-Object Windows.Media.Imaging.FormatConvertedBitmap($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)
    $maskStride = [int]([math]::Ceiling($size / 32.0) * 4)
    $mask = New-Object byte[] ($maskStride * $size)
    $memory = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter($memory)
    $writer.Write([int]40); $writer.Write([int]$size); $writer.Write([int]($size * 2))
    $writer.Write([int16]1); $writer.Write([int16]32); $writer.Write([int]0)
    $writer.Write([int]($pixels.Length + $mask.Length)); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        $writer.Write($pixels, $y * $stride, $stride)
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) { $mask[($size - 1 - $y) * $maskStride + [int][math]::Floor($x / 8)] = $mask[($size - 1 - $y) * $maskStride + [int][math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) }
        }
    }
    $writer.Write($mask)
    $writer.Flush()
    return , $memory.ToArray()
}

$iconSizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$iconFrames = foreach ($size in $iconSizes) {
    $bitmap = Get-IconBitmap $size
    if ($size -ge 256) { , (Get-PngBytes $bitmap) } else { , (Get-DibBytes $bitmap $size) }
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $iconTarget) | Out-Null
$iconFile = New-Object IO.MemoryStream
$iconWriter = New-Object IO.BinaryWriter($iconFile)
$iconWriter.Write([int16]0); $iconWriter.Write([int16]1); $iconWriter.Write([int16]$iconSizes.Count)
$iconOffset = 6 + 16 * $iconSizes.Count
for ($i = 0; $i -lt $iconSizes.Count; $i++) {
    $edge = if ($iconSizes[$i] -ge 256) { 0 } else { $iconSizes[$i] }
    $iconWriter.Write([byte]$edge); $iconWriter.Write([byte]$edge); $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
    $iconWriter.Write([int16]1); $iconWriter.Write([int16]32); $iconWriter.Write([int]$iconFrames[$i].Length); $iconWriter.Write([int]$iconOffset)
    $iconOffset += $iconFrames[$i].Length
}
foreach ($frame in $iconFrames) { $iconWriter.Write([byte[]]$frame) }
$iconWriter.Flush()
[IO.File]::WriteAllBytes($iconTarget, $iconFile.ToArray())
Write-Output ('已生成 ' + $iconTarget + '（' + $iconSizes.Count + ' 个尺寸）')

if ($Preview) {
    # 预览图：浅色与深色背景上各排一行全部尺寸，便于检查小尺寸是否清晰。
    $sheet = New-Object Windows.Media.DrawingVisual
    $context = $sheet.RenderOpen()
    $width = 40; foreach ($size in $iconSizes) { $width += $size + 24 }
    $context.DrawRectangle([Windows.Media.Brushes]::White, $null, (New-Object Windows.Rect(0, 0, $width, 300)))
    $context.DrawRectangle((New-Object Windows.Media.SolidColorBrush([Windows.Media.Color]::FromRgb(32, 32, 32))), $null, (New-Object Windows.Rect(0, 300, $width, 300)))
    foreach ($row in 0, 1) {
        $x = 20
        foreach ($size in $iconSizes) {
            $context.DrawImage((Get-IconBitmap $size), (New-Object Windows.Rect($x, ($row * 300 + 278 - $size), $size, $size)))
            $x += $size + 24
        }
    }
    $context.Close()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap([int]$width, 600, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($sheet)
    [IO.File]::WriteAllBytes($Preview, (Get-PngBytes $bitmap))
}
