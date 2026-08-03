# Peer host wire hardening — request-body ceiling + poll response id

**Ships in:** ToolUp.InterPlatform (Phase 315).

Two wire-level fixes on the JSON-RPC peer host. **The first can refuse a call
that previously succeeded** and is called out under [Rollout order](#rollout-order);
the second is additive on the wire and invisible to every in-tree caller.

---

## 1. The contract route reads the request body under a ceiling

### What changes

`POST /peer/v1/{contractId}` read the inbound body with
`ctx.ReadBodyFromRequestAsync()` — unbounded. A validated-but-hostile peer, or
simply a buggy one, could hand the receiver an arbitrarily large payload and
have it materialised as a `string` before anything looked at it.

Auth-gating was already in place and is a real bound on *who* can do that. It is
no bound at all on *how much*: a federation trusts its peers to be authentic,
not to be well-behaved, and the memory cost lands before any contract logic runs.

The route now reads under `PeerWireLimits.MaxRequestBytes`, **8 MiB by
default**. An over-ceiling request answers **HTTP 413** with a structured
`PeerRequestTooLarge limitBytes` (JSON-RPC code `-32007`).

The ceiling is enforced in two places, because either alone is bypassable:

- a declared `Content-Length` over the limit is refused **without reading a
  byte**;
- the read itself **stops at the limit**. `Content-Length` is absent under
  chunked transfer-encoding, which the *caller* chooses, and is in any case a
  claim rather than a measurement — so a header check alone would be a
  suggestion.

**Where the check sits is deliberate.** It runs *after* the credential check and
the Phase 330 delegation verification, and *before* the read. Above auth it
would answer 413 to an unauthenticated caller, reopening precisely the
status-code oracle Phase 343 closed (and handing out the receiver's ceiling for
free); after the read it would be a measurement, not a limit. Phase 330's
ordering is untouched — the body is still not read until the delegation has been
verified, so an unauthenticated caller still causes no buffering at all.

The read remains a single forward-only pass with **no `EnableBuffering`**, and
nothing downstream re-reads the body. That matters: a stage that reads the
request body after an earlier stage consumed or disposed it throws once the
response has already started, where the framework's exception handler can no
longer run — which surfaces to the caller as a 502 for a call that actually
succeeded.

### What you must do

**Nothing**, unless this deployment exchanges peer payloads larger than 8 MiB.
A peer contract's arguments cross the wire as a JSON array; 8 MiB of argument
array is far past anything the substrate is shaped for, so no realistic existing
deployment meets the ceiling (GP 11).

If yours does — or if you want a tighter boundary than the default — set it at
compose time:

```fsharp
PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withLocalPeer myIdentity
|> PeerServerApp.withWireLimits (
    PeerWireLimits.defaults
    |> PeerWireLimits.withMaxRequestBytes (32L * 1024L * 1024L))
|> PeerServerApp.run
```

The limit is **per-receiver policy, not a wire-format term**: the two ends need
not agree on it, it is not part of the capability handshake, and a caller that
exceeds it learns the ceiling from the structured refusal.

Note that ASP.NET Core's own `MaxRequestBodySize` (Kestrel default 30 MB) still
applies underneath. Raising `MaxRequestBytes` above it does nothing until the
host limit is raised too.

### How a caller sees the refusal

| Caller | What it observes |
|---|---|
| On this SDK version or later | `PeerRequestTooLarge 8388608L` |
| On an earlier SDK | `PeerTransport "Peer request too large: the receiver accepts at most 8388608 bytes of request body"` — `HttpPeerClient` cannot deserialise the unknown DU case and falls back to the message, so the refusal is one case coarser but still structured and still legible |
| Non-F# / hand-rolled | JSON-RPC `code = -32007`, HTTP `413` |

One caveat worth stating plainly: when the receiver answers 413 without draining
a large in-flight body, the connection may be reset before the caller reads the
response. A caller can therefore see a transport error rather than the 413 for
very large payloads. That is ordinary HTTP behaviour for an early rejection, and
it is the trade the ceiling exists to make — the alternative is reading the body
in order to be polite about refusing it.

### Compatibility note

`PeerServerApp` gains a `WireLimits: PeerWireLimits` field. As with Phase 309's
`StrictAudienceBinding` and Phase 343's `LegacyProfileFallback`, this widens the
record's compiler-generated constructor — a **binary** break for a caller that
constructs `PeerServerApp` by full record literal rather than through
`PeerServerApp.create ()` + the `with*` pipeline (the documented and only
recommended shape, which stays source-compatible). The
`api-baselines/InterPlatform.approved.txt` diff reports it as one removed
constructor plus the widened replacement.

`PeerError` gains a `PeerRequestTooLarge` case. Adding a case to a public DU
makes any **exhaustive** `match` on `PeerError` in consumer code incomplete —
`FS0025`, which this repo escalates to an error and yours may too. Either add an
arm or a wildcard.

---

## 2. The job-poll response carries a correlation id

### What changes

`GET /peer/v1/{contractId}/jobs/{jobId}` hard-coded `Id = ""` on every response
it emitted. A dispatch response has always echoed `request.Id`, so a caller can
pair it with the call that produced it; a poll response paired with nothing.
That is a hole in the wire's own correlation contract, and a real problem for a
non-F# peer SDK pipelining polls over one connection.

Every answer on the poll route now carries the polled `jobId` as its JSON-RPC
`Id` — the terminal status, the `Pending` answer, and the refusals alike. The
poll is a `GET` and carries no JSON-RPC request envelope, so there is no request
id to echo; the `jobId` is the identifier both sides already agree on and the
one the caller addressed the request with, so echoing it discloses nothing.

Dispatch-response behaviour is unchanged.

### What you must do

**Nothing.** The in-tree `HttpPeerClient` reads `Result` / `Error` and ignores
`Id` on this leg, so no existing caller observes a behaviour change — it gains a
field it was not reading (GP 11).

A hand-rolled or non-F# client may now correlate poll responses by `Id`. Do not
assume the field was empty before and treat non-empty as a new response kind.

---

## Rollout order

Both peers upgrade independently; there is no coordinated cutover.

1. **Receivers first.** Both changes are receiver-side. Neither breaks a caller
   on an older SDK: item 1 refuses only over-ceiling payloads (and does so
   legibly on both old and new callers, per the table above), and item 2 adds a
   field older callers ignore.
2. **Before upgrading a receiver**, check whether any peer sends contract
   arguments above 8 MiB. If one does, compose `withWireLimits` with a suitable
   ceiling in the same deploy — that is the single ordering constraint in this
   phase.

There is no caller-side step.

## See also

- [Phase 309 — audience-binding enforcement](309-peer-audience-binding-enforcement.md)
- [Phase 330 — delegation verification](330-peer-delegation-verification.md)
- [Phase 343 — peer robustness roundup](343-peer-robustness-roundup.md)
