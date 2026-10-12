# Builds dist\AirMouse.exe with the C# compiler that ships in Windows (.NET Framework 4.8, which
# every Windows 10 and 11 has, so the exe needs nothing installed). Building also needs the
# Windows 10/11 SDK for its WinRT metadata; Visual Studio installs it, or get it on its own.
$ErrorActionPreference = 'Stop'

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
# the newest versioned folder; the per-namespace copies in System32 don't line up with the facades
$winmd = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\UnionMetadata\10.*\Windows.winmd" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Name } | Select-Object -Last 1 -ExpandProperty FullName
if (-not $winmd) { throw 'needs the Windows 10/11 SDK (Windows Kits\10\UnionMetadata)' }
$refs = @($winmd) + ('System.Runtime.dll', 'System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll' |
    ForEach-Object { Join-Path $fw $_ }) + 'System.Windows.Forms.dll', 'System.Drawing.dll'

$dist = Join-Path $PSScriptRoot 'dist'
$assets = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Force $dist | Out-Null
& (Join-Path $fw 'csc.exe') /nologo /target:winexe /optimize+ "/out:$dist\AirMouse.exe" `
    "/win32icon:$assets\AirMouse.ico" "/win32manifest:$PSScriptRoot\app.manifest" `
    "/resource:$assets\AirMouse.ico,AirMouse.ico" "/resource:$assets\AirMouseIdle.ico,AirMouseIdle.ico" `
    ($refs | ForEach-Object { "/r:$_" }) (Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Get-Item "$dist\AirMouse.exe" | Select-Object Name, Length
