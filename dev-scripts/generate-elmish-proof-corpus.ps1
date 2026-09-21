#Requires -Version 7.0
# Phase 788 — regenerate the Elmish proof corpus:
#
#   tests/elmish-proof-corpus/ring-cases.txt    generated push/pop sequences and
#                                               the outputs the PROVED ring model
#                                               produced for them
#   tests/elmish-proof-corpus/diff-cases.txt    generated subscription inputs and
#                                               the four lists (+ the keys `change`
#                                               leaves active) the PROVED diff
#                                               model produced for them
#
# Both are projections of the extracted models under proofs/oracle/ over the
# seeded campaign in src/ToolUp.Platform.Tests/Client/ElmishProofDifferential.fs.
# The .NET pack (ToolUp.Platform.Tests, "Phase 788 - the proved Elmish runtime as
# oracle") runs the models LIVE beside production and holds these files to the
# model on every run; the Fable pack (ToolUp.AI.Client.Tests) replays them
# against the transpiled runtime, because the extraction cannot compile under
# Fable (its layout needs the strict-indentation opt-out, which Fable does not
# read from an fsproj). This script drives the two golden-file tests in
# regeneration mode: with TOOLUP_REGEN_ELMISH_PROOF_CORPUS=1 set they write their
# artefact instead of comparing. Run it after changing a model, the generator
# or the seed, then commit the updated corpus alongside that change.

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot/..

$env:TOOLUP_REGEN_ELMISH_PROOF_CORPUS = "1"
try {
    dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj `
        -- --filter-test-list "Phase 788"
}
finally {
    Remove-Item Env:TOOLUP_REGEN_ELMISH_PROOF_CORPUS -ErrorAction SilentlyContinue
}

Write-Host "Regenerated tests/elmish-proof-corpus/ring-cases.txt and diff-cases.txt" -ForegroundColor Green
