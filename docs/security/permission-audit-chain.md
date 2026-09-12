# Verifying the permission-audit chain

**Audience:** a third-party auditor, counterparty, or vendor-risk assessor who has
been given access to a ToolUp Platform deployment's audit trail and needs to
establish that the record of *who was granted access to what* has not been edited
since it was written.

This page is the procedure. It states what the chain proves, what it does not,
and how to check the claim yourself rather than take it on the deployment's word.

---

## 1. What is being claimed

Every `PermissionChanged` audit record written by a ToolUp deployment carries a
**hash-chain link**: the content hash of the record before it, plus its own
content hash taken over a canonical form that *includes* that predecessor. The
consequence is the whole point:

> Editing or deleting any permission record breaks the link at that record and at
> every record after it, and the break is **positioned** — the verifier names the
> index, not merely the fact.

Append-only storage makes tampering hard. It does not make tampering **evident**.
The chain does, and it does so without asking you to trust the storage layer, the
deployment's own tooling, or the operator.

### What it does not claim

Read these before relying on the chain; each is a real bound, not a caveat added
for form's sake.

| Not claimed | Why, and what to ask for instead |
|---|---|
| **Truncation from the END is detectable from the rows alone.** | Removing the most recent record leaves a shorter chain that is internally perfectly consistent. Detecting it requires a head recorded somewhere the operator does not control — a **signed head** over the chain length and the head hash. That is what the chained-ledger sink (§6) adds; the in-store chain alone cannot. |
| **The record's CONTENT is true.** | The chain proves the row has not changed since it was written. It says nothing about whether the row described reality at write time. |
| **Records that predate the chain are covered.** | A deployment upgraded from an SDK before this feature has an *unchained prefix*. The verifier counts those rows separately and never reports them as tampering — see §5. |
| **Timestamps are covered.** | The canonical form frames the scope, the event type and the payload's content fields. The audit envelope's `OccurredAt` is not inside the hash. |
| **A record was written PROMPTLY.** | Nothing here timestamps the chain against an external clock. |

---

## 2. The record shape

A permission audit record is a `PermissionChangedPayload`. Since SDK Phase 553 it
carries one additional, optional field:

| Field | Meaning |
|---|---|
| `UserId` | The actor who made the change. |
| `TeamId` | The team the change was made in. |
| `AffectedUserId` | The member whose permissions changed. Empty for a team-defaults or module-exposure change. |
| `ModuleName` | The module. Empty string denotes a team-defaults change. |
| `Permissions` | Comma-separated granted permissions, or empty for a revocation. |
| `Chain` | `Some { PrevHash; ContentHash }` for a chained record; **absent / `None`** for a record written before the chain existed. |

`PrevHash` and `ContentHash` are both **bare lowercase 64-character hex SHA-256**
— no `sha256:` prefix — matching the other digests in this substrate. The first
record in a chain names the **genesis predecessor**, 64 hex zeros:

```
0000000000000000000000000000000000000000000000000000000000000000
```

A record claiming the genesis value asserts it is *first*, which is what stops a
chain truncated from the **front** passing verification by simply starting later.

---

## 3. The canonical form and the hash

`ContentHash` is `SHA-256` over the UTF-8 bytes of a **canonical form**: a
length-prefixed framing of nine fields, in this exact order.

```
frame("toolup.permission-audit-chain.v1")   the framing version
frame(scopeId)                              the audit scope the record was written under
frame("PermissionChanged")                  the event type
frame(prevHash)                             the predecessor's ContentHash, or the genesis value
frame(payload.UserId)
frame(payload.TeamId)
frame(payload.AffectedUserId)
frame(payload.ModuleName)
frame(payload.Permissions)
```

where `frame(s)` appends `<length>:<s>` and `<length>` counts UTF-16 code units
(the measure `String.Length` reports).

Three properties of this framing matter to you as a verifier:

- **Length prefixes, not delimiters.** A delimiter can occur inside a field value
  and an escape scheme is one more thing to get wrong. With the length in front,
  no field value can be re-cut into a different field sequence — so two distinct
  records cannot canonicalise to the same text by smuggling a separator through a
  module id or a permission label.
- **`prevHash` is inside the framing.** This is the mechanism: a record's hash
  commits to the entire prefix of the chain, not merely to its own five content
  fields.
- **The framing version is inside the framing.** If the field set or the field
  order ever changes, the version changes with it, so a reordering cannot be
  passed off as the original.

The framing is small enough to reimplement in any language in a few lines, and
you are encouraged to — an independent implementation is worth more than the
deployment's own.

---

## 4. The procedure

### Step 1 — obtain the rows

Ask the deployment for every `PermissionChanged` audit record for the scope
(tenant / team) under review. The SDK's own read seam is
`IAuditLog.GetAuditTrail(scopeId, dateRange = None, eventType = Some
"PermissionChanged")`. Ask for the **whole** scope with no date filter: a
date-filtered slice cuts the chain and will report as a break, correctly, because
from the verifier's point of view it is one.

### Step 2 — verify

The verifier is **pure**: it recomputes every hash from the record content rather
than trusting the stored value, it takes the rows in **any order**, and it runs
offline. Against a live deployment:

```fsharp
let reportPermissionChain (auditLog: IAuditLog) (scopeId: string) = async {
    let! report = PermissionAuditChain.verifyScope auditLog scopeId

    match report with
    | PermissionAuditChain.ChainIntact summary ->
        printfn
            "intact — %d chained record(s), %d pre-chain record(s), head %A"
            summary.ChainedCount
            summary.UnchainedCount
            summary.Head
    | PermissionAuditChain.ChainBrokenAt break' ->
        printfn "BROKEN at position %d (%A): %s" break'.Position break'.Kind break'.Detail
}
```

