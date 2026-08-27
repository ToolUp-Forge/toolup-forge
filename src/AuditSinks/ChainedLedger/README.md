# Chained audit ledger

An `IAuditSink` companion that makes an application's audit trail **tamper-evident**. Every appended record carries the SHA-256 digest of its predecessor over a canonical serialisation; the chain head is signable through an injected signer; and a verification pass walks the stored chain and reports the first tampered, dropped, or reordered record **by position**.

No vendor SDK and no third-party crypto library — BCL SHA-256 and `System.Text.Json` only (GP 1). Storage goes through the abstract `IBlobStorage`, so the ledger lands wherever the deployment already keeps blobs.

## What problem this solves

The audit pipeline a composed application gets by default is append-reliable: it answers *what did the application record?* It does not answer *is this still what the application recorded?* Nothing chains, nothing is signable, and a record edited or deleted after the fact leaves no trace.

A digest chain turns each of those edits into an arithmetic contradiction that a verifier can find and position.

### What the chain proves, and what it does not

Stated plainly, because a reader who believes the chain alone is sufficient is worse off than one who knows its bound:

- **The chain alone** is tamper-**evident** against an editor who cannot rewrite the tail. Mutate one record and its own digest stops matching. Drop or reorder one and the link breaks at a nameable position.
- **The chain alone is not tamper-proof** against an attacker with write access to the entire ledger, who can recompute every record from the edit forward and produce a self-consistent chain.
- **Signing the head closes exactly that residual.** A head signature the attacker cannot forge pins the chain the ledger actually wrote — including its length, so a truncated ledger cannot be re-presented as a shorter valid one.

## How to enable

1. Add a `<ProjectReference>` to `ToolUp.AuditSinks.ChainedLedger.fsproj` from the consuming server project.

2. Construct the sink and register it. The unsigned form needs no key material at all:

   ```fsharp skip=fragment
   open ToolUp.Platform.AuditSinks.ChainedLedger

   let settings = {
       ChainedLedgerSettings.defaults with
           Container = "audit-ledger-prod"
           PathPrefix = Some "ledger"
   }

   let sink = create "ledger-prod" settings blobStorage
   ```

3. To sign the head, implement `ILedgerHeadSigner` against whatever signing substrate the deployment runs, and use `createSigned`:

   ```fsharp skip=fragment
   let sink = createSigned "ledger-prod" settings blobStorage signer
   ```

A deployment that never constructs the sink is byte-for-byte unchanged (GP 11 / GP 13).

## Verifying a ledger

```fsharp skip=fragment
match! verify settings blobStorage (Some verifier) with
| Ok(LedgerVerified(count, headDigest, signature)) -> // intact and trusted
| Ok(LedgerHeadUntrusted(count, headDigest, signature)) -> // records consistent, head not proven
| Ok(LedgerBroken breakage) -> // breakage.Position, breakage.Kind, breakage.Detail
| Error message -> // the ledger could not be read at all
```

`LedgerBreakKind` distinguishes the classes rather than collapsing them into one diagnostic string:

| Kind | Meaning |
|---|---|
| `TamperedRecord` | The record's stored digest disagrees with its recomputed digest — edited in place. |
| `DroppedRecord` | The sequence expected at this position is absent from the whole ledger — deleted. |
| `ReorderedRecord` | The expected sequence is present elsewhere in the ledger — records were permuted. |
| `BrokenLink` | Sequence and self-digest are internally consistent, but the record does not chain to its predecessor. |
| `TornTail` | The ledger ends in a record that could not be read — a crash part-way through an append. Detected, never absorbed. |

Verification stops at the **first** break. Everything after a break is unverifiable in principle, and reporting a hundred downstream consequences of one edit hides the edit.

`HeadSignatureStatus` keeps "I could not check" separate from "I checked and it was fine": a signed head with no verifier supplied reports `HeadSignatureUnverifiable`, and the overall result is `LedgerHeadUntrusted` — never a quiet pass. `LedgerVerification` has three cases rather than a boolean plus details, so no caller can read a success case while an unverified signature sits in a field beside it.

### Cold verification

`ILedgerHeadVerifier` needs only **public** key material. An auditor holding the ledger blobs and the public key can confirm the head with no access to the signing environment, in a process that has never run the sink.

## Determinism

The same records always produce the same chain, on any machine, in any process. Two mechanisms carry that:

- **Canonical JSON.** Object properties are sorted by ordinal name at every depth before hashing (array order is meaningful data and is preserved). A digest that depended on a serialiser's incidental property order would change when a library did.
- **Length-framed fields.** Each field is hashed as `<utf8-byte-length>:<bytes>` rather than joined with a delimiter, so no field value can be re-cut into a different field sequence by smuggling a separator through a scope id or a payload.

The audit event body is stored as an already-serialised canonical JSON *string* and the digest is taken over exactly those stored bytes. Verification is therefore a text-and-bytes operation that can never disagree with the writer about how a type serialises, and reading a ledger back never depends on a converter round-tripping a wide domain union faithfully.

Honest bound: canonicalisation normalises structure, not number spelling — `1.0` and `1` remain distinct. That is sufficient here because both sides of every comparison come from the same serialiser.

## Concurrency and failure honesty

**Append ordering.** A digest chain is inherently serial — record N+1's digest is a function of record N's. The sink serialises appends through a single in-process semaphore. Concurrent `Deliver` calls are linearised in acquisition order, and each batch is chained as one contiguous run, so a batch's records are never interleaved with another batch's. No ordering is promised *between* concurrent batches beyond "some serial order", because none can be — each record carries its own `OccurredAt`, which remains the temporal source of truth.

**Single-writer per ledger — detected, not prevented.** The semaphore is in-process. Two processes writing the same container and prefix would fork the chain. Every append re-reads the head pointer and refuses if it has moved beneath the writer, naming the conflict. That is detection, not exclusion: `IBlobStorage` has no compare-and-set, so a genuine race can still interleave two writes. Deployments needing multiple writers run one ledger per writer and verify each chain independently.

**Torn tail.** A crash part-way through an append leaves a segment whose final line does not parse. Verification reports `TornTail` at that position rather than skipping the line — skipping it would produce a chain with an invisible hole.

**Signing failures fail the delivery.** If a composed signer cannot sign, `Deliver` returns `Error` and the dispatcher retries the batch. Reporting success on a head that was not signed would make the signature meaningless precisely when it matters.

## Storage layout

```
{prefix}/records/{firstSequence:D20}-{contentDigest}.jsonl   one line per record
{prefix}/head.json                                            chain length, head digest, head signature
```

The zero-padded leading sequence makes lexical blob order equal chain order, so read-back needs a `List` and a sort rather than an index.

The content digest is what makes the sink **batch-idempotent**, as `IAuditSink` requires. The dispatcher re-delivers a batch after a transient failure, and the case that matters is a segment that landed before the head-pointer write failed. The retry recomputes the same chain — same head, same envelopes, same deterministic serialisation — so it produces the same bytes, the same digest, and the same blob name, and overwrites rather than appending beside. A randomly-named segment would leave two blobs claiming the same sequence range, and the duplicate would surface later as a spurious chain break.

Retention and write-once immutability are configured **at the destination** — an object-storage retention policy, a filesystem ACL. The sink writes the blob; the destination owns the promise.

## Distributed-readiness

**Single-writer per ledger**, by construction (see above). The sink holds an in-process head cache and an append semaphore, so it is not a stateless-between-calls component in the portability-rule-4 sense — that is inherent to a serial chain, not an oversight. Run one ledger per writer.
