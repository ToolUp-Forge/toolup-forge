module ToolUp.Platform.Tests.InProcess.PlatformTestingFrameworkTests

open System.Text
open Elmish
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Testing.Fakes
open ToolUp.Platform.Testing
open ToolUp.Platform.Testing.DataTypeTestKit

// ─── Phase 11a — Module Testing Framework smoke tests ────────────────
//
// Verify the public surface of `ToolUp.Platform.Testing`:
//   1. The shipped fakes (`TestBlobStorage`, `TestEventStore`,
//      `TestSecretStore`) satisfy the same contract packs the SDK's
//      own in-memory implementations do — portability gate.
//   2. `ModuleHarness` exercises a minimal Elmish init/update cycle
//      end to end (Dispatch → AssertModel → Dispatch).
//   3. `DataTypeTestKit` round-trips `Detect` and `Process` against
//      a hand-rolled `DataType` value.
//   4. `ServerHarness.create` wires the default fakes and exposes the
//      API record for direct invocation.

// ─── 1. Contract-test bindings for the shipped fakes ─────────────────

let private blobStorageContract =
    let factory () : IBlobStorage = TestBlobStorage() :> _
    IBlobStorageContract.tests "TestBlobStorage (Platform.Testing)" factory

let private eventStoreContract =
    let factory () : IEventStore = TestEventStore() :> _
    IEventStoreContract.tests "TestEventStore (Platform.Testing)" factory

let private secretStoreContract =
    let factory () : ISecretStore = TestSecretStore() :> _
    ISecretStoreContract.tests "TestSecretStore (Platform.Testing)" factory

// ─── 2. ModuleHarness demonstration ──────────────────────────────────

type private CounterModel = { Count: int }

type private CounterMsg =
    | Increment
    | DecrementBy of int

let private counterInit () : CounterModel * Cmd<CounterMsg> = { Count = 0 }, Cmd.none

let private counterUpdate (msg: CounterMsg) (model: CounterModel) : CounterModel * Cmd<CounterMsg> =
    match msg with
    | Increment -> { model with Count = model.Count + 1 }, Cmd.none
    | DecrementBy n -> { model with Count = model.Count - n }, Cmd.none

let private moduleHarnessTests =
    testList "ModuleHarness — fluent dispatch + assertions" [
        testCase "init seeds the model and Cmd.none"
        <| fun _ ->
            let h = ModuleHarness.fromUnitInit counterInit counterUpdate
            Expect.equal h.Model.Count 0 "init count"
            Expect.isEmpty h.Cmd "init Cmd.none"

        testCase "Dispatch chains through update"
        <| fun _ ->
            let final =
                (ModuleHarness.fromUnitInit counterInit counterUpdate)
                    .Dispatch(Increment)
                    .AssertModel(fun m -> m.Count = 1)
                    .Dispatch(Increment)
                    .Dispatch(DecrementBy 5)
                    .AssertModel(fun m -> m.Count = -3)
                    .AssertNoCmd()

            Expect.equal final.Model.Count -3 "post-chain count"

        testCase "DispatchAll replays a list of messages"
        <| fun _ ->
            let h =
                (ModuleHarness.fromUnitInit counterInit counterUpdate)
                    .DispatchAll([ Increment; Increment; Increment; DecrementBy 1 ])

            Expect.equal h.Model.Count 2 "post-replay count"
    ]

// ─── 3. DataTypeTestKit demonstration ────────────────────────────────

let private sampleDataType: DataType = {
    Info = {
        Id = "SampleCsv"
        DisplayName = "Sample CSV"
        Schema = None
    }
    Id = "SampleCsv"
    Detect =
        fun content -> async {
            let headers = CsvHeaders.parse content
            return headers |> CsvHeaders.containsAll [ "id"; "value" ]
        }
    Process =
        fun (fileName, content) -> async {
            let rowCount =
                content.Split('\n')
                |> Array.filter (fun l -> l.Trim() <> "")
                |> Array.length
                |> fun n -> max 0 (n - 1)

            let processed: ProcessedDataTypes.ProcessedData = {
                TypeName = "SampleCsv"
                Payload = """{"rows":""" + string rowCount + """}"""
            }

            let summary: ProcessedDataTypes.ProcessedFileEntry = {
                FileName = fileName
                DataType = "SampleCsv"
                ProcessedAt = System.DateTime.UtcNow
                Info = Some(box rowCount)
                Error = None
            }

            return processed, summary
        }
}

let private dataTypeTestKitTests =
    testList "DataTypeTestKit — Detect + Process assertions" [
        testCase "expectDetect succeeds on matching headers"
        <| fun _ -> expectDetect sampleDataType "id,value\n1,one\n2,two\n"

        testCase "expectNotDetect succeeds on mismatching headers"
        <| fun _ -> expectNotDetect sampleDataType "foo,bar\n1,2\n"

        testCase "expectProcess predicate sees parsed payload + summary"
        <| fun _ ->
            expectProcess sampleDataType "demo.csv" "id,value\n1,one\n2,two\n3,three\n" (fun (data, summary) ->
                data.TypeName = "SampleCsv"
                && summary.FileName = "demo.csv"
                && summary.DataType = "SampleCsv"
                && summary.Error.IsNone)
    ]

// ─── 4. ServerHarness demonstration ──────────────────────────────────

type private SampleApi = {
    StoreSecret: string -> string -> Async<Result<unit, string>>
    LoadSecret: string -> Async<string option>
}

let private buildSampleApi (fakes: ServerHarness.ServerFakes) : SampleApi = {
    StoreSecret = fun key value -> fakes.SecretStore.SetSecret(fakes.Scope.ScopeId, key, value)
    LoadSecret = fun key -> fakes.SecretStore.GetSecret(fakes.Scope.ScopeId, key)
}

let private serverHarnessTests =
    testList "ServerHarness — fake substrate + API round-trip" [
        testCaseAsync "create wires default fakes and round-trips through API"
        <| async {
            let h = ServerHarness.create buildSampleApi
            let! storeResult = h.Api.StoreSecret "openai_api_key" "sk-test-xyz"
            Expect.equal storeResult (Ok()) "store result"
            let! loaded = h.Api.LoadSecret "openai_api_key"
            Expect.equal loaded (Some "sk-test-xyz") "loaded value"
        }

        testCaseAsync "seedSecret pre-populates the fake before API call"
        <| async {
            let h =
                ServerHarness.create buildSampleApi
                |> fun h -> ServerHarness.seedSecret h h.Fakes.Scope.ScopeId "anthropic_key" "sk-ant-foo"

            let! loaded = h.Api.LoadSecret "anthropic_key"
            Expect.equal loaded (Some "sk-ant-foo") "seeded value"
        }
    ]

let tests =
    testList "ToolUp.Platform.Testing (Phase 11a)" [
        blobStorageContract
        eventStoreContract
        secretStoreContract
        moduleHarnessTests
        dataTypeTestKitTests
        serverHarnessTests
    ]