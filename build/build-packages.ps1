# Сборка дистрибутивов VpnUs: портативная версия (ZIP) и установщик (Inno Setup).
#
# Примеры:
#   powershell -File build\build-packages.ps1
#   powershell -File build\build-packages.ps1 -Version 1.0.1 -Runtimes win-x64,win-arm64
#   powershell -File build\build-packages.ps1 -SkipInstaller      # только portable ZIP
#
# Результат: каталог dist\
#   VpnUs-portable-<version>-<arch>.zip
#   VpnUs-Setup-<version>-<arch>.exe

[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string[]]$Runtimes = @("win-x64"),
    [switch]$SkipInstaller,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stagingRoot = Join-Path $env:TEMP "vpnus-staging"

function Get-Arch([string]$runtime) {
    if ($runtime -like '*arm64*') { return 'arm64' }
    if ($runtime -like '*x86*') { return 'x86' }
    return 'x64'
}

function Get-IsccCandidates {
    return @(
        (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
}

function Find-Iscc {
    $found = @(Get-IsccCandidates)
    if ($found.Count -gt 0) { return $found[0] }

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host 'Inno Setup не найден, устанавливаю через winget...'
        winget install -e --id JRSoftware.InnoSetup --accept-package-agreements --accept-source-agreements --silent | Out-Host
        $found = @(Get-IsccCandidates)
        if ($found.Count -gt 0) { return $found[0] }
    }

    return $null
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
& (Join-Path $PSScriptRoot 'make-icon.ps1')

foreach ($runtime in $Runtimes) {
    $arch = Get-Arch $runtime
    $staging = Join-Path $stagingRoot "$arch"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    $payload = Join-Path $staging 'payload'
    New-Item -ItemType Directory -Force -Path $payload | Out-Null

    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }

    Write-Host "== publish VpnUs.App ($runtime, self-contained=$selfContained)"
    dotnet publish (Join-Path $root 'src\VpnUs.App\VpnUs.App.csproj') -c Release -r $runtime `
        --self-contained $selfContained -p:Version=$Version -p:DebugType=none `
        -o $payload --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "publish app failed" }

    Write-Host "== publish VpnUs.Service ($runtime)"
    dotnet publish (Join-Path $root 'src\VpnUs.Service\VpnUs.Service.csproj') -c Release -r $runtime `
        --self-contained $selfContained -p:Version=$Version -p:DebugType=none `
        -o (Join-Path $payload 'service') --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "publish service failed" }

    if (-not (Test-Path (Join-Path $payload 'VpnUs.exe'))) { throw "VpnUs.exe не найден в payload" }
    if (-not (Test-Path (Join-Path $payload 'service\VpnUs.Service.exe'))) { throw "VpnUs.Service.exe не найден в payload\service" }

    # --- Портативная сборка ---
    $portableDir = Join-Path $dist "VpnUs-portable-$Version-$arch"
    if (Test-Path $portableDir) { Remove-Item -Recurse -Force $portableDir }
    New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
    Copy-Item -Recurse -Force (Join-Path $payload '*') $portableDir

    New-Item -ItemType File -Force -Path (Join-Path $portableDir 'portable.flag') | Out-Null

    $readme = @"
VpnUs $Version (портативная версия, $arch)

1. Запустите VpnUs.exe — все данные (настройки, подписка, ядро, логи) будут храниться
   в подпапке data рядом с этой папкой, в ProgramData ничего не пишется.
2. Для VPN нужен TUN, поэтому sing-box должен работать от администратора.
   Нажмите в приложении: Настройки -> «Установить / починить службу (UAC)»
   (служба зарегистрируется из папки service рядом с приложением).
3. Нажмите «Обновить ядро» (скачает sing-box и wintun) и вставьте ссылку подписки.

Портативную версию можно обновлять из самого приложения: Настройки -> «Проверить обновления».
"@
    Set-Content -LiteralPath (Join-Path $portableDir 'ПРОЧТИ_МЕНЯ.txt') -Value $readme -Encoding UTF8

    $zip = Join-Path $dist "VpnUs-portable-$Version-$arch.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $portableDir '*') -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "portable: $zip ($([Math]::Round((Get-Item $zip).Length / 1MB, 1)) МБ)"

    # --- Установщик ---
    if (-not $SkipInstaller) {
        $iscc = Find-Iscc
        if (-not $iscc) {
            Write-Warning 'Inno Setup не найден — установщик пропущен (portable ZIP собран).'
            continue
        }

        Write-Host "== Inno Setup: $iscc"
        & $iscc "/DAppVersion=$Version" "/DArch=$arch" "/DSourceDir=$payload" (Join-Path $root 'installer\VpnUs.iss') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "iscc failed" }

        $setup = Join-Path $dist "VpnUs-Setup-$Version-$arch.exe"
        if (Test-Path $setup) { Write-Host "installer: $setup ($([Math]::Round((Get-Item $setup).Length / 1MB, 1)) МБ)" }
    }
}

Write-Host 'Готово. Файлы в каталоге dist\.'
Get-ChildItem $dist | Where-Object { $_.Name -like "VpnUs-*" } | Select-Object Name, @{n = 'МБ'; e = { [Math]::Round($_.Length / 1MB, 1) } }
