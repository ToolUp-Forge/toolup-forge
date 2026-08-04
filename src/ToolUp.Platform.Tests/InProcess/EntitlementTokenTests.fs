module ToolUp.Platform.Tests.InProcess.EntitlementTokenTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open FSharp.Reflection
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 492 — offline entitlement verification ─────────────────────
//
// Six acceptance shapes, plus the two structural guarantees that are the
// point of the phase rather than features of it:
//
//   * **A signed token unlocks its capabilities offline** — with a REAL
//     ECDSA P-256 signature over the canonical bytes, verified through the
//     Phase 488.B `VerifyDetachedJws` seam. Not a stub verifier: the claim
//     is that a cryptographic signature admits the token, and a
//     `fun _ _ -> Ok ()` fixture proves only that the plumbing is
//     connected. The wrong-key arm uses a genuinely different key pair,
//     so it fails for the reason production would fail.
//   * **Tampered / wrong-key rejection NAMES the mismatch** — asserted on
//     the message text, because "refused" without a named cause is the
//     failure mode 492.A exists to close (Phase 488.B's argument, same
//     shape).
//   * **Expiry degrades, it does not lock out** — governed capabilities
//     drop to the floor while the floor itself, `platform.data.export`
//     included, resolves by the ordinary scope walk in every phase.
//   * **Clock skew is applied in the holder's favour** — a token three
//     minutes past expiry on a host with a five-minute declared allowance
//     is still Active. Paired with a zero-skew control differing in that
//     one field.
//   * **Capacity caps enforce, and a lapse does not zero them** —
//     `BudgetExceeded` carries the existing `QuotaBreached` shape.
//   * **GP 13: an unconfigured deployment is fully unlocked** — three
//     independent routes to that conclusion (no token, no governance, no
//     ceiling), because it is the one property a licensing mechanism is
//     most likely to get wrong in the restrictive direction.
//
// **Two structural guarantees, each falsified against a control.** A
// structural claim asserted by a walk that has silently stopped matching
// anything reports success for the wrong reason, so both walks are run
// against a deliberately-violating type in the same test list — the
// discipline Phase 488.C's diode-closure test established:
//
//   * **Offline by construction** — no `Uri`, no `HttpClient`, no
//     endpoint-shaped member anywhere in the closure of the verification
//     types. Falsified against a control record carrying a `Uri`.
//   * **No entitlement state can withhold data** —
//     `EntitlementGovernance.declare` refuses every `EntitlementFloor`
//     key, and `governs` refuses one even in a hand-built record that
//     bypassed `declare`. Falsified by the same call succeeding on an
//     ordinary key.
//
// **And the fail-safe one, asserted exhaustively rather than by example:**
// across every `EntitlementRefusal` case, every `EntitlementPhase`, and a
// raising status source, the boot preflight never returns
// `ValidationResult.Error` — because a Phase 9m `Error` aborts the boot,
// and a process that will not start is the most complete way to withhold a
// customer's own data. Falsified against a control validator that does
// return `Error`, so the assertion is known to be capable of failing.

// ── crypto fixtures ───────────────────────────────────────────────────

/// The key the deployment pins. Module-level so every arm verifies
/// against the same material a real pin would hold.
let private pinnedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)

/// A different, equally valid key. The wrong-key arm signs with this one,
/// so its refusal is a genuine cryptographic failure rather than a
/// bookkeeping one.
let private otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256)

[<Literal>]
let private PinnedKeyId = "issuer-2026-q3"

[<Literal>]
let private PinnedAlgorithm = "ES256"

let private pin: PinnedEntitlementKey = {
    KeyId = PinnedKeyId
    Algorithm = PinnedAlgorithm
}

let private signWith (key: ECDsa) (bytes: byte[]) : string =
    key.SignData(bytes, HashAlgorithmName.SHA256) |> Convert.ToBase64String

/// A `VerifyDetachedJws` bound to one key's material — the shape a
/// composition root adapts from its own verifier, per the seam's doc
/// comment. THIS is what makes the pin real: the function holds one key,
/// so a signature from any other fails here whatever the token's echoed
/// `KeyId` claims.
let private verifierFor (key: ECDsa) : VerifyDetachedJws =
    fun bytes jws -> async {
        try
            let signature = Convert.FromBase64String jws

            if key.VerifyData(bytes, signature, HashAlgorithmName.SHA256) then
                return Result.Ok()
            else
                return Result.Error "ES256 signature did not verify over the presented claim bytes"
        with ex ->
            return Result.Error(sprintf "signature could not be decoded: %s" ex.Message)
    }

let private run a = a |> Async.RunSynchronously

// ── claim fixtures ────────────────────────────────────────────────────

[<Literal>]
let private AdvancedAnalytics = "reporting.advanced-analytics"

[<Literal>]
let private FederationPeering = "interplatform.peering"

[<Literal>]
let private Seats = "entitlement.seats"

let private epoch = DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)

let private claimsFor (notBefore: DateTimeOffset) (expiresAt: DateTimeOffset) (grace: TimeSpan) : EntitlementClaims = {
    HolderId = "deployment-7f2a"
    TokenId = "tok-0001"
    IssuedAt = notBefore
    NotBefore = notBefore
    ExpiresAt = expiresAt
    Capabilities = Set.ofList [ AdvancedAnalytics; FederationPeering ]
    Capacities = [ { Dimension = Seats; Limit = 25L } ]
    GraceWindow = grace
}

/// The standard fixture: a 30-day token with a 7-day grace window.
let private standardClaims =
    claimsFor epoch (epoch.AddDays 30.0) (TimeSpan.FromDays 7.0)

