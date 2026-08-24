<!-- SPDX-License-Identifier: Apache-2.0 -->

# Deployment verification report

One command, one artefact: the evidence verifiers a deployment composes, run together, with a
typed verdict per section and an explicit statement of what the result does not prove.

## What changes

Nothing, until you opt in. `ServerConfig.DeploymentVerification` defaults to
`NoDeploymentVerification`; an existing deployment is byte-for-byte unchanged (GP 11 / GP 13).

## What it composes

Verifiers that already existed, none of them altered. **This table is the report's documented
section list — a phase that adds a section amends it here in the same commit.** The section ids are
string literals rather than a closed union precisely so that adding one is an addition and not a
break, which means nothing in the type system will remind you.

| Section | Verifier | Absent when | Since |
|---|---|---|---|
| `boot-seal` | the boot verification verdict over the sealed composition | no boot preflight was run | 686 |
| `grounding-continuity` | the grounding-envelope continuity walk | no grounding-envelope seal is composed | 686 |
| `audit-ledger` | the hash-chained audit ledger walk | no chained ledger is composed | 686 |
| `certificate-issuance` | the certificate issuance log | no certificate substrate is composed | 686 |
| `answer-verification-join` | the answer-verification provenance join | no answer-verification audit join is composed | 686 |
| `seam-authority` | each module's declared reachable-seam set beside the reach its registrations imply, and whether the composition was checked against it | no seam-authority declaration or check is composed | 693 |

New sections are **appended**, never inserted. Adding one moves every deployment's verdict digest
once, which is correct — the report grew. Inserting one among the others would move the section
lines after it too, and a reader diffing two canonical forms across the upgrade could not then tell
a re-ordering from a re-verdict.

## Wiring it

Every source arrives through `IDeploymentVerificationEvidence`, and nothing arrives by package
reference: the report lives in `ToolUp.Platform.Server`, upstream of the ledger, certificate and
answer tiers, so a reference in that direction would invert the dependency graph and nail every
deployment composing the report to those three choices (GP 1). The composition root — the only
place holding all the pieces — supplies what it has.

```fsharp
let! bootVerdict = ServerApp.verifyComposition bootOptions app

let evidence =
    DeploymentVerificationEvidence.create
        (Some(ServerApp.bootSealEvidence bootVerdict))
        None                                   // derived from the container — see below
        (Some(ChainedLedger.deploymentVerificationSource ledgerSettings blobStorage headVerifier))
        (Some(
            GroundingCertificate.deploymentVerificationSourceWithIntegrity
                auditLog
                (fun () -> async {
                    match! ChainedLedger.verify ledgerSettings blobStorage headVerifier with
                    | Ok(LedgerVerified _) -> return Ok()
                    | Ok other -> return Error(sprintf "%A" other)
                    | Error reason -> return Error reason
                })
                "_platform"
        ))
        (Some(AnswerVerifier.deploymentVerificationSource auditLog "_platform"))

app
|> ServerApp.withDeploymentVerificationEvidence evidence
|> ServerApp.withDeploymentVerification          // mounts the admin route; optional
|> ServerApp.run
```

Pass `None` for anything you do not compose — that section then reads `NotComposed` with a reason
naming what would have to be wired. The grounding-continuity member is normally left `None`: it is
the one section whose substrate is in the container, so `withDeploymentVerificationEvidence`
resolves `IGroundingEnvelopeMutator` and walks continuity at report time. Passing `Some` overrides
that, which is what a test wants and a composition root does not.

**Both arms of the boot result belong here.** `ServerApp.bootSealEvidence` takes the whole
`Result`: `Error` means the policy refused the start, not that the check produced nothing.

### The seam-authority section (Phase 693)

Supplied through a **sibling** interface rather than a sixth member on
`IDeploymentVerificationEvidence`: adding an abstract member to a shipped F# interface is a source
break, so an evidence value built before this phase still compiles and its sixth section reads
`NotComposed`. Pipe the value through `withSeamAuthority`, exactly the way
`withGroundingContinuity` already works:

```fsharp
let seamOutcome =
    SeamAuthorityEnforcement.verifyAudited auditLog "_platform" profile capabilities grants modules

let evidence =
    DeploymentVerificationEvidence.create bootSeal None ledger certificates answerJoins
    |> DeploymentVerificationEvidence.withSeamAuthority (
        Some(
            SeamAuthorityEnforcement.deploymentVerificationEvidence
                profile
                grants
                modules
                (Some seamOutcome)      // `None` if this deployment never ran the check
        ))
```

**The last argument is the whole point of the section.** Phase 691 gave the seam gate a production
call site, but *invoking* it stays a per-deployment act — so `None` means this composition never
asked the gate anything, and the section says so rather than borrowing the SDK's posture. Nothing
else is taken on trust: the component roster and both counts are recomputed here from the Phase
438/554 `Needs` projection, so a root cannot overstate its coverage by passing a flattering number,
and the report reads the same declaration→substrate map the gate does.

The verdicts, and why two plausible-looking states are deliberately *not* `Verified`:

