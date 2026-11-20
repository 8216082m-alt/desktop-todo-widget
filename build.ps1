param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$iconPath = Join-Path $dist 'DeskTodo.ico'
$frames = foreach ($size in 16, 32, 48, 256) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $stream = [System.IO.MemoryStream]::new()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(17, 100, 85))
    $foreground = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.1)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 32.0, $size / 32.0)
        $shape.AddArc(1, 1, 10, 10, 180, 90)
        $shape.AddArc(21, 1, 10, 10, 270, 90)
        $shape.AddArc(21, 21, 10, 10, 0, 90)
        $shape.AddArc(1, 21, 10, 10, 90, 90)
        $shape.CloseFigure()
        $graphics.FillPath($background, $shape)
        $foreground.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $foreground.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $foreground.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $tick = [System.Drawing.PointF[]] @(
            [System.Drawing.PointF]::new(8, 16),
            [System.Drawing.PointF]::new(13, 21),
            [System.Drawing.PointF]::new(24, 11)
        )
        $graphics.DrawLines($foreground, $tick)
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
    }
    finally {
        $shape.Dispose()
        $foreground.Dispose()
        $background.Dispose()
        $stream.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create($iconPath))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $writer.Write([byte]($frame.Size % 256))
        $writer.Write([byte]($frame.Size % 256))
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose() }

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$appName = [string]::Concat([char]0x684C,[char]0x9762,[char]0x5F85,[char]0x529E) + '.exe'
$destination = Join-Path $dist $appName
$files = 'Program.cs', 'State.cs', 'DesktopLayer.cs', 'WidgetForm.cs', 'Dialogs.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$manifest = Join-Path $PSScriptRoot 'app.manifest'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/win32manifest:$manifest" "/win32icon:$iconPath" "/out:$destination" /reference:System.Windows.Forms.dll,System.Drawing.dll,System.Runtime.Serialization.dll,System.Core.dll,Microsoft.CSharp.dll @files
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $destination)) {
    throw 'The desktop widget could not be compiled.'
}
Get-Item -LiteralPath $destination | Select-Object Name, Length, FullName

if ($Verify) {
    $verifyExe = Join-Path $dist 'Verify.exe'
    $verifyDirectory = Join-Path $dist 'checks'
    $stateSource = Join-Path $PSScriptRoot 'State.cs'
    $verifySource = Join-Path $PSScriptRoot 'Verify.cs'
    & $compiler /nologo /codepage:65001 "/out:$verifyExe" /reference:System.Runtime.Serialization.dll,Microsoft.CSharp.dll $stateSource $verifySource
    if ($LASTEXITCODE -ne 0) { throw 'The tests could not be compiled.' }
    & $verifyExe $verifyDirectory $destination
    if ($LASTEXITCODE -ne 0) { throw 'A test failed.' }
}