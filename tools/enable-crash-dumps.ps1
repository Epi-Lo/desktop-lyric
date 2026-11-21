# Requires Administrator. Writes a minidump on native crash
# (heap / WinRT Finalize never reach error.log).
# Dumps: %LOCALAPPDATA%\CrashDumps\DesktopLyric.exe.<pid>.dmp

$ErrorActionPreference = "Stop"
$key = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\DesktopLyric.exe"
$folder = Join-Path $env:LOCALAPPDATA "CrashDumps"
New-Item -ItemType Directory -Force -Path $folder | Out-Null
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key -Name DumpFolder -Type ExpandString -Value $folder
Set-ItemProperty $key -Name DumpCount -Type DWord -Value 8
Set-ItemProperty $key -Name DumpType -Type DWord -Value 2
Write-Host "LocalDumps enabled for DesktopLyric.exe -> $folder"
Write-Host "DumpType=2 (full). Re-run this script after OS changes if dumps stop appearing."
