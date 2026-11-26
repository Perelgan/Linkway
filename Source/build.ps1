[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist\VPNDesktop'),
    [switch] $ReleaseArchive
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'VPNDesktop\VPNDesktop.csproj'
$cache = Join-Path $PSScriptRoot 'build-cache'
$bundle = [IO.Path]::GetFullPath($OutputDirectory)
if ($ReleaseArchive -and (Split-Path -Leaf $bundle) -cne 'VPNDesktop') {
    throw 'A release bundle must be named VPNDesktop.'
}
if (Test-Path -LiteralPath $bundle) {
    throw 'Choose an output directory that does not exist.'
}
$libraries = Join-Path $bundle 'Libraries'
$languages = Join-Path $bundle 'Languages'
$licenses = Join-Path $bundle 'Licenses'
New-Item -ItemType Directory -Force $cache, $libraries, $languages, $licenses | Out-Null

function Get-VerifiedArchive([string] $Url, [string] $Name, [string] $Sha256) {
    $archive = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA-256 mismatch for $Name"
    }
    return $archive
}

$mihomo = Get-VerifiedArchive 'https://github.com/MetaCubeX/mihomo/releases/download/v1.19.34/mihomo-windows-amd64-compatible-v1.19.34.zip' 'mihomo.zip' 'cd6738d4648cc1a5f48049a1343b697ab510889d648f045c5c780fc1ab443a79'
$wintun = Get-VerifiedArchive 'https://www.wintun.net/builds/wintun-0.14.1.zip' 'wintun.zip' '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
$mihomoPath = Join-Path $cache 'mihomo'
Expand-Archive -LiteralPath $mihomo -DestinationPath $mihomoPath -Force
Expand-Archive -LiteralPath $wintun -DestinationPath $cache -Force
$mihomoLicense = Join-Path $mihomoPath 'LICENSE'
if (!(Test-Path -LiteralPath $mihomoLicense)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/MetaCubeX/mihomo/v1.19.34/LICENSE' -OutFile $mihomoLicense
}

dotnet publish $project -c Release -r win-x64 --self-contained true -o $libraries -p:RuntimeFrameworkVersion=10.0.15 -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Rename-Item -LiteralPath (Join-Path $libraries 'VPNDesktop.exe') -NewName 'VPNDesktop.App.exe'
foreach ($directory in Get-ChildItem -LiteralPath $libraries -Directory) {
    if (Get-ChildItem -LiteralPath $directory.FullName -File -Filter '*.resources.dll' | Select-Object -First 1) {
        if ($directory.Parent.FullName -ne $libraries -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected published resource directory.'
        }
        Move-Item -LiteralPath $directory.FullName -Destination $languages
    }
}
Copy-Item -LiteralPath (Join-Path $mihomoPath 'mihomo-windows-amd64-compatible.exe') -Destination (Join-Path $libraries 'mihomo.exe')
Copy-Item -LiteralPath (Join-Path $cache 'wintun\bin\amd64\wintun.dll') -Destination $libraries
Copy-Item -LiteralPath $mihomoLicense -Destination (Join-Path $licenses 'LICENSE-Mihomo.txt')
Copy-Item -LiteralPath (Join-Path $cache 'wintun\LICENSE.txt') -Destination (Join-Path $licenses 'LICENSE-Wintun.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE-Lucide.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE-YamlDotNet.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\README.md') -Destination $bundle

$launcherSources = @((Join-Path $PSScriptRoot 'Launcher\main.go'), (Join-Path $PSScriptRoot 'Launcher\update.go'), (Join-Path $PSScriptRoot 'Launcher\update_files.go'))
go build -trimpath -ldflags '-H windowsgui -s -w' -o (Join-Path $bundle 'VPNDesktop.exe') @launcherSources
if ($LASTEXITCODE -ne 0) { throw 'Go launcher build failed' }

if ($ReleaseArchive) {
    $archive = Join-Path (Split-Path -Parent $bundle) 'VPNDesktop-win-x64.zip'
    Compress-Archive -LiteralPath $bundle -DestinationPath $archive -Force
    & (Join-Path $PSScriptRoot 'write-update-manifest.ps1') -Archive $archive
}
