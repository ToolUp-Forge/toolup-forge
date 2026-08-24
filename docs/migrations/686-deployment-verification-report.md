<!-- SPDX-License-Identifier: Apache-2.0 -->

# Deployment verification report

One command, one artefact: the evidence verifiers a deployment composes, run together, with a
typed verdict per section and an explicit statement of what the result does not prove.

## What changes

Nothing, until you opt in. `ServerConfig.DeploymentVerification` defaults to
`NoDeploymentVerification`; an existing deployment is byte-for-byte unchanged (GP 11 / GP 13).

## What it composes

Five verifiers that already existed, none of them altered:

| Section | Verifier | Absent when |
|---|---|---|
| `boot-seal` | the boot verification verdict over the sealed composition | no boot preflight was run |
| `grounding-continuity` | the grounding-envelope continuity walk | no grounding-envelope seal is composed |
| `audit-ledger` | the hash-chained audit ledger walk | no chained ledger is composed |
| `certificate-issuance` | the certificate issuance log | no certificate substrate is composed |
| `answer-verification-join` | the answer-verification provenance join | no answer-verification audit join is composed |

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
five `NotComposed` sections and exits 0.

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
