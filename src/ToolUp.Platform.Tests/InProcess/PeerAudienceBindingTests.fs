module ToolUp.Platform.Tests.InProcess.PeerAudienceBindingTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 309 — audience binding for contract hosts ─────────────────
//
// Phase 130 shipped the `aud` check, but it only fires when the
// validator was handed a non-empty expected audience — and that value is
// `localIdentity.PeerId`, which is `""` whenever `withLocalPeer` was not
// composed. A **host-only** deployment is documented as the case that
// omits it, so the confused-deputy defence was disabled on precisely the
// contract-hosting deployments, with nothing said about it.
//
// This pack pins three things, in the order they matter:
//
//   1. **The exposure is real.** A wrong-audience token is refused by a
//      bound receiver and ACCEPTED by an unbound one. That second half
//      is the negative control: without it, "the mis-addressed token was
//      refused" would pass just as happily against a validator that had
//      broken and started refusing everything, proving nothing about
//      audience at all. It is also what makes the phase's premise
//      falsifiable rather than argued.
//   2. **The composition seam surfaces it** — advisory by default, and
//      every rejection case is paired with a control asserting the
//      identical composition is silent / starts cleanly when it should.
//   3. **Nothing else moved** (GP 11): the default is warn-not-fail, an
//      unrelated composition logs nothing, and the strip-imports path is
//      untouched.

// ─── Fixtures ────────────────────────────────────────────────────────

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

/// Captures `Warn` lines so the advisory can be asserted as an emitted
/// log line, not merely as a classification.
type private RecordingLogger() =
    let warnings = ResizeArray<string>()
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Add message
        member _.Error(_, _) = ()

/// A served contract, so a composition can genuinely *host* something.
/// NOT `private`: the host reflects via `FSharpType.IsRecord` without the
/// private-representation flag (see `PlatformPeerTests`).
type LedgerContract = { Balance: string -> Async<int> }

let private ledgerImpl: LedgerContract = {
    Balance = fun _ -> async { return 0 }
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
}

let private hostIdentity: PeerIdentity = {
    PeerId = "receiver-z"
    DisplayName = "Receiver Z"
}

/// The shape the phase is about: a deployment that exposes a contract and
/// never calls a peer, so `withLocalPeer` looks optional.
let private hostOnlyApp () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<LedgerContract> "example.ledger" [ v1 ] fusion ledgerImpl)

// ─── 1. The exposure the phase closes ────────────────────────────────
//
// Two peers, one issuer. A token peer `caller` minted for receiver Y is
// presented to receiver Z. `ValidatePeerToken` resolves the signing key
// from the token's own `iss`, so the signature verifies at Z regardless
// of who the token was addressed to; only the `aud` check can tell them
// apart.

let private callerId: PeerIdentity = {
    PeerId = "caller"
    DisplayName = "Calling Deployment"
}

let private otherReceiver: PeerIdentity = {
    PeerId = "receiver-y"
    DisplayName = "Receiver Y"
}

let private signingKey = "peer-audience-binding-shared-key-0123456789"

let private secretsWithKey () =
    let store = InMemorySecretStore() :> ISecretStore

    store.SetSecret("_platform", $"peers/{callerId.PeerId}/signing-key", signingKey)
    |> Async.RunSynchronously
    |> ignore

    store

/// Mint through the caller's own provider, addressed to `audience`.
let private mintFor (audience: PeerIdentity) = async {
    let issuer = JwtPeerAuthProvider(secretsWithKey ()) :> IPeerAuthProvider

    match! issuer.IssuePeerToken(callerId, audience, Anonymous) with
    | Ok token -> return token
    | Error e -> return failtestf "Expected a minted token, got %A" e
}

