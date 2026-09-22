module ToolUp.Stripe.Server.Tests.WebhookFixtureReplayTests

// Phase 211 — replay a corpus of real-shaped Stripe webhook event payloads
// through the SHIPPED wire: `Routes.stripeWebhookWith` → `WebhookSigner.verify`
// → `StripeEvent.decode` → `IWebhookIdempotencyStore` → `ITierTokenSink`.
//
// The sibling packs (`StripeEventTests`, `StripeWebhookHandlerTests`) exercise
// hand-built one-line bodies. Those bodies are written by the same hand that
// wrote the decoder, so they agree with it by construction; a real provider
// payload does not. The fixtures under `fixtures/` carry the genuine envelope
// (`object`/`api_version`/`livemode`/`pending_webhooks`/`request`), nested
// `data.object` sub-objects, `previous_attributes`, and a long tail of fields
// the catalogue does not model — the shapes that actually arrive.
//
// The fixture JSON carries NO secret: the `Stripe-Signature` header is
// synthesised at test time from the test secret with `WebhookTestHarness.signHeader`.
// The identifiers in the corpus are Stripe's documented sample-payload shapes,
// not captures of any live account.

open System
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.Server
open ToolUp.Stripe.Server.Tests.WebhookTestHarness

// ─── Corpus ───────────────────────────────────────────────────────────

/// Fixture corpus root beside the test binary — the fsproj's
/// `<None Include="fixtures\**\*.json" CopyToOutputDirectory="PreserveNewest" />`
/// puts it there (the `ToolUp.Platform.Build.Tests/fixtures/` precedent).
let private fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures")

let private readFixture (name: string) : string =
    let path = Path.Combine(fixtureRoot, name)

    if not (File.Exists path) then
        failtestf
            "fixture %s is missing from %s — the fsproj's <None Include=\"fixtures\\**\\*.json\"> copy did not run"
            name
            fixtureRoot

    File.ReadAllText path

/// One fixture and everything the shipped wire must make of it.
type private FixtureCase = {
    /// File name under `fixtures/`.
    File: string
    /// The `id` the envelope carries — the dedup key `Routes` reads.
    EventId: string
    /// Whether `TierTokenSink.isTierChanging` must classify it as
    /// entitlement-affecting (so the composed sink fires).
    TierChanging: bool
    /// Asserts the discriminated case and its decoded payload fields.
    Check: StripeEvent -> unit
}

let private subscriptionId = "sub_1PgF4A2eZvKYlo2CkHdVbTzp"
let private customerId = "cus_QZ1sT4hLmN8pKd"

let private cases: FixtureCase list = [
    {
        File = "customer-created.json"
        EventId = "evt_1PgF3a2eZvKYlo2CqLcAzUeR"
        TierChanging = false
        Check =
            fun ev ->
                match ev with
                | CustomerCreated p -> Expect.equal p.CustomerId customerId "customer id from data.object.id"
                | other -> failtestf "expected CustomerCreated, got %A" other
    }
    {
        File = "customer-subscription-created.json"
        EventId = "evt_1PgF4B2eZvKYlo2CwR7mNtQx"
        TierChanging = true
        Check =
            fun ev ->
                match ev with
                | SubscriptionCreated p ->
                    Expect.equal p.SubscriptionId subscriptionId "subscription id"
                    Expect.equal p.CustomerId customerId "owning customer id"
                    Expect.equal p.Status "active" "status maps from data.object.status"
                | other -> failtestf "expected SubscriptionCreated, got %A" other
    }
    {
        File = "customer-subscription-updated.json"
        EventId = "evt_1PgF5C2eZvKYlo2CdVpM3rTa"
        TierChanging = true
        Check =
            fun ev ->
                match ev with
                | SubscriptionUpdated p ->
                    Expect.equal p.SubscriptionId subscriptionId "subscription id"
                    Expect.equal p.CustomerId customerId "owning customer id"
                    // The fixture also carries `data.previous_attributes.status = "active"`.
                    // The decoder must read the CURRENT status, never the previous one.
                    Expect.equal p.Status "past_due" "current status, not previous_attributes.status"
                | other -> failtestf "expected SubscriptionUpdated, got %A" other
    }
    {
        File = "customer-subscription-deleted.json"
        EventId = "evt_1PgF6D2eZvKYlo2CzNrW9kBs"
        TierChanging = true
        Check =
            fun ev ->
                match ev with
                | SubscriptionDeleted p ->
                    Expect.equal p.SubscriptionId subscriptionId "subscription id"
                    Expect.equal p.CustomerId customerId "owning customer id"
                    Expect.equal p.Status "canceled" "status maps from data.object.status"
                | other -> failtestf "expected SubscriptionDeleted, got %A" other
    }
    {
        File = "checkout-session-completed.json"
        EventId = "evt_1PgF7E2eZvKYlo2CxLpQ4mVd"
        TierChanging = true
        Check =
            fun ev ->
                match ev with
                | CheckoutSessionCompleted p ->
                    Expect.equal p.SessionId "cs_test_a1BcD2eFgH3iJkL4mN5oP6qR7sT8uV9wX0yZ" "checkout session id"
                    Expect.equal p.CustomerId customerId "customer attached by the session"
                | other -> failtestf "expected CheckoutSessionCompleted, got %A" other
    }
    {
        File = "invoice-paid.json"
        EventId = "evt_1PgF8F2eZvKYlo2CtRbY5nKw"
        TierChanging = false
        Check =
            fun ev ->
                match ev with
                | InvoicePaid p ->
                    Expect.equal p.InvoiceId "in_1PgF4A2eZvKYlo2CqTzWdNmR" "invoice id"
                    Expect.equal p.CustomerId customerId "owning customer id"
                    // `amount_paid`, not the equal-valued `amount_due`/`total`
                    // sitting beside it — this fixture's three amounts agree, so
                    // the sibling `invoice-payment-failed` fixture (where they do
                    // NOT agree) is what pins the slot.
                    Expect.equal p.Amount 2000L "amount from data.object.amount_paid"
                | other -> failtestf "expected InvoicePaid, got %A" other
    }
    {
        File = "invoice-payment-failed.json"
        EventId = "evt_1PgF9G2eZvKYlo2CpWdN6rLt"
        TierChanging = false
        Check =
            fun ev ->
                match ev with
                | InvoicePaymentFailed p ->
                    Expect.equal p.InvoiceId "in_1PgF5B2eZvKYlo2CmXqRdTwN" "invoice id"
                    Expect.equal p.CustomerId customerId "owning customer id"
                    // `amount_due` = 2000 while `amount_paid` = 0: reading the
                    // wrong slot here bills nothing and looks green.
                    Expect.equal p.Amount 2000L "amount from data.object.amount_due"
                | other -> failtestf "expected InvoicePaymentFailed, got %A" other
    }
    {
        File = "unknown-event-tail.json"
        EventId = "evt_1PgFAH2eZvKYlo2CbNqT7sMv"
        TierChanging = false
        Check =
            fun ev ->
                match ev with
                | Unknown rawJson ->
                    // The long tail is absorbed, never an error, and the raw body
                    // is carried verbatim so a consumer can hand-route it.
                    Expect.stringContains rawJson "radar.early_fraud_warning.created" "raw body carried verbatim"
                | other -> failtestf "expected Unknown for an uncatalogued event type, got %A" other
    }
]

