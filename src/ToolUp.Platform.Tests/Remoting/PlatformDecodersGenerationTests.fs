// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.PlatformDecodersGenerationTests

open System
open System.IO
open System.Reflection
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Generator

// =============================================================================
// Phase 801 — the platform's own decoders are GENERATED, and held to it
// =============================================================================
//
// `src/ToolUp.Platform.Core/Shared/Remoting/PlatformDecoders.fs` is the
// source generator's emission over EVERY API record `ToolUp.Platform.Core`
// declares — 38 records, every wire type their methods return. Phase 785
// hand-wrote the first two records' decoders and Phase 69k proved the
// generator reproduces them field for field; Phases 800, 816 and 817 made
// every remaining record expressible; this phase adopts the whole set by
// generating it, and holds the committed file to the emission the way the
// AOT sample holds its own.
//
// Two things this pack asserts, and the order matters:
//
//   1. **The committed file IS the emission.** A generator change, a new
//      API record, a field added to a wire type — each moves the emission,
//      and the committed file must move with it or the client is decoding
//      through a decoder for a record that no longer has that shape. The
//      regen switch is `TOOLUP_REGEN_PLATFORM_DECODERS=1`; the drift case
//      names it.
//
//   2. **Every decoder the file registers agrees with the reflection
//      reader** over deterministic draws of its own type — Phase 801's
//      differential gate, run here as the recorded verification the
//      generated `registerAll` relies on: the browser registers what this
//      pack verified. A disagreement is refused by name, with the draw.
//
// The corpus-coverage flag each API record declares to the facet is
// COMPUTED from that gate rather than asserted: a record is corpus-covered
// exactly when every return type it carries verified. So the flag in the
// committed file is the gate's own answer at generation time, and this
// pack re-checks it on every run.

/// Repo root (toolup-forge) from the running test assembly:
/// bin/<Config>/net10.0/ToolUp.Platform.Tests.dll → up 5.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private committedPath () =
    Path.Combine(repoRoot (), "src", "ToolUp.Platform.Core", "Shared", "Remoting", "PlatformDecoders.fs")

let private platformCore = typeof<IHealthMonitorApi>.Assembly

/// Every API record the assembly declares, ordered by name — the census
/// Phase 69k measures and this file adopts.
let private apiRecords = Plan.apiRecordsIn platformCore

/// Every namespace a root reaches, so the emitted module opens each one
/// the spellings need. Module-qualified names (such as `ColumnMappingTypes.ColumnExpr`)
/// are emitted by the generator itself; only NAMESPACES are opened.
let private namespacesReachedBy (roots: Type list) : string list =
    let seen = Collections.Generic.HashSet<Type>()
    let found = Collections.Generic.HashSet<string>()

    let rec walk (t: Type) =
        if seen.Add t then
            if not (isNull t.Namespace) then
                found.Add t.Namespace |> ignore

            if t.IsArray then
                walk (t.GetElementType())
            elif t.IsGenericType then
                t.GetGenericArguments() |> Array.iter walk
            elif FSharpType.IsRecord(t, true) then
                FSharpType.GetRecordFields(t, true) |> Array.iter (fun f -> walk f.PropertyType)
            elif FSharpType.IsUnion(t, true) then
                FSharpType.GetUnionCases(t, true)
                |> Array.iter (fun c -> c.GetFields() |> Array.iter (fun f -> walk f.PropertyType))

    roots |> List.iter walk

    found
    |> Seq.filter (fun ns -> ns <> "System" && not (ns.StartsWith "Microsoft.FSharp"))
    |> Seq.sort
    |> List.ofSeq

/// The draw count and seed the recorded verification runs under. More
/// draws than `registerVerified`'s boot-time default, because this runs
/// once per pack rather than once per boot.
[<Literal>]
let private Draws = 64

[<Literal>]
let private Seed = 801

/// The gate's verdict per wire type, from the COMMITTED module's own
/// `verifyAll` — the recorded run the generated `registerAll` relies on.
let private verified: Map<string, Result<DecoderVerification, DecoderRefusal>> =
    PlatformDecoders.verifyAll Draws Seed
    |> List.map (fun outcome ->
        match outcome with
        | Ok v -> v.WireType, outcome
        | Error(DecoderDiverges v) -> v.WireType, outcome
        | Error(DecoderUndrawable(wireType, _)) -> wireType, outcome)
    |> Map.ofList

let private isVerified (wireType: Type) =
    match Map.tryFind (RemotingDecoders.keyFor wireType) verified with
    | Some(Ok _) -> true
    | _ -> false

/// What the generator emits for the platform today.
let private emission () : string =
    let roots = apiRecords |> List.collect Plan.returnTypes |> List.distinct
    let plan = Plan.forTypes roots

    let declarations =
        apiRecords
        |> List.map (fun record ->
            let returns = Plan.returnTypes record
            let corpusCovered = returns |> List.forall isVerified
            Plan.simpleName record, returns |> List.map Plan.typeSpelling, corpusCovered)

    Emit.compilationUnit
        {
            Namespace = "ToolUp.Remoting"
            ModuleName = "PlatformDecoders"
            Opens = namespacesReachedBy roots
            ApiRecords = declarations
        }
        plan

let private normalise (text: string) = text.Replace("\r\n", "\n").TrimEnd()

