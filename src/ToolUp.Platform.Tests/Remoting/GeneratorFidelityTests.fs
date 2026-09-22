// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.GeneratorFidelityTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Remoting
open ToolUp.Remoting.Generator
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69k.I — the generator's contract
// =============================================================================
//
// ─── What this pack proves, and why the chain is the way it is ─────────
//
// The phase's acceptance asks that the generated path agree with the
// reflection path. It cannot be asserted directly here, because a generated
// decoder is SOURCE TEXT and this pack has no compiler: executing it would
// mean compiling emitted F# at test time.
//
// What can be asserted is the link that closes the chain. Phase 785 already
// proved, differentially over the Phase 784 corpus, that the HAND-WRITTEN
// `PlatformDecoders` agree with the reflection reader — that is
// `DecoderAlgebraTests`, running beside this file. So if the generator's plan
// for the same records is the same decode, field for field and tag for tag,
// then generated agrees with reflection by composition of two checked claims
// rather than by assertion.
//
// That is what the fidelity cases below check, and they check it two ways: an
// EXECUTABLE equality against `PlatformDecoders.covered` — no transcription
// involved, so it cannot drift — and a PINNED per-field expectation for the
// shapes where a generator could plausibly be wrong (an option, an int64, a
// list of records, a union with payloads, a union without). A case at the end
// perturbs a pin and asserts the comparison goes red, so the fidelity cases
// are known to be capable of failing.
//
// ─── The census, and the measured claim this phase actually makes ──────
//
// Phase 69k's stated trigger was "the Phase 785 facet's `Reflection` count".
// That count is 0 today and structurally always will be: the facet classifies
// the records a composition root DECLARES, and the platform declares exactly
// the two records 785 covers, both `Algebra`. The real quantity is COVERAGE —
// how many of the SDK's own API records are on the algebra path at all — and
// the facet cannot report it, because a record nobody declared is not a
// `Reflection` binding, it is absent.
//
// The census cases below measure it. They are written as relationships rather
// than as pinned totals, so adding an API record does not redden them; the one
// that matters goes red exactly when the coverage gap CLOSES, which is the
// correct moment to revisit this phase.

// ─── Phase 816 — a two-member cycle with a bystander ─────────────────────
//
// `Ring` reaches `Chain`, `Chain` reaches `Ring` back; `Plain` is a
// dependency of `Ring` that the planner completes BETWEEN the two members
// (post-order: chain, plain, ring). The pin below asserts the planner
// regroups so the cycle is contiguous and the bystander stays a plain
// `let` — the ordering argument in `Plan.groupCycles`, made executable.

type Chain =
    | End
    | Link of Ring

and Ring = { Next: Chain; Pad: Plain }

and Plain = { Id: int }

// ─── The two Phase 785 records ───────────────────────────────────────────

let private healthMonitorApi = typeof<IHealthMonitorApi>
let private deploymentVerificationApi = typeof<IDeploymentVerificationApi>

let private phase785Roots =
    (Plan.returnTypes healthMonitorApi @ Plan.returnTypes deploymentVerificationApi)
    |> List.distinct

let private phase785Plan = Plan.forTypes phase785Roots

let private platformCore = healthMonitorApi.Assembly

let private fieldsOf (binding: string) =
    phase785Plan.Bindings
    |> List.tryPick (function
        | RecordDecoder(b, _, fields) when b = binding -> Some fields
        | _ -> None)

let private casesOf (binding: string) =
    phase785Plan.Bindings
    |> List.tryPick (function
        | UnionDecoder(b, _, cases) when b = binding -> Some cases
        | _ -> None)

/// A record binding reduced to the triple the pins below are written as.
let private shapeOf (binding: string) =
    match fieldsOf binding with
    | Some fields -> fields |> List.map (fun f -> f.FieldName, f.Position, f.Decoder)
    | None -> failtestf "the plan has no record decoder named '%s'" binding

// ─── The pins ────────────────────────────────────────────────────────────
//
// Transcribed from `Shared/Remoting/PlatformDecoders.fs`. Chosen for the
// shapes where a generator could plausibly be wrong rather than for coverage:
// a width that is not the default (`asInt64` where `asInt32` would compile
// and silently truncate), two different `option` payloads, three different
// `list` element decoders, a union whose every case carries a payload, and a
// union whose every case carries none.