// ─── Instrumented seams ───────────────────────────────────────────────

/// Wraps a real store and counts what the handler asked of it, so the
/// idempotency assertion is about the SEAM and not only about the status code.
type private CountingIdempotencyStore(inner: IWebhookIdempotencyStore) =
    let claims = ref 0
    let granted = ref 0
    /// Total `TryClaim` calls the handler made.
    member _.Claims = claims.Value
    /// How many of those calls won the claim (i.e. were not replays).
    member _.Granted = granted.Value

    interface IWebhookIdempotencyStore with
        member _.TryClaim(eventId: string) : Async<bool> = async {
            Interlocked.Increment claims |> ignore
            let! won = inner.TryClaim eventId

            if won then
                Interlocked.Increment granted |> ignore

            return won
        }

/// Records every event the sink is fired for — the shipped-path witness of
/// `TierTokenSink.isTierChanging`.
type private RecordingTierTokenSink() =
    let fired = ResizeArray<VerifiedEvent>()
    member _.Fired = List.ofSeq fired

    interface ITierTokenSink with
        member _.OnBillingEvent (event: VerifiedEvent) (_: Microsoft.AspNetCore.Http.HttpContext) : Task<unit> = task {
            lock fired (fun () -> fired.Add event)
        }

/// Consumer handler returning `Ok`, recording what it was handed.
let private recordingHandler
    (seen: ResizeArray<VerifiedEvent>)
    : VerifiedEvent -> Microsoft.AspNetCore.Http.HttpContext -> Task<Result<unit, string>> =
    fun ev _ -> task {
        lock seen (fun () -> seen.Add ev)
        return Ok()
    }

/// Flip the last hex digit of the `v1=` signature — a header that is
/// well-formed (so it is not `MalformedHeader`) but does not match the body.
let private tamperSignature (header: string) : string =
    let last = header[header.Length - 1]
    let replacement = if last = '0' then '1' else '0'
    header.Substring(0, header.Length - 1) + string replacement


/// The `id` the envelope carries — what `Routes` deduplicates on.
let private envelopeEventId (body: string) : string =
    use doc = System.Text.Json.JsonDocument.Parse body
    doc.RootElement.GetProperty("id").GetString()

/// Compose the shipped handler over instrumented seams.
let private serve
    (store: CountingIdempotencyStore)
    (sink: RecordingTierTokenSink)
    (handler: VerifiedEvent -> Microsoft.AspNetCore.Http.HttpContext -> Task<Result<unit, string>>)
    =
    let options =
        WebhookOptions.create ()
        |> WebhookOptions.withStore (store :> IWebhookIdempotencyStore)
        |> WebhookOptions.withTierTokenSink (sink :> ITierTokenSink)

    makeServer (Routes.stripeWebhookWith options config handler)