let bindingTests =
    testList "Phase 309 — audience binding at the receiver" [

        testCaseAsync "a bound receiver refuses a token minted for a different receiver"
        <| async {
            let! token = mintFor otherReceiver

            let receiverZ =
                JwtPeerAuthProvider(secretsWithKey (), hostIdentity.PeerId) :> IPeerAuthProvider

            match! receiverZ.ValidatePeerToken token with
            | Error(PeerUnauthorized message) ->
                Expect.stringContains
                    message
                    "audience"
                    "the refusal must name the audience, not some incidental defect — otherwise this case would pass on a token that failed for an unrelated reason"
            | Error e -> failtestf "Expected PeerUnauthorized, got %A" e
            | Ok p ->
                failtestf
                    "A token addressed to '%s' was admitted at '%s' as %s — this is the cross-receiver replay the 'aud' claim exists to stop"
                    otherReceiver.PeerId
                    hostIdentity.PeerId
                    p.Caller.PeerId
        }

        testCaseAsync "CONTROL — the same bound receiver accepts a correctly-addressed token"
        <| async {
            // Identical sequence, identical provider configuration, one
            // field different. Without this the case above would pass
            // against a validator that had broken and started refusing
            // every token.
            let! token = mintFor hostIdentity

            let receiverZ =
                JwtPeerAuthProvider(secretsWithKey (), hostIdentity.PeerId) :> IPeerAuthProvider

            match! receiverZ.ValidatePeerToken token with
            | Ok principal -> Expect.equal principal.Caller.PeerId callerId.PeerId "the validated caller is the issuer"
            | Error e -> failtestf "A correctly-addressed token must be accepted, got %A" e
        }

        testCaseAsync "NEGATIVE CONTROL — an unbound receiver admits the mis-addressed token"
        <| async {
            // The enforcement removed. This is the exposure Phase 309
            // surfaces, and it is why an empty expected audience is a
            // weaker posture rather than a neutral one: the ONLY thing
            // that refused the token above was the binding.
            let! token = mintFor otherReceiver

            let unbound = JwtPeerAuthProvider(secretsWithKey ()) :> IPeerAuthProvider

            match! unbound.ValidatePeerToken token with
            | Ok principal ->
                Expect.equal
                    principal.Caller.PeerId
                    callerId.PeerId
                    "a receiver with no LocalPeer accepts a token addressed to someone else — the check the composition advisory is about"
            | Error e ->
                failtestf
                    "Expected the unbound receiver to admit the mis-addressed token (that IS the gap), got %A — if this now refuses, the audience check has moved into the provider and the compose-time advisory needs rewriting"
                    e
        }
    ]

// ─── 2 & 3. The composition seam ─────────────────────────────────────