let private jobSchedulerTelemetryViewPin = [
    "HasScheduler", 0, "Decode.asBool"
    "TickMissedCount60Min", 1, "Decode.asInt32"
    "LastDriftMs", 2, "Decode.option Decode.asInt64"
    "LastTickMissedAt", 3, "Decode.option Decode.asDateTime"
    "GeneratedAt", 4, "Decode.asDateTime"
]

let private aiDenialRollupPin = [
    "GeneratedAt", 0, "Decode.asDateTime"
    "ScopeId", 1, "Decode.asString"
    "WindowMinutes", 2, "Decode.asInt32"
    "TotalDenialsAllTime", 3, "Decode.asInt32"
    "TotalDenialsInWindow", 4, "Decode.asInt32"
    "DenialsPerMinute", 5, "Decode.asFloat"
    "ByToolName", 6, "Decode.list aiDenialGroupCount"
    "ByActiveModule", 7, "Decode.list aiDenialGroupCount"
    "ByScopeId", 8, "Decode.list aiDenialGroupCount"
    "TopToolModulePairs", 9, "Decode.list aiDenialToolModulePair"
    "RecentDenials", 10, "Decode.list recentAIDenial"
]

let private healthProbeViewPin = [
    "Name", 0, "Decode.asString"
    "Kind", 1, "Decode.asString"
    "TimeoutMs", 2, "Decode.asInt32"
    "Status", 3, "Decode.asString"
    "Message", 4, "Decode.asString"
    "ElapsedMs", 5, "Decode.asInt64"
]

let private notProvedStatementPin = [
    "Id", 0, "Decode.asString"
    "Statement", 1, "Decode.asString"
    "Narrowing", 2, "Decode.option Decode.asString"
]

// ─── The census ──────────────────────────────────────────────────────────

let private allApiRecords = Plan.apiRecordsIn platformCore

let private censusOf (record: Type) =
    record, Plan.forTypes (Plan.returnTypes record)

let private census = allApiRecords |> List.map censusOf

let private expressible =
    census |> List.filter (fun (_, plan) -> List.isEmpty plan.Refusals)

let private everyRefusal =
    census
    |> List.collect (fun (_, plan) -> plan.Refusals)
    |> List.distinctBy _.RefusedType

/// Every type reachable from `roots` through generic arguments, array
/// elements, record fields and union-case fields — the same graph the
/// planner walks, so "the census reaches a tuple" is asserted over what
/// the planner actually saw rather than over the return types alone.
let private reachableFrom (roots: Type list) : Type list =
    let seen = Collections.Generic.HashSet<Type>()

    let rec walk (t: Type) =
        if seen.Add t then
            if t.IsArray then
                walk (t.GetElementType())
            elif t.IsGenericType then
                t.GetGenericArguments() |> Array.iter walk
            elif FSharpType.IsRecord t then
                FSharpType.GetRecordFields t |> Array.iter (fun f -> walk f.PropertyType)
            elif FSharpType.IsUnion t then
                FSharpType.GetUnionCases t
                |> Array.iter (fun c -> c.GetFields() |> Array.iter (fun f -> walk f.PropertyType))

    roots |> List.iter walk
    List.ofSeq seen

