# Structurally-enforced clean-room gate — the substrate applies the privacy floor

**Ships in:** ToolUp.InterPlatform (Phase 311).

The clean-room privacy gate is now enforced by the peer substrate rather than by
handler discipline. **Two source-breaking changes** for consumers — a new
`PeerError` case and a widened `PeerServerApp` constructor — and **no runtime
behaviour change at all** unless you opt in with the new compose helper.

---

## What changes

Phase 18b shipped `ICleanRoomBroker` and registered it as a DI singleton.
Nothing in the dispatch path ever called it. `JsonRpcPeerHost.contractHandler`
authenticated, derived the call context, and dispatched — and whatever the
handler returned went out on the wire. The gate therefore held exactly as well
as each contract author's memory: a handler that forgot to call
`broker.Enforce` returned row-level data with no error, no warning and a
passing build. `PrivacyGate.isStricterOrEqual` had no call site at all, which
was the visible tell that a validation path had been designed and never wired.

That is the same shape Phase 330 found in `IPeerAuthProvider.VerifyDelegation`
— shipped, documented, and never invoked — and it is why this phase does not
add "remember to call the broker" to a checklist.

`PeerServerApp.withCleanRoomTemplate contractId template` now wraps the named
contract's `PeerContractRegistration.Dispatch`, which is the only route
`IPlatformPeer` has to the wire. Every method on that contract has its answer
gated. The handler is not consulted and has no way to opt out.

Three invariants are checked by the wrapper itself, independently of which
`ICleanRoomBroker` is resolved — the seam is substitutable (GP 1), the composed
floor is not:

| Invariant | Behaviour |
|---|---|
| **Surface** | A method outside `template.AllowedMethods` is refused *before the handler runs*. A broker cannot widen the query surface the composition declared. |
| **Checkability** | An answer that does not deserialise as a `CohortResult` is withheld, not passed through. A gated method that answers in some other shape has produced something the floor cannot be evaluated against — so returning rows does not bypass the gate, it fails it. |
| **Release post-condition** | A `Released` decision is re-checked with `PrivacyGate.isStricterOrEqual template.Floor (PrivacyGate.observed released)`: every released cell clears the suppression threshold, the released cohort clears k, and the shape is permitted. A broker that released below the floor is overridden and the override is audited. |

The broker's own `Enforce` runs between the first and the last, and is where
the suppression + gate-composition mechanism lives.

`PrivacyGate.observed` is new and is what gives `isStricterOrEqual` a
production call site: it reports the strictest gate a materialised result
demonstrably satisfies, so "does this release clear the floor?" is one gate
comparison rather than three hand-written checks that can drift from
`PrivacyGate`'s own definition of strictness.

### The withhold is deliberately terse on the wire

A withhold reaches the caller as **`PeerCleanRoomWithheld templateId`**
(JSON-RPC code `-32008`, `JsonRpc.cleanRoomWithheld`, HTTP `200` like every
other structured dispatch outcome). It carries the template id and nothing
else.

That is not an oversight. The broker's own reasons are quantitative — *"released
cohort 7 is below the k-anonymity floor 10"* — and a caller able to vary its
query and read the reason back has a counting oracle over exactly the sub-k
cohorts the floor exists to hide. Handing it the number would leak more than
the answer it was denied.

The full reason is recorded receiver-side as a **`PeerCleanRoomDecision`** audit
row (`TemplateId`, `Released`, `SuppressedCells` labels, `Reason`,
`CallerPeerId`, `RootRequestId`), reserved `SourceModule = "_platform.peer"`,
best-effort like every other peer audit path. The Phase 18a audit-transparency
contract remains the deliberate, caller-scoped route to exposing any of it to
the calling peer.

### An inert gate cannot ship

`withCleanRoomTemplate` naming a contract id this deployment does not host is a
composition defect, and `run` **refuses to start** on it. A privacy gate that
looks composed and never runs is the failure this phase exists to remove, so it
is not something to discover from a missing audit row six months later.
`PeerServerApp.auditCleanRoomTemplates` reports the same finding as data
(`string list`, empty when healthy) for a deployment's own preflight — the
posture `auditAudienceBinding` takes for Phase 309.

