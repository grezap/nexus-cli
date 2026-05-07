#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Operator wrapper for nexus-cli — pwsh-native build/test/publish harness.

.DESCRIPTION
  The build host (Windows 11 + pwsh, no GNU make) runs every dotnet command
  through this script so the canonical invocation is
  `pwsh -File scripts\cli.ps1 <verb>`. Mirrors the shape of
  nexus-infra-vmware/scripts/foundation.ps1 +
  nexus-infra-swarm-nomad/scripts/swarm.ps1 (per
  memory/feedback_build_host_pwsh_native.md).

  AOT publish on Windows requires MSVC link.exe + the Windows SDK on PATH.
  The publish/cycle verbs source vsdevcmd.bat from a discovered Visual
  Studio install before invoking dotnet publish. CI runners
  (windows-2022 GHA) ship with the dev environment pre-on-path, so this
  shim is a no-op there.

.PARAMETER Verb
  build       -- dotnet build -c Release (no AOT)
  test        -- dotnet test --no-restore -c Release
  publish     -- dotnet publish src/Nexus.Cli (Native AOT) for one or both RIDs
  size-check  -- assert each published binary <= 25 MB (master plan exit gate)
  lint        -- dotnet format --verify-no-changes
  clean       -- remove bin/, obj/, artifacts/ across the repo
  cycle       -- clean -> build -> test -> publish -> size-check (halts on failure)

.PARAMETER Rid
  win-x64     -- Windows native AOT only
  linux-x64   -- Linux native AOT only (cross-compile from Windows is unsupported;
                 use this on a Linux runner / WSL)
  all         -- both (default for publish/cycle on the native runner)

.EXAMPLE
  pwsh -File scripts\cli.ps1 cycle

.EXAMPLE
  pwsh -File scripts\cli.ps1 publish -Rid win-x64

.EXAMPLE
  pwsh -File scripts\cli.ps1 size-check -Rid win-x64

.NOTES
  See docs/adr/ for ADRs covering AOT cadence and 25 MB exit gate.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('build', 'test', 'publish', 'size-check', 'lint', 'clean', 'cycle')]
    [string]$Verb,

    [ValidateSet('win-x64', 'linux-x64', 'all')]
    [string]$Rid = 'win-x64',

    [int]$MaxSizeMB = 25
)

$ErrorActionPreference = 'Stop'

$repoRoot      = Split-Path -Parent $PSScriptRoot
$publishProj   = Join-Path $repoRoot 'src\Nexus.Cli\Nexus.Cli.csproj'
$artifactsRoot = Join-Path $repoRoot 'artifacts'

function Write-Step([string]$title) {
    Write-Host ''
    Write-Host "=== $title ===" -ForegroundColor Cyan
}

function Get-Rids {
    param([string]$Rid)
    switch ($Rid) {
        'all'       { return @('linux-x64', 'win-x64') }
        default     { return @($Rid) }
    }
}