| State | Verdict | Why |
|---|---|---|
| the check ran, grants were declared, every reach admitted | `Verified` | the conjunction — anything less is not a bound |
| grants declared, nothing routed through the gate | `Observed` | declaring is not enforcing; the declarations bound nothing here |
| the check ran over a composition declaring nothing | `Observed` | every component resolves to `UnrestrictedSeams`, so the gate could not have refused — the Phase 688 additive floor, not a confinement result |
| a component reached a seam it did not declare | `Failed` | the finding, with the refusal enumerated |
| the verified profile could not be bound (no signature, half-declared grants) | `Failed` | a mandatory check answered by withholding its input is not an absence |
| no seam evidence supplied at all | `NotComposed` | the deployment's own boundary |

## Reading it

Three surfaces, one gatherer:

- **Library** — `DeploymentVerificationReport.run services actor`.
- **Platform-Admin endpoint** — `IDeploymentVerificationApi.GetVerificationReport`, mounted by
  `ServerApp.withDeploymentVerification`. Anonymous and non-admin callers get `Error`.
- **CI** — `--verify-deployment` on the process argv. Renders the report and exits with its exit
  code, binding no listener. It needs no route, so a deployment can verify itself in CI without
  exposing the report on the wire.

```
$ dotnet run --project src/Server -- --verify-deployment
── Deployment verification report ──
  outcome: partially-verified
  verdict digest: 3f9c…
  [VERIFIED] Sealed composition (boot verification) — …
  [OBSERVED] Certificate issuance log — 3 issuance(s) enumerated from a log that runs no
             integrity gate — this is the deployment's own assertion, not tamper-evident
  …
  What this report does NOT prove:
    - The boot seal is a statement about the composition as it stood at boot. …
      (narrowed: the five enumerated grounding facets … are covered post-boot …)
```

## The two rules the verdicts follow

**No boolean, anywhere.** A section carries one of five verdicts, and three of the five are
non-affirmative in *different* ways — `NotComposed` (nothing wired), `Unreadable` (wired and would
not answer), `Failed` (wired and the check does not hold). Those have different remedies, and an
assessor needs to tell them apart. `Observed` is the fourth non-affirmative: wired, read, with
nothing to affirm — an empty ledger, an issuance log behind no integrity gate, an envelope
declaring nothing. It is not a pass.

**Exit code:** non-zero when any *composed* section is `Failed` or `Unreadable`; zero otherwise.
Absence exits zero on purpose — a CI job that reddened because a deployment does not compose an
optional substrate would be reporting the deployment's shape as a defect, and would be switched
off. A composed-but-unreadable section exits non-zero for the mirror-image reason: it is the state
a deployment reaches by breaking its own evidence, and a zero there would make tampering cheaper
than compliance.

## The audited read

Each run records one `DeploymentVerified` row through `IAuditLog` under `_platform`: the actor, the
outcome label, the exit code, one `<section-id>=<verdict-label>` entry per section, and the verdict
digest. Recorded on **every** outcome including the clean one — a trail carrying only adverse runs
cannot distinguish a deployment nobody checked from one that was checked and was fine.

The row carries the digest, never the section detail: the detail is a deployment-wide evidence
summary and the audit trail has its own readership. The digest is SHA-256 over the report's
canonical form, which excludes the clock, the actor and the findings — so two runs against an
unchanged deployment digest identically, and drift is visible as a change rather than inferred.

## Rollback

Stop calling `ServerApp.withDeploymentVerification` and
`ServerApp.withDeploymentVerificationEvidence`. Nothing else observes them, no hosted service is
registered, and the `--verify-deployment` flag on a deployment that registered no evidence prints
a `NotComposed` section per row of the table above and exits 0. Dropping only the seam-authority
member (stop calling `withSeamAuthority`) rolls back that section alone.

## What this report does not prove

Carried as data on every report (`NotProved`), not only stated here — a bound that lives in a
migration doc is not available to the person reading the report:

- **Post-boot mutation.** The boot seal describes the composition at boot. Composing the grounding
  seal *narrows* this to "everything except the five enumerated grounding facets"; it does not
  close it.
- **Truth of recorded inputs.** Every check establishes that recorded evidence is internally
  consistent and unaltered. None establishes it was true when recorded.
- **Code never composed.** The report covers what this deployment wired. Read the `NotComposed`
  sections as the report's own boundary, not as a clean bill.
- **The gate is a decision point, not a sandbox.** It refuses a composition when presented; it does
  not confine running code.
- **Certificate bodies are not retained.** The issuance log proves a document with a given digest
  was issued. Re-verifying the document needs the holder's copy, checked against the log by digest.
- **The ledger covers what reached it.** A verified chain proves its records are the records it was
  given, in order — not that every event which occurred reached it.
- **Seam reach is a subset claim.** The seam-authority section reports the substrate each module's
  registrations *imply*. A module can still resolve substrate from the container by hand, and route
  handlers are closures whose reach is not enumerable — so a refusal is sound while an admission is
  never a proof of confinement. Composing the section narrows this to "the distance between what was
  declared and what is observable is visible rather than inferred"; it does not close it.
