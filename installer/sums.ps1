#!/usr/bin/env pwsh
# The SHA256SUMS file of an unsigned build (docs/round3/PLAN.md §3.5 item 6; docs/INSTALL.md says how to check it): one line per
# file under -Folder, "<sha256>  <path relative to the folder, with />", sorted by path, written as <Folder>/SHA256SUMS; with -Also,
# files outside the folder (the installer) are listed by their own name too. The sums show a download is intact, not who built it.
# With -Notice the job's summary carries the unsigned build's notice.
#   installer/sums.ps1 -Folder publish/PlayMap -Notice
#   installer/sums.ps1 -Folder out -Also out/PlayMap-0.2.0.17-win-x64-unsigned.exe
param(
    [Parameter(Mandatory)] [string] $Folder,
    [string[]] $Also = @(),
    [switch] $Notice
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path $Folder).Path
$sums = Join-Path $root 'SHA256SUMS'
if (Test-Path $sums) { Remove-Item $sums }
$lines = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
    [pscustomobject]@{ Path = $_.FullName; Name = [System.IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') }
}
foreach ($a in $Also) {
    $full = (Resolve-Path $a).Path
    if (-not ($files | Where-Object { $_.Path -eq $full })) { $files += [pscustomobject]@{ Path = $full; Name = Split-Path $full -Leaf } }
}
foreach ($f in ($files | Sort-Object { $_.Name } -Culture ([System.Globalization.CultureInfo]::InvariantCulture))) {
    $hash = (Get-FileHash -LiteralPath $f.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines.Add("$hash  $($f.Name)")
}
if ($lines.Count -eq 0) { throw "$Folder holds no file to sum" }
[System.IO.File]::WriteAllText($sums, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "SHA256SUMS: $($lines.Count) files in $root"
if ($Notice -and $env:GITHUB_STEP_SUMMARY) {
    @("> **unsigned: the owner's decision — PLAN.md §9 decision 2.** This build carries no signature: SmartScreen names an unknown publisher, and Smart App Control, once it enforces, blocks it (docs/INSTALL.md). ``SHA256SUMS`` lists $($lines.Count) files; it shows a download is intact, not who built it.", '') |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM
}
