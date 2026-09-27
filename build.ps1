param(
    [string]$OutputName = 'AutoClicker.exe',
    [switch]$IncludeTestTools
)
$ErrorActionPreference = 'Stop'
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or -not $OutputName.EndsWith('.exe')) { throw 'OutputName must be an .exe filename.' }
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 64,64
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(28,112,83))
$white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$points = [System.Drawing.Point[]]@([System.Drawing.Point]::new(20,12),[System.Drawing.Point]::new(20,48),[System.Drawing.Point]::new(29,39),[System.Drawing.Point]::new(36,54),[System.Drawing.Point]::new(43,50),[System.Drawing.Point]::new(36,35),[System.Drawing.Point]::new(49,35))
$graphics.FillPolygon($white,$points)
$stream = New-Object System.IO.MemoryStream
$bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
$png = $stream.ToArray()
$iconPath = Join-Path $root 'app.ico'
$iconFile = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter $iconFile
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
$writer.Write($png)
$writer.Dispose(); $stream.Dispose(); $white.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
$exe = Join-Path $dist $OutputName
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /warnaserror+ /codepage:65001 "/out:$exe" "/win32icon:$iconPath" "/win32manifest:$(Join-Path $root 'app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $exe"
$testSource = Join-Path $root 'tests\TestPad.cs'
if ($IncludeTestTools -and (Test-Path -LiteralPath $testSource)) {
    $testExe = Join-Path $root 'tests\TestPad.exe'
    & $compiler /nologo /target:winexe /main:MintClicker.TestPad /platform:anycpu /optimize+ /warn:4 /warnaserror+ /codepage:65001 "/out:$testExe" "/win32manifest:$(Join-Path $root 'app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll $sources $testSource
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Write-Output "Built test pad: $testExe"
}