[<Tests>]
let tests =
    testList "Phase 69k — the source generator" [

        testList "the generator reproduces the hand-written Phase 785 decoders" [

            testCase "the covered set is exactly the hand-written one"
            <| fun () ->
                // Executable, not transcribed: the shipped list on one side,
                // the generator's plan on the other. This is the whole
                // fidelity claim in one assertion — same types, same count,
                // same registration surface.
                // The plan carries F# SPELLINGS while `PlatformDecoders.covered`
                // carries `Type.FullName`, and the two are not the same string,
                // so the comparison is over the registration SURFACE: one entry
                // per wire type the module mounts, on each side. A type present
                // on one side and not the other moves the count.
                let generated = Emit.coveredSpellings phase785Plan |> List.map fst

                Expect.isNonEmpty generated "the generator planned nothing for the Phase 785 records"

                Expect.isTrue
                    (generated |> List.distinct |> List.length = List.length generated)
                    "the generator planned the same wire type twice — a duplicate `register` call"

                let generatedCount = List.length generated
                let handWrittenCount = List.length PlatformDecoders.covered

                Expect.equal
                    generatedCount
                    handWrittenCount
                    "the generator covers a different NUMBER of wire types than the hand-written set — \
                     one of the two has a type the other does not"

            testCase "the generator plans a decoder for every hand-written one, by name"
            <| fun () ->
                // `PlatformDecoders.covered` is `Type.FullName`; the plan's
                // roots carry the same. Bindings carry only spellings, so the
                // comparison runs over the ROOTS plus the bound records'
                // own full names, recovered from the assembly.
                let planned = phase785Plan.Bindings |> List.map TypePlan.binding |> Set.ofList

                let expected =
                    set [
                        "healthProbeView"
                        "preflightOutcomeView"
                        "healthSnapshot"
                        "preflightSnapshotView"
                        "jobSchedulerTelemetryView"
                        "degradedCapability"
                        "aiDenialGroupCount"
                        "aiDenialToolModulePair"
                        "recentAIDenial"
                        "aiDenialRollup"
                        "verificationSectionVerdict"
                        "deploymentVerificationOutcome"
                        "reportSection"
                        "notProvedStatement"
                        "deploymentVerificationReport"
                    ]

                Expect.equal
                    planned
                    expected
                    "the generator's binding names differ from the hand-written module's — \
                     the camelCase rule or the reachable type set has moved"

            testCase "`HealthProbeView` decodes field-for-field as hand-written"
            <| fun () -> Expect.equal (shapeOf "healthProbeView") healthProbeViewPin "field plan differs"

            testCase "`JobSchedulerTelemetryView` — the option and the non-default width"
            <| fun () ->
                Expect.equal
                    (shapeOf "jobSchedulerTelemetryView")
                    jobSchedulerTelemetryViewPin
                    "field plan differs — an `int64 option` read as anything but `Decode.option Decode.asInt64` \
                     either truncates or refuses"

            testCase "`AIDenialRollup` — three list element decoders and a float"
            <| fun () -> Expect.equal (shapeOf "aiDenialRollup") aiDenialRollupPin "field plan differs"

            testCase "`NotProvedStatement` — a nested record's option"
            <| fun () -> Expect.equal (shapeOf "notProvedStatement") notProvedStatementPin "field plan differs"

            testCase "a union whose every case carries a payload"
            <| fun () ->
                let cases =
                    casesOf "verificationSectionVerdict"
                    |> Option.defaultWith (fun () -> failtest "no union decoder for VerificationSectionVerdict")

                Expect.equal
                    (cases |> List.map (fun c -> c.Tag, c.CaseName, c.Payload))
                    [
                        0, "NotComposed", CasePayload.OneField "Decode.asString"
                        1, "Verified", CasePayload.OneField "Decode.asString"
                        2, "Observed", CasePayload.OneField "Decode.asString"
                        3, "Failed", CasePayload.OneField "Decode.asString"
                        4, "Unreadable", CasePayload.OneField "Decode.asString"
                    ]
                    "case tags or payloads differ from the hand-written decoder — the tag is the wire fact"

            testCase "a union whose every case carries none"
            <| fun () ->
                let cases =
                    casesOf "deploymentVerificationOutcome"
                    |> Option.defaultWith (fun () -> failtest "no union decoder for DeploymentVerificationOutcome")

                Expect.equal
                    (cases |> List.map (fun c -> c.Tag, c.CaseName, c.Payload))
                    [
                        0, "NothingComposed", CasePayload.NoFields
                        1, "AllComposedVerified", CasePayload.NoFields
                        2, "PartiallyVerified", CasePayload.NoFields
                        3, "FailuresPresent", CasePayload.NoFields
                    ]
                    "a field-less union case must plan as `case0` — reading it as a string enum would \
                     refuse every payload the writer emits"

            testCase "a union case carrying several fields plans as `fields n` over the case's own field order"
            <| fun () ->
                // Phase 800. `ConfigFieldKind.Int of min: int option * max: int option` —
                // one of the nine platform unions the census found blocked
                // on this shape. The plan is the record shape over the
                // inner array: the case's declared names as path labels,
                // its declaration order as positions.
                let plan = Plan.forTypes [ typeof<ConfigFieldKind> ]
                Expect.isEmpty plan.Refusals "ConfigFieldKind is expressible since Phase 800"

                let cases =
                    plan.Bindings
                    |> List.tryPick (function
                        | UnionDecoder(_, _, cases) -> Some cases
                        | _ -> None)
                    |> Option.defaultWith (fun () -> failtest "no union decoder for ConfigFieldKind")

                let intCase =
                    cases
                    |> List.tryFind (fun c -> c.CaseName = "Int")
                    |> Option.defaultWith (fun () -> failtest "no `Int` case planned")

                match intCase.Payload with
                | CasePayload.SeveralFields fields ->
                    Expect.equal
                        (fields |> List.map (fun f -> f.FieldName, f.Position, f.Decoder))
                        [
                            "min", 0, "Decode.option Decode.asInt32"
                            "max", 1, "Decode.option Decode.asInt32"
                        ]
                        "the several-field case's fields are planned by name, in declared order"
                | other -> failtestf "the two-field `Int` case must plan as SeveralFields, not %A" other

                // And the emission is `Decode.fields 2` over a `field`
                // pipeline — the text the consumer compiles.
                let emitted =
                    Emit.compilationUnit
                        {
                            Namespace = "Probe"
                            ModuleName = "Probe"
                            Opens = [ "ToolUp.Platform" ]
                            ApiRecords = []
                        }
                        plan

                Expect.stringContains emitted "Decode.fields" "the several-field arm takes `fields`"

                Expect.stringContains
                    emitted
                    "Decode.field \"min\" 0 (Decode.option Decode.asInt32)"
                    "the first field, by name and position"

                Expect.stringContains
                    emitted
                    "Decode.field \"max\" 1 (Decode.option Decode.asInt32)"
                    "the second field, by name and position"

                Expect.stringContains
                    emitted
                    "ConfigFieldKind.Int(min, max)"
                    "constructed through the case, in field order"

            testCase "a tuple plans as `tupleN` over its element decoders, inline"
            <| fun () ->
                // Phase 800. `string * TeamRole` is what `IPlatformTenantApi`
                // returns; the tuple is not bound under a name (it has
                // none) — it is an inline root, registered under the
                // instantiated tuple type.
                let plan = Plan.forTypes [ typeof<string * TeamRole> ]
                Expect.isEmpty plan.Refusals "a pair is expressible since Phase 800"

                let root =
                    plan.Roots
                    |> List.tryHead
                    |> Option.defaultWith (fun () -> failtest "no root planned for the pair")

                Expect.stringStarts
                    root.RootDecoder
                    "Decode.tuple2 Decode.asString "
                    "the pair is `tuple2` over its elements"

                Expect.equal root.RootFullName typeof<string * TeamRole>.FullName "registered under the tuple type"

                // A tuple wider than the algebra reaches is refused by name
                // — never guessed at, never sliced.
                let wide = Plan.forTypes [ typeof<int * int * int * int * int> ]

                Expect.hasLength wide.Refusals 1 "a 5-tuple has no combinator"

                Expect.stringContains
                    wide.Refusals.Head.Why
                    "reach arity 4"
                    "the refusal names the ceiling rather than reading as a generic miss"

            testCase "a recursive union plans as a `let rec` group, and the emission is eta-expanded"
            <| fun () ->
                // Phase 816. `ColumnExpr` reaches itself through a list
                // (`Concat`, `Format`) and through a several-field case
                // (`SplitTake`, `Substring`); until 816 the planner refused
                // the cycle. The plan names the recursive binding, and the
                // emission is one `let rec` whose body is `fun value -> (…)
                // value`, so the recursive reference is read on decode.
                let plan = Plan.forTypes [ typeof<ColumnMappingTypes.ColumnExpr> ]
                Expect.isEmpty plan.Refusals "ColumnExpr is expressible since Phase 816"
                Expect.equal plan.RecursiveGroups [ [ "columnExpr" ] ] "the cycle is one group of one"

                let emitted =
                    Emit.compilationUnit
                        {
                            Namespace = "Probe"
                            ModuleName = "Probe"
                            Opens = [ "ToolUp.Platform" ]
                            ApiRecords = []
                        }
                        plan

                Expect.stringContains
                    emitted
                    "    let rec columnExpr: Decoder<ColumnMappingTypes.ColumnExpr> =\n        fun value ->\n            (Decode.union"
                    "the recursive binding is `let rec`, eta-expanded over `value`"

                Expect.stringContains
                    emitted
                    "(Decode.field \"parts\" 0 (Decode.list columnExpr))"
                    "the back-edge is the binding's own name"

                Expect.stringContains
                    emitted
                    "| _ -> None))\n                value\n"
                    "the wrapped body is applied to `value`"

                // A plan with no cycle is untouched: plain `let`, no wrap.
                let plain = Plan.forTypes [ typeof<ConfigFieldKind> ]
                Expect.isEmpty plain.RecursiveGroups "no cycle, no recursive group"

                let plainText =
                    Emit.compilationUnit
                        {
                            Namespace = "Probe"
                            ModuleName = "Probe"
                            Opens = [ "ToolUp.Platform" ]
                            ApiRecords = []
                        }
                        plain

                Expect.isFalse (plainText.Contains "let rec") "a plan without a cycle emits plain `let`"
                Expect.isFalse (plainText.Contains "fun value ->") "and no eta-expansion"

            testCase
                "a two-member cycle is one contiguous `let rec … and …` group, and a bystander between them stays plain"
            <| fun () ->
                let plan = Plan.forTypes [ typeof<Ring> ]
                Expect.isEmpty plan.Refusals "the cycle is expressible"

                Expect.equal
                    (plan.Bindings |> List.map TypePlan.binding)
                    [ "plain"; "chain"; "ring" ]
                    "the bystander (completed between the members in post-order) is moved ahead of the group"

                Expect.equal
                    plan.RecursiveGroups
                    [ [ "chain"; "ring" ] ]
                    "the two members are one group, in completion order"

                let emitted =
                    Emit.compilationUnit
                        {
                            Namespace = "Probe"
                            ModuleName = "Probe"
                            Opens = [ "ToolUp.Platform.Tests.Remoting.GeneratorFidelityTests" ]
                            ApiRecords = []
                        }
                        plan

                Expect.stringContains
                    emitted
                    "    let plain: Decoder<GeneratorFidelityTests.Plain> =\n        Decode.succeed"
                    "the bystander is a plain `let`, not eta-expanded"

                Expect.stringContains
                    emitted
                    "    let rec chain: Decoder<GeneratorFidelityTests.Chain> =\n        fun value ->"
                    "the group opens with `let rec`"

                Expect.stringContains
                    emitted
                    "    and ring: Decoder<GeneratorFidelityTests.Ring> =\n        fun value ->"
                    "and continues with `and`"

            testCase "the fidelity comparison CATCHES a wrong plan — the go-red case"
            <| fun () ->
                // A pin that agrees with whatever it is compared against
                // proves nothing. Swap two positions in the real plan and
                // assert the comparison rejects it: the same equality the
                // cases above rely on, shown capable of failing.
                let real = shapeOf "healthProbeView"

                let perturbed =
                    match real with
                    | (n0, p0, d0) :: (n1, p1, d1) :: rest -> (n0, p1, d0) :: (n1, p0, d1) :: rest
                    | other -> other

                Expect.notEqual perturbed real "swapping two field positions must not compare equal"

                // And the perturbation is the one that matters: a swapped
                // pair of SAME-TYPED fields still decodes, so only the
                // position catches it.
                Expect.equal
                    (perturbed |> List.map (fun (n, _, d) -> n, d))
                    (real |> List.map (fun (n, _, d) -> n, d))
                    "the perturbation must be invisible to everything except the position — \
                     otherwise this case is not proving what it claims"
        ]

        testList "the census — how far the algebra reaches over the SDK's own API records" [

            testCase "the census is not vacuous"
            <| fun () ->
                Expect.isGreaterThan
                    (List.length allApiRecords)
                    25
                    "ToolUp.Platform.Core declares far more API records than this — the shape test \
                     (a record whose every field is a function) has stopped matching"

            testCase "the two Phase 785 records are among the wholly-expressible ones"
            <| fun () ->
                let names = expressible |> List.map (fst >> Plan.simpleName) |> Set.ofList

                Expect.isTrue
                    (names.Contains "IHealthMonitorApi"
                     && names.Contains "IDeploymentVerificationApi")
                    "the records Phase 785 hand-wrote decoders for must be expressible by construction"

            testCase "MORE records are expressible than the platform declares to the facet"
            <| fun () ->
                // The measured claim this phase makes, and the reason it
                // exists. `PlatformDecoders.coveredApiRecords` is what
                // `RemotingDecoderFacet.inspectPlatform` classifies; every
                // record outside it is on the reflection path and invisible
                // to the facet, because an undeclared record is absent
                // rather than `Reflection`.
                //
                // GOES RED WHEN THE GAP CLOSES — which is the correct moment
                // to revisit this phase, not a regression.
                Expect.isGreaterThan
                    (List.length expressible)
                    (List.length PlatformDecoders.coveredApiRecords)
                    "the coverage gap this phase measures has closed — every expressible API record is \
                     now declared to the facet, so re-read Phase 69k before changing this"

            testCase "the two refusal classes that capped coverage are both CLOSED, by name"
            <| fun () ->
                // Phase 69k left this case asserting both gaps OPEN — a
                // tuple combinator, and a union case carrying more than one
                // field — as the demand evidence for whichever phase would
                // extend the algebra. Phase 800 did, and this is that case
                // INVERTED: it goes red if either gap reopens. The census
                // must still REACH both shapes (asserted first, so a closed
                // gap is not a shape the census stopped seeing), and no
                // refusal may name either.
                let reached = allApiRecords |> List.collect Plan.returnTypes |> reachableFrom

                Expect.isTrue
                    (reached |> List.exists FSharpType.IsTuple)
                    "the census no longer reaches a tuple — the closure below would be vacuous"

                let severalFieldUnions =
                    reached
                    |> List.filter FSharpType.IsUnion
                    |> List.filter (fun t ->
                        FSharpType.GetUnionCases t |> Array.exists (fun c -> c.GetFields().Length > 1))

                Expect.isNonEmpty
                    severalFieldUnions
                    "the census no longer reaches a several-field union case — the closure below would be vacuous"

                Expect.isEmpty
                    (everyRefusal |> List.filter (fun r -> r.RefusedType.StartsWith "System.Tuple"))
                    "a tuple is refused — the tuple gap has REOPENED"

                Expect.isEmpty
                    (everyRefusal
                     |> List.filter (fun r -> r.Why.Contains "union case" || r.Why.Contains "more than one field"))
                    "a union case is refused — the several-field gap has REOPENED"

                // Phase 816 — and the cycle gap, likewise inverted.
                Expect.isEmpty
                    (everyRefusal |> List.filter (fun r -> r.Why.Contains "recursive"))
                    "a recursive type is refused — the cycle gap has REOPENED"

            testCase "every platform API record is expressible — the census has no refusal left"
            <| fun () ->
                // The measured end state of Phases 800, 816 and 817, pinned
                // so a regression that re-refuses any record goes red by
                // name. Fourteen records were blocked on the day 800 shipped
                // (69k counted 13 over a smaller assembly): twelve moved on
                // 800 (tuples, several-field cases), `IConversionApi` on 816
                // (a recursive union), and `FileManagementApi` on 817 —
                // whose `ProcessedFileEntry.Info: obj option` was the last
                // open point in the API type graph, closed by the
                // `ProcessedData` envelope and removed outright (operator
                // decision 2026-09-22, overriding the deprecation window).
                let expressibleNames =
                    expressible |> List.map (fst >> Plan.simpleName) |> Set.ofList

                let moved = [
                    "IConversionApi"
                    "IDataSubjectRequestApi"
                    "IConfigApi"
                    "IFeatureFlagApi"
                    "IModuleQueryBusApi"
                    "IPlatformTenantApi"
                    "IProvenanceQueryApi"
                    "IProviderProfileApi"
                    "ITeamInviteApi"
                    "IUserSchemaApi"
                    "IWebhookApi"
                    "JobApi"
                    "ModelExecutionApi"
                    "FileManagementApi"
                ]

                Expect.isEmpty
                    (moved |> List.filter (fun n -> not (expressibleNames.Contains n)))
                    "a record the three phases moved onto the algebra path is refused again"

                Expect.isEmpty everyRefusal "no platform API record is left on the reflection decode path"

                Expect.equal
                    (List.length expressible)
                    (List.length allApiRecords)
                    "every record the assembly declares is expressible"

            testCase "a refusal names the type it refused"
            <| fun () ->
                everyRefusal
                |> List.iter (fun r ->
                    Expect.isNotEmpty r.RefusedType "a refusal with no type name is unactionable"
                    Expect.isNotEmpty r.Why "a refusal with no reason is unactionable")
        ]

        testList "the typed server dispatch table" [

            testCase "every method on an API record is read off the record's own metadata"
            <| fun () ->
                let table = Dispatch.tableFor healthMonitorApi

                Expect.equal
                    (List.length table.Methods)
                    (List.length (Plan.returnTypes healthMonitorApi))
                    "the dispatch table and the decoder plan must see the same method set"

                table.Methods
                |> List.iter (fun m ->
                    Expect.isNotEmpty m.MethodName "a method with no name"

                    Expect.isGreaterThan
                        (List.length m.ArgumentTypes)
                        0
                        "a Remoting method always takes at least `unit`")

            testCase "the emitted argument parse is the typed STJ seam, never reflection"
            <| fun () ->
                let source =
                    Dispatch.compilationUnit "ToolUp.Remoting.Server.Generated" [] (Dispatch.tableFor healthMonitorApi)

                Expect.stringContains
                    source
                    "FableConverters.tryDeserialise<"
                    "the argument parse must go through the Phase 783 statically-typed seam"

                // Over emitted CODE, not over the banner. The generated
                // header says in prose that the parse is "never a reflective
                // MethodInfo walk", and a whole-text scan matched its own
                // documentation — a false positive the first run caught, and
                // the reason this case reads the code lines only.
                let codeLines =
                    source.Split '\n'
                    |> Array.filter (fun line -> not ((line.TrimStart()).StartsWith "//"))
                    |> String.concat "\n"

                Expect.isFalse
                    (codeLines.Contains "MethodInfo" || codeLines.Contains "GetMethod")
                    "the emitted dispatch table must contain no reflective member lookup"

                Expect.stringContains source "let methods:" "the method manifest must be emitted as a list"

            testCase "the emitted module names every method it dispatches"
            <| fun () ->
                let table = Dispatch.tableFor healthMonitorApi

                let source = Dispatch.compilationUnit "ToolUp.Remoting.Server.Generated" [] table

                table.Methods
                |> List.iter (fun m ->
                    Expect.stringContains
                        source
                        m.MethodName
                        (sprintf "method '%s' is missing from the emitted source" m.MethodName))
        ]

        testList "the emitted module" [

            testCase "registration order is dependency order"
            <| fun () ->
                // A decoder emitted as a plain `let` can only refer to
                // bindings already in scope, so the plan's order IS the
                // compile-correctness claim.
                let order =
                    phase785Plan.Bindings
                    |> List.map TypePlan.binding
                    |> List.mapi (fun i b -> b, i)
                    |> Map.ofList

                let position b = Map.find b order

                Expect.isLessThan
                    (position "healthProbeView")
                    (position "healthSnapshot")
                    "`healthSnapshot` decodes a list of `healthProbeView`, so it must be emitted after it"

                Expect.isLessThan
                    (position "aiDenialGroupCount")
                    (position "aiDenialRollup")
                    "`aiDenialRollup` decodes lists of `aiDenialGroupCount`"

                Expect.isLessThan
                    (position "verificationSectionVerdict")
                    (position "reportSection")
                    "`reportSection` decodes a `verificationSectionVerdict`"

            testCase "the module bakes no assembly version into a type key"
            <| fun () ->
                // A generic type's `FullName` embeds the assembly VERSION of
                // every type argument. Emitting those as literals compiles
                // and then silently stops matching the registry at the next
                // version bump — so the emitted form must COMPUTE them.
                let options = {
                    Namespace = "ToolUp.Remoting"
                    ModuleName = "GeneratedDecoders"
                    Opens = [ "ToolUp.Platform" ]
                    ApiRecords = [ "IHealthMonitorApi", [ "Result<HealthSnapshot, string>" ], false ]
                }

                let source = Emit.compilationUnit options phase785Plan

                Expect.isFalse
                    (source.Contains "Version=")
                    "an assembly version appears in the emitted source — a type key was emitted as a \
                     literal instead of as `typeof<_>.FullName`"

                Expect.stringContains
                    source
                    "typeof<Result<HealthSnapshot, string>>.FullName"
                    "the API-record declaration must compute its keys"

            testCase "the emitted source carries the do-not-edit banner"
            <| fun () ->
                let options = {
                    Namespace = "ToolUp.Remoting"
                    ModuleName = "GeneratedDecoders"
                    Opens = []
                    ApiRecords = []
                }

                let source = Emit.compilationUnit options phase785Plan

                Expect.stringContains source "<auto-generated>" "generated source must say so"
                Expect.stringContains source "DO NOT EDIT" "generated source must say so"

                Expect.stringContains
                    source
                    "SPDX-License-Identifier: Apache-2.0"
                    "every source file in this repository carries the SPDX header, generated ones included"
        ]
    ]