param([string]$Version)
# Builds the exe, the portable zip, the source zip and SHA256SUMS for one version.
# The version defaults to AppInfo.Version in src\Version.cs, so the tag, the file
# names and the assembly can never drift apart.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Version) {
    $versionText = Get-Content -LiteralPath (Join-Path $root 'src\Version.cs') -Raw
    $match = [regex]::Match($versionText, 'Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
    if (-not $match.Success) { throw 'AppInfo.Version was not found in src\Version.cs.' }
    $Version = $match.Groups[1].Value
}
$name = "AutoClicker-v$Version"
$exe = Join-Path $root "dist\$name.exe"
& (Join-Path $root 'build.ps1') -OutputName "$name.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Build output is missing: $exe" }

$out = Join-Path $root ".release\v$Version"
if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination $out

$stage = Join-Path $env:TEMP ("mint-portable-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $exe -Destination (Join-Path $stage "$name.exe")
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $stage '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $root '手动验收.md') -Destination (Join-Path $stage '手动验收.md')
    Copy-Item -LiteralPath (Join-Path $root 'RELEASE_NOTES.md') -Destination (Join-Path $stage '版本说明.md')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $out "$name-portable.zip") -CompressionLevel Optimal
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

$sourceStage = Join-Path $env:TEMP ("mint-source-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $sourceStage 'src') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $sourceStage 'tests') -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $root 'src\*.cs') -Destination (Join-Path $sourceStage 'src')
    Copy-Item -Path (Join-Path $root 'tests\*.cs') -Destination (Join-Path $sourceStage 'tests')
    Copy-Item -Path (Join-Path $root 'tests\*.ps1') -Destination (Join-Path $sourceStage 'tests') -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath (Join-Path $root 'tests\README.md') -Destination (Join-Path $sourceStage 'tests\README.md')
    foreach ($file in @('build.ps1', 'release.ps1', 'app.manifest', 'app.ico', 'README.md', 'RELEASE_NOTES.md', 'VERIFICATION.md', '手动验收.md', '.gitignore', '.gitattributes')) {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $sourceStage
    }
    Compress-Archive -Path (Join-Path $sourceStage '*') -DestinationPath (Join-Path $out "$name-source.zip") -CompressionLevel Optimal
}
finally {
    Remove-Item -LiteralPath $sourceStage -Recurse -Force -ErrorAction SilentlyContinue
}

$sums = Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $_.Name
}
Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Value $sums -Encoding ASCII
Get-ChildItem -LiteralPath $out | Select-Object Name, Length | Format-Table -AutoSize
Write-Output "Release assets written to $out"
