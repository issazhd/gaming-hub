<#
.SYNOPSIS  Builds, tests and publishes Gaming Hub into dist\.
.PARAMETER SelfContained  Bundle the .NET runtime in the zip (no .NET install needed; larger download).
.PARAMETER Installer      Also build dist\GamingHub-<version>-Setup.exe (Inno Setup 6, per-user, .NET bundled).
#>
param([switch]$SelfContained, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$releaseBuild = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()

dotnet test tests/GamingHub.Tests -c Release
if ($LASTEXITCODE) { throw "Tests failed" }

function Publish([string]$out, [bool]$sc) {
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish src/GamingHub.App -c Release -r win-x64 --self-contained $(if ($sc) { 'true' } else { 'false' }) -o $out `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "-p:ReleaseBuild=$releaseBuild"
    if ($LASTEXITCODE) { throw "Publish failed" }
}

# Zip + script installer (framework-dependent unless -SelfContained)
$out = Join-Path $root 'dist\GamingHub'
Publish $out $SelfContained
Copy-Item (Join-Path $PSScriptRoot 'Install-GamingHub.ps1') $out
Copy-Item README.md $out
$version = (Get-Item (Join-Path $out 'GamingHub.exe')).VersionInfo.ProductVersion -replace '\+.*'
$zip = Join-Path $root "dist\GamingHub-$version-win-x64$(if ($SelfContained) { '-selfcontained' }).zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Published to $out"
Write-Host "Package: $zip"

if ($Installer) {
    $sc = Join-Path $root 'dist\GamingHub-selfcontained'
    Publish $sc $true
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$sc" (Join-Path $PSScriptRoot 'GamingHub.iss')
    if ($LASTEXITCODE) { throw "Installer build failed" }
    [IO.File]::WriteAllText((Join-Path $root "dist\GamingHub-$version-Setup.label.txt"), "Build $releaseBuild")
    Write-Host "Installer: $(Join-Path $root "dist\GamingHub-$version-Setup.exe")"
}
