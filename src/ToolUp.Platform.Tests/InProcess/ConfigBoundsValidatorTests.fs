module ToolUp.Platform.Tests.InProcess.ConfigBoundsValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 465 — the format/range gate and the retention warning ─────
//
// Two validators, one phase. The first reads its subject from the
// process environment through the resolution seam, so every case here
// sets a variable and restores it; a leaked value contaminates every
// sibling case in the pack, and the validator quantifies over the WHOLE
// registry, so it would be contaminated by any leftover from anywhere.
//
// The bounds cases deliberately assert on the message text, not only on
// the severity. The whole point of the phase is that the operator is
// told which key, which value and which accepted form — a refusal that
// merely says "invalid configuration" would pass a severity-only test
// while leaving them exactly where the silent fallback did.

/// Run `body` with one env var temporarily set, restoring the prior
/// value — the shape every env-reading suite in this pack uses.
let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let private validateIntKeys () : ValidationResult =
    let v = IntConfigKeyValidator.IntConfigKeyValidator() :> IConfigValidator
    v.Validate() |> Async.RunSynchronously

let private expectErrorMentioning (fragments: string list) (result: ValidationResult) =
    match result with
    | Error msg ->
        for f in fragments do
            Expect.stringContains msg f "the refusal names it"
    | other -> failtestf "expected Error, got %A" other

/// The validator quantifies over the WHOLE registry, so a positive case
/// cannot assert `Ok` — an unrelated variable left set anywhere in the
/// pack (or on the developer's machine) would fail it for the wrong
/// reason. Asserting that the key under test is not among the refusals
/// is the same claim without the false failure.
let private expectAccepted (key: string) (result: ValidationResult) =
    match result with
    | Ok
    | Warning _ -> ()
    | Error msg -> Expect.isFalse (msg.Contains key) (sprintf "%s should have been accepted, but: %s" key msg)

// ─── Task A — typed-key format + bounds ──────────────────────────────

[<Tests>]
let intKeyTests =
    testList "Int config key format + bounds validator" [

        test "Non-numeric TOOLUP_MAX_REQUEST_BODY_BYTES → Error naming key, value and form" {
            withEnv ConfigKeys.Names.maxRequestBodyBytes (Some "1MB") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [
                    ConfigKeys.Names.maxRequestBodyBytes
                    "1MB"
                    "not a number"
                    "1024–10737418240"
                ])
        }

        test "Non-numeric TOOLUP_MAX_FILE_BYTES → Error (the reader used to fall back in silence)" {
            withEnv ConfigKeys.Names.maxFileBytes (Some "10 MB") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [ ConfigKeys.Names.maxFileBytes; "10 MB"; "not a number" ])
        }

        test "A valid byte cap → Ok" {
            withEnv ConfigKeys.Names.maxRequestBodyBytes (Some "5242880") (fun () ->
                validateIntKeys () |> expectAccepted ConfigKeys.Names.maxRequestBodyBytes)
        }

        test "Below the declared minimum → Error naming the minimum and the unit mistake" {
            withEnv ConfigKeys.Names.maxRequestBodyBytes (Some "1") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [
                    ConfigKeys.Names.maxRequestBodyBytes
                    "below the accepted minimum of 1024"
                    "1048576"
                ])
        }

        test "Above the declared maximum → Error naming the maximum" {
            withEnv ConfigKeys.Names.maxFileBytes (Some "99999999999") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [ ConfigKeys.Names.maxFileBytes; "above the accepted maximum of 10737418240" ])
        }

        test "The reader's own 'none' token is accepted, not refused" {
            withEnv ConfigKeys.Names.maxRequestBodyBytes (Some "none") (fun () ->
                validateIntKeys () |> expectAccepted ConfigKeys.Names.maxRequestBodyBytes)
        }

        test "TOOLUP_MAX_FILE_BYTES = 0 is accepted — it disables the per-file ceiling" {
            withEnv ConfigKeys.Names.maxFileBytes (Some "0") (fun () ->
                validateIntKeys () |> expectAccepted ConfigKeys.Names.maxFileBytes)
        }

        test "A fractional TOOLUP_STORE_EVICTION_MINUTES is accepted — its reader parses a Double" {
            withEnv ConfigKeys.Names.storeEvictionMinutes (Some "2.5") (fun () ->
                validateIntKeys () |> expectAccepted ConfigKeys.Names.storeEvictionMinutes)
        }

        test "A non-numeric TOOLUP_STORE_EVICTION_MINUTES is still refused" {
            withEnv ConfigKeys.Names.storeEvictionMinutes (Some "hourly") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [ ConfigKeys.Names.storeEvictionMinutes; "hourly" ])
        }

        test "An int key with no declared rule inherits the format check" {
            // TOOLUP_REPLICA_COUNT declares no bounds, so this asserts
            // the "every IntKey inherits it" half rather than the table.
            withEnv ConfigKeys.Names.replicaCount (Some "two") (fun () ->
                validateIntKeys ()
                |> expectErrorMentioning [ ConfigKeys.Names.replicaCount; "two" ])
        }

        test "Every declared rule names a registered IntKey" {
            // The rules are a table keyed by name beside the descriptors
            // rather than a field on them, so this is the join that
            // stops a renamed key leaving a rule governing nothing.
            for rule in ConfigKeys.intKeyRules do
                match ConfigKeys.all |> List.tryFind (fun k -> k.EnvVar = rule.EnvVar) with
                | Some descriptor ->
                    Expect.equal
                        descriptor.Type
                        ConfigKeys.IntKey
                        (sprintf "%s declares an int-value rule but is not an IntKey" rule.EnvVar)
                | None -> failtestf "%s declares an int-value rule but is not a registered key" rule.EnvVar
        }
    ]