let postureTests =
    testList "Phase 309 — compose-time audience-binding posture" [

        test "a contract host without LocalPeer classifies as missing" {
            Expect.equal
                (PeerServerApp.auditAudienceBinding (hostOnlyApp ()))
                AudienceBindingMissing
                "contracts hosted, no local identity — the composition defect"
        }

        test "CONTROL — the same host WITH LocalPeer classifies as enforced" {
            let app = hostOnlyApp () |> PeerServerApp.withLocalPeer hostIdentity

            Expect.equal
                (PeerServerApp.auditAudienceBinding app)
                (AudienceBindingEnforced hostIdentity.PeerId)
                "composing withLocalPeer is the lever that activates the check"
        }

        test "audit transparency alone counts as hosting a contract" {
            // `withPeerAuditTransparency` exposes a real contract that
            // reads audit rows, so it carries the same exposure as an
            // author contract even with `Contracts` empty.
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withPeerAuditTransparency

            Expect.equal
                (PeerServerApp.auditAudienceBinding app)
                AudienceBindingMissing
                "the audit-transparency contract is exposed to peers like any other"
        }

        test "a blank LocalPeer id is treated as absent, not composed" {
            // `checkAudience` short-circuits on an empty expected
            // audience, so this composition binds nothing while looking
            // configured — the one shape a naive `IsSome` check misses.
            let app =
                hostOnlyApp ()
                |> PeerServerApp.withLocalPeer {
                    PeerId = "   "
                    DisplayName = "Blank"
                }

            Expect.equal
                (PeerServerApp.auditAudienceBinding app)
                AudienceBindingMissing
                "a whitespace peer id cannot bind an audience, so it must not read as bound"
        }

        test "a deployment hosting nothing is idle, not defective" {
            let app = PeerServerApp.create () |> PeerServerApp.withConfig enabledConfig

            Expect.equal
                (PeerServerApp.auditAudienceBinding app)
                AudienceBindingIdle
                "no contract is exposed for a mis-addressed token to be spent against"
        }

        test "the strip-imports path is off entirely" {
            // `PeerSubstrate = NoPeerSubstrate` validates no peer token
            // at all, so the question does not arise (GP 13).
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withContract (fun fusion ->
                    JsonRpcPeerHost.contract<LedgerContract> "example.ledger" [ v1 ] fusion ledgerImpl)

            Expect.equal
                (PeerServerApp.auditAudienceBinding app)
                AudienceBindingOff
                "a deployment with the peer substrate disabled has nothing to bind"
        }

        test "enforcement logs the advisory for a contract host without LocalPeer" {
            let logger = RecordingLogger()
            let app = hostOnlyApp () |> PeerServerApp.withLogger logger

            PeerServerApp.enforceAudienceBinding app

            Expect.equal logger.Warnings.Length 1 "exactly one advisory, on the startup path"

            Expect.stringContains
                logger.Warnings[0]
                "withLocalPeer"
                "the advisory must name the lever that fixes it, or it is just noise"
        }

        test "CONTROL — the same composition WITH LocalPeer logs nothing" {
            let logger = RecordingLogger()

            let app =
                hostOnlyApp ()
                |> PeerServerApp.withLocalPeer hostIdentity
                |> PeerServerApp.withLogger logger

            PeerServerApp.enforceAudienceBinding app

            Expect.isEmpty
                logger.Warnings
                "a correctly-composed federated deployment must stay quiet — an advisory that fires for everyone is ignored by everyone"
        }

        test "CONTROL — a non-federating composition logs nothing" {
            let logger = RecordingLogger()

            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withLogger logger

            PeerServerApp.enforceAudienceBinding app
            Expect.isEmpty logger.Warnings "nothing hosted, nothing to warn about"
        }

        test "strict mode refuses to start, naming the defect and the fix" {
            let app = hostOnlyApp () |> PeerServerApp.withStrictAudienceBinding

            Expect.throwsC (fun () -> PeerServerApp.enforceAudienceBinding app) (fun ex ->
                Expect.stringContains ex.Message "withLocalPeer" "the refusal names the lever that fixes it"

                Expect.stringContains
                    ex.Message
                    "Refusing to start"
                    "the refusal says plainly that this is a hard stop, not another advisory")
        }

        test "CONTROL — strict mode starts cleanly once LocalPeer is composed" {
            // The identical strict composition, one call added. Without
            // this pair, "strict threw" would pass against a strict mode
            // that refused every composition.
            let logger = RecordingLogger()

            let app =
                hostOnlyApp ()
                |> PeerServerApp.withLocalPeer hostIdentity
                |> PeerServerApp.withStrictAudienceBinding
                |> PeerServerApp.withLogger logger

            PeerServerApp.enforceAudienceBinding app
            Expect.isEmpty logger.Warnings "a bound receiver passes the strict gate silently"
        }

        test "CONTROL — the default is warn-not-fail (GP 11)" {
            // The same defective composition without the opt-in must not
            // throw: every existing host-only deployment keeps starting.
            let logger = RecordingLogger()
            let app = hostOnlyApp () |> PeerServerApp.withLogger logger

            PeerServerApp.enforceAudienceBinding app
            Expect.equal logger.Warnings.Length 1 "surfaced, but the deployment still starts"
        }

        test "strict mode is off on a freshly created composition" {
            Expect.isFalse
                (PeerServerApp.create ()).StrictAudienceBinding
                "opt-in — an upgrading deployment inherits the advisory, never the refusal"
        }
    ]