#Requires -Version 7.0
# Phase 214 — regenerate docs/reference/config-reference.md from the central
# config-key registry (`ConfigKeys.all` in
# src/ToolUp.Platform.Server/Server/ConfigKeyDescriptor.fs).
#
# The reference doc is GENERATED, never hand-edited. This script drives the
# golden-file test in regeneration mode: with TOOLUP_REGEN_CONFIG_REFERENCE=1
# set, the test writes the file from `ReferenceDoc.render` instead of
# comparing. Run it after adding / changing a ConfigKeyDescriptor, then commit
# the updated doc alongside the registry change.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# The regen env var only changes the one golden-file test (it writes the doc
# instead of comparing); every other test in the pack runs normally.
$env:TOOLUP_REGEN_CONFIG_REFERENCE = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list ConfigReference
}
finally {
    Remove-Item Env:TOOLUP_REGEN_CONFIG_REFERENCE -ErrorAction SilentlyContinue
}

Write-Host "Regenerated docs/reference/config-reference.md" -ForegroundColor Green
