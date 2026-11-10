#Requires -Version 7.0
# Retired: regenerating docs/platform/message-catalog-skeleton.fs is `dev-scripts/regen.ps1 localization-skeleton`, which reads the
# row from projections.json. This name forwards there until 0.43.0, the same window
# the retired TOOLUP_REGEN_* switches are read for.
Write-Host "generate-*.ps1 is retired: running dev-scripts/regen.ps1 localization-skeleton" -ForegroundColor Yellow
& "$PSScriptRoot/regen.ps1" localization-skeleton
exit $LASTEXITCODE
