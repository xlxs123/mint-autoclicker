$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /main:MintClicker.MarkerTests /platform:anycpu /optimize+ /warn:4 /warnaserror+ /codepage:65001 "/out:$(Join-Path $PSScriptRoot 'MarkerTests.exe')" "/win32icon:$(Join-Path $root 'app.ico')" "/win32manifest:$(Join-Path $root 'app.manifest')" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Web.Extensions.dll (Join-Path $root 'src\Native.cs') (Join-Path $root 'src\ClickEngine.cs') (Join-Path $root 'src\Markers.cs') (Join-Path $root 'src\Profiles.cs') (Join-Path $root 'src\Program.cs') (Join-Path $PSScriptRoot 'MarkerTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Marker test build failed.' }
