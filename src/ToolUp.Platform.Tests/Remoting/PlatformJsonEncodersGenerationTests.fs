// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The platform's own RESPONSE encoders are generated, verified and pinned.
///
/// `src/ToolUp.Platform.Core/Shared/Remoting/Json/PlatformJsonEncoders.fs` is
/// the generator's `json-encoders` emission over every API record
/// `ToolUp.Platform.Core` declares: one JSON encoder per type their methods
/// return, which the server's response writer consults (`Codecs.resolveJsonEncoder`)
/// before System.Text.Json. A registered encoder WINS over the writer, so this
/// pack holds three things, in this order:
///
///   1. **The committed file IS the emission** — the regen switch is
///      `TOOLUP_REGEN_PLATFORM_DECODERS=1`, the one `PlatformDecoders.fs` uses.
///   2. **Every encoder it registers writes the System.Text.Json writer's
///      bytes** over deterministic draws of its own type, through the shipped
///      encoder gate (`JsonEncoders.gateWith FableConverters.decoderOracle`).
///   3. **The gate is known to refuse**: an encoder that swaps two fields is
///      refused by name and draw, and registration through the gated door
///      leaves the table untouched.
module ToolUp.Platform.Tests.Remoting.PlatformJsonEncodersGenerationTests

open System
open System.IO
open System.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Remoting.Generator

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private committedPath () =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Core", "Shared", "Remoting", "Json", "PlatformJsonEncoders.fs")

let private apiRecords = Plan.apiRecordsIn typeof<IHealthMonitorApi>.Assembly

let private roots = apiRecords |> List.collect Plan.returnTypes |> List.distinct

/// What the generator's `json-encoders` command emits for the platform: the
/// CLI's own options over every platform API record.
let private emission () : string =
    Emit.jsonEncoderCompilationUnit
        {
            EncoderNamespace = "ToolUp.Remoting.Json"
            EncoderModuleName = "PlatformJsonEncoders"
            EncoderOpens = Plan.namespacesReachedBy roots
            ReturnRecords =
                apiRecords
                |> List.map (fun r -> r.Name, Plan.returnTypes r |> List.map Plan.typeSpelling)
        }
        (Plan.forJsonResponseEncoders roots)

let private normalise (text: string) = text.Replace("\r\n", "\n").TrimEnd()

[<Literal>]
let private Draws = 64

[<Literal>]
let private Seed = 801

let private gate = JsonEncoders.gateWith FableConverters.decoderOracle

/// Two same-typed fields exchanged: every value still encodes, as the wrong
/// record. Only a byte comparison against the writer can see it.
let private swapped: JsonEncoder<AIDenialToolModulePair> =
    fun value ->
        JsonEncode.record [
            "ToolName", JsonEncode.string value.ActiveModule
            "ActiveModule", JsonEncode.string value.ToolName
            "Count", JsonEncode.int32 value.Count
        ]

