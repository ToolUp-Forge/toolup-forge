#Requires -Version 7.0
# Retired: regenerating docs/reference/audit-event-reference.md is `dev-scripts/regen.ps1 audit-event-reference`, which reads the
# row from projections.json. This name forwards there until 0.43.0, the same window
# the retired TOOLUP_REGEN_* switches are read for.
Write-Host "generate-*.ps1 is retired: running dev-scripts/regen.ps1 audit-event-reference" -ForegroundColor Yellow
& "$PSScriptRoot/regen.ps1" audit-event-reference
exit $LASTEXITCODE
