<#
.SYNOPSIS  Per-user install of Gaming Hub (no administrator rights needed).
.DESCRIPTION
  Copies the app next to this script into %LOCALAPPDATA%\Programs\GamingHub and creates a Start Menu shortcut
  (and optionally a Desktop shortcut). Settings/library stay in %LOCALAPPDATA%\GamingHub and survive reinstalling.
  Run with -Uninstall to remove the program files, shortcuts and the "start with Windows" entry (your data is kept).
#>
param([switch]$Uninstall, [switch]$DesktopShortcut)
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\GamingHub'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Gaming Hub.lnk'
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Gaming Hub.lnk'

Get-Process GamingHub -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }

if ($Uninstall) {
    Remove-Item $startMenu, $desktop -ErrorAction SilentlyContinue
    Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name GamingHub -ErrorAction SilentlyContinue
    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    Write-Host "Gaming Hub removed. Your settings and library remain in $env:LOCALAPPDATA\GamingHub (delete that folder to remove them)."
    return
}

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $PSScriptRoot '*') $target -Recurse -Force -Exclude 'Install-GamingHub.ps1'
Copy-Item $PSCommandPath $target -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu) + $(if ($DesktopShortcut) { $desktop })) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $target 'GamingHub.exe'
    $s.WorkingDirectory = $target
    $s.Description = 'Gaming Hub'
    $s.Save()
}
# Keep "start with Windows" (if the user turned it on) pointing at this installed copy.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty $run -Name GamingHub -ErrorAction SilentlyContinue) {
    Set-ItemProperty $run -Name GamingHub -Value "`"$(Join-Path $target 'GamingHub.exe')`" --startup"
}
Write-Host "Installed to $target. Start it from the Start Menu ('Gaming Hub')."
