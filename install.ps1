# Builds AirMouseReconnect with the C# compiler that ships in Windows, installs it to
# %LOCALAPPDATA%\AirMouse, starts it, and runs it at every sign-in. -Uninstall removes it.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'AirMouse'
$exe = Join-Path $dir 'AirMouseReconnect.exe'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process AirMouseReconnect -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Uninstall) {
    Remove-ItemProperty $runKey -Name AirMouseReconnect -ErrorAction SilentlyContinue
    Remove-Item $exe -ErrorAction SilentlyContinue
    'AirMouseReconnect removed'
    return
}

# WinRT metadata from the newest Windows SDK (installed with Visual Studio or on its own); the
# per-namespace copies in System32 don't line up with the .NET Framework facades
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$winmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata\10.*\Windows.winmd" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Name } | Select-Object -Last 1 -ExpandProperty FullName
if (-not $winmd) { throw 'needs the Windows 10/11 SDK (Windows Kits\10\UnionMetadata)' }
$facades = 'System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll' |
    ForEach-Object { Join-Path $fw $_ }

New-Item -ItemType Directory -Force $dir | Out-Null
& (Join-Path $fw 'csc.exe') /nologo /target:winexe "/out:$exe" ((@($winmd) + $facades) | ForEach-Object { "/r:$_" }) (Join-Path $PSScriptRoot 'AirMouseReconnect.cs')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Set-ItemProperty $runKey -Name AirMouseReconnect -Value "`"$exe`""
Start-Process $exe
"installed $exe (log: $dir\reconnect.log)"
