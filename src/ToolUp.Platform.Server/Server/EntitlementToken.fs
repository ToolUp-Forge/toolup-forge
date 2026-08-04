// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Globalization
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform.Usage

// ─── Phase 492 — offline entitlement verification ─────────────────────
//
// A deployment running inside somebody else's infrastructure — the
// Phase 488 in-situ appliance is the sharpest case, but not the only one
// — cannot be gated by a billing service it phones home to, because it
// may have no route to one. The mechanism that works instead is the one
// TLS certificates and software licences have always used: the party with
// the authority to grant signs a **statement of what is granted, and for
// how long**, and the deployment verifies that statement locally against
// a key it already holds.
//
// `EntitlementToken` is that statement. It carries a capability set, a
// capacity set, a validity window and a holder id; it is verified against
// a **pinned** public key with no network call in the path, by
// construction rather than by discipline (see "Offline by construction"
// below).
//
// **Three design decisions are load-bearing, and each is the opposite of
// what a billing-service mindset would produce.**
//
//   1. **Expiry is not a verification failure.** `resolve` answers one
//      question — "is this statement authentic" — and an expired
//      statement is perfectly authentic. What changes at expiry is what
//      the statement GRANTS, which is a separate, non-refusing axis
//      (`EntitlementPhase`). Conflating the two is how licence checks end
//      up throwing at boot, and a boot that fails on a lapsed entitlement
//      is a data lockout wearing a different hat.
//
//   2. **A floor of capabilities is structurally ungovernable.** Reading
//      and exporting your own data are in `EntitlementFloor`, and
//      `EntitlementGovernance.declare` REFUSES to govern a floor key,
//      naming it. This is not a convention a future author can forget or
//      a config a deployment can flip: there is no representable value of
//      any type in this file under which an entitlement state withholds a
//      customer's own data. The guarantee in the docs is a restatement of
//      the type's behaviour, not a promise beside it.
//
//   3. **Revocation is short-lived tokens plus renewal, never a fetch.**
//      A CRL / introspection endpoint is exactly the phone-home this
//      mechanism exists to avoid, and on an air-gapped host an
//      unreachable revocation list fails either open (useless) or closed
//      (a lockout). Bounding the validity window instead makes the
//      revocation latency an explicit, declared number —
//      `RenewalPolicy.MaxTokenLifetime` — and the preflight says so when a
//      token exceeds it, because a token valid for a year is a token that
//      cannot be revoked.
//
// **Offline by construction.** No type in this file's transitive closure
// carries a `Uri`, an endpoint, a host name, or any other field a network
// call could be built from, and the verification path's entire input is
// (token record, `VerifyDetachedJws` function, clock). There is nothing to
// discipline: a fetch cannot be added here without adding a field, and
// `EntitlementTokenTests` walks the closure and fails if one appears —
// falsified against a deliberately-networked control type, so a walk that
// had stopped matching could not report closure. This is the same
// structural argument Phase 488.C's telemetry diode makes about strings.
//
// **Verification reuses Phase 182's shape, it does not invent a second
// one.** `VerifyDetachedJws` (Phase 488.B) is the structural function seam
// `byte[] -> string -> Async<Result<unit, string>>`; the SDK core carries
// no crypto stack (GP 1) and a deployment adapts its own verifier at its
// own call site. The issuing side needs only
// `EntitlementClaims.canonicalBytes` — the exact bytes to sign.
//
// **A mechanism, not a licence (GP 13).** `EntitlementGovernance.none`
// governs nothing, and a composition that never mentions this file
// registers nothing and resolves every flag exactly as before (GP 11).
// forge imposes no entitlement of its own on anybody, and an unprovisioned
// deployment is fully unlocked unless its operator declares otherwise.

// ─── 492.A — the token model ──────────────────────────────────────────

/// One capacity dimension an entitlement bounds — seats, rows, compute
/// units, whatever the deployment meters.
///
/// `Dimension` is a `ResourceKinds.*`-shaped string rather than a closed
/// DU for the same reason `UsageRecord.ResourceKind` is: the set of things
/// a deployment might bound is open, and the SDK never interprets the
/// value. `Limit` is `int64` rather than `decimal` because every capacity
/// this bounds is a count.
type CapacityGrant = {
    /// The metered dimension. Matches a `ResourceKinds.*` constant where
    /// one applies, so a capacity entitlement and the usage records that
    /// measure it speak the same vocabulary.
    Dimension: string
    /// The maximum the grant permits. Non-negative; a negative limit is
    /// rejected at parse as `ClaimsIncomplete`, because "minus four seats"
    /// has no reading and silently clamping it would invent a grant
    /// nobody signed.
    Limit: int64
}

/// What an entitlement statement asserts. These are the claims that get
/// signed — `EntitlementClaims.canonicalBytes` is the exact byte
/// sequence, so an issuer and a verifier that agree on this record agree
/// on the signature without a shared serialiser.
type EntitlementClaims = {
    /// Who the entitlement is for. Opaque to the SDK — a deployment id, a
    /// customer id, a hostname. Carried so an operator reading a preflight
    /// line can tell whether the token on this host is the token meant for
    /// this host; the SDK never gates on it, because a holder-id check
    /// against a self-asserted value would prove nothing the signature
    /// does not already prove.
    HolderId: string
    /// Stable id for this token. The unit of renewal: a replacement token
    /// carries a new `TokenId`, so an operator can say which one is
    /// installed without diffing capability sets.
    TokenId: string
    /// When the issuer signed. Advisory — the validity window is
    /// `NotBefore`..`ExpiresAt`; `IssuedAt` exists so a support
    /// conversation can establish provenance.
    IssuedAt: DateTimeOffset
    /// Start of the validity window. A token presented before it is
    /// refused (`NotYetValid`) rather than treated as lapsed — a
    /// not-yet-valid token is a provisioning mistake, and reporting it as
    /// an expiry would send the operator looking for the wrong thing.
    NotBefore: DateTimeOffset
    /// End of the validity window. Passing it is a LAPSE, not a refusal —
    /// see `EntitlementPhase`.
    ExpiresAt: DateTimeOffset
    /// The capabilities granted. Each is a Phase 5c feature-flag key, so
    /// gated code reads a flag and never sees a token (492.B).
    Capabilities: Set<string>
    /// The capacity limits granted, at most one per dimension. A duplicate
    /// dimension is rejected at parse rather than last-write-wins, because
    /// two limits for one dimension is an ambiguous grant and picking one
    /// silently picks it wrong half the time.
    Capacities: CapacityGrant list
    /// How long past `ExpiresAt` the entitlement keeps functioning at full
    /// capability while the preflight escalates. `TimeSpan.Zero` means
    /// reduction begins at expiry.
    GraceWindow: TimeSpan
}

