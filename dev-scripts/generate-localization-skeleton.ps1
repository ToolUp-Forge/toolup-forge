#Requires -Version 7.0
# Phase 767 — regenerate the translation skeleton for `MessageCatalog`:
#
#   docs/platform/message-catalog-skeleton.fs    every catalog leaf, keyed by
#                                                section and field, with its
#                                                English text in a comment —
#                                                the file a consumer copies to
#                                                author a second language
#
# It is a projection of `MessageCatalog.english` (src/ToolUp.Platform.Client/
# Client/MessageCatalog.fs), rendered by the golden-file test in
# `LocalizationTests` (the platform test pack). This script drives that test in
# regeneration mode: with TOOLUP_REGEN_LOCALIZATION_SKELETON=1 set it writes the
# file instead of comparing. Run it after adding, removing or rewording a catalog
# field, then commit the updated skeleton alongside the catalog change.
#
# The skeleton is also COMPILED into the test project, so a field the catalog
# has renamed or removed fails the build here, naming the field. If that is the
# failure you are looking at, delete docs/platform/message-catalog-skeleton.fs
# and run this script again — the fsproj includes the file only when it exists,
# so the build goes through and the regenerated file will not set the vanished
# field.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# The regen env var only changes the one golden-file test (it writes its
# artefact instead of comparing); every other test in the pack runs normally.
$env:TOOLUP_REGEN_LOCALIZATION_SKELETON = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list "translation skeleton"
}
finally {
    Remove-Item Env:TOOLUP_REGEN_LOCALIZATION_SKELETON -ErrorAction SilentlyContinue
}

Write-Host "Regenerated docs/platform/message-catalog-skeleton.fs" -ForegroundColor Green
