module ToolUp.Platform.Tests.InProcess.FederatedIdentitySanitisationTests

open System
open System.Collections.Concurrent
open System.Text
open Expecto
open ToolUp.InterPlatform
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 334 — federated-identity sanitisation PARITY ──────────────
//
// Phase 6l.H shipped `IdentitySanitiser` and applied it at three
// boundaries; Phase 137 taught `PeerBearerAuthMiddleware` to run
// `X-Peer-Name` through it before building `peers/{name}/bearer`. Two
// federated boundaries were still raw:
//
//   • the Entra decorator's `applyEntraMapping` OVERWROTE the inner OIDC
//     provider's already-sanitised `UserId` / `TenantId` with the raw
//     `oid` / `sub` / `tid` claims — the sanitised value was only the
//     fallback, so 6l.H's guard was undone one line after it ran;
//   • `JwtPeerAuthProvider` interpolated the token's own unverified
//     `iss` straight into `peers/{iss}/signing-key`, and
//     `BlobPeerRegistry` used `PeerId` as a blob name unchecked.
//
// The word this pack exists for is **parity**, not coverage. Each of the
// three boundaries is driven with the SAME table of hostile identifiers,
// and every verdict is asserted equal to the canonical
// `IdentitySanitiser` verdict — so a future divergence on ANY side
// (a boundary that grows its own dialect, or quietly drops the guard)
// fails here, rather than in whichever pack happens to cover that one
// path. A per-boundary assertion would have passed happily through the
// whole pre-334 state of the world, because each boundary was
// individually self-consistent; only comparing them catches it.
//
// **Every hostile row is paired with a benign one through the identical
// fixture**, so a boundary that has broken and started refusing
// everything fails too. The peer case goes further and registers a real,
// strong signing key AT the traversal key path: refusal there can only
// be the sanitiser, because the lookup it would otherwise perform
// succeeds — asserted directly in `negativeControlTests`.

// ─── The shared hostile / benign corpus ──────────────────────────────

/// Identifiers that must be refused at every federated-identity
/// boundary. One row per rejection *class* the sanitiser recognises —
/// the classes are what a re-implemented dialect drops silently.
let private hostileIds = [
    "../../etc", "parent-directory traversal"
    "..\\..\\secrets", "backslash traversal"
    "peers/other/signing-key", "embedded path separator"
    "peer\u0000null", "NUL byte"
    "peer\u001Fctl", "control character"
    "CON", "Windows reserved device name"
    ".hidden", "leading period (Unix dotfile)"
    "peer name", "embedded whitespace"
    String.replicate 300 "a", "over-length identifier"
    "", "empty identifier"
]

/// Identifiers a real deployment actually uses. These must survive every
/// boundary BYTE-FOR-BYTE (GP 11) — the guard is defence-in-depth, not a
/// new naming policy, and a peer or tenant that authenticated yesterday
/// must authenticate today.
let private benignIds = [
    "buyer", "plain lower-case id"
    "seller-eu-1", "hyphenated regional id"
    "peer_2", "underscored id"
    "tenant.eu.buyer", "dotted hierarchical id"
    "0123abcd4567", "opaque hex id"
    "9f1c2e7a-4b3d-4e5f-8a90-1122334455ab", "GUID-shaped Entra `oid`"
]

/// The canonical verdict every boundary is measured against.
let private canonicalAccepts (id: string) =
    IdentitySanitiser.sanitiseScopeId id |> Result.isOk

// ─── Boundary 1 — the Entra claim decorator ──────────────────────────

let private base64UrlRaw (bytes: byte[]) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

/// The inner OIDC provider's output: already sanitised by `6l.H`, and
/// the value the decorator must fall back to when a claim is refused.
let private innerUser: AuthenticatedUser = {
    UserId = "inner-sanitised-subject"
    DisplayName = "Inner Display Name"
    Email = Some "inner@example.com"
    TenantId = Some "inner-tenant"
    Roles = []
}

