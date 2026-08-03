# Federation-seam wire specification

**Version 1 · Apache-2.0**

A federation is a set of independently-operated deployments that describe themselves to one
another, pin what they were told, and then call each other. This document specifies the wire
documents that make that possible, **language-neutrally**: an implementation in any language joins
a federation by *conforming* — emitting and consuming these documents byte-correctly — not by
adopting any particular stack.

Everything normative is in §1–§9. The conformance corpus in [`wire-fixtures/`](wire-fixtures/) is the
executable half: an implementation certifies by round-tripping, re-stamping, and refusing the
fixtures its profile requires. Where this prose and the corpus disagree, that is a defect in this
document, and the corpus is the tie-breaker — it is emitted from running code, and prose is not.

**Key words.** MUST, MUST NOT, SHOULD, MAY are used in the RFC 2119 sense.

---

## 1. Scope

Six shape families are specified.

| Family | Answers | Emitted by |
|---|---|---|
| **peer surface** | what does this deployment serve, consume, and stand behind? | every participant |
| **aggregate surface** | what does this *group* face the world as? | a gateway |
| **pinned exchange** | what did I record that counterparty claiming, and when? | every participant |
| **attestation** | what exact thing did these two parties agree, and who signed it? | every participant |
| **contract invocation** | how is a call made, answered, and failed? | every participant |
| **host envelope** | what can a module I run rely on here? | a module host |

**In scope:** the documents, their canonical encoding, their hashes and stamps, the error and
refusal classes, and the versioning discipline that keeps them compatible.

