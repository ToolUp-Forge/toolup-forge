module ToolUp.Platform.Tests.InProcess.RemotingAnalyzerRecognitionTests

open System
open System.Reflection
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Remoting.Server
open ToolUp.Remoting.Analyzers

// ─── Phase 195 — analyzer recognition parity ─────────────────────────
//
// The Phase 195 analyzer ships `Recognition.fs` as its decision core; the
// SAME source file is `<Compile Include>`-linked into this test pack (see the
// .fsproj entry). These tests prove the load-bearing acceptance criterion:
//
//   the analyzer's "unclassified" verdict is PROVABLY the same set the runtime
//   `AuthClassifier` (Auth.fs) refuses to start on.
//
// We feed `Recognition` the per-field attribute data extracted by reflection —
// the CLR `.Name` form ("RequiresRoleAttribute") — and compare its unclassified
// set against `AuthClassifier.classify >> unclassified` over the identical
// fixture types. The analyzer feeds `Recognition` the source-name form
// ("RequiresRole"); `Recognition.simpleName` normalises both to one key, so the
// two paths classify identically by construction — a separate test pins the
// source-form normalisation directly.

// ── Fixture API records (both attribute families + classification mix) ──

type private FullyServerClassifiedApi = {
    [<RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<RequiresClaim "scope">]
    NeedsScope: unit -> Async<int>
    [<TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<AllowAnonymous>]
    OpenToAll: unit -> Async<int>
    [<PublicEndpoint>]
    PublicSurface: unit -> Async<int>
}

type private FullyMirrorClassifiedApi = {
    [<ToolUp.Platform.RequiresRole "Admin">]
    AdminOnly: unit -> Async<int>
    [<ToolUp.Platform.TenantScoped>]
    TenantOnly: unit -> Async<int>
    [<ToolUp.Platform.AllowAnonymous>]
    OpenToAll: unit -> Async<int>
    [<ToolUp.Platform.PublicEndpoint>]
    PublicSurface: unit -> Async<int>
}

type private PartiallyClassifiedApi = {
    [<RequiresRole "Admin">]
    Guarded: unit -> Async<int>
    NakedOne: string -> Async<int>
    [<ToolUp.Platform.TenantScoped>]
    MirrorGuarded: unit -> Async<int>
    NakedTwo: int -> Async<int>
}

type private FullyNakedApi = {
    A: unit -> Async<int>
    B: string -> Async<int>
}

// ── Reflection extraction — the CLR `.Name` form the runtime sees ───────

let private fieldAttrsViaReflection (t: Type) : Recognition.FieldAttributes list =
    let flags = BindingFlags.Public ||| BindingFlags.NonPublic

    FSharpType.GetRecordFields(t, flags)
    |> Array.map (fun pi -> {
        Recognition.FieldName = pi.Name
        Recognition.AttributeNames =
            pi.GetCustomAttributes(true)
            |> Array.map (fun a -> a.GetType().Name)
            |> Array.toList
    })
    |> Array.toList

let private analyzerUnclassified (t: Type) : Set<string> =
    fieldAttrsViaReflection t
    |> Recognition.unclassifiedAuthFieldNames
    |> Set.ofList

let private runtimeUnclassified (t: Type) : Set<string> =
    AuthClassifier.classify t |> AuthClassifier.unclassified |> Set.ofList

let private parityFor (t: Type) =
    Expect.equal
        (analyzerUnclassified t)
        (runtimeUnclassified t)
        (sprintf "analyzer unclassified set must equal runtime AuthClassifier refusal set for %s" t.Name)

// ── Tests ───────────────────────────────────────────────────────────

let private parityTests =
    testList "recognition-vs-runtime parity" [
        test "fully server-classified record: both agree (empty refusal set)" {
            parityFor typeof<FullyServerClassifiedApi>
            Expect.isEmpty (runtimeUnclassified typeof<FullyServerClassifiedApi>) "fully classified ⇒ nothing refused"
        }

        test "fully mirror-classified record: both agree (empty refusal set)" {
            parityFor typeof<FullyMirrorClassifiedApi>
            Expect.isEmpty (runtimeUnclassified typeof<FullyMirrorClassifiedApi>) "fully classified ⇒ nothing refused"
        }

        test "partially-classified record: both flag exactly the naked methods" {
            parityFor typeof<PartiallyClassifiedApi>

            Expect.equal
                (analyzerUnclassified typeof<PartiallyClassifiedApi>)
                (set [ "NakedOne"; "NakedTwo" ])
                "exactly the two unattributed methods are unclassified"
        }

        test "fully-naked record: both flag every method" {
            parityFor typeof<FullyNakedApi>

            Expect.equal (analyzerUnclassified typeof<FullyNakedApi>) (set [ "A"; "B" ]) "every method is unclassified"
        }

        // The sweep against a real, fully-annotated forge API record: the
        // analyzer must NOT flag a record the dispatcher accepts.
        test "real forge API record (IPlatformTenantApi) parity" {
            parityFor typeof<ToolUp.Platform.IPlatformTenantApi>

            Expect.isEmpty
                (runtimeUnclassified typeof<ToolUp.Platform.IPlatformTenantApi>)
                "forge records ship fully classified"
        }
    ]