let private tokenSignedBy (key: ECDsa) (claims: EntitlementClaims) : EntitlementToken =
    let jws = signWith key (EntitlementClaims.canonicalBytes claims)
    EntitlementClaims.toToken PinnedKeyId PinnedAlgorithm jws claims

let private standardToken = tokenSignedBy pinnedKey standardClaims

let private governance =
    match EntitlementGovernance.declare [ AdvancedAnalytics; FederationPeering ] with
    | Result.Ok g -> g
    | Result.Error errs -> failwithf "fixture governance was refused: %s" (String.Join("; ", errs))

let private validationAt (now: DateTimeOffset) : EntitlementValidation = {
    EntitlementValidation.create pin (verifierFor pinnedKey) governance with
        Clock = fun () -> now
}

// ── flag fixtures ─────────────────────────────────────────────────────

let private boolFlag key : FeatureFlag = {
    Key = key
    DefaultValue = FlagValue.Bool true
    Description = key
    Owner = None
}

/// Every governed key plus the two floor keys, all declared ON by
/// default. Declaring them ON is deliberate: it means a `false` read can
/// only have come from the entitlement ceiling, never from the declared
/// default.
let private declaredFlags = [
    boolFlag AdvancedAnalytics
    boolFlag FederationPeering
    boolFlag EntitlementFloor.ReadOwnData
    boolFlag EntitlementFloor.ExportOwnData
]

/// In-memory `IFeatureFlagStore` — enough for the evaluator's scope walk.
type private MemoryFlagStore(seed: (FlagScope * string * FlagValue) list) =
    let mutable entries =
        seed |> List.map (fun (s, k, v) -> (FlagScope.slug s, k), v) |> Map.ofList

    interface IFeatureFlagStore with
        member _.GetFlag(scope, key) =
            async.Return(entries.TryFind(FlagScope.slug scope, key))

        member _.ListFlags scope =
            let slug = FlagScope.slug scope

            entries
            |> Map.toList
            |> List.choose (fun ((s, k), v) -> if s = slug then Some(k, v) else None)
            |> Map.ofList
            |> async.Return

        member _.SetFlag(scope, key, value) =
            entries <- entries.Add((FlagScope.slug scope, key), value)
            async.Return(Result.Ok())

        member _.ClearFlag(scope, key) =
            entries <- entries.Remove(FlagScope.slug scope, key)
            async.Return()

        member _.Erase(_, _, _, _) =
            failwith "erasure is not exercised by the Phase 492 pack"

let private ctx: AccessContext = {
    UserId = "u-1"
    TeamId = None
    Subject = AuthenticatedUser "u-1"
    ModulePermissions = Map.empty
    ModuleExposure = Map.empty
    PlatformRole = None
}

let private evaluatorWith (seed: (FlagScope * string * FlagValue) list) =
    FlagEvaluator.create (MemoryFlagStore seed) declaredFlags None

let private cappedAt (now: DateTimeOffset) (token: EntitlementToken option) =
    let status, _ =
        EntitlementValidation.resolveFailSafe (validationAt now) token |> run

    status, EntitlementFlagCeiling.decorate status governance (evaluatorWith [])

// ── 492.A — canonical form ────────────────────────────────────────────

let private canonicalTests =
    testList "492.A canonical claim form" [
        test "canonicalJson round-trips through parse" {
            let json = EntitlementClaims.canonicalJson standardClaims

            match EntitlementClaims.parse json with
            | Result.Error e -> failtestf "canonical form did not parse: %s" (EntitlementRefusal.describe e)
            | Result.Ok parsed ->
                Expect.equal parsed.HolderId standardClaims.HolderId "holder id survives"
                Expect.equal parsed.TokenId standardClaims.TokenId "token id survives"
                Expect.equal parsed.Capabilities standardClaims.Capabilities "capability set survives"
                Expect.equal parsed.Capacities standardClaims.Capacities "capacity grants survive"
                Expect.equal parsed.GraceWindow standardClaims.GraceWindow "grace window survives"
                Expect.equal parsed.NotBefore standardClaims.NotBefore "notBefore survives"
                Expect.equal parsed.ExpiresAt standardClaims.ExpiresAt "expiresAt survives"
        }

        test "the same instant in two timezone representations canonicalises identically" {
            // A token signed on a host at +01:00 must verify on a host at
            // UTC. Without the ToUniversalTime normalisation the bytes
            // differ and the signature fails for a reason no operator
            // could diagnose.
            let utc = {
                standardClaims with
                    NotBefore = DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            }

            let offset = {
                standardClaims with
                    NotBefore = DateTimeOffset(2026, 6, 1, 1, 0, 0, TimeSpan.FromHours 1.0)
            }

            Expect.equal
                (EntitlementClaims.canonicalJson utc)
                (EntitlementClaims.canonicalJson offset)
                "the same instant produces the same signed bytes regardless of declared offset"
        }

        test "canonicalJson is stable under capability and capacity ordering" {
            let reordered = {
                standardClaims with
                    Capabilities = Set.ofList [ FederationPeering; AdvancedAnalytics ]
                    Capacities = [ { Dimension = "zz.other"; Limit = 1L }; { Dimension = Seats; Limit = 25L } ]
            }

            let straight = {
                standardClaims with
                    Capacities = [ { Dimension = Seats; Limit = 25L }; { Dimension = "zz.other"; Limit = 1L } ]
            }

            Expect.equal
                (EntitlementClaims.canonicalJson reordered)
                (EntitlementClaims.canonicalJson straight)
                "sorting makes the signed bytes independent of the issuer's collection order"
        }

        test "every claim field appears in the declared canonical order" {
            // The drift guard: a claim added to the record without a name
            // in `canonicalOrder` would silently drop out of the signed
            // bytes, so the field would be unsigned and freely
            // alterable. Mapped by name because the JSON keys are
            // camelCase and `graceWindowSeconds` is a rendering of
            // `GraceWindow`.
            let expected =
                FSharpType.GetRecordFields typeof<EntitlementClaims>
                |> Array.map (fun p ->
                    match p.Name with
                    | "GraceWindow" -> "graceWindowSeconds"
                    | name -> string (Char.ToLowerInvariant name[0]) + name.Substring 1)
                |> Set.ofArray

            Expect.equal
                (Set.ofList EntitlementClaims.canonicalPropertyNames)
                expected
                "every EntitlementClaims field is named in the canonical order, so every field is signed"
        }

        test "a duplicate capacity dimension is refused rather than resolved" {
            let json =
                EntitlementClaims.canonicalJson {
                    standardClaims with
                        Capacities = [ { Dimension = Seats; Limit = 25L }; { Dimension = Seats; Limit = 500L } ]
                }

            match EntitlementClaims.parse json with
            | Result.Error(EntitlementRefusal.ClaimsIncomplete detail) ->
                Expect.stringContains detail Seats "the refusal names the contested dimension"
            | other -> failtestf "expected a duplicate-dimension refusal, got %A" other
        }

        test "a negative capacity limit is refused rather than clamped" {
            let json =
                EntitlementClaims.canonicalJson {
                    standardClaims with
                        Capacities = [ { Dimension = Seats; Limit = -4L } ]
                }

            match EntitlementClaims.parse json with
            | Result.Error(EntitlementRefusal.ClaimsIncomplete detail) ->
                Expect.stringContains detail Seats "the refusal names the offending dimension"
            | other -> failtestf "expected a negative-limit refusal, got %A" other
        }

        test "an inverted validity window is refused" {
            let json =
                EntitlementClaims.canonicalJson (claimsFor (epoch.AddDays 30.0) epoch TimeSpan.Zero)

            match EntitlementClaims.parse json with
            | Result.Error(EntitlementRefusal.ValidityWindowInverted _) -> ()
            | other -> failtestf "expected ValidityWindowInverted, got %A" other
        }
    ]

