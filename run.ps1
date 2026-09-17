#Requires -Version 5.1
<#
.SYNOPSIS
  Stop leftover NovaDB processes, build, and start the full local stack (Aspire).

.DESCRIPTION
  Fixes MSB3027/MSB3021 "file is locked by NovaDB.Admin / NovaDB.Server" by
  stopping those processes before build, then runs the Aspire AppHost the same
  way Visual Studio does (Server + Admin + dashboard).

.PARAMETER SkipBuild
  Skip `dotnet build` and go straight to run.

.PARAMETER Configuration
  Build/run configuration. Default: Debug.

.EXAMPLE
  .\run.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$AppHost = Join-Path $Root 'aspire\NovaDB.AppHost\NovaDB.AppHost.csproj'

if (-not (Test-Path $AppHost)) {
    Write-Error "AppHost project not found: $AppHost"
}

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Stop-NovaDbProcesses {
    $names = @(
        'NovaDB.AppHost',
        'NovaDB.Admin',
        'NovaDB.Server'
    )

    $stopped = @()
    foreach ($name in $names) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        foreach ($proc in $procs) {
            Write-Host "  Stopping $name (PID $($proc.Id))..."
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            $stopped += "$name($($proc.Id))"
        }
    }

    if ($stopped.Count -eq 0) {
        Write-Host "  No NovaDB processes were running."
    }
    else {
        # Give Windows a moment to release DLL locks / ports.
        Start-Sleep -Seconds 1
        Write-Host "  Stopped: $($stopped -join ', ')"
    }
}

function Clear-NovaDbPorts {
    $ports = @(6379, 7380, 7381)
    foreach ($port in $ports) {
        $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($listener in $listeners) {
            $pid = $listener.OwningProcess
            if ($pid -and $pid -ne 0) {
                $proc = Get-Process -Id $pid -ErrorAction SilentlyContinue
                $label = if ($proc) { $proc.ProcessName } else { 'unknown' }
                Write-Host "  Freeing port $port (PID $pid / $label)..."
                Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

Write-Host "NovaDB one-shot runner" -ForegroundColor Green
Write-Host "Root: $Root"

Write-Step "Stopping leftover NovaDB processes (unlocks DLLs for build)"
Stop-NovaDbProcesses

Write-Step "Checking NovaDB ports (6379 / 7380 / 7381)"
try {
    Clear-NovaDbPorts
}
catch {
    Write-Host "  Port check skipped (needs admin or Get-NetTCPConnection unavailable)."
}

if (-not $SkipBuild) {
    Write-Step "Building AppHost ($Configuration)"
    dotnet build $AppHost -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
    }
}

Write-Step "Starting Aspire AppHost (Server + Admin + dashboard)"
Write-Host "  RESP:  127.0.0.1:6379"
Write-Host "  HTTP:  127.0.0.1:7380"
Write-Host "  gRPC:  127.0.0.1:7381"
Write-Host "  Admin: login admin / changeme"
Write-Host "  Press Ctrl+C to stop."
Write-Host ""

Set-Location $Root
# Always --no-build here: we either just built, or the caller passed -SkipBuild.
dotnet run --project $AppHost -c $Configuration --no-build
