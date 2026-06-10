module ToolUp.Platform.Tests.Contracts.IPeerNonFSharpSdkContract

open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform

// ─── Phase 18e — non-F# peer SDK (schema + generators) contract pack ─
//
// Exercises Stage 1 (schema export — `PeerSchema.fromContract`) and the
// Stage 2 / 3 generators (`TypeScriptClientGen` / `PythonClientGen`):
//   • the neutral type vocabulary (primitives / option / list / record /
//     long-running unwrap),
//   • record flattening into the schema's Records table,
//   • JSON round-trip of the schema (golden-file-style),
//   • TypeScript + Python emit-correctness (the generated source contains
//     the expected interfaces / dataclasses / class + method signatures +
//     the JSON-RPC POST skeleton).
//
// Live cross-runtime execution (running a generated client against a real
// host under Node / Python) is the 18e.tail round-trip harness.

// Non-private contract types (reflection rejects private records).
type ReachRequest = { Segment: string; MinK: int }
type ReachResult = { Reach: int; Suppressed: bool }

type SchemaContract = {
    Echo: string -> Async<string>
    GetReach: ReachRequest -> Async<ReachResult>
    Tags: string -> Async<string list>
    MaybeName: string -> Async<string option>
    BuildReport: ReachRequest -> Async<PeerJobHandle<ReachResult>>
}

let private schema = PeerSchema.fromContract<SchemaContract> "schemaC"

let private methodNamed (name: string) =
    schema.Methods |> List.find (fun m -> m.Name = name)

let tests =
    testList "IPeerNonFSharpSdkContract" [

        // ─── Stage 1 — schema export ──────────────────────────────

        testCase "schema captures every method with the right id + count"
        <| fun _ ->
            Expect.equal schema.ContractId "schemaC" "the contract id is carried"
            Expect.equal schema.SchemaVersion PeerSchema.formatVersion "the schema-format version is stamped"
            Expect.equal schema.Methods.Length 5 "all five methods are described"

        testCase "primitive arg + return types map into the neutral vocabulary"
        <| fun _ ->
            let echo = methodNamed "Echo"
            Expect.equal echo.Arguments [ PrimString ] "Echo takes one string"
            Expect.equal echo.Returns PrimString "Echo returns a string"
            Expect.equal echo.Lifetime ImmediateMethod "Echo is immediate"

        testCase "list + option returns map to ListOf / OptionOf"
        <| fun _ ->
            Expect.equal (methodNamed "Tags").Returns (ListOf PrimString) "Tags returns a string list"
            Expect.equal (methodNamed "MaybeName").Returns (OptionOf PrimString) "MaybeName returns an optional string"

        testCase "record arg + return become RecordRefs and are flattened into Records"
        <| fun _ ->
            let getReach = methodNamed "GetReach"
            Expect.equal getReach.Arguments [ RecordRef "ReachRequest" ] "the arg references the request record"
            Expect.equal getReach.Returns (RecordRef "ReachResult") "the return references the result record"

            let req = schema.Records |> List.find (fun r -> r.Name = "ReachRequest")
            Expect.equal req.Fields [ "Segment", PrimString; "MinK", PrimInt ] "ReachRequest fields are captured"

            let res = schema.Records |> List.find (fun r -> r.Name = "ReachResult")
            Expect.equal res.Fields [ "Reach", PrimInt; "Suppressed", PrimBool ] "ReachResult fields are captured"

        testCase "a long-running method unwraps PeerJobHandle and is marked LongRunning"
        <| fun _ ->
            let report = methodNamed "BuildReport"
            Expect.equal report.Returns (RecordRef "ReachResult") "the handle's inner type is unwrapped"
            Expect.equal report.Lifetime LongRunningMethod "BuildReport is long-running"

        testCase "schema JSON round-trips (golden-file-style stability)"
        <| fun _ ->
            let json = PeerSchema.toJson schema
            let back = JsonRpc.deserialize<PeerContractSchema> json
            Expect.equal back schema "the schema survives a JSON round-trip unchanged"

        // ─── Stage 2 — TypeScript generator ───────────────────────

        testCase "TypeScript emit declares an interface per record"
        <| fun _ ->
            let ts = TypeScriptClientGen.emit schema
            Expect.stringContains ts "export interface ReachRequest {" "a record becomes an interface"
            Expect.stringContains ts "Segment: string;" "string field maps to string"
            Expect.stringContains ts "MinK: number;" "int field maps to number"
            Expect.stringContains ts "Suppressed: boolean;" "bool field maps to boolean"

        testCase "TypeScript emit declares the client class + typed method signatures"
        <| fun _ ->
            let ts = TypeScriptClientGen.emit schema
            Expect.stringContains ts "export class SchemaCClient {" "the class is named from the contract id"
            Expect.stringContains ts "async Echo(arg0: string): Promise<string>" "Echo has a typed signature"

            Expect.stringContains
                ts
                "async GetReach(arg0: ReachRequest): Promise<ReachResult>"
                "record types thread through"

            Expect.stringContains ts "async Tags(arg0: string): Promise<string[]>" "list maps to []"
            Expect.stringContains ts "async MaybeName(arg0: string): Promise<string | null>" "option maps to | null"

        testCase "TypeScript emit wires the JSON-RPC POST + bearer auth + long-running poll"
        <| fun _ ->
            let ts = TypeScriptClientGen.emit schema
            Expect.stringContains ts "/peer/v1/schemaC" "the contract route is templated"
            Expect.stringContains ts "Bearer ${this.token}" "the bearer token is sent"
            Expect.stringContains ts "async pollBuildReport(jobId: string)" "a long-running method emits a poll helper"

        // ─── Stage 3 — Python generator ───────────────────────────

        testCase "Python emit declares a dataclass per record"
        <| fun _ ->
            let py = PythonClientGen.emit schema
            Expect.stringContains py "@dataclass" "records use dataclasses"
            Expect.stringContains py "class ReachRequest:" "a record becomes a class"
            Expect.stringContains py "Segment: str" "string field maps to str"
            Expect.stringContains py "MinK: int" "int field maps to int"

        testCase "Python emit declares the client class + typed methods + stdlib transport"
        <| fun _ ->
            let py = PythonClientGen.emit schema
            Expect.stringContains py "class SchemaCClient:" "the client class is named from the contract id"
            Expect.stringContains py "def Echo(self, arg0: str) -> str:" "Echo has a typed signature"
            Expect.stringContains py "Optional[str]" "option maps to Optional"
            Expect.stringContains py "List[str]" "list maps to List"
            Expect.stringContains py "import urllib.request" "the transport is dependency-free stdlib"

            Expect.stringContains
                py
                "def poll_BuildReport(self, job_id: str)"
                "a long-running method emits a poll helper"
    ]