// ── 492.A — verification ──────────────────────────────────────────────

let private verificationTests =
    testList "492.A offline verification" [
        test "a validly signed token resolves Active with its capabilities" {
            match run (EntitlementValidation.resolve (validationAt (epoch.AddDays 1.0)) (Some standardToken)) with
            | Result.Error e -> failtestf "a validly signed token was refused: %s" (EntitlementRefusal.describe e)
            | Result.Ok status ->
                Expect.equal (EntitlementPhase.status status.Phase) "Active" "inside the window"
                Expect.equal status.HolderId "deployment-7f2a" "holder echoed"
                Expect.isTrue (EntitlementStatus.grants AdvancedAnalytics status) "granted capability is granted"

                Expect.isFalse
                    (EntitlementStatus.grants "reporting.not-granted" status)
                    "an ungranted capability is not"
        }

        test "tampered claims are rejected and the refusal says what happened" {
            // One byte of the holder id changed after signing — the
            // canonical bytes no longer match the signature.
            let tampered = {
                standardToken with
                    ClaimsJson = standardToken.ClaimsJson.Replace("deployment-7f2a", "deployment-7f2b")
            }

            match run (EntitlementValidation.resolve (validationAt (epoch.AddDays 1.0)) (Some tampered)) with
            | Result.Error(EntitlementRefusal.SignatureRejected _ as refusal) ->
                let described = EntitlementRefusal.describe refusal
                Expect.stringContains described "signature rejected" "the refusal names a signature failure"
                Expect.stringContains described "altered after signing" "and explains the likely cause"
            | other -> failtestf "expected SignatureRejected for tampered claims, got %A" other
        }

        test "a token signed by a different key is rejected by the pinned verifier" {
            // Signed with real, valid key material — just not the pinned
            // material. The token's echoed KeyId still says the pinned
            // key, which is exactly the case a KeyId comparison alone
            // would wave through.
            let wrongKey = tokenSignedBy otherKey standardClaims

            match run (EntitlementValidation.resolve (validationAt (epoch.AddDays 1.0)) (Some wrongKey)) with
            | Result.Error(EntitlementRefusal.SignatureRejected _) -> ()
            | other -> failtestf "expected SignatureRejected for a wrong-key signature, got %A" other
        }

        test "an unpinned key id is refused early and names both key ids" {
            let foreign = {
                standardToken with
                    KeyId = "issuer-2025-q1"
            }

            match run (EntitlementValidation.resolve (validationAt (epoch.AddDays 1.0)) (Some foreign)) with
            | Result.Error(EntitlementRefusal.KeyIdNotPinned(presented, pinned) as refusal) ->
                Expect.equal presented "issuer-2025-q1" "the presented key id"
                Expect.equal pinned PinnedKeyId "the pinned key id"
                let described = EntitlementRefusal.describe refusal
                Expect.stringContains described "issuer-2025-q1" "the message names the presented key"
                Expect.stringContains described PinnedKeyId "and the pinned one"
            | other -> failtestf "expected KeyIdNotPinned, got %A" other
        }

        test "an unpinned algorithm is refused without consulting the verifier" {
            let mutable verifierCalls = 0

            let counting: VerifyDetachedJws =
                fun _ _ ->
                    verifierCalls <- verifierCalls + 1
                    async.Return(Result.Ok())

            let validation = {
                validationAt (epoch.AddDays 1.0) with
                    Verify = counting
            }

            let substituted = {
                standardToken with
                    Algorithm = "none"
            }

            match run (EntitlementValidation.resolve validation (Some substituted)) with
            | Result.Error(EntitlementRefusal.AlgorithmNotPinned(presented, pinned)) ->
                Expect.equal presented "none" "the presented algorithm"
                Expect.equal pinned PinnedAlgorithm "the pinned algorithm"
                Expect.equal verifierCalls 0 "algorithm substitution is refused before any verification runs"
            | other -> failtestf "expected AlgorithmNotPinned, got %A" other
        }

        test "a verifier that raises is a rejection, not a crash" {
            // An entitlement check that can take the process down is a
            // lockout mechanism with extra steps.
            let raising: VerifyDetachedJws = fun _ _ -> failwith "HSM handle disposed"

            let validation = {
                validationAt (epoch.AddDays 1.0) with
                    Verify = raising
            }

            match run (EntitlementValidation.resolve validation (Some standardToken)) with
            | Result.Error(EntitlementRefusal.SignatureRejected reason) ->
                Expect.stringContains reason "HSM handle disposed" "the raised message reaches the refusal"
            | other -> failtestf "expected SignatureRejected from a raising verifier, got %A" other
        }

        test "clock skew is applied in the holder's favour on the expiry edge" {
            // Three minutes past expiry on a host with a five-minute
            // declared allowance. A drifting appliance clock must not
            // manufacture an expiry.
            let justPast = standardClaims.ExpiresAt.AddMinutes 3.0

            let tolerant =
                validationAt justPast
                |> EntitlementValidation.withClockSkew (TimeSpan.FromMinutes 5.0)

            match run (EntitlementValidation.resolve tolerant (Some standardToken)) with
            | Result.Ok status ->
                Expect.equal (EntitlementPhase.status status.Phase) "Active" "skew keeps it inside the window"
            | other -> failtestf "expected Active under skew tolerance, got %A" other
        }

        test "the zero-skew control at the same instant has already expired" {
            // The falsifier for the arm above — one field differs.
            let justPast = standardClaims.ExpiresAt.AddMinutes 3.0

            match run (EntitlementValidation.resolve (validationAt justPast) (Some standardToken)) with
            | Result.Ok status ->
                Expect.equal
                    (EntitlementPhase.status status.Phase)
                    "Grace"
                    "with no declared allowance the same instant is past expiry, so the skew arm above is measuring skew"
            | other -> failtestf "expected Grace with zero skew, got %A" other
        }

        test "a not-yet-valid token is refused as itself, not as an expiry" {
            match run (EntitlementValidation.resolve (validationAt (epoch.AddDays -2.0)) (Some standardToken)) with
            | Result.Error(EntitlementRefusal.NotYetValid _ as refusal) ->
                Expect.stringContains
                    (EntitlementRefusal.describe refusal)
                    "provisioning"
                    "the message sends the operator to the right problem"
            | other -> failtestf "expected NotYetValid, got %A" other
        }

        test "clock skew also admits a token whose window has barely opened" {
            let barely = epoch.AddMinutes -3.0

            let tolerant =
                validationAt barely
                |> EntitlementValidation.withClockSkew (TimeSpan.FromMinutes 5.0)

            match run (EntitlementValidation.resolve tolerant (Some standardToken)) with
            | Result.Ok _ -> ()
            | other -> failtestf "expected the skew allowance to admit the token, got %A" other
        }

        test "the appliance-profile bridge reads the declared skew rather than restating it" {
            let profile = {
                ApplianceProfile.offline with
                    ClockSkewTolerance = TimeSpan.FromMinutes 17.0
            }

            Expect.equal
                (EntitlementValidation.skewFromApplianceProfile profile)
                (TimeSpan.FromMinutes 17.0)
                "the bridge is a value read — entitlements are not appliance-only"
        }
    ]