function Initialize-MsvcEnvironment {
    # No-op on Linux (no MSVC) and inside an already-active Developer pwsh.
    if ($IsLinux -or $env:VSCMD_ARG_TGT_ARCH) { return }

    # AOT publish on Windows shells out to a script that calls vswhere.exe + link.exe.
    # Both must be on PATH for the duration of `dotnet publish`. We always re-source
    # vsdevcmd (cheap, ~200ms) rather than trying to detect partial dev envs.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        $vswhere = Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe'
    }
    if (-not (Test-Path $vswhere)) {
        throw 'vswhere.exe not found; install Visual Studio (any edition) + the C++ x64/x86 workload, or run from a Developer pwsh.'
    }

    # Prepend the Installer dir so vswhere is reachable even when MSBuild children
    # don't inherit the full vsdevcmd PATH (the AOT linker target relies on this).
    $installerDir = Split-Path -Parent $vswhere
    if ($env:Path -notlike "*$installerDir*") {
        $env:Path = "$installerDir;$env:Path"
    }

    $vsRoot = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vsRoot) {
        throw 'No VS install with the C++ x64/x86 workload was found; AOT publish needs MSVC + Windows SDK.'
    }
    $vsdevcmd = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'
    Write-Step "Sourcing $vsdevcmd"
    $envDump = & cmd.exe /c "`"$vsdevcmd`" -arch=x64 -host_arch=x64 -no_logo && set"
    foreach ($line in $envDump) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item -Path "Env:$($Matches[1])" -Value $Matches[2]
        }
    }
}

function Invoke-Build {
    Write-Step 'dotnet build -c Release'
    & dotnet build (Join-Path $repoRoot 'Nexus.Cli.slnx') -c Release
    if ($LASTEXITCODE) { throw "dotnet build failed ($LASTEXITCODE)" }
}

function Invoke-Test {
    Write-Step 'dotnet test -c Release'
    & dotnet test (Join-Path $repoRoot 'Nexus.Cli.slnx') -c Release --no-restore
    if ($LASTEXITCODE) { throw "dotnet test failed ($LASTEXITCODE)" }
}

function Invoke-Publish {
    Initialize-MsvcEnvironment

    foreach ($r in (Get-Rids $Rid)) {
        if ($r -eq 'linux-x64' -and -not $IsLinux) {
            Write-Warning "Skipping linux-x64 publish from a Windows host (cross-AOT unsupported). Run on Linux/WSL."
            continue
        }
        Write-Step "dotnet publish -r $r"
        $outDir = Join-Path $artifactsRoot $r
        & dotnet publish $publishProj -c Release -r $r -o $outDir
        if ($LASTEXITCODE) { throw "dotnet publish ($r) failed ($LASTEXITCODE)" }
    }
}

function Invoke-SizeCheck {
    foreach ($r in (Get-Rids $Rid)) {
        $outDir = Join-Path $artifactsRoot $r
        if (-not (Test-Path $outDir)) {
            Write-Warning "no artifacts/$r/ found; run publish first."
            continue
        }
        $exe = if ($r -eq 'win-x64') { 'nexus.exe' } else { 'nexus' }
        $path = Join-Path $outDir $exe
        if (-not (Test-Path $path)) {
            throw "expected $exe under $outDir but it's missing"
        }
        $sizeMB = [Math]::Round((Get-Item $path).Length / 1MB, 2)
        $status = if ($sizeMB -le $MaxSizeMB) { 'OK' } else { 'OVER' }
        $color  = if ($status -eq 'OK') { 'Green' } else { 'Red' }
        Write-Host ("{0,-10} {1,8:N2} MB / {2,3} MB max  [{3}]" -f $r, $sizeMB, $MaxSizeMB, $status) -ForegroundColor $color
        if ($status -ne 'OK') {
            throw "size budget exceeded for $r ($sizeMB MB > $MaxSizeMB MB)"
        }
    }
}

function Invoke-Lint {
    Write-Step 'dotnet format --verify-no-changes'
    & dotnet format (Join-Path $repoRoot 'Nexus.Cli.slnx') --verify-no-changes
    if ($LASTEXITCODE) { throw "dotnet format failed ($LASTEXITCODE) — run dotnet format to fix." }
}

function Invoke-Clean {
    Write-Step 'clean bin/, obj/, artifacts/'
    Get-ChildItem -Path $repoRoot -Directory -Recurse -Force `
        | Where-Object { $_.Name -in @('bin','obj') } `
        | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $artifactsRoot) {
        Remove-Item -Recurse -Force $artifactsRoot
    }
}

switch ($Verb) {
    'build'      { Invoke-Build }
    'test'       { Invoke-Build; Invoke-Test }
    'publish'    { Invoke-Publish }
    'size-check' { Invoke-SizeCheck }
    'lint'       { Invoke-Lint }
    'clean'      { Invoke-Clean }
    'cycle'      {
        Invoke-Clean
        Invoke-Build
        Invoke-Test
        Invoke-Publish
        Invoke-SizeCheck
    }
}

Write-Host ''
Write-Host "[ok] $Verb done." -ForegroundColor Green
