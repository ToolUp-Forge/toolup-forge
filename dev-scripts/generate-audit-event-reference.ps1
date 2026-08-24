#Requires -Version 7.0
# Regenerate docs/reference/audit-event-reference.md from the
# `AuditEvent` union (src/ToolUp.Platform.Core/Shared/AuditTypes.fs) and the codec
# registry `auditEventCodecs` (src/ToolUp.Platform.Server/Server/AuditLog.fs).
#
# The reference doc is GENERATED, never hand-edited. This script drives the
# golden-file test in regeneration mode: with TOOLUP_REGEN_AUDIT_EVENT_REFERENCE=1
# set, the test writes the file instead of comparing. Run it in the same commit
# that adds, removes or renames an AuditEvent case, and commit the updated doc
# alongside the union change — otherwise the gate fails the build.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

# The regen env var only changes the one golden-file test (it writes the doc
# instead of comparing); every other test in the pack runs normally.
$env:TOOLUP_REGEN_AUDIT_EVENT_REFERENCE = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list "Phase 114"
}
finally {
    Remove-Item Env:TOOLUP_REGEN_AUDIT_EVENT_REFERENCE -ErrorAction SilentlyContinue
}

Write-Host "Regenerated docs/reference/audit-event-reference.md" -ForegroundColor Green
