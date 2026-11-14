#Requires -Version 5.1
<#
.SYNOPSIS
    Checks or updates the per-RID NuGet lock files of the production projects.

.DESCRIPTION
    Every project under src/ commits packages.lock.json plus packages.<rid>.lock.json for
    win-x64, linux-x64 and osx-arm64 (Directory.Build.props selects the RID-specific file).
    A dependency change has to be restored for every RID, not only the one this machine runs,
    or the packaging job discovers the drift hours later in locked mode.

    -Check restores every project/RID combination with --locked-mode and fails on any
    difference; it never writes a lock file. Without -Check the script restores with
    --force-evaluate, which rewrites the lock files. Use it only after changing a dependency,
    then review and commit the result together with THIRD-PARTY-NOTICES.md.

.PARAMETER Check
    Verify only. Exits non-zero when a lock file is missing or out of date.

.EXAMPLE
    pwsh -File build/tools/Update-LockFiles.ps1 -Check

.EXAMPLE
    pwsh -File build/tools/Update-LockFiles.ps1
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
$sourceDirectory = Join-Path $root 'src'
$rids = @('win-x64', 'linux-x64', 'osx-arm64')
$projects = @(Get-ChildItem -Path $sourceDirectory -Filter '*.csproj' -Recurse -File | Sort-Object FullName)

if ($projects.Count -eq 0) {
    [Console]::Error.WriteLine("No projects found under $sourceDirectory.")
    exit 2
}

$restoreArguments = @()
if ($Check) {
    $restoreArguments += '--locked-mode'
}
else {
    $restoreArguments += '--force-evaluate'
}
$failures = @()

foreach ($project in $projects) {
    $relative = $project.FullName.Substring($root.Length).TrimStart('\', '/')
    foreach ($rid in $rids) {
        # A global RuntimeIdentifier property, not --runtime: only the property is visible when
        # Directory.Build.props chooses NuGetLockFilePath, so --runtime would check packages.lock.json
        # (the non-RID file) and report a runtime-identifier change.
        $output = & dotnet restore $project.FullName "-p:RuntimeIdentifier=$rid" @restoreArguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $failures += "$relative ($rid)"
            Write-Host ""
            Write-Host "--- $relative ($rid) ---"
            $output | ForEach-Object { Write-Host $_ }
        }
    }
}

if ($failures.Count -gt 0) {
    $mode = if ($Check) { 'out of date' } else { 'could not be restored' }
    [Console]::Error.WriteLine(("{0} lock file(s) {1}:" -f $failures.Count, $mode))
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("  $failure")
    }

    if ($Check) {
        [Console]::Error.WriteLine("Run build/tools/Update-LockFiles.ps1 after changing a dependency, review the lock files, and commit them.")
    }

    exit 1
}

if ($Check) {
    Write-Host ("All {0} project/RID lock files are current." -f ($projects.Count * $rids.Count))
}
else {
    Write-Host ("Updated lock files for {0} projects across {1} RIDs." -f $projects.Count, $rids.Count)
}
