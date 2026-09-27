$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $PSScriptRoot 'RouteTests.exe'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warn:4 /warnaserror+ /codepage:65001 "/out:$exe" /reference:System.Drawing.dll (Join-Path $PSScriptRoot '..\src\ClickEngine.cs') (Join-Path $PSScriptRoot 'RouteTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Route test build failed.' }
$testOutput = & $exe
$testExitCode = $LASTEXITCODE
$testOutput | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'route-results.txt') -Encoding UTF8
$testOutput | Write-Output
if ($testExitCode -ne 0) { throw 'Route tests failed.' }
