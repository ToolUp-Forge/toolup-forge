#Requires -Version 7.0
# Regenerate EVERY projection of the central config-key registry
# (`ConfigKeys.keys` in src/ToolUp.Platform.Core/Shared/Types/ConfigKeyDescriptor.fs):
#
#   docs/reference/config-reference.md          every key, for a reader
#   docs/reference/toolup.config.schema.json    the manifest-bindable subset,
#                                               for an editor
#   docs/operations/env-vars.md                 the Description COLUMN of the
#                                               operations tables, for an
#                                               operator (the page itself stays
#                                               hand-authored)
#   DEPLOYMENT.md                               the generated region: which keys
#                                               refuse a typo, and what the boot
#                                               seal covers
#
# All of them are projections of one registry and all of them ride one target,
# because refreshing some and not the others would leave the rest lying. The
# work is the `GenerateConfigReference` target in Build.fs; this script is the
# short name for it. Run it after adding / changing a ConfigKey, then commit the
# updated artefacts alongside the registry change.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

dotnet run --project Build.fsproj -- GenerateConfigReference
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
