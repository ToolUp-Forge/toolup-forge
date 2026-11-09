// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Remoting.ApiContractTests

open System
open System.Collections.Generic
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

// ─── Phase 1055 — the compose-time refusals, pinned word for word ──────
//
// Every sentence below was captured from the tree BEFORE `ApiContract`
// existed, one fixture record per refusal. `ContractRefusal.render` is held
// to them byte for byte: a refusal is a value now, but the operator still
// reads the same prose.

/// Collide by simple name with a sanctioned marker; declared outside both
/// sanctioned families.
module Foreign =
    [<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
    type PublicEndpointAttribute() =
        inherit Attribute()

    [<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
    type IdempotentAttribute() =
        inherit Attribute()

type NotARecordApi() =
    member _.Echo(x: int) = x

type ForeignAuthApi = {
    [<Foreign.PublicEndpoint>]
    Echo: int -> Async<int>
}

type UnclassifiedApi = {
    [<PublicEndpoint>]
    Open: int -> Async<int>
    Bare: int -> Async<int>
}

type RoundTripApi = {
    [<PublicEndpoint>]
    Echo: int -> Async<int>
}

type RoleGatedApi = {
    [<RequiresRole "Admin">]
    Purge: int -> Async<int>
    [<RequiresRole "Admin"; Audit "PolicyChanged">]
    Grant: int -> Async<int>
}

type CollisionApi = {
    [<Foreign.Idempotent>]
    Pay: int -> Async<int>
}

type StreamingGuardedApi = {
    [<RateLimit(5, 60)>]
    Ticks: int -> IAsyncEnumerable<int>
}

type VersionedStreamingApi = {
    Ticks_V2: int -> IAsyncEnumerable<int>
}

type NotAValidatorInput = {
    [<Custom(typeof<System.Text.StringBuilder>)>]
    Code: string
}

type UnbuildableValidatorApi = {
    Send: NotAValidatorInput -> Async<unit>
}

type NonPositiveSchemaApi = {
    [<SupportsSchema 0>]
    Get: int -> Async<int>
}

type UnparseableRetirementApi = {
    [<DeprecatedSchema "not-a-date">]
    Get_V1: int -> Async<int>
}

type DuplicateSchemaApi = {
    [<SupportsSchema 1>]
    Get: int -> Async<int>
    Get_V1: int -> Async<int>
}

let private echo (x: int) = async { return x }

let private ticks (_: int) : IAsyncEnumerable<int> =
    { new IAsyncEnumerable<int> with
        member _.GetAsyncEnumerator _ =
            failwith "a refused composition never enumerates"
    }

let private anonymous =
    { new IAuthContext with
        member _.SubjectId = "anon"
        member _.IsAnonymous() = true
        member _.HasRole _ = false
        member _.HasClaim(_, _) = false
        member _.HasTenant() = false
    }

let private resolver (_: HttpContext) = async { return anonymous }

let private noRateLimit =
    { new IRateLimitStore with
        member _.TryAcquire(_, _, _) = async { return RateLimitAllowed }
    }

/// The message a composition raises, or a failure naming that it started.
let private refusalOf (build: unit -> obj) : string =
    let started =
        try
            build () |> ignore
            None
        with :? InvalidOperationException as ex ->
            Some ex.Message

    match started with
    | Some message -> message
    | None -> failwith "the composition started; a refusal was expected"

let private compose (options: RemotingOptions<HttpContext, 'impl>) () = box (Remoting.buildHttpHandler options)

let private nl = Environment.NewLine

/// The thirteen pins: twelve compose-time sentences and the evaluator's
/// defensive arm over the legacy `Unclassified` case (I77).
[<Tests>]
let tests =
    testList "Phase 1055 — the compose-time refusals, byte for byte" [

        testCase "I01 — a non-record API type with a record seam composed" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue (NotARecordApi())
                    |> Remoting.withRateLimitStore noRateLimit
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API type 'NotARecordApi' is not an F# record, but at least one attribute-driven seam (authorisation / rate-limit / audit / idempotency / validation) was composed. These seams require record-field reflection. Convert 'NotARecordApi' to an F# record, or remove the seam composition."
                "the not-a-record sentence")

        testCase "I02 — a foreign authorisation marker" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { ForeignAuthApi.Echo = echo }
                    |> Remoting.withAuthContext resolver
                    |> compose
                )

            let rendered = typeof<Foreign.PublicEndpointAttribute>.AssemblyQualifiedName

            Expect.equal
                message
                (sprintf
                    "ToolUp.Remoting refused to start: API record 'ForeignAuthApi' has 1 field(s) carrying an attribute whose name matches an authorisation marker but which is NOT one of the two sanctioned families: [Echo carries '%s']. Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) markers classify a method; an attribute of the same name from any other namespace or assembly is refused rather than honoured, because a name collision must never decide a method's authorisation. Replace it with the sanctioned attribute of the same name, or rename your own attribute. See docs/migrations/335-qualified-auth-attribute-matching.md."
                    rendered)
                "the foreign-auth-marker sentence")

        testCase "I03 — an unclassified method under a composed resolver" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { Open = echo; Bare = echo }
                    |> Remoting.withAuthContext resolver
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'UnclassifiedApi' has 1 unclassified method(s): [Bare]. Phase 69d requires every API record field to carry one of [<RequiresRole>], [<RequiresClaim>], [<TenantScoped>], [<AllowAnonymous>], or [<PublicEndpoint>]. See ToolUp.Remoting README §69d."
                "the unclassified sentence")

        testCase "I04 — a route that does not round-trip to its field" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { RoundTripApi.Echo = echo }
                    |> Remoting.withAuthContext resolver
                    |> Remoting.withRouteBuilder (fun t m -> sprintf "/%s/%s" t (m.ToLowerInvariant()))
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'RoundTripApi' has 1 classified method(s) whose field name does not round-trip through the active RouteBuilder: [Echo → runtime-key 'echo']. The dispatcher keys per-request authorisation by the trailing path segment of the built route; when that diverges from the field name the classification lookup misses and (Phase 132 deny-on-miss) denies every call. Use a RouteBuilder whose trailing segment equals the method name."
                "the round-trip sentence")

        testCase "I05 — a role-gated method without an audit annotation" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { Purge = echo; Grant = echo }
                    |> Remoting.withAuthContext resolver
                    |> Remoting.withAuditRequiredOnRoleGated
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: audit-required-on-role-gated is composed (TOOLUP_AUDIT_ADMIN_REQUIRED), but API record 'RoleGatedApi' has 1 role-gated method(s) without an [<Audit>] annotation: [Purge]. Annotate each with [<Audit \"<kind>\">] (see ToolUp.Remoting README §69h), or unset the gate."
                "the role-gated-unaudited sentence")

        testCase "I06 — a pre-flight marker collision" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { CollisionApi.Pay = echo }
                    |> compose
                )

            let rendered = typeof<Foreign.IdempotentAttribute>.AssemblyQualifiedName

            Expect.equal
                message
                (sprintf
                    "ToolUp.Remoting refused to start: API record 'CollisionApi' has 1 attribute(s) whose name matches a dispatch pre-flight marker but which are NOT one of the two sanctioned families: [Pay [idempotency] carries '%s']. Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) attributes arm rate limiting, idempotency, audit emission or PII-safe payload inclusion; an attribute of the same name from any other namespace or assembly is refused rather than honoured, because a name collision must never silently decide whether a guard is armed — in either direction. Replace it with the sanctioned attribute of the same name, or rename your own attribute. See docs/migrations/727-attribute-recognition-sweep.md."
                    rendered)
                "the marker-collision sentence")

        testCase "I07 — a streaming method carrying a pre-flight attribute" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { StreamingGuardedApi.Ticks = ticks }
                    |> compose
                )

            Expect.equal
                message
                (sprintf
                    "ToolUp.Remoting refused to start: API record 'StreamingGuardedApi' has streaming method(s) carrying pre-flight attributes the SSE dispatch path does not enforce today. Silently honouring them would route around the declared guard.%s  'Ticks' carries: [RateLimit(5, 60s)]%sFix: either remove the attribute, or convert the method off the IAsyncEnumerable<'T> return shape until per-frame seam composition lands."
                    nl
                    nl)
                "the streaming-preflight sentence")

        testCase "I08 — a versioned streaming method" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue {
                        VersionedStreamingApi.Ticks_V2 = ticks
                    }
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'VersionedStreamingApi' has streaming method(s) carrying wire-schema versioning the SSE dispatch path does not honour: [Ticks]. The SSE short-circuit resolves its handler before the negotiated version is applied, so the declared versions would be silently ignored. Fix: drop the [<SupportsSchema>] / [<DeprecatedSchema>] annotation, or move the method off the IAsyncEnumerable<'T> return shape."
                "the versioned-streaming sentence")

        testCase "I10 — a declared validator that cannot be built" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue {
                        UnbuildableValidatorApi.Send = fun _ -> async { return () }
                    }
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record UnbuildableValidatorApi declares validators that cannot run: Send Code: [<Custom>] validator System.Text.StringBuilder cannot be built (it does not implement IFieldValidator)"
                "the unbuildable-validator sentence")

        testCase "I90 — a non-positive wire-schema version" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue { NonPositiveSchemaApi.Get = echo }
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'NonPositiveSchemaApi' declares an invalid wire-schema versioning. Field 'Get' declares wire-schema version 0; versions are positive integers. Fix the annotation, or drop it — a method with neither a [<SupportsSchema>] attribute nor a _V<n> field-name suffix serves the composed default version only."
                "the non-positive-version sentence")

        testCase "I90 (extra) — an unparseable retirement date" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue {
                        UnparseableRetirementApi.Get_V1 = echo
                    }
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'UnparseableRetirementApi' declares an invalid wire-schema versioning. Field 'Get_V1' carries [<DeprecatedSchema \"not-a-date\">], which is not an ISO-8601 date. Use a form like \"2027-01-01\". Fix the annotation, or drop it — a method with neither a [<SupportsSchema>] attribute nor a _V<n> field-name suffix serves the composed default version only."
                "the unparseable-retirement sentence")

        testCase "I90 (extra) — two handlers for one version" (fun () ->
            let message =
                refusalOf (
                    Remoting.createApi ()
                    |> Remoting.fromValue {
                        DuplicateSchemaApi.Get = echo
                        Get_V1 = echo
                    }
                    |> compose
                )

            Expect.equal
                message
                "ToolUp.Remoting refused to start: API record 'DuplicateSchemaApi' declares an invalid wire-schema versioning. Method 'Get' has two handlers for wire-schema version 1 ('Get' and 'Get_V1'). One version, one handler. Fix the annotation, or drop it — a method with neither a [<SupportsSchema>] attribute nor a _V<n> field-name suffix serves the composed default version only."
                "the duplicate-version sentence")

        testCase "I77 — the evaluator still denies the legacy unclassified case" (fun () ->
            Expect.equal
                (AuthClassifier.evaluate MethodClassification.Unclassified None)
                (Deny "unclassified-method")
                "the defensive arm denies, by its reason")
    ]