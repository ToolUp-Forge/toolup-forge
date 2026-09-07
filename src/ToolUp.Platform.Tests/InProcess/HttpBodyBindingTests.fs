// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.HttpBodyBindingTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.AuthProviders.Passkey.PasskeyTypes
open ToolUp.AuthProviders.Passkey.PasskeyStores
open ToolUp.AuthProviders.Passkey.PasskeyHost

// ─── Phase 763 — the null-literal request body ───────────────────────
//
// `null` is valid JSON. The idiom this phase closes —
//
//     try Some (JsonSerializer.Deserialize<'T>(body, options))
//     with _ -> None
//
// — reads as "a body that will not bind becomes `None`", and for a
// truncated body that is what happens. For `null` nothing throws: the
// deserialiser returns a null reference typed as `'T`, the `Some` arm
// runs, and the first field access afterwards throws OUTSIDE the
// `try`. Every handler that dereferenced the bound value after its own
// `try` answered a 500 for what is a 400-class input; the one that did
// not dereference it (consent) wrote a null row into the audit trail
// and answered 204.
//
// Two levels of assertion here, and both are needed:
//
//   * the SEAM (`HttpBodyBinding`) is pinned directly, including the
//     property the whole phase rests on — `Ok` never carries a null —
//     and the arms are told apart, so a bind that collapsed every
//     failure into one cause would fail here rather than pass three
//     endpoint tests;
//   * each SWEPT ENDPOINT is driven end to end through a TestServer,
//     because the seam being right is not the claim: the claim is that
//     these handlers now use it. A test only at the seam would have
//     passed against the unswept tree.
//
// The ad-analytics pair is asserted in `AdAnalyticsObservabilityTests`
// instead, beside the Phase 466 parse-drop counter their fix has to
// keep feeding — the counter is half the acceptance there and none of
// it here.

let private jsonOptions = FableConverters.create ()

/// The four bytes a client sends when it serialises an absent value and
/// posts it anyway. Written as a literal on purpose — round-tripping a
/// `None` through the serialiser would produce whatever the converters
/// happen to emit today, and the fixture is about the wire, not about
/// our own encoder.
let private nullLiteral = "null"

// ── Section 1 — the seam ────────────────────────────────────────────

type private Sample = { Name: string; Count: int }

let private bindSample (body: string) =
    HttpBodyBinding.tryBindJsonString<Sample> jsonOptions body