// ── 492.C — lapse semantics ───────────────────────────────────────────

let private lapseTests =
    testList "492.C fail-safe lapse" [
        test "inside the grace window capability is unreduced" {
            let status, capped =
                cappedAt (standardClaims.ExpiresAt.AddDays 3.0) (Some standardToken)

            Expect.equal (EntitlementPhase.status status.Phase) "Grace" "past expiry, inside grace"
            Expect.isTrue (run (capped.IsEnabled AdvancedAnalytics ctx)) "grace is a FULL-capability state"

            Expect.stringContains
                (EntitlementPhase.describe status.Phase)
                "Renew now"
                "and it says so loudly rather than reducing quietly"
        }

        test "past the grace window governed capabilities reduce" {
            let status, capped =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            Expect.equal (EntitlementPhase.status status.Phase) "Lapsed" "past grace"
            Expect.isFalse (run (capped.IsEnabled AdvancedAnalytics ctx)) "a governed capability is capped off"
            Expect.isFalse (run (capped.IsEnabled FederationPeering ctx)) "every governed capability, not just one"
        }

        test "a lapsed deployment can still read and export its own data" {
            // The acceptance criterion of the whole phase.
            let _, capped =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            Expect.isTrue (run (capped.IsEnabled EntitlementFloor.ReadOwnData ctx)) "reading own data survives a lapse"

            Expect.isTrue
                (run (capped.IsEnabled EntitlementFloor.ExportOwnData ctx))
                "exporting own data survives a lapse"
        }

        test "a lapse does not zero the declared capacity limits" {
            // A zero budget would present the lapse as a capacity breach —
            // a second, contradictory explanation for one event. Reduction
            // acts through the capability set and nowhere else.
            let status, _ =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            let budget = EntitlementBudget.ofStatus status

            Expect.equal (budget.TryLimit Seats) (Some 25L) "the declared limit is still reported after a lapse"
        }

        test "a refusal folds to the same reduced state, with the refusal preserved" {
            let tampered = {
                standardToken with
                    ClaimsJson = standardToken.ClaimsJson.Replace("tok-0001", "tok-9999")
            }

            let status, refusal =
                EntitlementValidation.resolveFailSafe (validationAt (epoch.AddDays 1.0)) (Some tampered)
                |> run

            Expect.equal (EntitlementPhase.status status.Phase) "Lapsed" "knowing nothing resolves to the floor"
            Expect.isSome refusal "and the refusal is preserved so the preflight can say what happened"

            Expect.equal
                status.GrantedCapabilities
                EntitlementFloor.capabilities
                "the reduced state grants exactly the floor"
        }

        test "the lapse description promises the data is not withheld" {
            Expect.stringContains
                (EntitlementPhase.describe (EntitlementPhase.Lapsed 4.0))
                "fully exportable"
                "the operator-facing line states the guarantee the mechanism enforces"
        }
    ]

