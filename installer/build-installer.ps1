<#
.SYNOPSIS
    Publishes FluentFlyout and builds the Inno Setup installer(s).

.DESCRIPTION
    The version comes from <Version> in FluentFlyoutWPF\FluentFlyout.csproj (the only place to change it).

.EXAMPLE
    ./installer/build-installer.ps1                  # release build: x64 + arm64, version from the csproj
    ./installer/build-installer.ps1 -Arch x64        # quicker local test build

    Output (upload all of these to the GitHub release):
      artifacts\installer\FluentFlyoutFree-Setup-x64.exe
      artifacts\installer\FluentFlyoutFree-Setup-arm64.exe
      artifacts\installer\latest.json
#>
param(
    # App version (x.y.z). Defaults to <Version> in FluentFlyout.csproj.
    [string]$Version,

    [ValidateSet('x64', 'arm64')]
    [string[]]$Arch = @('x64', 'arm64'),

    # Path to ISCC.exe. Auto-detected when omitted.
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'FluentFlyoutWPF\FluentFlyout.csproj'
$script = Join-Path $PSScriptRoot 'FluentFlyoutFree.iss'
$publishRoot = Join-Path $root 'artifacts\publish'
$installerDir = Join-Path $root 'artifacts\installer'

if (-not $Version) {
    [xml]$csproj = Get-Content $project
    $Version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
}
$Version = $Version.Trim().TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 1.2.3 (got '$Version')"
}

if (-not $Iscc) {
    $candidates = @(
        (Get-Command 'iscc.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $Iscc = $candidates | Select-Object -First 1
}
if (-not $Iscc) {
    throw 'Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup'
}

Write-Host "Building FluentFlyout Free v$Version for: $($Arch -join ', ')" -ForegroundColor Cyan

# start clean so stale installers / manifests from older builds can't be uploaded by mistake
if (Test-Path $installerDir) { Remove-Item $installerDir -Recurse -Force }

foreach ($a in $Arch) {
    $publishDir = Join-Path $publishRoot $a
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    Write-Host "`n==> dotnet publish (win-$a)" -ForegroundColor Cyan
    # self-contained: no separate .NET runtime install needed
    # ReadyToRun: precompiled code -> faster startup and less JIT CPU/memory
    # (no -p:Platform here: it would also apply to the source generator project and make it
    #  unloadable by the compiler when cross-building arm64; the runtime identifier is enough)
    dotnet publish $project `
        -c Release `
        -r "win-$a" `
        --self-contained true `
        -p:PublishReadyToRun=true `
        -p:DebugType=none `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $a" }

    Write-Host "`n==> Inno Setup ($a)" -ForegroundColor Cyan
    & $Iscc "/DAppVersion=$Version" "/DArch=$a" "/DSourceDir=$publishDir" "/DOutputDir=$installerDir" $script
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $a" }
}

# Release manifest read by the in-app updater (published as a release asset next to the installers).
# It is fetched from /releases/latest/download/latest.json, which avoids the GitHub API rate limit.
$installers = [ordered]@{}
foreach ($a in $Arch) {
    $file = Get-Item (Join-Path $installerDir "FluentFlyoutFree-Setup-$a.exe")
    $installers[$a] = [ordered]@{
        name   = $file.Name
        size   = $file.Length
        sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    }
}
$manifest = [ordered]@{ version = $Version; installers = $installers }
$manifestPath = Join-Path $installerDir 'latest.json'
# UTF-8 without BOM
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

Write-Host "`nDone. Create a GitHub release with:" -ForegroundColor Green
Write-Host "  Tag:    v$Version   (must be exactly this - the updater downloads from it)"
Write-Host "  Upload: (all files below, keep the names)"
Get-ChildItem $installerDir | Where-Object { $_.Name -like 'FluentFlyoutFree-Setup-*.exe' -or $_.Name -eq 'latest.json' } | ForEach-Object {
    '          {0}  ({1:N1} MB)' -f $_.FullName, ($_.Length / 1MB)
}
Write-Host "  Leave 'Set as a pre-release' unchecked so installed apps pick it up."
if ($Arch.Count -lt 2) {
    Write-Host "  Note: only built $($Arch -join ', '); run without -Arch for a full release." -ForegroundColor Yellow
}