/// The verification key this deployment will accept, by identity.
///
/// **The pin is enforced by the verifier's key material, not by this
/// record.** A composed `VerifyDetachedJws` holds one key — the pinned one
/// — so a signature from any other key fails cryptographically whatever
/// this record says. What `KeyId` / `Algorithm` add is a *diagnosis*: the
/// token echoes the key and algorithm the issuer used, and comparing them
/// first turns "signature rejected" into "signed with key
/// vendor-2025-rotation, this host pins vendor-2024" — which is the
/// difference between an operator resolving the problem and an operator
/// escalating to the party they may not be able to reach.
///
/// Treating those echoed fields as *authority* would be the same trust
/// mistake Phase 488.B refuses when it verifies provenance before reading
/// requirement declarations out of an artefact. They are read to explain a
/// refusal, never to admit one.
type PinnedEntitlementKey = {
    /// The key id this deployment pins. Compared against the token's
    /// echoed `KeyId` to name a mismatch early.
    KeyId: string
    /// The signature algorithm this deployment pins. A token declaring a
    /// different algorithm is refused without consulting the verifier —
    /// algorithm confusion is a class of attack, not a compatibility
    /// question.
    Algorithm: string
}

/// A signed entitlement statement as it arrives — claims as canonical
/// JSON text plus a detached signature over those exact bytes.
///
/// The claims travel as TEXT, not as a re-serialised record, because the
/// signature is over bytes: parsing to a record and re-canonicalising
/// before verifying would make every future serialisation change a silent
/// signature break. `resolve` verifies the bytes it was given and parses
/// afterwards.
type EntitlementToken = {
    /// Canonical JSON of the claims — the exact text that was signed.
    ClaimsJson: string
    /// Detached JWS over `ClaimsJson`'s UTF-8 bytes (the Phase 182 sidecar
    /// shape `VerifyDetachedJws` consumes).
    DetachedJws: string
    /// The key id the issuer says it signed with. Echoed, self-asserted,
    /// used for diagnosis only — see `PinnedEntitlementKey`.
    KeyId: string
    /// The algorithm the issuer says it signed with. Same status.
    Algorithm: string
}

/// Why an entitlement statement could not be established.
///
/// **Expiry is deliberately absent.** Every case here is "this is not an
/// authentic statement about this deployment"; a lapse is an authentic
/// statement whose window has passed, and it lives on
/// `EntitlementPhase` instead. Keeping them apart is what lets the
/// preflight surface a lapse loudly without ever aborting a boot.
///
/// Every case NAMES what did not match, for the reason Phase 488.B gives:
/// an operator who cannot tell a tampered file from a wrong key from a
/// clock problem has to escalate to make any progress, and escalation is
/// the dependency this whole mechanism exists to remove.
type EntitlementRefusal =
    /// The token was signed with a key this deployment does not pin.
    | KeyIdNotPinned of presented: string * pinned: string
    /// The token declares an algorithm this deployment does not pin.
    | AlgorithmNotPinned of presented: string * pinned: string
    /// The signature did not verify over the claim bytes — tampering, a
    /// truncated file, or a key that matches by id but not by material.
    | SignatureRejected of reason: string
    /// The claim bytes verified but are not well-formed claims.
    | ClaimsUnparseable of reason: string
    /// A claim the check needs is absent, blank, or nonsensical.
    | ClaimsIncomplete of field: string
    /// `NotBefore` is after `ExpiresAt` — a window that never opens.
    | ValidityWindowInverted of notBefore: DateTimeOffset * expiresAt: DateTimeOffset
    /// The window has not opened yet, even allowing for declared clock
    /// skew. A provisioning mistake, reported as itself.
    | NotYetValid of notBefore: DateTimeOffset * now: DateTimeOffset * skewAllowance: TimeSpan

[<RequireQualifiedAccess>]
module EntitlementRefusal =

    /// A one-line operator-readable description naming the mismatch.
    let describe (refusal: EntitlementRefusal) : string =
        match refusal with
        | EntitlementRefusal.KeyIdNotPinned(presented, pinned) ->
            sprintf
                "entitlement token was signed with key id '%s'; this deployment pins '%s'. Either the issuer rotated its signing key and this host has not been updated with the new pin, or the token was issued for a different trust root."
                presented
                pinned
        | EntitlementRefusal.AlgorithmNotPinned(presented, pinned) ->
            sprintf
                "entitlement token declares signature algorithm '%s'; this deployment pins '%s'. Algorithm substitution is refused without consulting the verifier."
                presented
                pinned
        | EntitlementRefusal.SignatureRejected reason ->
            sprintf
                "entitlement token signature rejected over the claim bytes: %s. The claims were altered after signing, the file is truncated, or the key material behind the pinned key id does not match."
                reason
        | EntitlementRefusal.ClaimsUnparseable reason ->
            sprintf
                "entitlement claims could not be parsed: %s. The signature verified, so this is a format mismatch."
                reason
        | EntitlementRefusal.ClaimsIncomplete field ->
            sprintf "entitlement claims incomplete: %s is absent, blank, or out of range." field
        | EntitlementRefusal.ValidityWindowInverted(notBefore, expiresAt) ->
            sprintf
                "entitlement validity window is inverted: notBefore %s is after expiresAt %s, so the window never opens."
                (notBefore.ToString("o", CultureInfo.InvariantCulture))
                (expiresAt.ToString("o", CultureInfo.InvariantCulture))
        | EntitlementRefusal.NotYetValid(notBefore, now, skew) ->
            sprintf
                "entitlement is not valid yet: notBefore %s, this host reads %s, declared clock-skew allowance %g minutes. This is a provisioning ordering problem, not an expiry."
                (notBefore.ToString("o", CultureInfo.InvariantCulture))
                (now.ToString("o", CultureInfo.InvariantCulture))
                skew.TotalMinutes

