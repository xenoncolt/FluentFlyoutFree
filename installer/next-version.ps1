<#
.SYNOPSIS
    Works out the next release version from what changed since the latest release.

.DESCRIPTION
    Rules (the biggest bump wins):
      - Upstream FluentFlyout released a new version (FluentFlyoutMSIX/Package.appxmanifest):
        the same kind of bump. 2.16.1 -> 2.17.0 is minor, 2.x -> 3.0.0 is major, 2.16.1 -> 2.16.2 is patch.
      - Commits made in this fork (not coming from upstream) using Conventional Commit prefixes:
        "feat: ..." is minor, "fix!: ..." / "feat!: ..." or a "BREAKING CHANGE" line is major.
      - Anything else: patch.
      - No release yet (no -LatestTag): the csproj <Version>.
    To force a specific version, type it when running the "Release installer" workflow instead.

    Returns an object with Version (x.y.z), Bump (major/minor/patch/initial/none), Reason and
    UpstreamVersion. Bump is "none" when nothing changed since the latest release.

.EXAMPLE
    ./installer/next-version.ps1 -LatestTag v1.0.0
#>
param(
    # Tag of the latest release, e.g. v1.0.0. Empty when there is no release yet.
    [string]$LatestTag,

    # Commit to release.
    [string]$Ref = 'HEAD',

    # Upstream branch; commits reachable from it are judged by upstream's version, not their messages.
    [string]$UpstreamRef = 'upstream/master'
)

# native git calls report failures through $LASTEXITCODE; don't let stderr output abort the script
$ErrorActionPreference = 'Continue'

$root = Split-Path $PSScriptRoot -Parent
$manifestPath = 'FluentFlyoutMSIX/Package.appxmanifest'

function Format-Version([version]$v) { if ($v) { '{0}.{1}.{2}' -f $v.Major, $v.Minor, $v.Build } else { $null } }

function Read-UpstreamVersion([string]$rev) {
    $text = (git -C $root show "${rev}:$manifestPath" 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0 -or -not $text) { return $null }
    $start = $text.IndexOf('<')
    if ($start -lt 0) { return $null }
    try { return [version]([xml]$text.Substring($start)).Package.Identity.Version } catch { return $null }
}

[xml]$csproj = Get-Content (Join-Path $root 'FluentFlyoutWPF/FluentFlyout.csproj')
$projectVersion = [version](@($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0])
$upstreamNow = Read-UpstreamVersion $Ref

function New-Result($version, $bump, $reason) {
    [pscustomobject]@{
        Version         = Format-Version $version
        Bump            = $bump
        Reason          = $reason
        UpstreamVersion = Format-Version $upstreamNow
    }
}

if (-not $LatestTag) {
    return New-Result $projectVersion 'initial' 'first release (version from FluentFlyout.csproj)'
}

$latest = [version]$LatestTag.Trim().TrimStart('v')

git -C $root rev-parse --verify --quiet "refs/tags/$LatestTag^{commit}" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Tag $LatestTag isn't available locally. Fetch tags first (git fetch origin --tags, or actions/checkout with fetch-depth: 0)."
}

$changes = [int](git -C $root rev-list --count "$LatestTag..$Ref")
if ($changes -eq 0) {
    return New-Result $latest 'none' "nothing changed since $LatestTag"
}

# 1 = patch, 2 = minor, 3 = major
$level = 1
$reasons = [System.Collections.Generic.List[string]]::new()

# upstream released a new version since our latest release?
$upstreamThen = Read-UpstreamVersion $LatestTag
if ($upstreamThen -and $upstreamNow -and $upstreamNow -gt $upstreamThen) {
    $upstreamLevel = if ($upstreamNow.Major -gt $upstreamThen.Major) { 3 }
                     elseif ($upstreamNow.Minor -gt $upstreamThen.Minor) { 2 }
                     else { 1 }
    $level = [Math]::Max($level, $upstreamLevel)
    $reasons.Add("FluentFlyout $(Format-Version $upstreamThen) -> $(Format-Version $upstreamNow)")
}

# this fork's own commits (upstream's are excluded when the upstream ref is available)
$range = @("$LatestTag..$Ref")
git -C $root rev-parse --verify --quiet $UpstreamRef | Out-Null
if ($LASTEXITCODE -eq 0) { $range += "^$UpstreamRef" }

$log = (git -C $root log --no-merges --format='%s%n%b%x1e' @range) -join "`n"
foreach ($message in ($log -split [char]0x1e)) {
    $message = $message.Trim()
    if (-not $message) { continue }
    $subject = ($message -split "`n")[0].Trim()

    if ($subject -match '^[a-zA-Z]+(\([^)]*\))?!:' -or $message -match '(?m)^BREAKING[ -]CHANGE') {
        if ($level -lt 3) { $reasons.Add("breaking change: $subject") }
        $level = 3
    }
    elseif ($subject -match '^feat(\([^)]*\))?:') {
        if ($level -lt 2) { $reasons.Add("new feature: $subject") }
        $level = [Math]::Max($level, 2)
    }
}

$next = switch ($level) {
    3 { [version]::new($latest.Major + 1, 0, 0) }
    2 { [version]::new($latest.Major, $latest.Minor + 1, 0) }
    default { [version]::new($latest.Major, $latest.Minor, $latest.Build + 1) }
}
$bump = @{ 1 = 'patch'; 2 = 'minor'; 3 = 'major' }[$level]

if ($reasons.Count -eq 0) {
    $reasons.Add("$changes commit(s) without a new FluentFlyout version")
}

return New-Result $next $bump ($reasons -join '; ')
