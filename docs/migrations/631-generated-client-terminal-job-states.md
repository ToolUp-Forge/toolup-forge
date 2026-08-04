# Migration — generated clients surface terminal job states

**Status: BREAKING**, to the *generated client code*, not to the wire. **Consumer action required:**
regenerate your TypeScript or Python peer client and update the call sites that poll a long-running
method. Nothing else changes: the receiver, the routes, the documents on the wire and every
immediate method are byte-for-byte as before.

## The defect

A long-running peer call returns a job id; the caller polls `GET /peer/v1/{contractId}/jobs/{jobId}`
and the receiver answers with one of **three** states — `"Pending"`, `{"Completed": …}` or
`{"Failed": …}` (federation-seam wire specification §5.5.6).

Both generated poll helpers projected those three onto **two**. The emitted return type was "the
result, or nothing", which has room for `Completed` and everything-else — so a job that had
*terminally failed* was reported as one that had *not finished*. A caller following the idiom the
generated code invites — poll until a result appears — therefore polls a dead job **forever**.

The receiver was answering correctly the whole time. The generated client threw the answer away.

This was found by [Phase 189](189-cross-runtime-federation-conformance-harness.md)'s cross-runtime
harness, by *executing* the generated clients rather than reading them, and deliberately left unfixed
there: the fix changes the emitted return type, which is a breaking change to every generated client
and belongs in its own phase with its own note. This is that note.

## What changed

Both generators now emit a **three-way terminal discriminator**, in each language's own idiom and
with no runtime dependency added (the TypeScript client still transports on bare `fetch`; the Python
client still uses only `urllib.request` + `json` + `dataclasses` from the standard library).

Two further consequences of getting the type right, both of which affect call sites:

- **The completed result is now decoded.** An embedded result rides as a *string* whose content is
  itself a document (§3.1 rule 12). The old signature promised `T` and handed back that string; it
  was a type lie either way, and parsing is the half that makes it true. You no longer parse it
  yourself.
- **A failure carries the receiver's outcome string** — the failing error's case name, which is the
  same value the receiver records against the job — plus the error's payload. A caller can now log
  *why* it failed, not only *that* it did.

The poll machinery is emitted only for a contract that actually has a long-running method. A client
with nothing to poll carries none of it.

### TypeScript

**Before**

```ts
async pollBuildReport(jobId: string): Promise<ReachResult | null>
```

```ts
// The old call site. `null` means "not finished" — and also, silently,
// "finished and failed", which is why this loop never ends.
let report = await client.pollBuildReport(jobId);
while (report === null) {
  await sleep(2000);
  report = await client.pollBuildReport(jobId);
}
use(JSON.parse(report as unknown as string));   // the "result" was really a string
```

**After**

```ts
export type PeerJobPoll<T> =
  | { state: "pending" }
  | { state: "succeeded"; result: T }
  | { state: "failed"; outcome: string; detail: unknown };

async pollBuildReport(jobId: string): Promise<PeerJobPoll<ReachResult>>
```

```ts
// The new call site. Both terminal states end the loop, and the failure
// says which class of failure it was.
for (;;) {
  const poll = await client.pollBuildReport(jobId);
  if (poll.state === "succeeded") { use(poll.result); break; }       // already decoded
  if (poll.state === "failed") { logFailure(poll.outcome, poll.detail); break; }
  await sleep(2000);
}
```

### Python

**Before**

```python
def poll_BuildReport(self, job_id: str)   # -> the completed payload, or None
```

```python
# The old call site — same defect, same infinite loop.
report = client.poll_BuildReport(job_id)
while report is None:
    time.sleep(2)
    report = client.poll_BuildReport(job_id)
use(json.loads(report))                   # the "result" was really a string
```

**After**

```python
@dataclass
class PeerJobPoll:
    state: str                      # "pending" | "succeeded" | "failed"
    result: Any = None              # decoded result, when succeeded
    outcome: Optional[str] = None   # the receiver's outcome string, when failed
    detail: Any = None              # the failing error's payload, when failed


def poll_BuildReport(self, job_id: str) -> PeerJobPoll
```

```python
# The new call site.
while True:
    poll = client.poll_BuildReport(job_id)
    if poll.state == "succeeded":
        use(poll.result)                          # already decoded
        break
    if poll.state == "failed":
        log_failure(poll.outcome, poll.detail)
        break
    time.sleep(2)
```

Two smaller Python fixes ride along, both parity repairs against the TypeScript twin: the poll helper
now raises on a JSON-RPC `Error` envelope (it previously ignored one and then failed obscurely
reading an absent `Result`), and an unrecognisable status is **refused** rather than reported as
pending — treating a status you cannot read as "not finished yet" is the same poll-forever defect in
a different costume. The TypeScript helper refuses on the same grounds.

## Consumer action

1. Regenerate the client (`TypeScriptClientGen.emit schema` / `PythonClientGen.emit schema` —
   `docs/interplatform/non-fsharp-peers.md`).
2. Update every `poll*` / `poll_*` call site to branch on `state` instead of testing for
   `null` / `None`, and drop the manual parse of the completed result.
3. A client that does not call a long-running method is unaffected; so is every immediate method,
   the capability handshake, and the transport.

A checked-in client that is *not* regenerated keeps working exactly as it did — including polling a
failed job forever. There is no wire-level incompatibility either way, which is precisely why this
one can hide.

## Specification

The three states, their encoding, and the corpus vector that pins all three were already specified
and already committed — this was a client-side projection defect, not a protocol gap, so no new
vector was minted. What §5.5.6 did **not** say is that `Completed` and `Failed` are *both terminal*
and that a conforming client must expose all three distinguishably. That is now stated normatively,
with the failure-class rule beside it, and appears in the implementation checklist. It is written
down because it turned out not to be inferable from the encoding: the states are enumerated plainly,
and an implementer reading only the encoding can still project them onto two — which is exactly what
happened here, twice, in two languages.

## Gating

[Phase 189](189-cross-runtime-federation-conformance-harness.md)'s harness gates it in both runtimes:
a terminal-failure leg per runtime asserts the reported state is `failed`, that it is not `pending`,
and that it carries the receiver's outcome string and the error's payload. Those legs were run
against the **pre-fix** generator before the fix landed — both Node and Python reported `null`, the
defect exactly — so the gate is known to be able to fail rather than assumed to be.
