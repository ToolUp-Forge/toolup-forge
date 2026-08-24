#Requires -Version 7.0
# Phase 214 / 697 — regenerate BOTH projections of the central config-key
# registry (`ConfigKeys.all` in
# src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs):
#
#   docs/reference/config-reference.md          every key, for a reader
#   docs/reference/toolup.config.schema.json    the manifest-bindable subset,
#                                               for an editor
#
# Both are GENERATED, never hand-edited, and both ride one flag: they read one
# registry, so refreshing only one would leave the other lying. This script
# drives the golden-file tests in regeneration mode — with
# TOOLUP_REGEN_CONFIG_REFERENCE=1 set they write their file instead of
# comparing. Run it after adding / changing a ConfigKeyDescriptor, then commit
# the updated artefacts alongside the registry change.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# The regen env var only changes the two golden-file tests (they write their
# artefact instead of comparing); every other test in the pack runs normally.
$env:TOOLUP_REGEN_CONFIG_REFERENCE = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list ConfigReference
}
finally {
    Remove-Item Env:TOOLUP_REGEN_CONFIG_REFERENCE -ErrorAction SilentlyContinue
}

Write-Host "Regenerated docs/reference/config-reference.md and docs/reference/toolup.config.schema.json" -ForegroundColor Green