/// An Entra id token carrying the given claims, in the shape the
/// decorator re-reads AFTER the inner provider has verified signature,
/// issuer, audience and expiry. The signature segment is deliberately
/// junk: `applyValidatedClaims` performs no verification of its own
/// (that already happened), so signing it would test nothing and would
/// hide the fact that this is a post-validation re-read.
let private entraToken (claims: (string * string) list) =
    let escape (s: string) =
        s
        |> String.collect (fun ch ->
            match ch with
            | '"' -> "\\\""
            | '\\' -> "\\\\"
            | c when Char.IsControl c -> sprintf "\\u%04x" (int c)
            | c -> string c)

    let body =
        claims
        |> List.map (fun (k, v) -> sprintf "\"%s\":\"%s\"" k (escape v))
        |> String.concat ","

    let header = base64UrlRaw (Encoding.UTF8.GetBytes """{"alg":"RS256","typ":"JWT"}""")
    let payload = base64UrlRaw (Encoding.UTF8.GetBytes $"{{{body}}}")
    $"{header}.{payload}.not-a-signature"

/// Drive the real decorator mapping. `applyValidatedClaims` is exactly
/// what `wrapWithEntraMapping` calls on the validated user, exposed by
/// Phase 334 so this pack asserts against the shipped path rather than a
/// re-implementation of it.
let private entraMapped (claims: (string * string) list) =
    ToolUp.AuthProviders.EntraExternalIdAuthProvider.applyValidatedClaims (entraToken claims) innerUser

// ─── Boundary 2 — the peer `iss` claim ───────────────────────────────

type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private signingKey = "federated-identity-parity-shared-signing-key-0123456789"

let private receiverId: PeerIdentity = {
    PeerId = "receiver"
    DisplayName = "Receiving Deployment"
}

/// A secret store holding a strong signing key at the EXACT key path the
/// given peer id produces — including a traversal-shaped one. This is
/// what makes the refusal attributable: the lookup the provider would
/// otherwise perform resolves, so the only thing that can refuse the
/// token is the shape check on `iss`.
let private secretsFor (peerIds: string seq) =
    let store = InMemorySecretStore() :> ISecretStore

    for id in peerIds do
        store.SetSecret("_platform", $"peers/{id}/signing-key", signingKey)
        |> Async.RunSynchronously
        |> ignore

    store

/// Mint through the provider's own issue path (which Phase 334 leaves
/// unguarded by design — the caller id comes from local composition, not
/// the wire), then validate through the guarded one. A raw mint is
/// avoided deliberately: hand-rolling the HMAC here would let the mint
/// and validate sides disagree about the token shape and turn a genuine
/// acceptance into a spurious refusal.
let private validateTokenFrom (secrets: ISecretStore) (issuer: string) = async {
    let provider = JwtPeerAuthProvider(secrets, "") :> IPeerAuthProvider

    let caller: PeerIdentity = {
        PeerId = issuer
        DisplayName = "Calling Deployment"
    }

    match! provider.IssuePeerToken(caller, receiverId, Anonymous) with
    | Error e -> return failtestf "Expected the issue path to mint a token for '%s', got %A" issuer e
    | Ok token -> return! provider.ValidatePeerToken token
}

let private peerAccepts (issuer: string) =
    async {
        let! result = validateTokenFrom (secretsFor [ issuer ]) issuer

        return
            match result with
            | Ok _ -> true
            | Error _ -> false
    }
    |> Async.RunSynchronously

// ─── Boundary 3 — the blob-backed peer registry ──────────────────────

let private registryAccepts (peerId: string) =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let registry = BlobPeerRegistry(blobs) :> IPeerRegistry

    let target: TargetPeer = {
        Peer = {
            PeerId = peerId
            DisplayName = "Directory Entry"
        }
        BaseUrl = "https://peer.example.com/peer/v1"
    }

    match registry.Register target |> Async.RunSynchronously with
    | Error _ -> false
    | Ok() -> true

// ─── The parity assertion ────────────────────────────────────────────