// ── 492.B — flag projection ───────────────────────────────────────────

let private projectionTests =
    testList "492.B feature-flag projection" [
        test "gated code reads a flag and an active entitlement lets it through" {
            let _, capped = cappedAt (epoch.AddDays 1.0) (Some standardToken)
            Expect.isTrue (run (capped.IsEnabled AdvancedAnalytics ctx)) "granted and declared-on"
        }

        test "an entitlement caps a flag, it does not substitute a value" {
            // Granted by the entitlement but switched OFF at Platform
            // scope: the ceiling is a bound, matching the Phase 62
            // PremiumOnly precedent.
            let status, _ = cappedAt (epoch.AddDays 1.0) (Some standardToken)

            let capped =
                EntitlementFlagCeiling.decorate
                    status
                    governance
                    (evaluatorWith [ FlagScope.Platform, AdvancedAnalytics, FlagValue.Bool false ])

            Expect.isFalse
                (run (capped.IsEnabled AdvancedAnalytics ctx))
                "the deployment's own switch still wins downward"
        }

        test "no scope override can lift a capped capability" {
            // The reason this is a ceiling over the evaluator rather than
            // an IFlagSource: a source is consulted only when no scope set
            // the key, so this Platform-scope `true` would have lifted the
            // entitlement entirely.
            let status, _ =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            let capped =
                EntitlementFlagCeiling.decorate
                    status
                    governance
                    (evaluatorWith [ FlagScope.Platform, AdvancedAnalytics, FlagValue.Bool true ])

            Expect.isFalse
                (run (capped.IsEnabled AdvancedAnalytics ctx))
                "a Platform-scope override does not lift a lapsed entitlement"

            Expect.equal
                (run (capped.TryEvaluate AdvancedAnalytics ctx))
                (Some(FlagValue.Bool false))
                "the override reader is capped too, so a caller cannot route around IsEnabled"
        }

        test "an ungoverned flag is untouched by the ceiling" {
            let status, _ =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            let plain =
                evaluatorWith [ FlagScope.Platform, "unrelated.flag", FlagValue.Bool true ]

            let capped = EntitlementFlagCeiling.decorate status governance plain

            Expect.equal
                (run (capped.TryEvaluate "unrelated.flag" ctx))
                (run (plain.TryEvaluate "unrelated.flag" ctx))
                "a key outside the governed set resolves exactly as it would with no entitlement at all"
        }

        test "capacity entitlements expose a typed budget" {
            let status, _ = cappedAt (epoch.AddDays 1.0) (Some standardToken)
            let budget = EntitlementBudget.ofStatus status

            Expect.equal (budget.Check Seats 25L) (CapacityDecision.WithinBudget(25L, 25L)) "at the limit is within it"

            match budget.Check Seats 26L with
            | CapacityDecision.BudgetExceeded breach ->
                Expect.equal breach.Kind Seats "the breach names the dimension"
                Expect.equal breach.Limit 25M "the declared limit"
                Expect.equal breach.Requested 26M "the requested amount"
                Expect.equal breach.ScopeId "deployment-7f2a" "attributed to the holder"
            | other -> failtestf "expected BudgetExceeded past the cap, got %A" other

            Expect.equal
                (budget.Check "compute.units" 10_000L)
                CapacityDecision.Unbudgeted
                "a dimension the token never mentioned is unbounded"
        }

        test "an unbounded budget is the identity" {
            Expect.equal
                (EntitlementBudget.unbounded.Check Seats 1_000_000L)
                CapacityDecision.Unbudgeted
                "nothing is capped"

            Expect.equal (EntitlementBudget.unbounded.TryLimit Seats) None "and no limit is reported"
        }
    ]

// ── GP 13 — an unconfigured deployment is fully unlocked ──────────────