let private seamTests =
    testList "the seam" [

        test "a null-literal body binds to Error BodyNull, never to a null record" {
            match bindSample nullLiteral with
            | Error HttpBodyBinding.BodyNull -> ()
            | Error other -> failtestf "expected BodyNull, got %A" other
            | Ok value ->
                // The pre-763 behaviour, stated as the failure it is:
                // the old idiom reached exactly here with `value` null.
                failtestf "a null body must not bind at all; it bound to %A (isNull = %b)" value (isNull (box value))
        }

        test "an absent, empty or whitespace body is the same class as a null one" {
            // Not cosmetic. `Deserialize("")` throws, so these used to
            // be reported to an operator as MALFORMED — sending whoever
            // read the log hunting a wire-format skew that does not
            // exist. The status they answer is unchanged (GP 11).
            for body in [ null; ""; "   "; "\n" ] do
                match bindSample body with
                | Error HttpBodyBinding.BodyNull -> ()
                | other -> failtestf "expected BodyNull for %A, got %A" body other
        }

        test "a truncated body is BodyMalformed and carries the parser's own account" {
            match bindSample "{ this is not json" with
            | Error(HttpBodyBinding.BodyMalformed detail) ->
                Expect.isNotEmpty detail "the deserialiser's message is kept, for the log line"
            | other -> failtestf "expected BodyMalformed, got %A" other
        }

        test "the two causes are distinguishable, and stay so" {
            // The control the endpoint tests lean on: they assert a
            // `reason=` token on a log line, which would agree with a
            // bind that answered `BodyNull` for everything.
            Expect.equal (HttpBodyBinding.BodyBindError.reason HttpBodyBinding.BodyNull) "null-body" "null token"

            Expect.equal
                (HttpBodyBinding.BodyBindError.reason (HttpBodyBinding.BodyMalformed "x"))
                "malformed-body"
                "malformed token"

            Expect.notEqual
                (HttpBodyBinding.BodyBindError.reason HttpBodyBinding.BodyNull)
                (HttpBodyBinding.BodyBindError.reason (HttpBodyBinding.BodyMalformed "x"))
                "an operator can tell them apart"
        }

        test "a well-formed body binds unchanged" {
            let sample = { Name = "slot"; Count = 3 }

            match bindSample (JsonSerializer.Serialize(sample, jsonOptions)) with
            | Ok bound -> Expect.equal bound sample "the valid path is byte-for-byte what it was (GP 11)"
            | other -> failtestf "expected Ok, got %A" other
        }

        test "every failure arm answers 400 — the one fact the whole tier shares" {
            Expect.equal
                (HttpBodyBinding.statusCodeFor HttpBodyBinding.BodyNull)
                400
                "a null body is the caller's error"

            Expect.equal
                (HttpBodyBinding.statusCodeFor (HttpBodyBinding.BodyMalformed "x"))
                400
                "and so is a truncated one"
        }

        test "the bind is generic over the record, not written per handler" {
            // The swept handlers bind four different records through
            // one function. Pinning a second type here is what says the
            // seam is generic rather than accidentally right for one
            // shape — `RegisterBeginRequest` is all-optional fields, so
            // a `null` body is exactly the shape a lenient binder would
            // be tempted to accept.
            match HttpBodyBinding.tryBindJsonString<RegisterBeginRequest> jsonOptions nullLiteral with
            | Error HttpBodyBinding.BodyNull -> ()
            | other -> failtestf "expected BodyNull for an all-optional record, got %A" other
        }
    ]

// ── Section 2 — the consent endpoint ────────────────────────────────

/// Captures what reached `IAuditLog`, so the consent test can assert
/// the row that used to be written is not written any more. A status
/// assertion alone would miss it: the pre-763 handler answered 204 and
/// recorded a null.
type private CapturingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Recorded = recorded |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) =
            lock recorded (fun () -> recorded.Add((scopeId, audit)))
            async { return () }

        member _.GetAuditTrail(_scopeId, _dateRange, _eventType) = async { return [] }

type private ConsentHarness = {
    Client: HttpClient
    Audit: CapturingAuditLog
    Dispose: unit -> unit
}

let private buildConsent () : ConsentHarness =
    let audit = CapturingAuditLog()

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<IAuditLog>(audit :> IAuditLog) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose ConsentApiHandler.routes))
                |> ignore)
            .Build()

    host.Start()

    {
        Client = host.GetTestClient()
        Audit = audit
        Dispose = fun () -> host.Dispose()
    }

let private post (client: HttpClient) (path: string) (body: string) =
    let req =
        new HttpRequestMessage(
            HttpMethod.Post,
            path,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        )

    client.SendAsync req |> Async.AwaitTask |> Async.RunSynchronously

let private validConsentBody () =
    let ev: ConsentEvent = {
        AnonymousUserId = "anon-763"
        Category = Analytics
        Decision = Granted
        Timestamp = DateTimeOffset.UtcNow
        CmpProvider = "test-cmp"
    }

    JsonSerializer.Serialize(ev, jsonOptions)