Against rows you were handed — an export, a replica, a backup — call the pure
form directly and never touch the deployment at all:

```fsharp
let verifyExportedRows (scopeId: string) (rows: PermissionChangedPayload list) =
    PermissionAuditChain.verify scopeId rows
```

To recompute a single record's hash by hand, e.g. while cross-checking an
independent implementation of §3:

```fsharp
let recomputeOne (scopeId: string) (prevHash: string) (payload: PermissionChangedPayload) =
    PermissionAuditChain.contentHash scopeId prevHash payload
```

### Step 3 — read the verdict

`ChainIntact` carries three numbers, and **all three matter**:

- `ChainedCount` — records verified along the chain.
- `UnchainedCount` — records carrying no link. See §5.
- `Head` — the verified head hash. **Record this value and the date.** It is what
  makes a *later* audit able to detect truncation from the end (§1): a chain
  whose head has changed without growing has lost records.

---

## 5. Reading a break

| Verdict | What happened | What to ask |
|---|---|---|
| `TamperedRecord` at *k* | The record at index *k* was edited in place: its content no longer hashes to its stored `ContentHash`. | The clearest finding available. Ask for the change-management record for that row. |
| `OrphanedRecords` at *k* | The chain stops at *k* while chained records remain that no longer join to anything. This is what **deleting** a record in the middle leaves — and also what editing a record *and* restoring its stored hash leaves, because its successor's `PrevHash` then names a record that no longer exists. | The `Detail` names how many records were stranded. |
| `MissingGenesis` at 0 | Chained records exist but none claims the genesis predecessor: the **front** of the chain was removed. | A different act from removing the middle, and worth treating as such. |
| `ForkedChain` at *k* | Two records claim the same predecessor. Either two writers raced (see §7) or a record was replaced by a re-chained substitute without the original being removed. | The `Detail` names the competing records. |

Verification **stops at the first break**. Everything after a break is
unverifiable in principle, and a verifier that reported a hundred downstream
consequences of one edit would hide the edit.

### The unchained prefix is not a break

A non-zero `UnchainedCount` means records with no link. There are exactly two
causes, and they call for different questions:

1. **The deployment predates the chain.** Records written before the feature
   shipped have no link and never will. For an upgraded deployment this count is
   expected to be non-zero forever, and it should be **stable** — it can never
   grow from this cause.
2. **A write could not read the chain head.** If the deployment's audit failure
   policy is `LogAndContinue`, a record whose scope head was unreadable is written
   *unchained* rather than chained onto a guessed predecessor. That is deliberate:
   a guessed link would fork the stream silently, and a silent fork is the state
   the chain exists to make impossible.

Distinguishing the two: cause 2 increments the counter
`toolup.audit.chain_head_unreadable_total`, tagged `reason` = `read_failed`
(a store outage), `forked`, or `unanchored`. Ask the deployment for that counter's
history. A deployment configured `RefuseAction` cannot have cause 2 at all — there,
an unreadable head fails the administrative action rather than recording it
un-evidenced.

---

## 6. The two chains — which one you are looking at

A ToolUp deployment may have **two** independent tamper-evident chains, and they
answer different questions. Ask which you have been given.

| | **In-store permission chain** (this page) | **Chained audit ledger** ([`../migrations/chained-audit-ledger.md`](../migrations/chained-audit-ledger.md)) |
|---|---|---|
| Covers | `PermissionChanged` records in the deployment's own event store | every audit record replicated to the sink |
| Requires | nothing — always on | the deployment composes the `ToolUp.AuditSinks.ChainedLedger` companion |
| Signed head | no | yes, optionally — which is what makes end-truncation detectable |
| Scoped counterparty export | no | yes — per-party, with digest-plus-facet witnesses for the positions a party is not entitled to see, wrapped as a DSSE / in-toto statement stock tooling verifies |

They are deliberately independent rather than layered, because **a replicated
chain proves nothing about the store it was replicated from**. If you need a
signed, scoped, exportable artefact you can verify cold, that is the ledger sink
and you should ask whether the deployment composes it. If it does not, the
in-store chain is what you have: tamper-evidence over the deployment's own rows,
with no sink, no key material and no export format.

---

## 7. Concurrency, honestly

Two administrators changing permissions in the same scope at the same instant can
both read the same head and both chain onto it. That produces a **fork**, and the
verifier reports it as one — `ForkedChain` at the contested index.

This is stated rather than engineered away because the alternative would be
worse. Serialising every permission write behind a lock would make an
administrative surface depend on a distributed lock for correctness of an audit
property; picking a winner at verification time would mean the verifier absorbing
exactly the ambiguity it exists to surface. A fork is rare, visible, and
resolvable by inspection — the two competing records are both real writes and the
`Detail` names them.

The writer never *compounds* a fork: once a scope has two heads, the head is
**unreadable**, and the next permission write takes the deployment's audit failure
policy (refuse, or record unchained) rather than picking a side.

---

## See also

- [`PLATFORM-SECURITY-RULES.md`](PLATFORM-SECURITY-RULES.md) — the versioned statement of what the substrate enforces.
- [`../migrations/chained-audit-ledger.md`](../migrations/chained-audit-ledger.md) — the chained-ledger sink, its signed head, and the scoped counterparty export.
- [`../reference/audit-event-reference.md`](../reference/audit-event-reference.md) — every audit event the SDK emits.