let private gp13Tests =
    testList "GP 13 unconfigured means unrestricted" [
        test "no token, default posture: every governed capability is granted" {
            match run (EntitlementValidation.resolve (validationAt epoch) None) with
            | Result.Ok status ->
                Expect.equal (EntitlementPhase.status status.Phase) "Unentitled" "the identity phase"
                Expect.isTrue (EntitlementStatus.grants AdvancedAnalytics status) "a governed capability is granted"

                Expect.isTrue
                    (EntitlementStatus.grants "anything.at.all" status)
                    "and so is a capability no token ever mentioned — an absent licence is not a restrictive one"
            | other -> failtestf "expected Unentitled, got %A" other
        }

        test "no token: the ceiling changes no flag" {
            let _, capped = cappedAt epoch None
            let plain = evaluatorWith []

            for flag in declaredFlags do
                Expect.equal
                    (run (capped.IsEnabled flag.Key ctx))
                    (run (plain.IsEnabled flag.Key ctx))
                    $"'{flag.Key}' resolves identically with and without the entitlement layer"
        }

        test "governing nothing is the identity, whatever the entitlement says" {
            let status, _ =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            let plain = evaluatorWith []
            let capped = EntitlementFlagCeiling.decorate status EntitlementGovernance.none plain

            for flag in declaredFlags do
                Expect.equal
                    (run (capped.IsEnabled flag.Key ctx))
                    (run (plain.IsEnabled flag.Key ctx))
                    $"'{flag.Key}' is untouched when nothing is governed — even under a lapsed token"
        }

        test "a declared reduced-when-unprovisioned posture reduces, and still cannot withhold data" {
            let strict = {
                validationAt epoch with
                    Governance = governance |> EntitlementGovernance.withUnprovisioned ReducedWhenUnprovisioned
            }

            match run (EntitlementValidation.resolve strict None) with
            | Result.Ok status ->
                Expect.equal (EntitlementPhase.status status.Phase) "Lapsed" "the declared posture is honoured"
                Expect.isFalse (EntitlementStatus.grants AdvancedAnalytics status) "governed capability reduced"

                Expect.isTrue
                    (EntitlementStatus.grants EntitlementFloor.ExportOwnData status)
                    "the floor holds even under the strictest declared posture"
            | other -> failtestf "expected a declared reduction, got %A" other
        }
    ]

// ── the data-sovereignty guarantee, structurally ──────────────────────

let private floorTests =
    testList "the floor cannot be governed" [
        test "declare refuses an EntitlementFloor key and names it" {
            match EntitlementGovernance.declare [ AdvancedAnalytics; EntitlementFloor.ExportOwnData ] with
            | Result.Error messages ->
                Expect.hasLength messages 1 "one message per offending key"

                Expect.stringContains
                    messages[0]
                    EntitlementFloor.ExportOwnData
                    "the composition defect names the key that cannot be governed"

                Expect.stringContains messages[0] "hostage" "and says why in terms a reviewer can act on"
            | Result.Ok _ ->
                failtest
                    "declaring an EntitlementFloor key succeeded — the data-sovereignty guarantee is enforced by nothing"
        }

        test "declare refuses every floor member, not just one" {
            match EntitlementGovernance.declare (Set.toList EntitlementFloor.capabilities) with
            | Result.Error messages ->
                Expect.hasLength messages (Set.count EntitlementFloor.capabilities) "every floor key is refused"
            | Result.Ok _ -> failtest "the whole floor was accepted as governable"
        }

        test "the same call on an ordinary key succeeds" {
            // The falsifier: `declare` is capable of returning Ok, so the
            // two refusals above are about the floor and not about
            // `declare` refusing everything.
            match EntitlementGovernance.declare [ AdvancedAnalytics ] with
            | Result.Ok g -> Expect.isTrue (g.GovernedKeys.Contains AdvancedAnalytics) "an ordinary key is governable"
            | Result.Error messages -> failtestf "an ordinary key was refused: %s" (String.Join("; ", messages))
        }

        test "governs refuses a floor key even in a hand-built record that bypassed declare" {
            // Belt and braces: a governance record assembled by some other
            // route (a future loader, a deserialiser) still cannot reach
            // the floor.
            let smuggled: EntitlementGovernance = {
                GovernedKeys = Set.ofList [ EntitlementFloor.ExportOwnData ]
                Unprovisioned = UnlockedWhenUnprovisioned
            }

            Expect.isFalse
                (EntitlementGovernance.governs EntitlementFloor.ExportOwnData smuggled)
                "the floor is refused at the read too, not only at declaration"

            let status, _ =
                cappedAt (standardClaims.ExpiresAt.AddDays 10.0) (Some standardToken)

            let capped = EntitlementFlagCeiling.decorate status smuggled (evaluatorWith [])

            Expect.isTrue
                (run (capped.IsEnabled EntitlementFloor.ExportOwnData ctx))
                "so export stays reachable under a lapsed token even with a smuggled governance record"
        }

        test "the floor is granted in every phase, exhaustively" {
            let phases = [
                EntitlementPhase.Unentitled
                EntitlementPhase.Active 5.0
                EntitlementPhase.Grace(1.0, 6.0)
                EntitlementPhase.Lapsed 40.0
            ]

            // Enumerated against the DU's own case count so a phase added
            // later without a floor guarantee fails here.
            Expect.equal
                (List.length phases)
                (FSharpType.GetUnionCases typeof<EntitlementPhase>).Length
                "every EntitlementPhase case is covered by this assertion"

            for phase in phases do
                let status = {
                    EntitlementStatus.unentitled with
                        Phase = phase
                        GrantedCapabilities = Set.empty
                }

                for floorKey in EntitlementFloor.capabilities do
                    Expect.isTrue
                        (EntitlementStatus.grants floorKey status)
                        $"'{floorKey}' is granted in phase {EntitlementPhase.status phase} even with an empty granted set"
        }
    ]

// ── offline by construction ───────────────────────────────────────────

/// The falsifier for the closure walk below — a record that plainly
/// carries a network destination. If the walk stopped detecting anything,
/// this arm would pass and the real arm would pass for the wrong reason.
///
/// **Deliberately NOT `private`.** It was, on the first draft, and the
/// falsifier arm failed: `FSharpType.IsRecord` does not report a record
/// whose representation is private without non-public binding flags, so
/// the walk descended into nothing and found nothing. That is the exact
/// mode this arm exists to catch, and it caught it on its own probe first —
/// the control must be reflected over on the same terms as the real types,
/// which are public.
type NetworkedControl = { Endpoint: Uri; Retries: int }