**Out of scope, deliberately:** transport security (a deployment's hosting concern — the seam
reports its stance, it does not enforce one), key custody and signature algorithms (an attestation
carries a signature; how it was produced is the signer's business), registry and discovery
(participants exchange identities and base URLs out of band), and **execution of an evaluation or
placement leg** — a participant that computes on another's behalf is certified by a separate
evaluator-seam corpus, not by this one. Being conformant here makes a participant *visible,
preflightable, attestable and callable*. It does not make it an evaluator.

---

## 2. Conformance profiles

Conformance stratifies. Requiring every family of every participant would silently re-impose one
composition model on a federation whose whole point is that it does not have one — the host
envelope in particular is only meaningful to an implementation that hosts third-party modules with
a comparable composition model. So an implementation certifies against a **named profile**, and a
claim of conformance MUST name it.

| Profile | Required families | What participation it grants |
|---|---|---|
| **participant** | peer surface, pinned exchange, attestation, contract invocation | The full bilateral relationship: publish a label, be pinned, sign and verify agreements, and **be called** over the data plane. |
| **gateway** | participant + aggregate surface | The above, plus fronting a group of deployments as one peer. |
| **module-host** | gateway + host envelope | The above, plus offering a surface a third party can author a module against. |

Profiles are cumulative and the corpus is partitioned accordingly — `manifest.json` maps each
profile to its required families, and every vector declares the lowest profile that must run it.

**The gateway shape is the intended cheap path.** A group does not have to re-platform to join a
federation. A thin conformant gateway in front of an existing estate — one that derives an
aggregate surface from whatever labels the group holds, and forwards contract calls to the member
that owns each contract — is a first-class participant, indistinguishable on the wire from a single
deployment. §5 is written so that implementing it is a small amount of pure code.

---

## 3. Canonical encoding

Every document in this specification is a **canonical JSON** document. Canonical means: given the
same values, every conformant emitter produces the same bytes, so a hash over those bytes is a
stable identity. This is the single choice that dominates third-party conformance cost, so the
rules are stated exhaustively rather than left to a library.

### 3.1 Rules

1. **No insignificant whitespace.** No spaces, no newlines, no indentation, anywhere.
2. **UTF-8**, no byte-order mark.
3. **Member order is the shape's declaration order**, as given by the field tables in §5. It is
   *not* lexicographic.
4. **Every declared member is present.** An absent optional value is encoded as `null`; it is never
   omitted.
5. **Strings** escape `"`, `\`, and characters below U+0020 (using `\u00XX` where no short escape
   exists). Characters at or above U+0020 — including all non-ASCII — are emitted literally.
6. **32-bit integers** are JSON numbers.
7. **64-bit integers are decimal strings with an explicit sign** (`"+8388608"`, `"-1"`). See §3.2.
8. **Booleans** are `true` / `false`.
9. **Instants** are strings: ISO-8601 with an explicit UTC offset, e.g.
   `"2026-07-16T09:30:00+00:00"`.
10. **Arrays** preserve the order the emitter produced; where a field's ordering is load-bearing,
    §5 states the sort key. **A sort is ordinal (code-unit) and MUST NOT be culture-sensitive** — a
    locale-dependent sort makes a document's hash depend on the machine that produced it.
11. **Tagged unions.** A case with no payload is the bare case-name string (`"Pending"`). A case
    with one payload is a single-member object keyed by the case name
    (`{"Completed":<payload>}`). A case with several payloads is a single-member object whose value
    is an array of them, in declaration order
    (`{"PeerVersionMismatch":[<requested>,[<supported>…]]}`).
12. **Embedded documents ride as strings.** Where §5 marks a field *embedded*, its value is a JSON
    **string** whose content is itself a canonical JSON document — not a nested object. The
    receiver decodes it as a second step. This is what lets a relay carry a payload it does not
    understand, and lets a result be forwarded without a re-encode that would change its bytes.

### 3.2 Divergences from RFC 8785 (JCS), field by field

This encoding is close to JCS but is **not** JCS. Every divergence is deliberate and enumerated
here; there are no others.

| # | JCS | Here | Why |
|---|---|---|---|
| 1 | Object members sorted lexicographically by UTF-16 code unit. | Members in the shape's **declaration order** (§5). | The shapes are versioned records with a published field order; that order is part of the contract, and re-sorting it would make the document's structure depend on the accident of its field names. An implementation that sorts lexicographically produces a **different, non-conformant document** — this is the divergence most likely to bite, so check it first. |
| 2 | Numbers serialised as ECMAScript doubles. | 32-bit integers as JSON numbers; **64-bit integers as sign-prefixed decimal strings**. | A 64-bit integer is not exactly representable as a double, and silently rounding a byte ceiling or a budget is worse than encoding it as text. The sign prefix is always present, including for zero (`"+0"`). No document in this specification carries a floating-point number. |
| 3 | Says nothing about absent members. | Optional members are always present, `null` when absent. | A reader that distinguishes "absent" from "null" cannot be written against a document where emitters disagree about which to produce. Fixing it to `null` removes the question. |
| 4 | Says nothing about instants. | ISO-8601 strings with an explicit offset. | An instant with no offset is ambiguous, and an epoch number would re-open divergence 2. |
| 5 | Says nothing about unions. | §3.1 rule 11. | JSON has no sum type; the encoding has to be specified or every implementation invents one. |
| 6 | Says nothing about nesting. | §3.1 rule 12 (embedded documents as strings). | See above. |

An implementation MAY use a JCS library for string escaping (rule 5 is JCS-compatible) but MUST NOT
delegate member ordering or number formatting to one.

---

## 4. Hashes, stamps and signing inputs

Three distinct digests appear in this specification, and conflating them is a common
implementation error.

**4.1 A stamp is a digest over a document's canonical bytes.** `SurfaceHash` and
`StampContentHash` are lowercase-hex SHA-256 over the UTF-8 canonical encoding of the document they
stamp. A stamp is never included in the bytes it covers: a stamped envelope carries the stamp
*beside* the stamped document, never inside it.

Recomputing a stamp is how a holder detects that a document changed. A stamp that does not match a
recomputation over the document's own content means the document is **corrupt or was edited after
stamping** — which is a different condition from stale, and MUST be treated differently (§6.3).

**4.2 A content address names an exact value.** A `sha256:{lowercase hex}` string identifies the
thing it addresses (e.g. an agreement's subject). The algorithm is named inside the value so that a
future digest change is a visible discontinuity rather than a silent one.

**4.3 A signing input is not JSON.** An attestation is signed over a **length-prefixed,
domain-separated UTF-8 encoding** of its fields, specified in §6.4 — not over its JSON. JSON is a
transport for the record; the signature binds the values. An implementation that signs the JSON
will interoperate with nobody, and the corpus's attestation vectors exist mainly to catch exactly
that.

---

## 5. Shape families

Field tables give each member in **declaration order** — which is also its encoding order (§3.1
rule 3). "Opt" marks a member encoded as `null` when absent.

### 5.1 Peer surface (profile: participant)

A deployment's cross-instance face: what it serves, what it consumes, and the posture a
counterparty may rely on without seeing inside.

**`ServedContract`**

| Member | Type | Notes |
|---|---|---|
| `ContractId` | string | The id the contract is addressed by. |
| `Versions` | `ContractVersion[]` | Sorted ascending by `(Major, Minor)`. |
| `Routines` | string[] | Handler names of long-running methods that are actually dispatchable here. Sorted ordinally. Empty when the deployment cannot dispatch them. |

**`ContractVersion`**: `Major` (int32), `Minor` (int32). Ordering compares `Major` first.

**`ConsumedContract`**

| Member | Type | Notes |
|---|---|---|
| `ContractId` | string | A contract this deployment calls on a counterpart. |
| `Versions` | `ContractVersion[]` | The caller's half of the highest-mutual handshake. |
| `CounterpartRole` | string | A label for tooling (`"hub"`, `"registry"`, …). Not a routing input. |

**`TrustPosture`**

| Member | Type | Notes |
|---|---|---|
| `AuthProfile` | string | How inbound callers are authenticated. |
| `AudienceBound` | bool | Whether an inbound credential's audience is bound to this deployment's own identity. |
| `DelegationVerification` | string | How a multi-hop delegated assertion is verified. |
| `ReplayStance` | string | The replay defence a counterparty may rely on. |
| `TransportSecurity` | string | The transport stance. |

**`BudgetShape`**: `CascadeGuard` (string), `LongRunningEnabled` (bool).

**`VocabularyPin`**: `PackId` (string), `Version` (`{Major, Minor}`), `Hash` (string). Sorted by
`(PackId, Major, Minor)`.

**`PeerSurface`**

| Member | Type | Notes |
|---|---|---|
| `Enabled` | bool | `false` for a deployment with no federation surface. |
| `LocalPeerId` | string, opt | The identity this deployment presents. |
| `Serves` | object | `Contracts` (`ServedContract[]`, sorted ordinally by `ContractId`) and `Endpoints` (string[], §7.1, in the order given there). |
| `Consumes` | `ConsumedContract[]` | Sorted ordinally by `ContractId`. |
| `TrustPosture` | object, opt | `null` exactly when `Enabled` is `false`. |
| `Budgets` | object, opt | `null` exactly when `Enabled` is `false`. |
| `PinnedVocabulary` | `VocabularyPin[]` | Data-vocabulary packs this deployment pins. |

**`PeerSurfaceExport`** — the publishable envelope.

| Member | Type | Notes |
|---|---|---|
| `FormatVersion` | int32 | This specification's version. Currently `1`. |
| `SurfaceHash` | string | Stamp over the canonical encoding of `Surface` (§4.1). |
| `Surface` | `PeerSurface` | |

A non-federating deployment publishes the **empty surface** — `Enabled: false`, everything else
empty or `null` — and that is a conformant document, not an error. Corpus:
`peer-surface/empty.json`.

### 5.2 Aggregate surface (profile: gateway)

A group faces the world as one peer. Its document **is** a `PeerSurface` (§5.1) and is exported
through the identical path, so a counterparty cannot — and need not — distinguish a gateway-fronted
group from a single deployment.

A gateway derives its surface from (a) the member surfaces it holds and (b) an **exposure
selection**: an allow-list of `(ContractId, Owner?)` pairs. The rules are:

1. **Default-deny.** A contract not named in the exposure stays internal. A group never leaks a
   member's contract by omission.
2. **Resolution.** Each exposed contract MUST resolve to exactly one owning member: the sole
   member serving it, or the named `Owner`. An exposure that names a contract nobody serves, that
   is ambiguous, or whose owner is unknown or does not serve it, MUST fail derivation — a gateway
   that cannot say what it fronts MUST NOT compose. Every unresolved entry SHOULD be reported, not
   just the first.
3. **Exposing members** are the distinct owners of resolved routes. A member serving only unlisted
   contracts contributes **nothing** — no posture, no pin, no consumption.
4. **Posture floors.** The collective posture is never stronger than the weakest exposing member's,
   because a call routed through the group lands on that member. The **gateway edge itself
   participates in the floor**, with the posture it would publish alone.
   - **Boolean facets conjoin.** A group binds audiences only if every participant does.
   - **String facets take the unanimous value**, or — on any divergence — the marker
     `mixed:` followed by the ordinally-sorted distinct values joined with `|`
     (e.g. `mixed:deployment-managed|tls-required`). The sort makes the marker independent of
     member order. A `mixed:` value is **strictly weaker than any single member's claim** and
     satisfies no requirement; a consumer MUST NOT read it as satisfying either constituent.
5. **Vocabulary pins carry only on exact unanimity.** A pack surfaces on the aggregate only where
   every exposing member pins the identical `(Version, Hash)`. Any divergence — a different
   version, the same version at a different hash, or one member not pinning it at all — omits the
   pack entirely. Never averaged, never majority-voted: a group cannot honestly assert a shared
   meaning its members do not share.
6. **Consumption is external-only.** The group's `Consumes` is the exposing members' consumed
   contracts minus every contract any member of the group serves. Traffic satisfied inside the
   group is no part of the group's external face.
7. **Routines and long-running dispatch.** A gateway that forwards only the invoke leg advertises
   `Routines: []` and `LongRunningEnabled: false`. That is the honest report of what the group
   itself dispatches: a member's long-running result is parked in the member's own store, which the
   group's poll route cannot read.

Corpus: `aggregate-surface/group.json` (divergent posture, unanimous and non-unanimous pins, an
unexposed member) and `aggregate-surface/solo.json` (the unanimous control).

### 5.3 Pinned exchange (profile: participant)

What a consumer records of a counterparty's published surface. **Nothing here opens a socket**: a
pin is taken from a document the operator already holds — a file, a registry entry, an out-of-band
exchange. A label fetched at boot is a label that changed after it was read, which is exactly the
property pinning exists to remove.

**`PinnedTrustFacet`**: `Facet` (string — a `TrustPosture` member name), `Value` (string).
Booleans render lowercase (`"true"` / `"false"`), so a requirement reads the way an operator types
it.

**`PinnedPeerSurface`**

| Member | Type | Notes |
|---|---|---|
| `CounterpartyId` | string | Who this pin describes. |
| `Source` | string | Where the snapshot came from. Purely descriptive — nothing dereferences it. |
| `FormatVersion` | int32 | The version the label was published under. |
| `SurfaceHash` | string | The label's own stamp, verified at pinning time. |
| `PinnedAt` | instant | When the snapshot was taken — the age a staleness rule measures. |
| `Serves` | array | `{ContractId, Versions}` per contract, sorted ordinally by `ContractId`. |
| `TrustFacets` | `PinnedTrustFacet[]` | Sorted ordinally by `Facet`. Empty when the label declares no posture. |

**Verification happens once, at pinning — not at every use.** A reader taking a pin from a published
export MUST, in this order:

1. Parse the document. A document that is not a well-formed export is refused
   (`pin-unparseable`).
2. Refuse a `FormatVersion` greater than the reader supports (`pin-format-version-unreadable`). A
   half-read label is worse than none: a facet the reader has no field for would satisfy a trust
   requirement by omission.
3. Recompute the stamp over `Surface` and require it to equal the document's own `SurfaceHash`
   (`pin-stamp-mismatch`).
4. Require the same recomputed value to equal a hash **agreed out of band**
   (`pin-hash-not-agreed`). The document's self-stamp only proves internal consistency, which a
   substituted document also has. Where the exchange channel itself was the trusted one, the
   agreed hash is the document's own stamp and this check is trivially satisfied — but it is not
   optional to *implement*.

Only then is the document projected into a `PinnedPeerSurface`. Corpus: `pinned-exchange/pin.json`
plus one reject vector per refusal class.

### 5.4 Attestation (profile: participant)

A signed record of one step of a bilateral agreement over one exact, content-addressed subject.

**`ApprovalRecord`**

| Member | Type | Notes |
|---|---|---|
| `TemplateId` | string | What the agreement is about. |
| `TemplateVersion` | string | `sha256:{hex}` content address of the exact subject agreed (§4.2). An edit yields a different value, which is what makes a stale agreement unusable rather than merely discouraged. |
| `ActingPeerId` | string | Who took the action and signed. |
| `CounterpartyPeerId` | string | The other party. Both halves of a live agreement name the same pair in opposite roles. |
| `Action` | union (no payload) | `TemplateProposed` \| `TemplateReviewed` \| `TemplateApproved` \| `TemplateRevoked`. |
| `IssuedAt` | instant | When the record was produced. |
| `NotBefore` | instant | When it takes effect. |
| `ExpiresAt` | instant, opt | When it stops taking effect. `null` = no end date. |
| `Signature` | string | Base64url signature by `ActingPeerId` over the signing input below. |

**Signing input (§4.3).** Let `field(v)` be `{utf8ByteLength(v)}:{v}\n`. The signed bytes are the
UTF-8 concatenation, in this order:

```
field("toolup.cleanroom.approval/1")   ← domain separator
field(TemplateId)
field(TemplateVersion)
field(ActingPeerId)
field(CounterpartyPeerId)
field(actionName)                      ← "Proposed" | "Reviewed" | "Approved" | "Revoked"
field(unixSeconds(IssuedAt))
field(unixSeconds(NotBefore))
field(ExpiresAt is null ? "none" : unixSeconds(ExpiresAt))
```

Four details, each of which an independent implementation gets wrong at least once:

- **The length prefix is what makes the encoding injection-proof.** A value containing a newline or
  a colon cannot shift a field boundary, because the reader is told how many bytes it occupies
  before it starts.
- **`actionName` is not the union's wire case name.** The JSON carries `"TemplateApproved"`; the
  signing input carries `"Approved"`. The short names are fixed by this specification and MUST NOT
  be derived from the case names, so that renaming a case in any implementation cannot silently
  invalidate signatures already made across a federation.
- **Instants are truncated to whole Unix seconds** in the signing input, and an emitter MUST
  truncate the record's own `IssuedAt` / `NotBefore` / `ExpiresAt` to the same precision — otherwise
  a record survives a JSON round trip and no longer re-canonicalises to the bytes it was signed
  over.
- **`ExpiresAt = null` encodes as the literal `"none"`**, which no seconds rendering can collide
  with.

**Record identity.** A record's content address is SHA-256 over the signing input **followed by the
UTF-8 signature bytes**, so re-persisting an identical record is idempotent while two records
differing only in signature stay distinct. The corpus records this digest per attestation vector.

A **live** agreement requires both parties to hold a current, unexpired, unrevoked approval of the
identical `TemplateVersion`. Evaluation is fail-closed: a revocation from either party, or an
expiry on either side, ends it. A clock-skew tolerance MAY be applied to both ends of a validity
window; implementations SHOULD use the same tolerance they apply to credentials.

### 5.5 Contract invocation (profile: participant)

The data plane. A participant that can be described and pinned but not called is not a participant.

#### 5.5.1 Endpoints

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/peer/v1/{contractId}` | Invoke a method of a contract. |
| `GET` | `/peer/v1/capabilities` | The contracts and versions this deployment serves. |
| `GET` | `/peer/v1/capabilities/profile` | Per-version, per-method lifecycle detail. |
| `GET` | `/peer/v1/{contractId}/jobs/{jobId}` | Poll a long-running call. |

The contract id is carried in the **path**, not in the method string. Every endpoint is
authenticated: capability discovery is not anonymous, and an unauthenticated caller MUST receive
the same shape of refusal from all of them (§5.5.5).

#### 5.5.2 Request

**`PeerCallContext`**

| Member | Type | Notes |
|---|---|---|
| `Peer` | `{PeerId, DisplayName}` | The calling peer as asserted. |
| `User` | union | `"Anonymous"` \| `{"Direct":{Subject, Issuer, DisplayName?}}` \| `{"Delegated":{Subject, DelegationChain[], Signature}}`. |
| `ContractVersion` | `ContractVersion` | The version this call is made under. |
| `Route` | string[] | Peer ids traversed, originator first. A repeat entry is a loop. |
| `RootRequestId` | string | Stable across the whole cascade. |
| `ParentRequestId` | string, opt | `null` at the originating hop. |
| `HopsRemaining` | int32 | Decremented per hop. |

**`WirePayload`**: `Context` (`PeerCallContext`), `Arguments` (**embedded** — a JSON array of the
method's positional arguments).

**`Request`**: `JsonRpc` (always `"2.0"`), `Method` (the contract method name), `Params`
(**embedded** `WirePayload`), `Id` (correlation id; derived from `RootRequestId` so the wire id and
any audit id line up).

**The receiver MUST NOT trust the request body for anything security-relevant.** `Peer` and `User`
are re-derived from the *validated* credential; a `Delegated` originator's `Signature` is verified
against the delegating peer's trust anchor before anything acts on it; and `Route`,
`RootRequestId`, `ParentRequestId` and `HopsRemaining` are **derived by the receiver**, not copied
from the wire — otherwise the receiver's own loop and hop-limit guards bind on values the caller
chose. The body is the caller's proposal; the context the handler sees is the receiver's
conclusion.

#### 5.5.3 Response

**`ErrorBody`**: `Code` (int32, §5.5.4), `Message` (string, one line, human-readable), `Data`
(**embedded** structured error, opt).

**`Response`**: `JsonRpc` (`"2.0"`), `Result` (**embedded** method result, opt), `Error`
(`ErrorBody`, opt), `Id`. Exactly one of `Result` / `Error` is non-`null`.

`Result` carries the method's **already-encoded** result. An emitter MUST NOT encode it a second
time.

#### 5.5.4 Errors

| Code | Class | Meaning |
|---|---|---|
| `-32700` | `PeerDeserialization` | A body could not be decoded. |
| `-32601` | `PeerMethodNotFound` | The contract exposes no method of that name. |
| `-32000` | `PeerUnauthorized` | Credential rejected, or the identity is not authorised here. |
| `-32001` | `PeerContractNotFound` | No contract is hosted under that id. |
| `-32002` | `PeerVersionMismatch` | Payload `[requested, [supported…]]`. |
| `-32003` | `PeerLoopDetected` | Payload: the route that repeats. |
| `-32004` | `PeerHopLimitExceeded` | No payload. |
| `-32005` | `PeerHandler` | The handler failed. |
| `-32006` | `PeerTransport` | Connection, timeout, non-JSON body. |
| `-32007` | `PeerRequestTooLarge` | Payload: the receiver's ceiling in bytes (64-bit, §3.1 rule 7). Carries the **limit**, not the observed size — the receiver stops reading at the ceiling, so it does not know the observed size, and the ceiling is the only number a caller can act on. |
| `-32008` | `PeerCleanRoomWithheld` | Payload: the gate id, and **nothing else** — see below. |

`-32700`, `-32601` and the reserved JSON-RPC range are used per JSON-RPC 2.0; `-32000`…`-32008` are
implementation-defined server codes fixed by this specification.

**A withheld answer says only that it was withheld.** A privacy gate's own reasons are
quantitative ("the released cohort is below the floor"), and returning them hands a caller a
counting oracle over exactly the data the floor protects, one refusal at a time. An implementation
MUST NOT include the reason in the wire error. Recording it locally, and disclosing it to the
calling party through a scoped, deliberate audit channel, is the sanctioned route.

**Forward compatibility.** A reader that encounters an error class it does not know MUST fall back
to the numeric `Code` and the human-readable `Message`, and MUST NOT fail the call for that reason
alone. This is why the code is normative and the class name is a convenience.

#### 5.5.5 HTTP status mapping

| Condition | Status |
|---|---|
| Missing or rejected credential; unverifiable delegation; polling a job owned by another peer | `401` |
| Request body over the receiver's ceiling | `413` |
| Request envelope unparseable | `400` |
| **Any structured dispatch outcome, success or failure** | `200` |

A refusal decided *before* dispatch carries `Id: ""` when the id has not been read yet. This is the
one place an empty `Id` is conformant.

Two rules that look like details and are not:

- **The size ceiling is enforced after authentication and before the body is read.** Above
  authentication, it answers `413` to an unauthenticated caller, which is a status-code oracle;
  after the read, it is a measurement rather than a limit. A conformant receiver MUST refuse a
  declared length over the ceiling without reading, and MUST stop reading a chunked body the moment
  the ceiling is passed.
- **Every credential defect answers `401` with the same shape.** A status an unauthenticated caller
  can flip at will distinguishes "malformed encoding" from "wrong key" before any credential has
  been accepted.

#### 5.5.6 Long-running calls

A long-running method returns a job id rather than a result; the caller polls
`GET /peer/v1/{contractId}/jobs/{jobId}`. The poll response is an ordinary `Response` whose
`Result` carries an embedded **`JobStatus`**:

`"Pending"` | `{"Completed": <embedded result>}` | `{"Failed": <structured error>}`

Rules:

- **The poll response echoes the polled `jobId` as its `Id`**, on every path including refusals. A
  `GET` carries no request envelope, so the job id is the only identifier both sides already agree
  on — and a response that correlates to nothing breaks pipelining for any client that reuses a
  connection. Echoing it on refusals discloses nothing: it is the value the caller put in the URL.
- **Possession of a job id is not authorisation.** A parked result belongs to the peer that
  scheduled it; a different validated peer polling it MUST be refused `401` with no result body.
- **An absent record reports `Pending` to every validated caller** — deliberately the same answer
  for "not finished" and "never existed", so an unknown job id discloses nothing.

### 5.6 Host envelope (profile: module-host)

What a host offers a module it will run — the type of the module-shaped hole. Only the module-host
profile requires it, because it is only meaningful where third-party modules are composed into a
host with a comparable composition model. A participant is under no obligation to have one.

| Member | Type | Notes |
|---|---|---|
| `EnvelopeSchemaVersion` | int32 | The envelope shape's own version, so a consumer can reject a snapshot it cannot read. |
| `EnvelopePlatform` | `{Package, Version, Assembly}` | The host build the envelope was derived under. An unresolvable value reports `"unknown"` rather than failing. |
| `EnvelopeCapabilities` | array | `{LayerKind, LayerCount, LayerIds[]}` per composed kind. A kind with nothing composed still appears with count `0` — the honest answer to "does this host offer any of these?". |
| `EnvelopeSlots` | array | `{OfferSlot, OfferInterface, OfferCardinality, OfferState, OfferImpls[]}`. `OfferCardinality` is `SingleImpl` \| `MultiImpl`; `OfferState` is `FilledSlot` \| `OpenSlot`. **An open slot is the load-bearing half**: it is precisely what a module may NOT rely on. |
| `EnvelopeModules` | array | Each composed module's surface: what it provides, what it needs, what is honestly not enumerable (`Opaque`), which registration fields the descriptor classifies (`Coverage`), and any it does not (`Unclassified`) or no longer finds (`Stale`). |
| `EnvelopeKnobs` | array | `{KnobName, KnobAdmissible[], KnobResolved}` — the values a knob *could* take and the value it *did* take here. |
| `EnvelopeRoutes` | array | `{RouteKey, RouteOwner, RouteAdmits, RouteExact}` — the prefix space a new module must not collide with. |

**`EnvelopeStamp`** is a **sidecar**, not a member: `StampSchemaVersion`, `StampPlatformVersion`,
`StampContentHash`. It is a separate document because the hash is taken *over* the envelope's
canonical bytes and cannot live inside what it hashes. A consumer pins the stamp beside a generated
module and re-checks it later to learn whether the host moved underneath it.

---

## 6. Labels are assertions

Everything a participant publishes about itself — its served contracts, its trust posture, its
budget shape, its vocabulary pins — is an **assertion**, not a proof. This is a normative statement
about how the protocol is to be read, and it has three consequences.

1. **Derivation is an implementation virtue the wire cannot see.** An implementation that derives
   its surface from its live composition is less likely to publish something untrue, and that is a
   good reason to build one that way. It is not something a counterparty can verify, so the
   protocol does not pretend to require it.
2. **A hand-authored surface is fully conformant.** A surface written by hand in front of a service
   that shares no architecture with any of this is a first-class participant, provided the document
   is well-formed, correctly stamped, and honest. This is not a loophole; it is the intended
   adapter path (§2).
3. **Preflight semantics are unchanged by that honesty.** A consumer checks a counterparty's *label*
   against what it needs, and nothing else. It never asks a counterparty to prove a posture,
   because a posture claim is exactly what a label **is**. What makes the check meaningful is not
   proof but **commitment**: the label is stamped, pinned, and quotable, so a party that publishes
   one thing and does another has left a signed record of the discrepancy.

The security boundary therefore sits at the credential and the signature, not at the label. A label
tells you what to expect and gives you something to hold someone to; it is not an entitlement.

---

## 7. Constants

### 7.1 Endpoint templates

Published verbatim in a peer surface's `Serves.Endpoints`, in this order:

```
POST /peer/v1/{contractId}
GET /peer/v1/capabilities
GET /peer/v1/capabilities/profile
GET /peer/v1/{contractId}/jobs/{jobId}
```

### 7.2 Refusal classes

Stable identifiers for the documents an implementation MUST refuse, used by the corpus's `reject`
vectors. The *class* is normative; the wording of a refusal is not, and an implementation in
another language will and should word it differently.

| Class | Family | Condition |
|---|---|---|
| `pin-unparseable` | pinned exchange | Not a well-formed export document. |
| `pin-format-version-unreadable` | pinned exchange | `FormatVersion` beyond what the reader supports. |
| `pin-stamp-mismatch` | pinned exchange | The document's stamp does not match a recomputation over its own surface. |
| `pin-hash-not-agreed` | pinned exchange | Internally consistent, but not the document agreed out of band. |
| `invocation-unparseable` | contract invocation | Not a well-formed request envelope. Refused before dispatch as a decode failure (`-32700`, HTTP `400`) — never partially read. |

---

## 8. Versioning

**`FormatVersion` is this specification's version**, carried by every export. It is distinct from
any contract's own wire version (`ContractVersion`) and from a host envelope's
`EnvelopeSchemaVersion`; the three move independently and MUST NOT be conflated.

- **Additive changes** — a new optional member, a new union case, a new error code — do **not**
  bump `FormatVersion`. A reader MUST ignore members it does not recognise and MUST fall back to
  the numeric error code for a class it does not know (§5.5.4).
- **A change to any existing member's meaning, type, ordering, or to the canonical encoding
  itself** bumps `FormatVersion`. Every such change alters published stamps, and a stamp that
  changed for a reason the reader cannot see is indistinguishable from a corrupt document.
- **A reader MUST refuse a document whose `FormatVersion` exceeds what it supports** (§5.3), naming
  both versions so an operator upgrades rather than guesses. It MUST NOT read such a document
  partially.
- **A reader SHOULD accept a document at a lower `FormatVersion`** it still understands.

**Inclusion rule for the stamped surface.** Not everything a deployment configures belongs in its
published surface. A member is included **only if a counterparty could act differently on it** —
that is, only if it is part of what the deployment is asking to be relied upon. Local operational
policy that a counterparty can neither observe nor depend on (request ceilings, retry and cascade
tuning, private templates, transport timeouts, internal budgets) is deliberately excluded: including
it would churn every published stamp on an operational tweak and would invite counterparties to
build expectations on values the deployment never committed to. When in doubt, ask whether a change
to the member should invalidate every pin a counterparty holds. If not, it does not belong in the
stamped surface.

---

## 9. Certifying against the corpus

The corpus lives in [`wire-fixtures/`](wire-fixtures/). `manifest.json` is the **authoritative
enumeration** — the count of vectors, the profile partition, and the digest of every fixture.

Each vector declares a `kind`:

| Kind | What certifying means |
|---|---|
| `round-trip` | Decode the document into your shape and re-encode it. The bytes MUST be identical. |
| `hash` | Round-trip, **and** reproduce the document's stamp by recomputing it — from the document's own content for a surface, from the stamped document for a sidecar stamp, or from the signing input for an attestation (the manifest's `digest`). |
| `reject` | Feed the document to your reader. It MUST be refused, with the refusal class the manifest names (§7.2). |

To certify:

1. Choose a profile (§2) and take the families `manifest.json` lists for it.
2. Run every vector in those families. Certifying a subset is not certifying.
3. Report the profile you certified against. A conformance claim without a profile is unfalsifiable.

**Two things the corpus asks of your harness, not of your emitter.** A conformance suite is the
kind of code that passes by doing nothing, so: assert that the number of vectors you executed
equals the number the manifest enumerates, and prove at least once that a mutated document makes
your harness go **red**. A green run that exercised nothing looks exactly like a green run.

**Forward-coupling rule.** A change to any field, ordering, encoding or stamp specified here
updates **this document, the emitter, and the corpus in the same commit**. A corpus that lags its
emitter certifies nothing; a specification that lags either is worse than absent, because it is
believed.

**Two emitters.** [`wire-fixtures/emit.mjs`](wire-fixtures/emit.mjs) is a second, dependency-free
emitter written against this document alone, in a different language and runtime from the reference
implementation. It regenerates the round-trip and hash documents and compares them byte-for-byte.
Its purpose is triangulation: one emitter cannot distinguish the protocol from its own accidents,
because whatever it does becomes "the format" by default. **A divergence between the two emitters
is a defect in this specification by definition** — either a rule is missing here, or a rule stated
here is not the rule being followed.

---

## Appendix A — implementation checklist

Ordered by how often each one is the thing that is wrong.

- [ ] Object members in **declaration order**, not sorted (§3.2 divergence 1).
- [ ] No whitespace anywhere.
- [ ] Optional members present as `null`, never omitted.
- [ ] 64-bit integers as **sign-prefixed strings**.
- [ ] Embedded documents encoded as **strings**, and results not re-encoded.
- [ ] Union cases: bare string with no payload, single-member object with one, array with several.
- [ ] Every list sorted by the key §5 names, **ordinally**.
- [ ] Stamps computed over the canonical bytes of the stamped document, never including the stamp.
- [ ] Attestations signed over the **length-prefixed signing input**, not the JSON, with the short
      action names and whole-second instants.
- [ ] Pinning refuses in the order of §5.3, including the out-of-band hash check.
- [ ] `mixed:` facets sorted, and read as satisfying nothing.
- [ ] Cascade context derived by the receiver, never copied from the request body.
- [ ] Poll responses echo the job id; a job is polled only by the peer that scheduled it.
- [ ] Unknown members ignored; unknown error classes fall back to the numeric code.

---

## Appendix B — reference implementation (non-normative)

This repository ships the first conformant emitter of this specification, under
`src/InterPlatform/`. The corpus is emitted from those live emitters rather than hand-authored,
and `src/ToolUp.Platform.Tests/InProcess/FederationWireConformanceTests.fs` certifies them against
the committed corpus on every test run — including the forward-coupling check that fails when an
emitter shape changes without regenerating the corpus.

Regenerating the corpus:

```powershell
$env:TOOLUP_EMIT_WIRE_FIXTURES = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_EMIT_WIRE_FIXTURES = $null
```

Running the second emitter:

```powershell
node docs/interplatform/wire-fixtures/emit.mjs          # check
node docs/interplatform/wire-fixtures/emit.mjs --write  # rewrite
```

**Lifting this specification.** §1–§9 and Appendix A name no implementation, no product and no
language, and reference nothing outside this file and `wire-fixtures/`. The pair
`FEDERATION_WIRE.md` + `wire-fixtures/` is therefore self-contained and moves to a standalone
specification home unchanged; this appendix is the only part that would become a pointer back to a
certified emitter.