let private consentTests =
    testList "the consent endpoint" [

        test "a null-literal consent body answers 400 and records nothing" {
            let h = buildConsent ()

            try
                let response = post h.Client "/api/_platform/consent" nullLiteral

                // This endpoint never dereferenced the event, so it did
                // not 500 — it did something quieter and worse: it
                // answered 204 and put a null row in the consent trail,
                // which is a compliance surface.
                Expect.equal response.StatusCode HttpStatusCode.BadRequest "a null body is refused, not accepted"

                Expect.isEmpty h.Audit.Recorded "and nothing reaches the audit trail — the pre-763 row was a null"
            finally
                h.Dispose()
        }

        test "a well-formed consent body still records and still answers 204" {
            // The GP 11 control. Without it, a fix that refused every
            // body would pass the test above.
            let h = buildConsent ()

            try
                let response = post h.Client "/api/_platform/consent" (validConsentBody ())

                Expect.equal response.StatusCode HttpStatusCode.NoContent "unchanged"

                // The audit write is fire-and-forget (`Async.Start`), so
                // give it a moment rather than racing it.
                let mutable waited = 0

                while List.isEmpty h.Audit.Recorded && waited < 40 do
                    System.Threading.Thread.Sleep 25
                    waited <- waited + 1

                Expect.equal (List.length h.Audit.Recorded) 1 "the consent row is still recorded"
            finally
                h.Dispose()
        }

        test "a truncated consent body answers 400, exactly as it did before" {
            let h = buildConsent ()

            try
                let response = post h.Client "/api/_platform/consent" "{ not json"
                Expect.equal response.StatusCode HttpStatusCode.BadRequest "unchanged"
                Expect.isEmpty h.Audit.Recorded "and still records nothing"
            finally
                h.Dispose()
        }
    ]

// ── Section 3 — the ad-unit admin endpoint ──────────────────────────

let private adminContext () = {
    AccessContext.unrestricted (AuthenticatedUser "admin-763") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private buildAdUnits () =
    let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<IEntityStore>(store) |> ignore
                        services.AddSingleton<EntityRegistry>(registry) |> ignore
                        services.AddSingleton<AccessContext>(adminContext ()) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose AdUnitConfigApi.routes))
                |> ignore)
            .Build()

    host.Start()
    host.GetTestClient(), (fun () -> host.Dispose())

let private validSlotBody () =
    let config: AdSlotConfig = {
        AdClientId = "ca-pub-test"
        SlotId = "sidebar-top"
        Format = AdAuto
        Style = None
    }

    JsonSerializer.Serialize(config, jsonOptions)

let private adUnitTests =
    testList "the ad-unit admin endpoint" [

        test "a null-literal ad-unit body answers 400 rather than 500" {
            let client, dispose = buildAdUnits ()

            try
                let response = post client "/api/_platform/admin/ad-units" nullLiteral

                // `upsertHandler`'s `String.IsNullOrWhiteSpace
                // config.SlotId` guard took the dereference. Being
                // admin-gated makes it a smaller exposure, not a
                // different defect.
                Expect.equal response.StatusCode HttpStatusCode.BadRequest "400"

                Expect.notEqual
                    response.StatusCode
                    HttpStatusCode.InternalServerError
                    "the pre-763 answer was an unhandled NullReferenceException"
            finally
                dispose ()
        }

        test "a well-formed ad-unit body still round-trips" {
            let client, dispose = buildAdUnits ()

            try
                let response = post client "/api/_platform/admin/ad-units" (validSlotBody ())
                Expect.equal response.StatusCode HttpStatusCode.OK "unchanged (GP 11)"
            finally
                dispose ()
        }

        test "a body whose SlotId is blank is still refused for THAT reason" {
            // The guard the null used to crash inside must still bite on
            // the input it was written for.
            let client, dispose = buildAdUnits ()

            try
                let blank: AdSlotConfig = {
                    AdClientId = "ca-pub-test"
                    SlotId = "  "
                    Format = AdAuto
                    Style = None
                }

                let body = JsonSerializer.Serialize(blank, jsonOptions)

                let response = post client "/api/_platform/admin/ad-units" body
                Expect.equal response.StatusCode HttpStatusCode.BadRequest "still refused"
            finally
                dispose ()
        }
    ]

// ── Section 4 — the passkey ceremony-begin endpoints ────────────────
//
// These two are the one place in the sweep where the correct answer is
// NOT a 400. A bodyless `register/begin` is how an already-signed-in
// caller adds a passkey, and a bodyless `assert/begin` is the
// usernameless (discoverable-credential) flow — both are supported, and
// both are why the handlers fall back to an empty request record rather
// than refusing. So the assertion is an EQUIVALENCE: a `null` body must
// behave exactly as no body does. Pre-763 it did not — it produced a
// null record whose first field read threw, and the endpoints answered
// 500.

