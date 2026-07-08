# Installs the Terraforming Mars QoL mod on Windows.
# Run it by double-clicking "Install (Windows).bat" (which calls this script).

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'

function Find-GameFolder {
    $libraries = @()
    try {
        $steamPath = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath
    } catch {
        $steamPath = 'C:\Program Files (x86)\Steam'
    }
    $libraries += $steamPath

    # Extra Steam libraries live in libraryfolders.vdf as "path" "D:\\SteamLibrary".
    $vdf = Join-Path $steamPath 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $libraries += ($matches[1] -replace '\\\\', '\')
            }
        }
    }

    foreach ($lib in ($libraries | Select-Object -Unique)) {
        $game = Join-Path $lib 'steamapps\common\Terraforming Mars'
        # Match on the (stable) Steam folder name containing a game exe, rather
        # than a hard-coded exe name, in case the executable is named differently.
        if ((Test-Path $game) -and
            (Get-ChildItem -Path $game -Filter '*.exe' -File -ErrorAction SilentlyContinue)) {
            return $game
        }
    }
    return $null
}

Write-Host ''
Write-Host '  Terraforming Mars mod - installer' -ForegroundColor Cyan
Write-Host '  ---------------------------------'
Write-Host ''

$game = Find-GameFolder
if (-not $game) {
    Write-Host 'Could not find Terraforming Mars in your Steam library.' -ForegroundColor Red
    Write-Host 'Install it through Steam first, then run this again.'
    exit 1
}
Write-Host "Found the game at:`n  $game"
Write-Host ''

$plugin = Join-Path $ScriptDir 'TfmCardRefresh.dll'
if (-not (Test-Path $plugin)) {
    Write-Host "TfmCardRefresh.dll is missing next to this installer." -ForegroundColor Red
    Write-Host 'Unzip the whole download and keep the files together, then run again.'
    exit 1
}

if (-not (Test-Path (Join-Path $game 'BepInEx\core'))) {
    Write-Host 'Downloading BepInEx (the mod loader)...'
    $tmp = Join-Path $env:TEMP 'bepinex_tfm.zip'
    Invoke-WebRequest -Uri $BepInExUrl -OutFile $tmp
    Write-Host 'Installing BepInEx...'
    Expand-Archive -Path $tmp -DestinationPath $game -Force
    Remove-Item $tmp -Force
} else {
    Write-Host 'BepInEx already installed.'
}

$plugins = Join-Path $game 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item $plugin $plugins -Force

Write-Host ''
Write-Host '  Installed!' -ForegroundColor Green
Write-Host '  Just launch Terraforming Mars from Steam as usual.'
Write-Host '  The mod loads automatically.'
Write-Host ''
