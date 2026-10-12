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

# the Microsoft Store package: the exe, its tile images and msix\AppxManifest.xml with this version.
# The Store signs it, so it stays unsigned; dist\msix is also the folder to register for testing
# (Add-AppxPackage -Register dist\msix\AppxManifest.xml, with Developer Mode on).
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty DirectoryName
if (-not $sdk) { throw 'needs the Windows 10/11 SDK (Windows Kits\10\bin)' }
$version = (Select-String -Path "$PSScriptRoot\src\Program.cs" -Pattern 'Version = "(.+)"').Matches[0].Groups[1].Value
$layout = Join-Path $dist 'msix'
if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item "$dist\AirMouse.exe" $layout
Copy-Item -Recurse "$PSScriptRoot\msix\Assets" $layout
[xml]$manifest = Get-Content "$PSScriptRoot\msix\AppxManifest.xml"
$manifest.Package.Identity.Version = "$version.0"
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))
# resources.pri lets Windows pick the right size of each image
& "$sdk\makepri.exe" createconfig /cf "$dist\priconfig.xml" /dq en-US /pv 10.0.0 /o | Out-Null
# one package, so every size goes in the one resources.pri rather than per-scale resource packs
[xml]$priconfig = Get-Content "$dist\priconfig.xml"
$packaging = $priconfig.SelectSingleNode('//packaging')
if ($packaging) { $packaging.ParentNode.RemoveChild($packaging) | Out-Null }
$priconfig.Save("$dist\priconfig.xml")
& "$sdk\makepri.exe" new /pr $layout /cf "$dist\priconfig.xml" /mn "$layout\AppxManifest.xml" /of "$layout\resources.pri" /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makepri failed' }
& "$sdk\makeappx.exe" pack /o /h SHA256 /d $layout /p "$dist\AirMouse.msix" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }
Get-Item "$dist\AirMouse.exe", "$dist\AirMouse.msix" | Select-Object Name, Length
