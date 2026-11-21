#!/usr/bin/env pwsh
# CI's one way to find the Windows C and C++ compiler (.github/workflows/build.yml: the windows job's native test doubles, the
# installer job's libvmx, which the installer ships). It is Visual Studio's own clang, the C++ Clang compiler component of the
# newest Visual Studio that has it (vswhere -requires Microsoft.VisualStudio.Component.VC.Llvm.Clang), at VC\Tools\Llvm\x64\bin:
# Visual Studio ships the clang its own standard library accepts (the STL's STL1000 check names the clang version it expects),
# so a runner image whose own LLVM is older than that STL cannot hand it the wrong compiler. Never a clang on the PATH, and never
# a fallback to one: where the component or either binary is absent this fails with words that name the component and the image.
# Prints the clang's version, the Visual Studio version and the MSVC toolset the clang compiles against, writes them as one line
# to the job's step summary, and returns an object with the C (clang.exe) and C++ (clang++.exe) compilers' full paths.
#   $clang = ./tools/ci-clang.ps1 -Label 'The native test doubles'   # then $clang.C and $clang.Cxx
param(
    [Parameter(Mandatory)] [string] $Label
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$component = 'Microsoft.VisualStudio.Component.VC.Llvm.Clang'
$image = "runner image $env:ImageOS $env:ImageVersion".Trim()

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw "no vswhere.exe at $vswhere on the ${image}: no Visual Studio to take $component from" }
$vs = "$(& $vswhere -nologo -latest -products '*' -requires $component -property installationPath)".Trim()
if ($LASTEXITCODE -ne 0) { throw "vswhere.exe failed ($LASTEXITCODE) asking for a Visual Studio with $component on the $image" }
if (-not $vs) { throw "no Visual Studio on the $image has $component (C++ Clang compiler for Windows): the libvmx and native test double builds need it, and the PATH's LLVM is never taken in its place" }
$vsVersion = "$(& $vswhere -nologo -latest -products '*' -requires $component -property installationVersion)".Trim()
if ($LASTEXITCODE -ne 0 -or -not $vsVersion) { throw "vswhere.exe gave no installationVersion for the Visual Studio at $vs on the $image" }

$bin = Join-Path $vs 'VC\Tools\Llvm\x64\bin'
$c = Join-Path $bin 'clang.exe'
$cxx = Join-Path $bin 'clang++.exe'
foreach ($binary in $c, $cxx) {
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Visual Studio $vsVersion at $vs lists $component but has no $binary on the $image" }
}

$said = @(& $cxx --version)
if ($LASTEXITCODE -ne 0 -or $said.Count -eq 0) { throw "$cxx --version failed ($LASTEXITCODE) on the $image" }
$said | ForEach-Object { Write-Host $_ }
$clangVersion = "$($said[0])".Trim()

# The MSVC toolset (headers, the STL and libraries) the clang compiles against, in the order clang's MSVC driver looks for one
# when no option names it: a toolset the environment names (VCToolsInstallDir), a cl.exe on the PATH, else the Visual Studio
# instance's default toolset (VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt).
$tools = Join-Path $vs 'VC\Tools\MSVC'
$cl = Get-Command cl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($env:VCToolsInstallDir) {
    $toolset = "MSVC $(Split-Path ($env:VCToolsInstallDir.TrimEnd('\', '/')) -Leaf) (named by VCToolsInstallDir)"
} elseif ($cl) {
    $toolset = "MSVC $(Split-Path (Split-Path (Split-Path (Split-Path (Split-Path $cl.Source -Parent) -Parent) -Parent) -Parent) -Leaf) (the cl.exe on the PATH, $($cl.Source))"
} else {
    $default = Join-Path $vs 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
    $named = if (Test-Path -LiteralPath $default -PathType Leaf) { "$(Get-Content -LiteralPath $default -TotalCount 1)".Trim() } else { '' }
    if ($named -and (Test-Path -LiteralPath (Join-Path $tools $named) -PathType Container)) {
        $toolset = "MSVC $named (the instance's default toolset)"
    } else {
        $newest = Get-ChildItem -LiteralPath $tools -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^\d+(\.\d+){1,3}$' } | Sort-Object { [version]$_.Name } | Select-Object -Last 1
        if (-not $newest) { throw "Visual Studio $vsVersion at $vs has no MSVC toolset under $tools on the $image" }
        $toolset = "MSVC $($newest.Name) (the newest under VC\Tools\MSVC; the instance names no default)"
    }
}

$words = "${Label}: $clangVersion ($cxx), Visual Studio $vsVersion ($vs), $toolset, $image"
Write-Host $words
# The summary is a record beside the log, which already carries the line: a summary that cannot be written warns, never fails the
# step once the compiler is found.
if ($env:GITHUB_STEP_SUMMARY) {
    try { "- $words" | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8NoBOM }
    catch { Write-Host "::warning::the step summary could not be written: $($_.Exception.Message)" }
}
[pscustomobject]@{ C = $c; Cxx = $cxx }
