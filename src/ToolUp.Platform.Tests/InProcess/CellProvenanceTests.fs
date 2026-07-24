module ToolUp.Platform.Tests.InProcess.CellProvenanceTests

// Phase 12d — AG Grid value-provenance overlay. Pins the headless-testable
// surface of `ToolUp.Platform.CellProvenance`: the provenance value types,
// the Enterprise-gated overlay-enabled flag, and the typed click seam. The
// Fable-only parts (the `ProvenanceOverlay` React component, the
// `ColumnDef.provenance` colDef factory, the JS `?` param readers) carry
// no .NET runtime and are exercised by the `samples/MinimalClient` Fable
// transpile + the build type-check, per the `ClientBrandLiftTests`
// precedent.

open Expecto
open ToolUp.Platform.CellProvenance

// Sequenced: the overlay-enabled flag and the click-handler registry are
// process-global module mutables, so these tests must not interleave with
// each other under Expecto's default parallelism.
let tests =
    testSequenced
    <| testList "CellProvenance (Phase 12d)" [
        test "ProvenanceLocation cases + CellProvenance construct and compare by value" {
            let a = {
                SourceLabel = "Q4 revenue"
                SourceLocation = ProvenanceLocation.Computed "sum(monthly)"
                Detail = Some "rolled up from 3 months"
                LinkedEntity = Some("Dataset", "ds-42")
            }

            let b = {
                SourceLabel = "Q4 revenue"
                SourceLocation = ProvenanceLocation.Computed "sum(monthly)"
                Detail = Some "rolled up from 3 months"
                LinkedEntity = Some("Dataset", "ds-42")
            }

            Expect.equal a b "structurally-equal provenance values compare equal"

            Expect.notEqual
                a
                {
                    a with
                        SourceLocation = ProvenanceLocation.InputField "revenue"
                }
                "differing SourceLocation makes them unequal"
        }

        test "DataObject location carries id + version" {
            match (ProvenanceLocation.DataObject("obj-7", 3)) with
            | ProvenanceLocation.DataObject(id, v) ->
                Expect.equal id "obj-7" "object id"
                Expect.equal v 3 "version"
            | other -> failtestf "expected DataObject, got %A" other
        }

        test "overlay is disabled until the Enterprise companion enables it" {
            // Default (no Enterprise companion loaded in this test process):
            // the overlay is a no-op so a Community deployment renders nothing.
            Expect.isFalse (isProvenanceOverlayEnabled ()) "overlay off by default (Community no-op)"
            setProvenanceOverlayEnabled ()
            Expect.isTrue (isProvenanceOverlayEnabled ()) "companion activation flips the flag on"
        }

        test "typed click subscriber receives the clicked provenance; dispose stops it" {
            let received = ResizeArray<CellProvenance>()
            let dispose = subscribeProvenanceClick received.Add

            let prov = {
                SourceLabel = "Margin"
                SourceLocation = ProvenanceLocation.Module "PricingModule"
                Detail = None
                LinkedEntity = Some("LineageRecord", "lr-9")
            }

            publishProvenanceClick prov
            Expect.equal (List.ofSeq received) [ prov ] "handler receives the published provenance"

            dispose ()
            publishProvenanceClick prov
            Expect.equal (received.Count) 1 "disposed handler receives nothing further"
        }

        test "dispose is idempotent and isolates unrelated subscribers" {
            let seenA = ResizeArray<CellProvenance>()
            let seenB = ResizeArray<CellProvenance>()
            let disposeA = subscribeProvenanceClick seenA.Add
            let disposeB = subscribeProvenanceClick seenB.Add

            let prov = {
                SourceLabel = "Units"
                SourceLocation = ProvenanceLocation.InputField "units"
                Detail = None
                LinkedEntity = None
            }

            disposeA ()
            disposeA () // second dispose is a no-op, must not throw
            publishProvenanceClick prov

            Expect.isEmpty (List.ofSeq seenA) "disposed subscriber A saw nothing"
            Expect.equal (List.ofSeq seenB) [ prov ] "live subscriber B still received the event"

            disposeB ()
        }
    ]