[<Tests>]
let tests =
    testList "Platform JSON encoders — generated, verified and pinned" [

        testCase "the census is whole: every type a platform API record returns has an encoder"
        <| fun () ->
            let plan = Plan.forJsonResponseEncoders roots

            Expect.isEmpty
                (plan.Refusals |> List.map (fun r -> sprintf "%s: %s" r.RefusedType r.Why))
                "a return type the generator refuses keeps the converter set's writer"

            Expect.equal
                (List.length PlatformJsonEncoders.coveredApiRecords)
                (List.length apiRecords)
                "every platform API record answers through the algebra"

        testCase "the committed PlatformJsonEncoders.fs is what the generator emits"
        <| fun () ->
            let expected = emission ()
            let path = committedPath ()

            if Environment.GetEnvironmentVariable "TOOLUP_REGEN_PLATFORM_DECODERS" = "1" then
                File.WriteAllText(path, expected.Replace("\r\n", "\n"))

            let committed = File.ReadAllText path

            if normalise committed <> normalise expected then
                let expectedLines = (normalise expected).Split '\n'
                let committedLines = (normalise committed).Split '\n'

                let firstDiff =
                    Seq.zip expectedLines committedLines
                    |> Seq.tryFindIndex (fun (e, c) -> e <> c)
                    |> Option.defaultValue (min expectedLines.Length committedLines.Length)

                failtestf
                    "src/ToolUp.Platform.Core/Shared/Remoting/Json/PlatformJsonEncoders.fs is not what the generator emits (first difference at line %d; %d committed lines vs %d expected). Regenerate and commit it:\n  $env:TOOLUP_REGEN_PLATFORM_DECODERS = \"1\"\n  dotnet run --project src/ToolUp.Platform.Tests.Remoting/ToolUp.Platform.Tests.Remoting.fsproj -- --filter-test-list \"Platform JSON encoders\"\n  $env:TOOLUP_REGEN_PLATFORM_DECODERS = $null\nthen rebuild ToolUp.Platform.Core and re-run WITHOUT the variable."
                    (firstDiff + 1)
                    committedLines.Length
                    expectedLines.Length

        testCase "every encoder the file registers writes the System.Text.Json writer's bytes over its own draws"
        <| fun () ->
            let refused =
                PlatformJsonEncoders.verifyAll gate Draws Seed
                |> List.choose (function
                    | Error refusal -> Some(JsonEncoders.describeRefusal refusal)
                    | Ok _ -> None)

            Expect.isEmpty
                refused
                (sprintf
                    "a generated platform encoder writes different text from the converter set:\n  %s"
                    (String.concat "\n  " refused))

            Expect.equal
                (List.length (PlatformJsonEncoders.verifyAll gate 1 Seed))
                (List.length PlatformJsonEncoders.covered)
                "one verification per registration"

        testCase "registerAllVerified registers every covered encoder"
        <| fun () ->
            Codecs.resetLegForTests CodecLeg.EncodeJson

            match PlatformJsonEncoders.registerAllVerified gate Draws Seed with
            | Error refusals ->
                failtestf
                    "registerAllVerified refused:\n  %s"
                    (refusals |> List.map JsonEncoders.describeRefusal |> String.concat "\n  ")
            | Ok _ ->
                let registered = Codecs.registered CodecLeg.EncodeJson |> List.map snd |> Set.ofList

                let missing =
                    PlatformJsonEncoders.covered |> List.filter (registered.Contains >> not)

                Expect.isEmpty missing "an encoder the file covers must be registered"

            PlatformJsonEncoders.registerAll ()

        testCase "the encoder gate is known to refuse — a swapped-field encoder is refused by name and draw"
        <| fun () ->
            match
                JsonEncoders.verifyWith<AIDenialToolModulePair> FableConverters.decoderOracle.Write Draws Seed swapped
            with
            | Ok _ -> failtest "an encoder that swaps two fields must not verify"
            | Error(DecoderDiverges v) ->
                Expect.equal v.WireType typeof<AIDenialToolModulePair>.FullName "the refusal names the type"
                Expect.isSome v.Divergence "and carries the diverging draw"

                Expect.stringContains
                    (JsonEncoders.describeRefusal (DecoderDiverges v))
                    "encoder:"
                    "both texts are quoted"
            | Error other -> failtestf "wrong refusal class: %A" other

            Codecs.resetLegForTests CodecLeg.EncodeJson

            match JsonEncoders.registerVerifiedWith<AIDenialToolModulePair> FableConverters.decoderOracle swapped with
            | Ok _ -> failtest "registerVerifiedWith must refuse a diverging encoder"
            | Error _ ->
                Expect.isNone
                    (Codecs.tryJsonEncoder Scope.Unscoped typeof<AIDenialToolModulePair>)
                    "nothing was registered"

            PlatformJsonEncoders.registerAll ()
    ]