let parityTests =
    testList "Phase 334 — federated-identity sanitisation parity" [

        testList "hostile identifiers are refused identically at every boundary" [
            for id, description in hostileIds do
                test $"{description} is refused by all three federated boundaries" {
                    Expect.isFalse
                        (canonicalAccepts id)
                        $"the corpus row '{description}' must be one IdentitySanitiser itself rejects — otherwise this whole row proves nothing"

                    Expect.notEqual
                        (entraMapped [ "oid", id ]).UserId
                        id
                        $"an Entra `oid` that is a {description} must never become the effective scope identity"

                    Expect.equal
                        (entraMapped [ "oid", id ]).UserId
                        innerUser.UserId
                        $"a refused `oid` ({description}) falls back to the inner provider's already-sanitised UserId"

                    Expect.equal
                        (entraMapped [ "sub", id ]).UserId
                        innerUser.UserId
                        $"a refused `sub` ({description}) falls back to the inner provider's already-sanitised UserId"

                    Expect.equal
                        (entraMapped [ "tid", id ]).TenantId
                        innerUser.TenantId
                        $"a refused `tid` ({description}) falls back to the inner provider's TenantId"

                    Expect.isFalse
                        (peerAccepts id)
                        $"a peer token whose `iss` is a {description} must be refused before the signing-key lookup"

                    Expect.isFalse
                        (registryAccepts id)
                        $"a peer id that is a {description} must not become a blob name in the peer directory"
                }
        ]

        testList "well-formed identifiers are unchanged at every boundary (GP 11)" [
            for id, description in benignIds do
                test $"{description} passes every federated boundary byte-for-byte" {
                    Expect.isTrue
                        (canonicalAccepts id)
                        $"the corpus row '{description}' must be one IdentitySanitiser accepts"

                    Expect.equal
                        (entraMapped [ "oid", id ]).UserId
                        id
                        $"a well-formed `oid` ({description}) still overrides the inner UserId, unchanged"

                    Expect.equal
                        (entraMapped [ "sub", id ]).UserId
                        id
                        $"a well-formed `sub` ({description}) is still applied, unchanged"

                    Expect.equal
                        (entraMapped [ "tid", id ]).TenantId
                        (Some id)
                        $"a well-formed `tid` ({description}) is still applied, unchanged"

                    Expect.isTrue
                        (peerAccepts id)
                        $"a peer token issued by a well-formed peer id ({description}) still validates"

                    Expect.isTrue
                        (registryAccepts id)
                        $"a well-formed peer id ({description}) still registers in the directory"
                }
        ]

        test "every boundary's verdict equals the canonical IdentitySanitiser verdict" {
            // The load-bearing assertion. Each boundary is compared to
            // the ONE sanitiser rather than to a locally-restated
            // expectation, so a boundary that grows its own rule set —
            // stricter or looser — fails here even if its own pack is
            // internally consistent.
            for id, description in hostileIds @ benignIds do
                let canonical = canonicalAccepts id

                Expect.equal
                    ((entraMapped [ "oid", id ]).UserId = id)
                    canonical
                    $"Entra `oid` boundary diverged from IdentitySanitiser on: {description}"

                Expect.equal
                    (peerAccepts id)
                    canonical
                    $"peer `iss` boundary diverged from IdentitySanitiser on: {description}"

                Expect.equal
                    (registryAccepts id)
                    canonical
                    $"BlobPeerRegistry boundary diverged from IdentitySanitiser on: {description}"
        }
    ]

// ─── Negative controls ───────────────────────────────────────────────
//
// Each asserts that the refusal above is caused by the sanitisation and
// nothing else — i.e. that removing the guard would make the case go
// green. Without these, "the hostile input was refused" is satisfied
// just as well by a provider that has broken and refuses everything, or
// by a key lookup that was going to miss anyway.

