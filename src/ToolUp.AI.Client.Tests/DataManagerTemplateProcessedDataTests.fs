// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.DataManagerTemplateProcessedDataTests

// ─── Phase 901 — the data-manager template publishes processed data ───
//
// `templates/platformsdk-datamanager/MyDataManager` fills the shell's
// data-manager slot (docs/platform/modules.md, "Replacing a built-in —
// shell slots") when a deployment wires `Slots.DataManager =
// SlotFill.External(MyDataManager.ClientView.register ())`. That
// section's contract, clause 4: once the slot is filled, every OTHER
// data module sees only what the filling module provides via
// `ClientModule.withProcessedData` — so a data-manager replacement that
// omits it leaves the rest of the shell blind the moment it mounts.
// Phase 879 wrote that contract and left a comment on the template
// naming the gap; this phase closes the gap and gates it here.
//
// This drives the template's REAL `init` / `update` / `register()`
// through `ModuleHarness`, exactly as `ModuleViewA11yTests` does for
// `UsageDashboard`, and for the same reason recorded there:
// `ClientModel.fs` holds a module-level `Api.makeProxy` (reflection-
// shaped) that raises at static-init time under plain .NET reflection,
// and `register()`'s `Icon` field builds a real Feliz `ReactElement`
// via `createElement` — `jsNative` under plain .NET — so this pack, not
// `ToolUp.Platform.Tests`, is where a template with a real UI can be
// driven end to end. `VerifyTemplates` (Build.fs) already proves the
// template's `Server.fs` / `SharedTypes.fs` compile against a packed
// SDK; this pack is the client-tier counterpart `templates/README.md`'s
// "the result builds ... Fable" line asks for, and it runs as part of
// the same `VerifyFable` / `fable-tier` CI job every other case in this
// project already gates on — no separate CI wiring needed.
//
// The template's three files are compiled in directly from
// `templates/platformsdk-datamanager/MyDataManager/` (see the
// `.fsproj`), not copied, so this test reads whatever a real
// `dotnet new platformsdk-datamanager` scaffold ships —
// `templates/README.md`'s "Programmatic invocation" acceptance bar
// (byte-identical `dotnet new` vs. direct compilation, since the
// template carries no scripted post-action) is what makes that a
// faithful instantiation rather than a stand-in.
//
// Go-red proof (recorded here rather than reproduced as a second test):
// before this phase, `ClientView.register ()` never called
// `ClientModule.withProcessedData`, so `erased.ProvidesProcessedData`
// was `None` and the first case below failed with exactly the message
// it asserts. Removing the `|> ClientModule.withProcessedData _.Processed`
// line from `ClientView.fs` reproduces that red; restoring it (and
// keeping `ClientModel.fs`'s `Processed` field / `IngestSucceeded`
// handling) is what turns this pack green again.

open ProcessedDataTypes
open ToolUp.Platform.Testing.ModuleHarness
open ToolUp.AI.Client.Tests.NodeTest
open MyDataManager.SharedTypes

/// The `ProcessedFileEntry` a real server's `Ingest` would return —
/// built the way `Server.fs` builds one (`ProcessedFileEntry.summarised`
/// over a `ProcessedDataCodec.encode`-shaped envelope), so this test
/// exercises the same client-side contract a live round trip would.
let private sampleProcessed: ProcessedFileEntry =
    ProcessedFileEntry.summarised "orders.csv" "MyDataManager" System.DateTime.UtcNow {
        TypeName = "Gate.IngestResult"
        Payload = """{"DatasetId":"orders.csv::csv","RowCount":42}"""
    }

let private sampleResponse: IngestResponse = {
    Result = {
        DatasetId = "orders.csv::csv"
        RowCount = 42
    }
    Processed = sampleProcessed
}

let tests =
    testList "DataManagerTemplateProcessedData" [
        testCase "register() declares ClientModule.withProcessedData" (fun () ->
            let erased = MyDataManager.ClientView.register ()

            Expect.isTrue
                erased.ProvidesProcessedData.IsSome
                "MyDataManager.ClientView.register() does not declare ClientModule.withProcessedData \
                 — once this module fills the data-manager shell slot (SlotFill.External), every \
                 other data module sees NOTHING, because the shell aggregates processed data only \
                 from modules that declare the extractor (docs/platform/modules.md, \"Replacing a \
                 built-in — shell slots\", the data-manager slot contract).")

        testCase "the extractor publishes what IngestSucceeded recorded" (fun () ->
            let harness =
                (fromUnitInit MyDataManager.ClientModel.init MyDataManager.ClientModel.update)
                    .Dispatch(MyDataManager.ClientModel.Msg.IngestSucceeded sampleResponse)

            let erased = MyDataManager.ClientView.register ()

            match erased.ProvidesProcessedData with
            | None -> failwith "unreachable — covered by the case above"
            | Some getter ->
                let entries = getter (box harness.Model)

                Expect.isFalse
                    (List.isEmpty entries)
                    "MyDataManager declares withProcessedData but it returned no entries after \
                     IngestSucceeded — the extractor is not reading the field IngestSucceeded \
                     populates."

                Expect.isTrue
                    (List.contains sampleProcessed entries)
                    "MyDataManager published entries that do not match the ProcessedFileEntry the \
                     (simulated) server returned from Ingest — the client must carry the server's \
                     envelope through unchanged (ProcessedDataCodec.encode is a server-tier concern; \
                     the shared/client tier carries no JSON codec of its own).")
    ]