let private offlineTests =
    let rec walk (seen: Set<string>) (t: Type) : (string * Type) list =
        if
            isNull t
            || t.IsPrimitive
            || t = typeof<string>
            || t = typeof<DateTimeOffset>
            || t = typeof<TimeSpan>
            || t = typeof<decimal>
            || seen.Contains t.FullName
        then
            []
        else
            let seen = seen.Add t.FullName

            let children =
                if t.IsArray then
                    [ t.GetElementType() |> walk seen ]
                elif t.IsGenericType then
                    t.GetGenericArguments() |> Array.toList |> List.map (walk seen)
                elif FSharpType.IsRecord t then
                    FSharpType.GetRecordFields t
                    |> Array.toList
                    |> List.map (fun p -> (p.Name, p.PropertyType) :: walk seen p.PropertyType)
                elif FSharpType.IsUnion t then
                    FSharpType.GetUnionCases t
                    |> Array.toList
                    |> List.collect (fun c -> c.GetFields() |> Array.toList)
                    |> List.map (fun p -> (p.Name, p.PropertyType) :: walk seen p.PropertyType)
                else
                    []

            List.concat children

    let networkShaped (t: Type) =
        [ typeof<Uri> ] |> List.exists (fun banned -> t = banned)
        || t.FullName.Contains "System.Net."

    testList "offline by construction" [
        test "no verification type's closure carries a network destination" {
            let offenders =
                [
                    typeof<EntitlementToken>
                    typeof<EntitlementClaims>
                    typeof<PinnedEntitlementKey>
                    typeof<EntitlementStatus>
                    typeof<EntitlementGovernance>
                    typeof<RenewalPolicy>
                    typeof<CapacityGrant>
                    typeof<EntitlementRefusal>
                ]
                |> List.collect (walk Set.empty)
                |> List.filter (snd >> networkShaped)

            Expect.isEmpty
                offenders
                "a fetch cannot be added to the entitlement path without adding a field, and no field here could carry one"
        }

        test "the walk detects a deliberately networked control" {
            // Without this arm, the assertion above would be satisfied by
            // a walk that had stopped matching anything at all.
            let offenders =
                walk Set.empty typeof<NetworkedControl> |> List.filter (snd >> networkShaped)

            Expect.isNonEmpty offenders "the closure walk is capable of finding a network destination"
        }
    ]

// ── 492.C — the boot preflight ────────────────────────────────────────

/// A validator that DOES return `Error`, proving the exhaustive
/// no-Error assertion below is capable of failing.
type private AbortingControl() =
    interface IConfigValidator with
        member _.Name = "aborting-control"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return Error "this control aborts the boot" }