/// The capabilities no entitlement state can withhold.
///
/// **This is the whole data-sovereignty guarantee, and it is enforced by
/// refusal rather than by care.** `EntitlementGovernance.declare` rejects
/// any attempt to govern one of these keys, so there is no configuration —
/// no token, no lapse, no posture, no combination — under which a
/// customer loses the ability to read or export their own data. A
/// mechanism that CAN hold data hostage and merely promises not to is a
/// different mechanism from one that cannot, and the second is what a
/// customer's own security review can verify in an afternoon.
///
/// The keys are ordinary Phase 5c flag keys under the reserved `platform.`
/// namespace, so a deployment can still turn export off for its own
/// reasons through the normal scope walk. What it cannot do is make that
/// switch answer to an entitlement.
module EntitlementFloor =

    /// Reading the deployment's own stored data.
    [<Literal>]
    let ReadOwnData = "platform.data.read"

    /// Exporting the deployment's own stored data — the one capability
    /// whose absence would make a lapse indistinguishable from
    /// confiscation.
    [<Literal>]
    let ExportOwnData = "platform.data.export"

    /// The full ungovernable set. `EntitlementGovernance.declare` refuses
    /// every member, and the resolved capability set always contains it
    /// whatever phase the entitlement is in.
    let capabilities: Set<string> = Set.ofList [ ReadOwnData; ExportOwnData ]

// ─── Canonical form ───────────────────────────────────────────────────

