#Requires -Version 7.0
# Phase 214 / 697 / 765 — regenerate EVERY projection of the central config-key
# registry (`ConfigKeys.all` in
# src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs):
#
#   docs/reference/config-reference.md          every key, for a reader
#   docs/reference/toolup.config.schema.json    the manifest-bindable subset,
#                                               for an editor
#   docs/operations/env-vars.md                 the Description COLUMN of the
#                                               operations tables, for an
#                                               operator (Phase 765 — the page
#                                               itself stays hand-authored)
#
# All three are projections of one registry and all three ride one flag, because
# refreshing some and not the others would leave the rest lying — which is
# exactly what happened to the operations page before it was wired in here. This
# script drives the golden-file tests in regeneration mode: with
# TOOLUP_REGEN_CONFIG_REFERENCE=1 set they write their artefact instead of
# comparing. Run it after adding / changing a ConfigKeyDescriptor, then commit
# the updated artefacts alongside the registry change.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# The regen env var only changes the three golden-file tests (they write their
# artefact instead of comparing); every other test in the pack runs normally.
$env:TOOLUP_REGEN_CONFIG_REFERENCE = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list ConfigReference
}
finally {
    Remove-Item Env:TOOLUP_REGEN_CONFIG_REFERENCE -ErrorAction SilentlyContinue
}

Write-Host "Regenerated docs/reference/config-reference.md, docs/reference/toolup.config.schema.json and the Description column of docs/operations/env-vars.md" -ForegroundColor Green