[<Tests>]
let tests =
    testList "Phase 801 — the platform's decoders are generated and verified" [

        testCase "the census is whole: every API record the assembly declares is expressible"
        <| fun () ->
            let plan =
                Plan.forTypes (apiRecords |> List.collect Plan.returnTypes |> List.distinct)

            Expect.isEmpty plan.Refusals "a record the generator refuses cannot be adopted"
            Expect.isGreaterThan (List.length apiRecords) 30 "the census reaches the platform's API records"

        testCase "the committed PlatformDecoders.fs is what the generator emits"
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
                    "src/ToolUp.Platform.Core/Shared/Remoting/PlatformDecoders.fs is not what the generator emits (first difference at line %d; %d committed lines vs %d expected). A wire type moved, an API record was added, or the emitter changed. Regenerate and commit the file with your change:\n  $env:TOOLUP_REGEN_PLATFORM_DECODERS = \"1\"\n  dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list \"Phase 801\"\n  $env:TOOLUP_REGEN_PLATFORM_DECODERS = $null\nthen rebuild ToolUp.Platform.Core and re-run this pack WITHOUT the variable."
                    (firstDiff + 1)
                    committedLines.Length
                    expectedLines.Length

        testCase "every decoder the file registers agrees with the reflection reader over its own draws"
        <| fun () ->
            // The recorded verification. `registerAll` in the browser is
            // safe exactly because this ran here, on .NET, over the same
            // decoders.
            let refused =
                verified
                |> Map.toList
                |> List.choose (fun (_, outcome) ->
                    match outcome with
                    | Error refusal -> Some(DecoderRefusal.describe refusal)
                    | Ok _ -> None)

            Expect.isEmpty
                refused
                (sprintf
                    "a generated platform decoder disagrees with the reflection reader:\n  %s"
                    (String.concat "\n  " refused))

            Expect.isGreaterThan
                (Map.count verified)
                100
                "the verification covers the whole registration set, not a sample"

        testCase "every API record declares corpus coverage, because every return type verified"
        <| fun () ->
            let uncovered =
                PlatformDecoders.coveredApiRecords
                |> List.filter (fun (_, _, covered) -> not covered)
                |> List.map (fun (name, _, _) -> name)

            Expect.isEmpty
                uncovered
                "a record declared uncovered means a return type failed the gate at generation time"

            Expect.equal
                (List.length PlatformDecoders.coveredApiRecords)
                (List.length apiRecords)
                "one declaration per API record"

        testCase
            "every wire type the file covers is registered by registerAll, and registerAllVerified registers the same set"
        <| fun () ->
            RemotingDecoders.resetForTests ()

            match PlatformDecoders.registerAllVerified Draws Seed with
            | Error refusals ->
                failtestf
                    "registerAllVerified refused:\n  %s"
                    (refusals |> List.map DecoderRefusal.describe |> String.concat "\n  ")
            | Ok verifications ->
                let registered = RemotingDecoders.registered () |> Set.ofList

                let missing =
                    PlatformDecoders.covered
                    |> List.filter (fun name -> not (registered.Contains name))

                Expect.isEmpty missing "a decoder the file covers must be registered"

                Expect.equal
                    (List.length verifications)
                    (List.length PlatformDecoders.covered)
                    "one verification per registration"

            // Leave the process as the client leaves it: everything registered.
            PlatformDecoders.registerAll ()

        testCase "the gate is known to refuse — a swapped-field decoder is refused by name and draw"
        <| fun () ->
            // Two same-typed fields exchanged: every value still decodes,
            // to the wrong record. The differential is the only thing
            // that can see it, and this is the case that shows it does.
            let swapped: Decoder<AIDenialToolModulePair> =
                Decode.succeed (fun tool activeModule count ->
                    ({
                        ToolName = activeModule
                        ActiveModule = tool
                        Count = count
                    }
                    : AIDenialToolModulePair))
                |> Decode.apply (Decode.field "ToolName" 0 Decode.asString)
                |> Decode.apply (Decode.field "ActiveModule" 1 Decode.asString)
                |> Decode.apply (Decode.field "Count" 2 Decode.asInt32)

            match RemotingDecoders.verify<AIDenialToolModulePair> Draws Seed swapped with
            | Ok _ -> failtest "a decoder that swaps two fields must not verify"
            | Error(DecoderDiverges v) ->
                Expect.equal v.WireType typeof<AIDenialToolModulePair>.FullName "the refusal names the type"
                Expect.isSome v.Divergence "and carries the diverging draw"

                Expect.stringContains
                    (DecoderRefusal.describe (DecoderDiverges v))
                    "candidate:"
                    "both renderings are quoted"
            | Error other -> failtestf "wrong refusal class: %A" other

            // And it refuses REGISTRATION, leaving the table untouched.
            RemotingDecoders.resetForTests ()

            match RemotingDecoders.registerVerified<AIDenialToolModulePair> swapped with
            | Ok _ -> failtest "registerVerified must refuse a diverging decoder"
            | Error _ ->
                Expect.isFalse (RemotingDecoders.isRegistered typeof<AIDenialToolModulePair>) "nothing was registered"

            // A correct decoder through the same door registers.
            match RemotingDecoders.registerVerified<AIDenialToolModulePair> PlatformDecoders.aiDenialToolModulePair with
            | Ok v -> Expect.equal v.Draws RemotingDecoders.DefaultDraws "verified over the default draw count"
            | Error refusal -> failtestf "the generated decoder must verify: %s" (DecoderRefusal.describe refusal)

            Expect.isTrue (RemotingDecoders.isRegistered typeof<AIDenialToolModulePair>) "and is registered"
            PlatformDecoders.registerAll ()

        testCase "a type the shape generator cannot draw is a refusal, never a pass"
        <| fun () ->
            // `obj` has no wire shape. A gate that could not run must say
            // so rather than report agreement over zero draws.
            match RemotingDecoders.verify<obj> Draws Seed (fun _ -> Ok(box ())) with
            | Error(DecoderUndrawable(wireType, reason)) ->
                Expect.equal wireType "System.Object" "the refusal names the type"
                Expect.stringContains reason "closed algebra" "and says why no draw exists"
            | other -> failtestf "an undrawable type must be refused as such, not %A" other
    ]