let private stubFido2 () : Fido2NetLib.IFido2 =
    // Neither ceremony-BEGIN handler reaches the completion members, so
    // the canned values are unreachable placeholders rather than
    // fixtures: what these two endpoints ask of Fido2 is an options
    // object, and that is all this double promises.
    { new Fido2NetLib.IFido2 with
        member _.RequestNewCredential(_) =
            Fido2NetLib.CredentialCreateOptions(
                Rp = Fido2NetLib.PublicKeyCredentialRpEntity("example.com", "Example", ""),
                User = Fido2NetLib.Fido2User(Id = [| 0uy |], Name = "x", DisplayName = "x"),
                Challenge = [| 0uy |],
                PubKeyCredParams = ResizeArray<Fido2NetLib.PubKeyCredParam>()
            )

        member _.GetAssertionOptions(_) = Fido2NetLib.AssertionOptions()

        member _.MakeNewCredentialAsync(_, _: CancellationToken) =
            Task.FromResult(Unchecked.defaultof<Fido2NetLib.Objects.RegisteredPublicKeyCredential>)

        member _.MakeAssertionAsync(_, _: CancellationToken) =
            Task.FromResult(Unchecked.defaultof<Fido2NetLib.Objects.VerifyAssertionResult>)
    }

/// An `IAuthProvider` that reports nobody. `registerBeginHandler`
/// resolves the current user through it, and an anonymous caller with
/// no bootstrap token is exactly the case whose refusal we want to see
/// INSTEAD of a crash.
let private anonymousAuthProvider () : IAuthProvider =
    { new IAuthProvider with
        member _.GetUser(_) = async { return AuthenticatedUser.anonymous }
        member _.ValidateRequest(_) = async { return Error "anonymous" }
        member _.IsCryptographicallyVerified = true
    }

let private buildPasskey () =
    let runtime: PasskeyRuntime = {
        Config = PasskeyConfig.create "example.com" "Example" [ "https://example.com" ]
        Fido2 = stubFido2 ()
        Credentials = PasskeyCredentialStore(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage)
        Challenges = InMemoryPasskeyChallengeStore()
        SecretCache = ref None
    }

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<PasskeyRuntime>(runtime) |> ignore
                        services.AddSingleton<IAuthProvider>(anonymousAuthProvider ()) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe routes)
                |> ignore)
            .Build()

    host.Start()
    host.GetTestClient(), (fun () -> host.Dispose())

let private passkeyTests =
    testList "the passkey ceremony-begin endpoints" [

        test "a null-literal register/begin body behaves exactly as no body at all" {
            let client, dispose = buildPasskey ()

            try
                let noBody = post client "/api/passkey/register/begin" ""
                let nullBody = post client "/api/passkey/register/begin" nullLiteral

                Expect.notEqual
                    nullBody.StatusCode
                    HttpStatusCode.InternalServerError
                    "pre-763 this was a 500: resolveIdentity read BootstrapToken off a null record"

                Expect.equal
                    nullBody.StatusCode
                    noBody.StatusCode
                    "a null body IS an absent body here — the empty request is the supported bodyless flow"
            finally
                dispose ()
        }

        test "a null-literal assert/begin body behaves exactly as no body at all" {
            let client, dispose = buildPasskey ()

            try
                let noBody = post client "/api/passkey/assert/begin" ""
                let nullBody = post client "/api/passkey/assert/begin" nullLiteral

                Expect.notEqual
                    nullBody.StatusCode
                    HttpStatusCode.InternalServerError
                    "pre-763 this was a 500: beginAssertion read Username off a null record"

                Expect.equal nullBody.StatusCode noBody.StatusCode "the usernameless flow is unchanged (GP 11)"
            finally
                dispose ()
        }
    ]

[<Tests>]
let tests =
    testList "Phase 763 — null-literal JSON body handling" [ seamTests; consentTests; adUnitTests; passkeyTests ]