let private newStore () =
    CountingIdempotencyStore(InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

// ─── Tests ────────────────────────────────────────────────────────────

let private corpusTest = test "every fixture on disk is replayed by a declared case" {
    let onDisk =
        Directory.GetFiles(fixtureRoot, "*.json")
        |> Array.map Path.GetFileName
        |> Array.sort

    let declared = cases |> List.map (fun c -> c.File) |> List.sort |> List.toArray

    Expect.isNonEmpty onDisk "the fixture corpus was copied beside the test binary"
    Expect.equal onDisk declared "fixtures on disk and declared cases are the same set"
}

let private replayTest (case: FixtureCase) =
    let name = sprintf "%s — replays through the shipped handler" case.File

    test name {
        let body = readFixture case.File
        let seen = ResizeArray<VerifiedEvent>()
        let store = newStore ()
        let sink = RecordingTierTokenSink()
        use server = serve store sink (recordingHandler seen)

        let resp = (post server (signHeader DateTimeOffset.UtcNow body) body).Result

        Expect.equal resp.StatusCode HttpStatusCode.OK "handler Ok maps to 200"
        Expect.hasLength seen 1 "consumer handler invoked exactly once"

        let verified = seen[0]
        Expect.equal verified.Body body "the verified body is the exact bytes the HMAC covered"
        Expect.equal (envelopeEventId body) case.EventId "the declared dedup key is the id the envelope carries"
        case.Check verified.Event

        Expect.equal (TierTokenSink.isTierChanging verified.Event) case.TierChanging "isTierChanging classification"

        let expectedFires = if case.TierChanging then 1 else 0
        Expect.hasLength sink.Fired expectedFires "the composed sink fires only on a tier-changing event"

        Expect.equal store.Claims 1 "the handler claimed the event id once"
        Expect.equal store.Granted 1 "a first delivery wins its claim"
    }

let private redeliveryTest (case: FixtureCase) =
    let name = sprintf "%s — redelivery is idempotent through the store" case.File

    test name {
        let body = readFixture case.File
        let seen = ResizeArray<VerifiedEvent>()
        let store = newStore ()
        let sink = RecordingTierTokenSink()
        use server = serve store sink (recordingHandler seen)

        // Stripe redelivers on a lost 200. The redelivery is signed afresh (a
        // new `t=`, so a different header), which is exactly why the event id —
        // not the signature — has to be what deduplicates it.
        let first = (post server (signHeader DateTimeOffset.UtcNow body) body).Result
        let second = (post server (signHeader DateTimeOffset.UtcNow body) body).Result

        Expect.equal first.StatusCode HttpStatusCode.OK "first delivery 200"
        Expect.equal second.StatusCode HttpStatusCode.OK "redelivery 200 (short-circuit, not an error)"
        Expect.hasLength seen 1 "consumer handler applied exactly once across the redelivery"
        Expect.equal store.Claims 2 "both deliveries reached the idempotency seam"
        Expect.equal store.Granted 1 "only the first delivery won the claim"
        let expectedFires = if case.TierChanging then 1 else 0
        Expect.hasLength sink.Fired expectedFires "the sink does not re-fire on a replay"
    }

let private tamperTest (case: FixtureCase) =
    let name =
        sprintf "%s — a tampered signature is rejected before any handler effect" case.File

    test name {
        let body = readFixture case.File
        let seen = ResizeArray<VerifiedEvent>()
        let store = newStore ()
        let sink = RecordingTierTokenSink()
        use server = serve store sink (recordingHandler seen)
        let tampered = tamperSignature (signHeader DateTimeOffset.UtcNow body)

        // The verifier itself first, then the same header over the wire.
        match WebhookSigner.verify secret body tampered with
        | Error SignatureMismatch -> ()
        | other -> failtestf "WebhookSigner.verify should report SignatureMismatch, got %A" other

        let resp = (post server tampered body).Result

        Expect.equal resp.StatusCode HttpStatusCode.BadRequest "400 on signature mismatch"
        Expect.isEmpty seen "consumer handler never invoked"
        Expect.isEmpty sink.Fired "tier-token sink never fired"
        Expect.equal store.Claims 0 "the event id was never claimed — rejection precedes dedup"
    }

let private handlerErrorTest (case: FixtureCase) =
    let name = sprintf "%s — a handler Error maps to 500 and fires no sink" case.File

    test name {
        let body = readFixture case.File
        let store = newStore ()
        let sink = RecordingTierTokenSink()

        let errorHandler: VerifiedEvent -> Microsoft.AspNetCore.Http.HttpContext -> Task<Result<unit, string>> =
            fun _ _ -> task { return Error "domain failure" }

        use server = serve store sink errorHandler

        let resp = (post server (signHeader DateTimeOffset.UtcNow body) body).Result

        Expect.equal resp.StatusCode HttpStatusCode.InternalServerError "500 on handler Error"
        Expect.isEmpty sink.Fired "the sink does not fire when the handler failed"
    }

[<Tests>]
let tests =
    testList
        "webhook fixture replay"
        ([ corpusTest ]
         @ (cases |> List.map replayTest)
         @ (cases |> List.map redeliveryTest)
         @ (cases |> List.map tamperTest)
         @ (cases |> List.map handlerErrorTest))