let negativeControlTests =
    testList "Phase 334 — negative controls (the refusal is the sanitiser)" [

        test "CONTROL — the traversal `iss` resolves a real, strong signing key" {
            // Pre-334, `peers/{iss}/signing-key` was built from the raw
            // claim, so this token validated. The key IS present at the
            // traversal path, and it clears the strength guard — so the
            // refusal in `parityTests` cannot be "no key registered" or
            // "weak key", the two other ways that path returns
            // PeerUnauthorized.
            let traversal = "../../etc"
            let secrets = secretsFor [ traversal ]

            let resolved =
                secrets.GetSecret("_platform", $"peers/{traversal}/signing-key")
                |> Async.RunSynchronously

            Expect.equal
                resolved
                (Some signingKey)
                "the unsanitised key path the pre-334 provider would have built resolves a key — so only the shape check can be refusing the token"

            Expect.isGreaterThanOrEqual
                (Encoding.UTF8.GetByteCount signingKey)
                32
                "and that key clears the minimum signing-key strength, so the strength guard is not what refuses it either"
        }

        test "CONTROL — the same fixture accepts a benign issuer" {
            // If the harness itself were broken, every case would refuse
            // and the hostile assertions would pass vacuously.
            Expect.isTrue
                (peerAccepts "receiver-adjacent-peer")
                "a well-formed issuer validates through the identical mint→validate fixture"
        }

        test "CONTROL — the Entra mapping is live, not inert" {
            // If `applyEntraMapping` had stopped applying claims
            // altogether, every hostile case would still return the
            // inner UserId and look like a pass.
            let mapped = entraMapped [ "oid", "entra-object-id"; "tid", "entra-tenant" ]

            Expect.equal mapped.UserId "entra-object-id" "a well-formed `oid` genuinely overrides the inner UserId"

            Expect.equal
                mapped.TenantId
                (Some "entra-tenant")
                "a well-formed `tid` genuinely overrides the inner TenantId"
        }

        test "CONTROL — a refused peer id leaves the directory untouched" {
            // `Register` returning Error is not the same claim as
            // "nothing was written". A traversal that wrote its document
            // and then reported failure would be the worst of both.
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let registry = BlobPeerRegistry(blobs) :> IPeerRegistry

            let hostile: TargetPeer = {
                Peer = {
                    PeerId = "../../_platform/secrets"
                    DisplayName = "Hostile"
                }
                BaseUrl = "https://evil.example.com"
            }

            let result = registry.Register hostile |> Async.RunSynchronously
            let listed = registry.List() |> Async.RunSynchronously

            Expect.isError result "a traversal peer id is refused registration"
            Expect.isEmpty listed "and no directory document was written anywhere"

            Expect.isNone
                (registry.Resolve hostile.Peer.PeerId |> Async.RunSynchronously)
                "nor can the same traversal id resolve one"
        }
    ]

// ─── Boundary-specific behaviour the parity table cannot express ─────

let boundaryDetailTests =
    testList "Phase 334 — boundary-specific behaviour" [

        test "a refused `oid` falls through to a well-formed `sub` before the inner UserId" {
            // Rejection is modelled as ABSENCE, so the decorator walks
            // the same oid → sub → inner candidate chain it always had
            // rather than short-circuiting to the inner value.
            let mapped = entraMapped [ "oid", "../../etc"; "sub", "well-formed-subject" ]

            Expect.equal mapped.UserId "well-formed-subject" "a refused `oid` is treated as absent, not as a hard stop"
        }

        test "display name and email are untouched by the sanitiser" {
            // They never become a scope or key-path segment, so
            // constraining them would reject legitimate human names for
            // no security gain.
            let mapped =
                entraMapped [
                    "oid", "user-1"
                    "name", "Ada Lovelace-King (Dr.)"
                    "email", "ada@example.co.uk"
                ]

            Expect.equal
                mapped.DisplayName
                "Ada Lovelace-King (Dr.)"
                "a human display name with spaces and punctuation survives"

            Expect.equal mapped.Email (Some "ada@example.co.uk") "an email address with an `@` survives"
        }

        test "a wire-supplied delegation chain hop is sanitised before its key lookup" {
            // The last hop of `DelegationChain` addresses a signing key
            // through the same interpolation as `iss`, and arrives just
            // as unverified.
            let secrets = secretsFor [ "../../etc" ]
            let provider = JwtPeerAuthProvider(secrets, "") :> IPeerAuthProvider

            let assertion: DelegatedAssertion = {
                Subject = "end-user-1"
                DelegationChain = [ "../../etc" ]
                Signature = "irrelevant-the-id-never-reaches-the-lookup"
            }

            match provider.VerifyDelegation assertion |> Async.RunSynchronously with
            | Error(PeerUnauthorized _) -> ()
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok() -> failtest "a traversal-shaped delegating peer must not verify"
        }

        test "a rejection reason never echoes the offending identifier" {
            // Reasons flow into logs and audit; an attacker-controlled
            // string does not belong there. Same posture as
            // `IdentitySanitiser`, which categorises rather than quotes.
            let traversal = "../../etc/passwd"
            let secrets = secretsFor [ traversal ]

            match validateTokenFrom secrets traversal |> Async.RunSynchronously with
            | Error(PeerUnauthorized reason) ->
                Expect.isFalse
                    (reason.Contains traversal)
                    "the refusal reason must not carry the rejected value into the log"
            | other -> failtestf "Expected PeerUnauthorized, got %A" other
        }
    ]