[<RequireQualifiedAccess>]
module EntitlementClaims =

    /// Property order in the canonical form. Alphabetical, fixed, and
    /// exhaustive — the ONLY thing that makes issuer and verifier agree on
    /// a byte sequence without shipping a shared serialiser. Adding a
    /// claim appends a name here and is a wire change by definition.
    let private canonicalOrder = [
        "capabilities"
        "capacities"
        "expiresAt"
        "graceWindowSeconds"
        "holderId"
        "issuedAt"
        "notBefore"
        "tokenId"
    ]

    /// Every canonical timestamp is normalised to UTC first. Two
    /// representations of the same instant — `+00:00` and `+01:00` an hour
    /// later — must produce identical bytes, or a token signed in one
    /// timezone fails verification in another.
    let private stamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)

    /// The canonical JSON text of a claim set. Deterministic: fixed
    /// property order, capabilities sorted, capacities sorted by
    /// dimension, timestamps UTC round-trip, whitespace absent.
    let canonicalJson (claims: EntitlementClaims) : string =
        let capabilities = JsonArray()

        claims.Capabilities
        |> Set.toList
        |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.iter (fun c -> capabilities.Add(JsonValue.Create c))

        let capacities = JsonArray()

        claims.Capacities
        |> List.sortWith (fun a b -> String.CompareOrdinal(a.Dimension, b.Dimension))
        |> List.iter (fun grant ->
            let entry = JsonObject()
            entry["dimension"] <- JsonValue.Create grant.Dimension
            entry["limit"] <- JsonValue.Create grant.Limit
            capacities.Add entry)

        let root = JsonObject()

        let put (name: string) (node: JsonNode) = root[name] <- node

        // Written in `canonicalOrder`; JsonObject preserves insertion
        // order, so the order below IS the canonical order.
        put "capabilities" capabilities
        put "capacities" capacities
        put "expiresAt" (JsonValue.Create(stamp claims.ExpiresAt))
        put "graceWindowSeconds" (JsonValue.Create(int64 claims.GraceWindow.TotalSeconds))
        put "holderId" (JsonValue.Create claims.HolderId)
        put "issuedAt" (JsonValue.Create(stamp claims.IssuedAt))
        put "notBefore" (JsonValue.Create(stamp claims.NotBefore))
        put "tokenId" (JsonValue.Create claims.TokenId)

        root.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    /// The exact bytes an issuer signs and a verifier verifies. This is
    /// the entire issuing-side contract: a vendor that can produce a
    /// detached JWS over these bytes can issue tokens this SDK accepts,
    /// with no forge dependency of any kind.
    let canonicalBytes (claims: EntitlementClaims) : byte[] =
        Encoding.UTF8.GetBytes(canonicalJson claims)

    /// The declared names, in canonical order — read by the round-trip
    /// test so a claim added without extending `canonicalOrder` fails
    /// rather than silently dropping out of the signed bytes.
    let canonicalPropertyNames: string list = canonicalOrder

    /// Minimal `Result` computation expression, private to the parse.
    ///
    /// The alternative shapes are both worse here: eight nested
    /// `Result.bind` lambdas indent past readability, and a
    /// collect-the-errors-then-unwrap pass needs an unreachable failure
    /// branch per field. This is four lines and every field reads as one
    /// line of the record it populates.
    type private ResultBuilder() =
        member _.Bind(r: Result<'a, 'e>, f: 'a -> Result<'b, 'e>) = Result.bind f r
        member _.Return(v: 'a) : Result<'a, 'e> = Result.Ok v
        member _.ReturnFrom(r: Result<'a, 'e>) = r

    let private claim = ResultBuilder()

    /// `JsonObject.TryGetPropertyValue` carries two byref overloads, so
    /// F#'s tuple-return sugar is ambiguous at the call site. One explicit
    /// wrapper keeps the four readers below free of that noise, and folds
    /// "absent" and "present but JSON null" into the same `None` — a null
    /// claim is an absent claim as far as every reader here is concerned.
    let private tryProperty (root: JsonObject) (name: string) : JsonNode option =
        let mutable node: JsonNode = null

        if root.TryGetPropertyValue(name, &node) && not (isNull node) then
            Some node
        else
            None

    let private requiredString (root: JsonObject) (name: string) : Result<string, EntitlementRefusal> =
        match tryProperty root name with
        | Some node ->
            try
                let value = node.GetValue<string>()

                if String.IsNullOrWhiteSpace value then
                    Result.Error(EntitlementRefusal.ClaimsIncomplete name)
                else
                    Result.Ok value
            with _ ->
                Result.Error(EntitlementRefusal.ClaimsUnparseable(sprintf "%s is not a JSON string" name))
        | None -> Result.Error(EntitlementRefusal.ClaimsIncomplete name)

    let private requiredStamp (root: JsonObject) (name: string) : Result<DateTimeOffset, EntitlementRefusal> = claim {
        let! text = requiredString root name
        let mutable parsed = DateTimeOffset.MinValue

        if DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, &parsed) then
            return parsed
        else
            return! Result.Error(EntitlementRefusal.ClaimsIncomplete name)
    }

    let private optionalInt64 (root: JsonObject) (name: string) : Result<int64 option, EntitlementRefusal> =
        match tryProperty root name with
        | Some node ->
            try
                Result.Ok(Some(node.GetValue<int64>()))
            with _ ->
                Result.Error(EntitlementRefusal.ClaimsUnparseable(sprintf "%s is not a JSON integer" name))
        | None -> Result.Ok None

    let private parseCapabilities (root: JsonObject) : Result<Set<string>, EntitlementRefusal> =
        match tryProperty root "capabilities" with
        | Some(:? JsonArray as arr) ->
            try
                arr
                |> Seq.choose (fun node ->
                    if isNull node then
                        None
                    else
                        let value = node.GetValue<string>()
                        if String.IsNullOrWhiteSpace value then None else Some value)
                |> Set.ofSeq
                |> Result.Ok
            with _ ->
                Result.Error(EntitlementRefusal.ClaimsUnparseable "capabilities contains a non-string entry")
        | Some _ -> Result.Error(EntitlementRefusal.ClaimsUnparseable "capabilities is not a JSON array")
        | None -> Result.Ok Set.empty

    let private parseGrant (node: JsonNode) : Result<CapacityGrant, EntitlementRefusal> =
        match node with
        | :? JsonObject as entry -> claim {
            let! dimension = requiredString entry "dimension"
            let! limit = optionalInt64 entry "limit"

            match limit with
            | None ->
                return! Result.Error(EntitlementRefusal.ClaimsIncomplete(sprintf "capacities['%s'].limit" dimension))
            | Some limit when limit < 0L ->
                // "minus four seats" has no reading, and clamping it to
                // zero would invent a grant nobody signed.
                return! Result.Error(EntitlementRefusal.ClaimsIncomplete(sprintf "capacities['%s'].limit" dimension))
            | Some limit -> return { Dimension = dimension; Limit = limit }
          }
        | _ -> Result.Error(EntitlementRefusal.ClaimsUnparseable "capacities entry is not a JSON object")

    let private parseCapacities (root: JsonObject) : Result<CapacityGrant list, EntitlementRefusal> =
        match tryProperty root "capacities" with
        | Some(:? JsonArray as arr) ->
            let parsed = arr |> Seq.map parseGrant |> List.ofSeq

            let firstError =
                parsed
                |> List.tryPick (function
                    | Result.Error e -> Some e
                    | Result.Ok _ -> None)

            match firstError with
            | Some e -> Result.Error e
            | None ->
                let grants =
                    parsed
                    |> List.choose (function
                        | Result.Ok g -> Some g
                        | Result.Error _ -> None)

                let duplicated =
                    grants
                    |> List.countBy _.Dimension
                    |> List.tryPick (fun (dimension, count) -> if count > 1 then Some dimension else None)

                match duplicated with
                | Some dimension ->
                    // Last-write-wins would resolve this silently and pick
                    // wrong half the time.
                    Result.Error(
                        EntitlementRefusal.ClaimsIncomplete(
                            sprintf "capacities declares dimension '%s' twice" dimension
                        )
                    )
                | None -> Result.Ok grants
        | Some _ -> Result.Error(EntitlementRefusal.ClaimsUnparseable "capacities is not a JSON array")
        | None -> Result.Ok []

    let private parseGraceWindow (root: JsonObject) : Result<TimeSpan, EntitlementRefusal> = claim {
        let! seconds = optionalInt64 root "graceWindowSeconds"

        match seconds with
        | None -> return TimeSpan.Zero
        | Some s when s < 0L -> return! Result.Error(EntitlementRefusal.ClaimsIncomplete "graceWindowSeconds")
        | Some s -> return TimeSpan.FromSeconds(float s)
    }

    /// Parse canonical claim text.
    ///
    /// Called only AFTER the signature over those bytes verified, so every
    /// failure here is a format mismatch rather than a trust problem —
    /// which is why `ClaimsUnparseable`'s description says so. The one
    /// exception is `ValidityWindowInverted`, which is a semantic check on
    /// authentic claims and is refused because a window that never opens
    /// cannot be resolved to any phase.
    let parse (json: string) : Result<EntitlementClaims, EntitlementRefusal> =
        let root =
            try
                match JsonNode.Parse json with
                | null -> Result.Error(EntitlementRefusal.ClaimsUnparseable "claims text is JSON null")
                | :? JsonObject as root -> Result.Ok root
                | _ -> Result.Error(EntitlementRefusal.ClaimsUnparseable "claims text is not a JSON object")
            with ex ->
                Result.Error(EntitlementRefusal.ClaimsUnparseable ex.Message)

        claim {
            let! root = root
            let! holderId = requiredString root "holderId"
            let! tokenId = requiredString root "tokenId"
            let! issuedAt = requiredStamp root "issuedAt"
            let! notBefore = requiredStamp root "notBefore"
            let! expiresAt = requiredStamp root "expiresAt"
            let! capabilities = parseCapabilities root
            let! capacities = parseCapacities root
            let! graceWindow = parseGraceWindow root

            if notBefore > expiresAt then
                return! Result.Error(EntitlementRefusal.ValidityWindowInverted(notBefore, expiresAt))
            else
                return {
                    HolderId = holderId
                    TokenId = tokenId
                    IssuedAt = issuedAt
                    NotBefore = notBefore
                    ExpiresAt = expiresAt
                    Capabilities = capabilities
                    Capacities = capacities
                    GraceWindow = graceWindow
                }
        }

    /// How long the token is valid for. Read by the renewal advisory: a
    /// long lifetime is a long revocation latency.
    let lifetime (claims: EntitlementClaims) : TimeSpan = claims.ExpiresAt - claims.NotBefore

    /// Build a token from claims and a detached signature over
    /// `canonicalBytes`. The claims are canonicalised ONCE here so the
    /// text the token carries is provably the text that was signed.
    let toToken
        (keyId: string)
        (algorithm: string)
        (detachedJws: string)
        (claims: EntitlementClaims)
        : EntitlementToken =
        {
            ClaimsJson = canonicalJson claims
            DetachedJws = detachedJws
            KeyId = keyId
            Algorithm = algorithm
        }

// ─── 492.C — lifecycle phases ─────────────────────────────────────────

/// Where an authentic entitlement sits in its lifecycle. This is the
/// non-refusing axis: every case here describes a deployment that
/// verified something, including the lapsed one.
///
/// `RequireQualifiedAccess` is mandatory — `Active` would otherwise shadow
/// `JobStatus.Active` in the shared namespace.
[<RequireQualifiedAccess>]
type EntitlementPhase =
    /// No entitlement is provisioned and the deployment's declared posture
    /// is `UnlockedWhenUnprovisioned` — governed capabilities are all
    /// granted. The identity case: forge imposes no licence (GP 13).
    | Unentitled
    /// Inside the validity window.
    | Active of daysRemaining: float
    /// Past `ExpiresAt` but inside `GraceWindow` — STILL FULLY
    /// FUNCTIONAL. Grace is deliberately a full-capability state with a
    /// loud preflight rather than a partial one: a reduction that begins
    /// quietly at expiry is discovered by users, and a reduction announced
    /// for days beforehand is discovered by the operator.
    | Grace of daysSinceExpiry: float * graceDaysRemaining: float
    /// Past the grace window. Governed capabilities reduce to
    /// `EntitlementFloor`; nothing else changes, and no stored data
    /// becomes unreachable.
    | Lapsed of daysSinceExpiry: float

[<RequireQualifiedAccess>]
module EntitlementPhase =

    /// Stable wire string — read by the preflight line and any external
    /// monitor keying off it.
    let status =
        function
        | EntitlementPhase.Unentitled -> "Unentitled"
        | EntitlementPhase.Active _ -> "Active"
        | EntitlementPhase.Grace _ -> "Grace"
        | EntitlementPhase.Lapsed _ -> "Lapsed"

    /// Whether governed capabilities beyond the floor are granted in this
    /// phase. `Grace` is `true` — that is the point of grace.
    let grantsGovernedCapabilities =
        function
        | EntitlementPhase.Unentitled
        | EntitlementPhase.Active _
        | EntitlementPhase.Grace _ -> true
        | EntitlementPhase.Lapsed _ -> false

    /// An operator-readable line. Days-remaining and lapse state are the
    /// two numbers 492.C requires be surfaced loudly, so they are in the
    /// string rather than only in the data.
    let describe =
        function
        | EntitlementPhase.Unentitled ->
            "no entitlement provisioned — every governed capability is granted (this deployment imposes no licence of its own)"
        | EntitlementPhase.Active days -> sprintf "entitlement active, %.1f day(s) remaining before expiry" days
        | EntitlementPhase.Grace(since, remaining) ->
            sprintf
                "entitlement EXPIRED %.1f day(s) ago and is running on its grace window — full capability continues for a further %.1f day(s), after which governed capabilities reduce to read + export. Renew now."
                since
                remaining
        | EntitlementPhase.Lapsed since ->
            sprintf
                "entitlement LAPSED %.1f day(s) ago — governed capabilities have reduced to read + export. All stored data remains readable and fully exportable; nothing has been withheld. Renew to restore."
                since

/// The resolved entitlement state a deployment runs under: which phase,
/// which capabilities that phase actually grants, and which capacity
/// limits apply.
type EntitlementStatus = {
    /// Lifecycle phase.
    Phase: EntitlementPhase
    /// Holder the token names, or `""` when unprovisioned.
    HolderId: string
    /// Token id, or `""` when unprovisioned.
    TokenId: string
    /// Window end. `DateTimeOffset.MaxValue` when unprovisioned — an
    /// absent entitlement does not expire.
    ExpiresAt: DateTimeOffset
    /// The capabilities in force RIGHT NOW, floor included. In `Lapsed`
    /// this is the floor alone; in every other phase it is the token's set
    /// unioned with the floor.
    GrantedCapabilities: Set<string>
    /// Capacity limits by dimension.
    ///
    /// **Lapse does not zero these.** Reduction acts through the
    /// capability set and nowhere else — one gating model (492.B) — so a
    /// lapsed deployment reports the limits its token declared rather than
    /// a synthetic zero. A zero budget would present a lapse as a capacity
    /// breach, which is a second, contradictory explanation for the same
    /// event.
    Capacities: Map<string, int64>
    /// Declared token lifetime, for the renewal advisory.
    Lifetime: TimeSpan
}

[<RequireQualifiedAccess>]
module EntitlementStatus =

    /// The identity state: nothing provisioned, everything granted. What a
    /// deployment that composes none of this behaves as.
    let unentitled: EntitlementStatus = {
        Phase = EntitlementPhase.Unentitled
        HolderId = ""
        TokenId = ""
        ExpiresAt = DateTimeOffset.MaxValue
        GrantedCapabilities = EntitlementFloor.capabilities
        Capacities = Map.empty
        Lifetime = TimeSpan.MaxValue
    }

    /// Whether a capability is granted in this state.
    ///
    /// `Unentitled` grants everything asked of it — the phase means "no
    /// entitlement governs this deployment", so answering `false` for an
    /// unknown key would turn an absent licence into a restrictive one.
    /// Floor keys are granted in every phase without exception.
    let grants (capability: string) (status: EntitlementStatus) : bool =
        if EntitlementFloor.capabilities.Contains capability then
            true
        else
            match status.Phase with
            | EntitlementPhase.Unentitled -> true
            | _ -> status.GrantedCapabilities.Contains capability

// ─── 492.B — governance: which flag keys an entitlement may reach ─────

/// What an absent entitlement means. Declared, never inferred: a
/// deployment with the mechanism composed and no token yet installed is
/// indistinguishable from one that will never have a token, and guessing
/// either way is wrong for somebody.
type UnprovisionedPosture =
    /// Default and identity — no token means no gating (GP 13). forge
    /// imposes no licence, and a deployment that has not been provisioned
    /// is not thereby restricted.
    | UnlockedWhenUnprovisioned
    /// The deployment declares that an absent token is a lapse. The floor
    /// still holds — this reduces capability, it never withholds data.
    | ReducedWhenUnprovisioned

/// Which feature-flag keys an entitlement is allowed to gate.
///
/// The set is CLOSED and declared at compose time, for two reasons. An
/// open set would mean the entitlement layer answers for every flag in the
/// deployment, including ones no entitlement was ever meant to reach; and
/// a closed set is what makes `EntitlementFloor` enforceable, because
/// `declare` can refuse the keys that must never be governed.
type EntitlementGovernance = {
    /// Governed flag keys. Never contains a floor key — `declare` refuses.
    GovernedKeys: Set<string>
    /// What an absent token means here.
    Unprovisioned: UnprovisionedPosture
}

[<RequireQualifiedAccess>]
module EntitlementGovernance =

    /// The identity: nothing governed, unprovisioned means unlocked. A
    /// ceiling built from this is a no-op on every key (GP 11).
    let none: EntitlementGovernance = {
        GovernedKeys = Set.empty
        Unprovisioned = UnlockedWhenUnprovisioned
    }

    /// Declare the governed key set.
    ///
    /// **Refuses any `EntitlementFloor` key, naming it.** This is the
    /// enforcement point for the data-sovereignty guarantee: it is not
    /// possible to compose a governance record under which reading or
    /// exporting the deployment's own data answers to an entitlement, so
    /// the guarantee holds for every downstream deployment without any of
    /// them having to know about it. `Result.Error` carries one message per
    /// offending key — a composition defect, surfaced at the call that made
    /// it rather than as a mysterious ungated export months later.
    let declare (keys: string seq) : Result<EntitlementGovernance, string list> =
        let keys = keys |> Seq.filter (String.IsNullOrWhiteSpace >> not) |> Set.ofSeq

        let offending =
            keys
            |> Set.intersect EntitlementFloor.capabilities
            |> Set.toList
            |> List.map (fun key ->
                sprintf
                    "'%s' is an EntitlementFloor capability and cannot be governed by an entitlement. Reading and exporting a deployment's own data must never answer to an entitlement state — that is the difference between a licence and a hostage. Gate this key through the ordinary feature-flag scope walk if the deployment wants it off for its own reasons."
                    key)

        match offending with
        | [] ->
            Result.Ok {
                GovernedKeys = keys
                Unprovisioned = UnlockedWhenUnprovisioned
            }
        | _ -> Result.Error offending

    /// Set the unprovisioned posture.
    let withUnprovisioned (posture: UnprovisionedPosture) (governance: EntitlementGovernance) : EntitlementGovernance = {
        governance with
            Unprovisioned = posture
    }

    /// Whether this key answers to the entitlement. Floor keys never do,
    /// belt-and-braces against a governance record built some other way.
    let governs (key: string) (governance: EntitlementGovernance) : bool =
        not (EntitlementFloor.capabilities.Contains key)
        && governance.GovernedKeys.Contains key

// ─── Renewal policy (the revocation mechanism) ────────────────────────

/// How this deployment expects entitlements to be renewed. Read by the
/// preflight advisory only — nothing here refuses a token.
///
/// **This record IS the revocation mechanism.** There is no CRL and no
/// introspection call; the bound on how long a compromised or withdrawn
/// entitlement stays effective is exactly `MaxTokenLifetime`, and the
/// preflight says so when a presented token exceeds it. Making the latency
/// a declared number is the honest version of a revocation story for a
/// host that may have no route to anywhere.
type RenewalPolicy = {
    /// The longest validity window this deployment considers reasonable. A
    /// token whose lifetime exceeds it draws a preflight `Warning` naming
    /// both numbers — never a refusal, because refusing a valid token over
    /// a local policy preference would be a self-inflicted lockout.
    MaxTokenLifetime: TimeSpan
    /// How long before expiry the preflight escalates from a quiet line to
    /// a `Warning`.
    RenewalNotice: TimeSpan
}

[<RequireQualifiedAccess>]
module RenewalPolicy =

    /// A conventional starting point: renew quarterly, start saying so a
    /// fortnight out. Not a default anyone is held to — it is the number an
    /// operator who has not thought about revocation latency should think
    /// about first.
    let conventional: RenewalPolicy = {
        MaxTokenLifetime = TimeSpan.FromDays 90.0
        RenewalNotice = TimeSpan.FromDays 14.0
    }

    /// No advisory at all — every lifetime accepted quietly.
    let silent: RenewalPolicy = {
        MaxTokenLifetime = TimeSpan.MaxValue
        RenewalNotice = TimeSpan.Zero
    }

// ─── 492.A — resolution ───────────────────────────────────────────────

/// Everything the offline check needs. Note what is absent: no endpoint,
/// no client, no host. The closure of this record is the closure the
/// offline-by-construction test walks.
type EntitlementValidation = {
    /// The key this deployment pins.
    Pin: PinnedEntitlementKey
    /// The verifier, bound to the pinned key's material. Structural
    /// function seam, not an interface over a crypto stack (GP 1) — the
    /// same shape Phase 488.B uses.
    Verify: VerifyDetachedJws
    /// Clock. A function so a test can move time; a deployment passes
    /// `fun () -> DateTimeOffset.UtcNow`.
    Clock: unit -> DateTimeOffset
    /// How far this host's clock may legitimately be from the issuer's.
    ///
    /// Applied IN THE HOLDER'S FAVOUR on both edges of the window: skew
    /// delays a lapse and admits a token whose `NotBefore` is barely in the
    /// future. That direction is the fail-safe one — a drifting appliance
    /// clock must not manufacture an expiry — and it is a deliberate
    /// choice, not an accident of the comparison order.
    ClockSkewTolerance: TimeSpan
    /// Which flag keys the entitlement reaches.
    Governance: EntitlementGovernance
    /// Renewal advisory thresholds.
    Renewal: RenewalPolicy
}

[<RequireQualifiedAccess>]
module EntitlementValidation =

    /// Read the skew allowance a Phase 488 appliance already declared,
    /// rather than making an operator state the same drift twice.
    ///
    /// A one-line bridge on purpose: entitlements are NOT appliance-only —
    /// the mechanism is generic offline licensing and works identically on
    /// an ordinary networked deployment — so the integration is a value
    /// read, not a dependency. Nothing in this file requires an
    /// `ApplianceProfile` to exist.
    let skewFromApplianceProfile (profile: ApplianceProfile) : TimeSpan = profile.ClockSkewTolerance

    /// A validation config with the conventional renewal policy and no
    /// skew allowance — the shape a networked deployment wants.
    let create
        (pin: PinnedEntitlementKey)
        (verify: VerifyDetachedJws)
        (governance: EntitlementGovernance)
        : EntitlementValidation =
        {
            Pin = pin
            Verify = verify
            Clock = fun () -> DateTimeOffset.UtcNow
            ClockSkewTolerance = TimeSpan.Zero
            Governance = governance
            Renewal = RenewalPolicy.conventional
        }

    /// Same, with a declared clock-skew allowance.
    let withClockSkew (tolerance: TimeSpan) (validation: EntitlementValidation) : EntitlementValidation = {
        validation with
            ClockSkewTolerance = tolerance
    }

    /// Same, with a renewal policy.
    let withRenewal (policy: RenewalPolicy) (validation: EntitlementValidation) : EntitlementValidation = {
        validation with
            Renewal = policy
    }

    /// Phase for an authentic claim set at `now`, given the skew allowance.
    let private phaseAt (now: DateTimeOffset) (skew: TimeSpan) (claims: EntitlementClaims) : EntitlementPhase =
        // Skew shifts the observed instant EARLIER, which is the
        // holder's favour on the expiry edge.
        let effective = now - skew
        let graceEnd = claims.ExpiresAt + claims.GraceWindow

        if effective <= claims.ExpiresAt then
            EntitlementPhase.Active((claims.ExpiresAt - effective).TotalDays)
        elif effective <= graceEnd then
            EntitlementPhase.Grace((effective - claims.ExpiresAt).TotalDays, (graceEnd - effective).TotalDays)
        else
            EntitlementPhase.Lapsed((effective - claims.ExpiresAt).TotalDays)

    /// Establish the entitlement state from a presented token, offline.
    ///
    /// Order matters and is not arbitrary:
    ///
    ///   1. **Pin comparison** on the echoed key id / algorithm — cheap,
    ///      and it turns an opaque signature failure into a named one.
    ///      Diagnosis only; the verifier's key material is the authority.
    ///   2. **Signature over the presented bytes.** Nothing is read out of
    ///      the claims before this, for the reason Phase 488.B gives about
    ///      reading requirements out of an unauthenticated artefact.
    ///   3. **Parse**, then the window checks.
    ///
    /// `None` means no token is provisioned, which is resolved from the
    /// declared `UnprovisionedPosture` rather than guessed.
    let resolve
        (validation: EntitlementValidation)
        (token: EntitlementToken option)
        : Async<Result<EntitlementStatus, EntitlementRefusal>> =
        async {
            match token with
            | None ->
                match validation.Governance.Unprovisioned with
                | UnlockedWhenUnprovisioned -> return Result.Ok EntitlementStatus.unentitled
                | ReducedWhenUnprovisioned ->
                    // A declared reduction, not a refusal — and the floor
                    // still holds, so data stays readable and exportable.
                    return
                        Result.Ok {
                            EntitlementStatus.unentitled with
                                Phase = EntitlementPhase.Lapsed 0.0
                                ExpiresAt = DateTimeOffset.MinValue
                                GrantedCapabilities = EntitlementFloor.capabilities
                                Lifetime = TimeSpan.Zero
                        }
            | Some token ->
                if not (String.Equals(token.KeyId, validation.Pin.KeyId, StringComparison.Ordinal)) then
                    return Result.Error(EntitlementRefusal.KeyIdNotPinned(token.KeyId, validation.Pin.KeyId))
                elif not (String.Equals(token.Algorithm, validation.Pin.Algorithm, StringComparison.Ordinal)) then
                    return
                        Result.Error(EntitlementRefusal.AlgorithmNotPinned(token.Algorithm, validation.Pin.Algorithm))
                else
                    let bytes = Encoding.UTF8.GetBytes token.ClaimsJson

                    let! verified = async {
                        try
                            return! validation.Verify bytes token.DetachedJws
                        with ex ->
                            // A throwing verifier is a rejection, not a
                            // crash: an entitlement check that can take
                            // the process down is a lockout mechanism.
                            return Result.Error(sprintf "verifier raised %s: %s" (ex.GetType().Name) ex.Message)
                    }

                    match verified with
                    | Result.Error reason -> return Result.Error(EntitlementRefusal.SignatureRejected reason)
                    | Result.Ok() ->
                        match EntitlementClaims.parse token.ClaimsJson with
                        | Result.Error e -> return Result.Error e
                        | Result.Ok claims ->
                            let now = validation.Clock()

                            if now + validation.ClockSkewTolerance < claims.NotBefore then
                                return
                                    Result.Error(
                                        EntitlementRefusal.NotYetValid(
                                            claims.NotBefore,
                                            now,
                                            validation.ClockSkewTolerance
                                        )
                                    )
                            else
                                let phase = phaseAt now validation.ClockSkewTolerance claims

                                let granted =
                                    if EntitlementPhase.grantsGovernedCapabilities phase then
                                        Set.union claims.Capabilities EntitlementFloor.capabilities
                                    else
                                        EntitlementFloor.capabilities

                                return
                                    Result.Ok {
                                        Phase = phase
                                        HolderId = claims.HolderId
                                        TokenId = claims.TokenId
                                        ExpiresAt = claims.ExpiresAt
                                        GrantedCapabilities = granted
                                        Capacities =
                                            claims.Capacities |> List.map (fun g -> g.Dimension, g.Limit) |> Map.ofList
                                        Lifetime = EntitlementClaims.lifetime claims
                                    }
        }

    /// Resolve, folding a refusal into a REDUCED state rather than
    /// propagating it.
    ///
    /// This is the shape a composition root wants, and the reason it exists
    /// is 492.C: a token that cannot be established leaves the deployment
    /// knowing nothing about its entitlement, and the only fail-safe
    /// reading of "I know nothing" is the floor. Refusing to boot, or
    /// granting everything, are the two tempting alternatives and both are
    /// wrong — the first confiscates, the second makes the mechanism
    /// decorative. The refusal is returned alongside so the preflight can
    /// say exactly what happened.
    let resolveFailSafe
        (validation: EntitlementValidation)
        (token: EntitlementToken option)
        : Async<EntitlementStatus * EntitlementRefusal option> =
        async {
            match! resolve validation token with
            | Result.Ok status -> return status, None
            | Result.Error refusal ->
                return
                    {
                        EntitlementStatus.unentitled with
                            Phase = EntitlementPhase.Lapsed 0.0
                            ExpiresAt = DateTimeOffset.MinValue
                            GrantedCapabilities = EntitlementFloor.capabilities
                            Lifetime = TimeSpan.Zero
                    },
                    Some refusal
        }

// ─── 492.B — the typed capacity budget ────────────────────────────────

/// What a capacity check concluded.
///
/// `RequireQualifiedAccess` is mandatory — `WithinBudget` would otherwise
/// shadow `HostRenderBudgetResult.WithinBudget` in the shared namespace.
[<RequireQualifiedAccess>]
type CapacityDecision =
    /// The dimension carries no limit — an unentitled deployment, or a
    /// dimension the token never mentioned. Unbounded.
    | Unbudgeted
    /// Within the declared limit.
    | WithinBudget of limit: int64 * requested: int64
    /// Over it. Carries the existing `QuotaBreached` shape rather than a
    /// new one, so a capacity entitlement and the Phase 9 quota policy
    /// report a breach in one vocabulary instead of two.
    | BudgetExceeded of QuotaBreached

/// The read surface relevant seams consult for capacity entitlements.
///
/// A record of functions rather than an interface for the same three
/// reasons `FlagEvaluator` is: trivial to construct literally in a test,
/// idiomatic at this size, and a later move to an interface stays
/// non-breaking.
///
/// **Read-only by construction.** There is no `Consume` and no counter
/// here: measuring usage is `IUsageLog`'s job and enforcing it is
/// `ITeamQuotaPolicy`'s, and an entitlement that kept its own parallel
/// tally would be a second answer to "how many seats are in use" — which
/// is how the two drift and the customer gets billed against the wrong one.
/// This surface answers only "what is the ceiling".
type EntitlementBudget = {
    /// The declared limit for a dimension, or `None` when unbudgeted.
    TryLimit: string -> int64 option
    /// Check a requested amount against the dimension's limit.
    Check: string -> int64 -> CapacityDecision
}

[<RequireQualifiedAccess>]
module EntitlementBudget =

    /// The identity: every dimension unbudgeted. What an unentitled
    /// deployment reads.
    let unbounded: EntitlementBudget = {
        TryLimit = fun _ -> None
        Check = fun _ _ -> CapacityDecision.Unbudgeted
    }

    /// Project the budget from a resolved status.
    let ofStatus (status: EntitlementStatus) : EntitlementBudget =
        let tryLimit (dimension: string) = status.Capacities.TryFind dimension

        {
            TryLimit = tryLimit
            Check =
                fun dimension requested ->
                    match tryLimit dimension with
                    | None -> CapacityDecision.Unbudgeted
                    | Some limit when requested <= limit -> CapacityDecision.WithinBudget(limit, requested)
                    | Some limit ->
                        CapacityDecision.BudgetExceeded {
                            Kind = dimension
                            Limit = decimal limit
                            Requested = decimal requested
                            ScopeId = status.HolderId
                        }
        }