Unlike the Phase 309 audience gate there is no advisory mode. Every reachable
case is code written after this phase shipped, because the helper that creates
one did not exist before it.

## What you must do

**Nothing, to keep current behaviour.** A composition that never calls
`withCleanRoomTemplate` wraps nothing, probes nothing, and registers the same
`PeerContractRegistration` values it did before (GP 11 / GP 13). The raw
`ICleanRoomBroker` API is unchanged and still supported for bespoke callers —
notably any caller that has a *caller-requested* gate to compose, which the peer
wire format does not carry and the composed path therefore passes as `None`.

**Two source-breaking changes** need a compile fix if they touch you:

1. **`PeerError` gained a case.** An exhaustive `match` over `PeerError` in
   consumer code stops compiling until `PeerCleanRoomWithheld` is handled. A
   peer running a pre-311 SDK cannot deserialise the case and degrades to
   `PeerTransport` carrying the same message (the `Data` fallback in
   `parseResponse`), exactly as Phase 315's `PeerRequestTooLarge` does;
   language-neutral callers read the JSON-RPC `code`, which is stable
   regardless.
2. **`PeerServerApp` gained a field** (`CleanRoomTemplates`), widening its
   generated constructor. `PeerServerApp.create ()` and every `with*` helper are
   source-compatible; only code constructing the record literally is affected —
   the same shape Phases 309 / 315 / 331 / 343 each took.

**To adopt the gate**, add one line per clean-room contract:

```fsharp
let reachTemplate: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach"; "Histogram" ]
    Floor = {
        MinCohortSize = 50
        SuppressionThreshold = 50
        PermittedShapes = Set.ofList [ Count; Histogram ]
    }
}

PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withLocalPeer thisPeerId
|> PeerServerApp.withContract (fun fusion ->
    JsonRpcPeerHost.contract<IReachApi> "example.reach" [ v1 ] fusion reachImpl)
|> PeerServerApp.withCleanRoomTemplate "example.reach" reachTemplate
|> PeerServerApp.run
```

The gated methods must answer with a `CohortResult`. A method on a gated
contract that answers with anything else is withheld — which is the point, but
it means **adopting the gate on an existing contract is a review of that
contract's return types, not just a compose line**. Read the withhold rows in
`PeerCleanRoomDecision` after enabling it in a staging federation: a burst of
`"not a gate-checkable CohortResult"` reasons means a method needs its answer
reshaped or its id kept off the template's surface.

**Already calling `broker.Enforce` by hand?** Keep the call or drop it; either
works. The default broker is idempotent over an already-gated result — the
surviving cells are by construction at or above the threshold and the surviving
cohort at or above k, so a second `Enforce` releases it unchanged. Dropping the
hand-written call is the tidier end state, and the composed gate is now the
documented default.

## Rollout order

1. **Upgrade the receiver** and compose no template. Nothing changes; this is
   purely the compile-fix step for the two source-breaking changes above.
2. **Compose the template in staging.** Watch `PeerCleanRoomDecision` rows for
   withholds you did not expect — an off-surface method id, or a method whose
   return type is not a `CohortResult`.
3. **Callers need no change**, but a caller that was silently receiving
   under-floor answers will start seeing `PeerCleanRoomWithheld`. That is the
   phase working; it is also a conversation to have with the counterparty before
   they see it in production.

## Rollback

Remove the `withCleanRoomTemplate` line. The contract registers exactly as it
did before, ungated. There is no flag that keeps the template composed and the
gate off — a gate that can be composed and not run is the state this phase
exists to make unrepresentable.

## See also

- [`18a-cross-deployment-audit-transparency.md`](18a-cross-deployment-audit-transparency.md)
  — the caller-scoped route to receiver-side audit detail, and therefore the
  sanctioned way to tell a peer *why* something was withheld.
- [`330-peer-delegation-verification.md`](330-peer-delegation-verification.md)
  — the previous shipped-but-uncalled enforcement seam, and the reason this one
  is a wrapper rather than a documented call.
- [`331-receiver-side-cascade-budget-authority.md`](331-receiver-side-cascade-budget-authority.md)
  — the `RootRequestId` a `PeerCleanRoomDecision` row is filed under is the one
  the receiver derived, not the one the caller asserted.
