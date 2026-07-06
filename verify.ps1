# ToolUp Forge — verify gate entry point.
#
# The repo's quality gates as *gates*: format-check + SPDX headers + full-solution build + the eight
# Expecto test packs, non-zero exit on any violation. Mirrors the CI checks (Fantomas + SPDX headers)
# plus the local non-breakage gate documented in CLAUDE.md "Build pipeline" (a full
# `dotnet build ToolUp.Forge.sln` + `dotnet run --project Build.fsproj -- VerifyAll`). Never mutates
# the tree: Fantomas runs in --check mode (`dotnet run -- Format` is the formatter).
#
# The AIProviders pack is env-gated — per-provider arms report Pending (not Failed) when their API-key
# env var is unset, so a fresh checkout is green without credentials.

#Requires -Version 7.0
[CmdletBinding()]
param(
    # Skip the Fantomas --check + SPDX-header passes (build + tests only) — for fast iteration.
    [switch] $SkipFormatCheck
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# Machine quirk: dotnet may not be on the default shell PATH.
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    $candidates = @('C:\Program Files\dotnet\x64', 'C:\Program Files\dotnet')
    $found = $candidates | Where-Object { Test-Path (Join-Path $_ 'dotnet.exe') } | Select-Object -First 1
    if ($null -eq $found) {
        Write-Error 'verify: dotnet not found on PATH or under C:\Program Files\dotnet'
        exit 1
    }
    $env:PATH = "$found;$env:PATH"
}

# The workspace-shared local feed at ../../local-nuget-feed is referenced by nuget.config for
# dev-machine iteration; ensure it exists so NuGet can enumerate it (mirrors the CI workaround).
$feed = Join-Path $PSScriptRoot '..\..\local-nuget-feed'
if (-not (Test-Path $feed)) { New-Item -ItemType Directory -Force -Path $feed | Out-Null }

function Step([string] $name, [scriptblock] $body) {
    Write-Host "== verify: $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "== verify FAILED at: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

if (-not $SkipFormatCheck) {
    Step 'tool restore' { dotnet tool restore }
    Step 'format check (fantomas --check)' { dotnet fantomas --check . }
    Step 'SPDX headers (AddHeaders --check)' { dotnet run --project Build.fsproj -- AddHeaders --check }
}

Step 'build (ToolUp.Forge.sln)' { dotnet build ToolUp.Forge.sln --nologo }
Step 'test suites (VerifyAll — eight Expecto packs)' { dotnet run --project Build.fsproj -- VerifyAll }

Write-Host '== verify: all gates green' -ForegroundColor Green
exit 0