// ── TUR0001 finding shape ───────────────────────────────────────────

let private tur0001Tests =
    testList "TUR0001 findings" [
        test "unclassified fields surface TUR0001 with a placeholder codefix" {
            let findings =
                fieldAttrsViaReflection typeof<PartiallyClassifiedApi>
                |> Recognition.authFindings

            let names = findings |> List.map _.FieldName |> Set.ofList
            Expect.equal names (set [ "NakedOne"; "NakedTwo" ]) "TUR0001 raised for exactly the naked methods"

            findings
            |> List.iter (fun f ->
                Expect.equal f.Code Recognition.TUR0001 "code is TUR0001"

                Expect.equal
                    f.SuggestedAttribute
                    "[<AllowAnonymous>]"
                    "codefix inserts the fail-closed placeholder, never a real role")
        }

        test "a fully-classified record raises no TUR0001" {
            let findings =
                fieldAttrsViaReflection typeof<FullyServerClassifiedApi>
                |> Recognition.authFindings

            Expect.isEmpty findings "fully classified ⇒ clean"
        }
    ]

// ── source-name normalisation (the analyzer's input form) ────────────

let private sourceNameTests =
    testList "source-name recognition" [
        test "bare source names classify (no Attribute suffix)" {
            let fields = [
                {
                    Recognition.FieldName = "Guarded"
                    Recognition.AttributeNames = [ "RequiresRole" ]
                }
                {
                    Recognition.FieldName = "Naked"
                    Recognition.AttributeNames = []
                }
            ]

            Expect.equal
                (Recognition.unclassifiedAuthFieldNames fields)
                [ "Naked" ]
                "bare RequiresRole classifies; empty does not"
        }

        test "dotted mirror-family source names classify (last segment wins)" {
            let fields = [
                {
                    Recognition.FieldName = "M"
                    Recognition.AttributeNames = [ "ToolUp.Platform.AllowAnonymous" ]
                }
            ]

            Expect.isEmpty (Recognition.unclassifiedAuthFieldNames fields) "dotted AllowAnonymous classifies"
        }

        test "simpleName reconciles source and CLR forms to one key" {
            Expect.equal (Recognition.simpleName "RequiresRole") "RequiresRole" "source form"
            Expect.equal (Recognition.simpleName "RequiresRoleAttribute") "RequiresRole" "CLR form"

            Expect.equal
                (Recognition.simpleName "ToolUp.Platform.RequiresRoleAttribute")
                "RequiresRole"
                "dotted CLR form"
        }
    ]

// ── TUR0002 audit heuristic ──────────────────────────────────────────

let private tur0002Tests =
    testList "TUR0002 findings" [
        let methodWith hasAudit piiInput : Recognition.ApiMethod = {
            Field = {
                FieldName = "Pay"
                AttributeNames = (if hasAudit then [ "Audit" ] else []) @ [ "RequiresRole" ]
            }
            InputRecordHasPiiSafeField = piiInput
        }

        test "PII input + no audit ⇒ TUR0002" {
            let findings = Recognition.auditFindings [ methodWith false true ]
            Expect.equal (findings |> List.map _.Code) [ Recognition.TUR0002 ] "flagged"

            Expect.equal
                (findings |> List.map _.SuggestedAttribute)
                [ "[<Audit(\"Custom:TODO\")>]" ]
                "codefix inserts the audit placeholder"
        }

        test "PII input + audit present ⇒ clean" {
            Expect.isEmpty (Recognition.auditFindings [ methodWith true true ]) "audited ⇒ no finding"
        }

        test "no PII input ⇒ clean even without audit" {
            Expect.isEmpty (Recognition.auditFindings [ methodWith false false ]) "no PII ⇒ no finding"
        }
    ]

[<Tests>]
let tests =
    testList "RemotingAnalyzerRecognition" [ parityTests; tur0001Tests; sourceNameTests; tur0002Tests ]