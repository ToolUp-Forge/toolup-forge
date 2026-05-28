<#
.SYNOPSIS
  Pre-validate ports against the workspace, then invoke `dotnet new
  platformsdk-solution` or `platformsdk-application`.

.DESCRIPTION
  The `dotnet new` template engine has no native filesystem scan-and-
  reject. This wrapper:

    1. Runs tools/ToolUp.PortGuard against -PortServer / -PortClientVite.
    2. If a clash is detected, the script exits non-zero BEFORE invoking
       `dotnet new` — so the acceptance criterion ("errors out before
       writing any files") is satisfied.
    3. Otherwise it forwards arguments to `dotnet new`.

.EXAMPLE
  pwsh New-ToolUpApp.ps1 -Template platformsdk-solution -Name MyTestApp `
      -PortServer 5010 -PortClientVite 8100

.EXAMPLE
  pwsh New-ToolUpApp.ps1 -Template platformsdk-application -Name MyTestApp2 `
      -PortServer 5020 -PortClientVite 8110 -Output src
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [ValidateSet("platformsdk-solution","platformsdk-application","platformsdk-module","platformsdk-datamanager")]
    [string] $Template,

    [Parameter(Mandatory=$true)]
    [Alias("n")]
    [string] $Name,

    [Parameter(Mandatory=$false)]
    [int] $PortServer = 0,

    [Parameter(Mandatory=$false)]
    [int] $PortClientVite = 0,

    [Parameter(Mandatory=$false)]
    [string] $Output = ".",

    [Parameter(Mandatory=$false)]
    [string] $App = "",

    [Parameter(Mandatory=$false)]
    [string] $Source = ""
)

$ErrorActionPreference = "Stop"

$forgeRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $forgeRoot
$portGuardProject = Join-Path $forgeRoot "tools/ToolUp.PortGuard"

if ($Template -in @("platformsdk-solution","platformsdk-application")) {
    if ($Template -eq "platformsdk-application" -and $PortServer -eq 0) {
        Write-Error "platformsdk-application requires -PortServer."
        exit 2
    }

    $guardArgs = @()
    if ($PortServer -ne 0) { $guardArgs += @("--server-port", $PortServer) }
    if ($PortClientVite -ne 0) { $guardArgs += @("--client-port", $PortClientVite) }
    $guardArgs += @("--workspace-root", $workspaceRoot)

    if ($guardArgs.Count -gt 2) {
        Write-Host "ToolUp.PortGuard: validating ports against $workspaceRoot ..."
        & dotnet run --project $portGuardProject -- @guardArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Port clash detected. No files were written." -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }
}

$dotnetArgs = @("new", $Template, "-n", $Name, "-o", $Output)
if ($PortServer -ne 0)     { $dotnetArgs += @("--port-server", "$PortServer") }
if ($PortClientVite -ne 0) { $dotnetArgs += @("--port-client-vite", "$PortClientVite") }
if ($App -ne "")           { $dotnetArgs += @("--app", $App) }
if ($Source -ne "")        { $dotnetArgs += @("--source", $Source) }

Write-Host "Invoking: dotnet $($dotnetArgs -join ' ')"
& dotnet @dotnetArgs
exit $LASTEXITCODE