let private preflightTests =
    let validatorFor (source: EntitlementStatusSource) =
        EntitlementPreflight.EntitlementConfigValidator(source, declaredFlags, governance, RenewalPolicy.conventional)
        :> IConfigValidator

    let statusIn (phase: EntitlementPhase) : EntitlementStatus = {
        EntitlementStatus.unentitled with
            Phase = phase
            HolderId = "deployment-7f2a"
            TokenId = "tok-0001"
            ExpiresAt = standardClaims.ExpiresAt
            Lifetime = TimeSpan.FromDays 30.0
    }

    let everyRefusal = [
        EntitlementRefusal.KeyIdNotPinned("a", "b")
        EntitlementRefusal.AlgorithmNotPinned("none", "ES256")
        EntitlementRefusal.SignatureRejected "bytes do not verify"
        EntitlementRefusal.ClaimsUnparseable "not an object"
        EntitlementRefusal.ClaimsIncomplete "holderId"
        EntitlementRefusal.ValidityWindowInverted(epoch.AddDays 1.0, epoch)
        EntitlementRefusal.NotYetValid(epoch.AddDays 1.0, epoch, TimeSpan.Zero)
    ]

    testList "492.C boot preflight" [
        test "no input produces Error, across every refusal case" {
            Expect.equal
                (List.length everyRefusal)
                (FSharpType.GetUnionCases typeof<EntitlementRefusal>).Length
                "every EntitlementRefusal case is covered by this assertion"

            for refusal in everyRefusal do
                let source: EntitlementStatusSource =
                    fun () -> async.Return(statusIn (EntitlementPhase.Lapsed 0.0), Some refusal)

                match run ((validatorFor source).Validate()) with
                | Error message ->
                    failtestf
                        "the entitlement preflight returned Error for %A ('%s') — a Phase 9m Error aborts the boot, and a process that will not start is the most complete way to withhold a customer's own data"
                        refusal
                        message
                | Ok
                | Warning _ -> ()
        }

        test "no input produces Error, across every lifecycle phase" {
            let phases = [
                EntitlementPhase.Unentitled
                EntitlementPhase.Active 5.0
                EntitlementPhase.Grace(1.0, 6.0)
                EntitlementPhase.Lapsed 40.0
            ]

            Expect.equal
                (List.length phases)
                (FSharpType.GetUnionCases typeof<EntitlementPhase>).Length
                "every EntitlementPhase case is covered by this assertion"

            for phase in phases do
                let source: EntitlementStatusSource = fun () -> async.Return(statusIn phase, None)

                match run ((validatorFor source).Validate()) with
                | Error message -> failtestf "the preflight returned Error for phase %A ('%s')" phase message
                | Ok
                | Warning _ -> ()
        }

        test "a raising status source is a Warning, not an Error" {
            // Left as a throw, the Phase 9m aggregator converts it to
            // Error itself and aborts — the exact hole Phase 488.A's
            // decorator had to close.
            let source: EntitlementStatusSource =
                fun () -> failwith "token mount is not readable"

            match run ((validatorFor source).Validate()) with
            | Warning message ->
                Expect.stringContains message "token mount is not readable" "the cause reaches the operator"
                Expect.stringContains message "No stored data has been withheld" "and the guarantee is restated"
            | other -> failtestf "expected a Warning from a raising source, got %A" other
        }

        test "the control validator does return Error" {
            // The falsifier for the two exhaustive arms above.
            match run ((AbortingControl() :> IConfigValidator).Validate()) with
            | Error _ -> ()
            | other ->
                failtestf "the control was supposed to return Error, got %A — the assertions above cannot fail" other
        }

        test "an active entitlement with nothing to say is quiet" {
            let source: EntitlementStatusSource =
                fun () -> async.Return(statusIn (EntitlementPhase.Active 25.0), None)

            Expect.equal (run ((validatorFor source).Validate())) Ok "no findings means no noise"
        }

        test "a lapse is surfaced with days-since and the data guarantee" {
            let source: EntitlementStatusSource =
                fun () -> async.Return(statusIn (EntitlementPhase.Lapsed 4.0), None)

            match run ((validatorFor source).Validate()) with
            | Warning message ->
                Expect.stringContains message "LAPSED" "the lapse is loud"
                Expect.stringContains message "4.0 day(s) ago" "with the elapsed time"
                Expect.stringContains message "fully exportable" "and the guarantee stated"
            | other -> failtestf "expected a Warning for a lapse, got %A" other
        }

        test "days-remaining is surfaced inside the renewal notice window" {
            let source: EntitlementStatusSource =
                fun () -> async.Return(statusIn (EntitlementPhase.Active 9.0), None)

            match run ((validatorFor source).Validate()) with
            | Warning message ->
                Expect.stringContains message "expires in 9.0 day(s)" "the operator sees the number, not just a state"
            | other -> failtestf "expected a renewal Warning, got %A" other
        }

        test "an over-long token lifetime is surfaced as revocation latency" {
            let source: EntitlementStatusSource =
                fun () ->
                    async.Return(
                        {
                            statusIn (EntitlementPhase.Active 300.0) with
                                Lifetime = TimeSpan.FromDays 365.0
                        },
                        None
                    )

            match run ((validatorFor source).Validate()) with
            | Warning message ->
                Expect.stringContains message "revocation latency" "the advisory explains what a long lifetime costs"
                Expect.stringContains message "365 day(s)" "naming the presented lifetime"
            | other -> failtestf "expected a lifetime Warning, got %A" other
        }

        test "a governed key no module declared is surfaced as an ungated capability" {
            match EntitlementGovernance.declare [ "reporting.advanced-analytcis" ] with
            | Result.Error errs -> failtestf "fixture refused: %s" (String.Join("; ", errs))
            | Result.Ok typo ->
                let findings = EntitlementPreflight.auditGovernance declaredFlags typo

                Expect.hasLength findings 1 "one finding"
                Expect.stringContains findings[0] "no module declared" "the typo is named as such"
                Expect.stringContains findings[0] "effectively ungated" "with its consequence"
        }

        test "a governed key declared as a Variant is surfaced as uncappable" {
            let variantFlag: FeatureFlag = {
                Key = AdvancedAnalytics
                DefaultValue = FlagValue.Variant([ "a"; "b" ], "a")
                Description = ""
                Owner = None
            }

            match EntitlementGovernance.declare [ AdvancedAnalytics ] with
            | Result.Error errs -> failtestf "fixture refused: %s" (String.Join("; ", errs))
            | Result.Ok g ->
                let findings = EntitlementPreflight.auditGovernance [ variantFlag ] g
                Expect.hasLength findings 1 "one finding"
                Expect.stringContains findings[0] "Variant" "the shape mismatch is named"
        }

        test "the preflight is structural-class and carries no security marker" {
            let source: EntitlementStatusSource =
                fun () -> async.Return(statusIn (EntitlementPhase.Active 25.0), None)

            let validator = validatorFor source

            Expect.isTrue
                (validator :? IStructuralClassValidator)
                "SkipPreflight must not silence a lapse an operator needs to see"

            Expect.isFalse
                (validator :? ISecurityClassValidator)
                "the security marker's contract is 'runs anyway AND still aborts on Error', and this validator has no Error to abort on"
        }

        test "registration is the opt-in and adds exactly one validator" {
            let source: EntitlementStatusSource =
                fun () -> async.Return(statusIn (EntitlementPhase.Active 25.0), None)

            let bare = ServiceCollection() :> IServiceCollection
            let baseline = bare.Count

            let registered =
                EntitlementCompose.serviceRegistration source declaredFlags governance RenewalPolicy.conventional bare

            Expect.equal (registered.Count - baseline) 1 "one IConfigValidator, nothing else"

            let untouched = ServiceCollection() :> IServiceCollection
            Expect.equal untouched.Count baseline "a composition that never calls it registers nothing (GP 13)"
        }

        test "resolveAndCap wires the ceiling, the budget and the registration in one call" {
            let services = ServiceCollection() :> IServiceCollection

            let capped, budget, registration =
                EntitlementCompose.resolveAndCap
                    (validationAt (epoch.AddDays 1.0))
                    (Some standardToken)
                    declaredFlags
                    (evaluatorWith [])
                |> run

            Expect.isTrue (run (capped.IsEnabled AdvancedAnalytics ctx)) "the capped evaluator honours the token"
            Expect.equal (budget.TryLimit Seats) (Some 25L) "the budget carries the capacity grant"
            Expect.equal ((registration services).Count) 1 "the registration is ready to apply"
        }
    ]

let tests =
    testList "Phase 492 — offline entitlement verification" [
        canonicalTests
        verificationTests
        lapseTests
        projectionTests
        gp13Tests
        floorTests
        offlineTests
        preflightTests
    ]