// ─── Task C — event-store retention warning ──────────────────────────

let private validateRetention (config: ServerConfig) : ValidationResult =
    let v =
        EventStoreRetentionValidator.EventStoreRetentionValidator(config) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

/// An anonymous, single-instance shape carrying the unlimited policy —
/// a local dev shell. `ServerConfig.defaults` is anonymous, so this is
/// the default plus the store.
let private devShaped: ServerConfig = {
    ServerConfig.defaults with
        EventStore = PersistentBlobBacked EventRetentionPolicy.unlimited
        ReplicaCount = 1
}

/// The same store in a production shape: an authenticated surface, so
/// `requiresAnyAuth` holds.
let private productionShaped: ServerConfig = {
    devShaped with
        Surfaces = Surfaces.individual
}

[<Tests>]
let retentionTests =
    testList "Event-store retention validator" [

        test "Unlimited retention in a production shape → Warning naming the policy and the hatch" {
            match validateRetention productionShaped with
            | Warning msg ->
                Expect.stringContains msg "MaxAge = None" "names the unset dimension"
                Expect.stringContains msg "MaxCountPerScope = None" "names the other one"
                Expect.stringContains msg "100_000" "recommends a per-scope ceiling"
                Expect.stringContains msg "365" "recommends the audit-scope retention"
                Expect.stringContains msg ConfigKeys.Names.acceptUnlimitedEventRetention "offers the acknowledgement"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "In-memory event store → Ok (nothing accumulates)" {
            Expect.equal
                (validateRetention {
                    productionShaped with
                        EventStore = InMemoryOnly
                })
                Ok
                "the warning is about a durable trail"
        }

        test "A bounded policy → Ok" {
            Expect.equal
                (validateRetention {
                    productionShaped with
                        EventStore = PersistentBlobBacked EventRetentionPolicy.ninetyDays
                })
                Ok
                "an age limit is a policy"
        }

        test "A count-only policy → Ok" {
            Expect.equal
                (validateRetention {
                    productionShaped with
                        EventStore = PersistentBlobBacked(EventRetentionPolicy.byCount 100_000)
                })
                Ok
                "either dimension is enough to bound the trail"
        }

        test "Anonymous single-instance shape → Ok (a dev shell earns no line)" {
            Expect.equal (validateRetention devShaped) Ok "the gate is the deployment shape, not the policy alone"
        }

        test "Multi-instance is production-shaped even when anonymous" {
            match validateRetention { devShaped with ReplicaCount = 3 } with
            | Warning msg -> Expect.stringContains msg "ReplicaCount = 3" "names why the shape qualifies"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "The acknowledgement silences it" {
            withEnv ConfigKeys.Names.acceptUnlimitedEventRetention (Some "1") (fun () ->
                Expect.equal (validateRetention productionShaped) Ok "an acknowledged risk is not re-reported")
        }

        test "An unrecognised acknowledgement value does not silence it" {
            withEnv ConfigKeys.Names.acceptUnlimitedEventRetention (Some "maybe") (fun () ->
                match validateRetention productionShaped with
                | Warning _ -> ()
                | other -> failtestf "expected Warning, got %A" other)
        }
    ]