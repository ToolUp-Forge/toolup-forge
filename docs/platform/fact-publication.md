# Team-to-team fact publication

One team can hold a consolidated view of other teams' fact tables because those teams **published**
them to it. No code path reads another team's scope, with any privilege. Every question the receiving
team asks is an ordinary question inside its own scope.

This is the in-deployment counterpart of the certificate-verified import door
([facts.md](facts.md)): the peer layer exchanges facts between deployments, and publication exchanges
declared fact tables ([fact-tables.md](fact-tables.md)) between teams of one deployment.

## What this is not

**It is not a cross-team query.** Nothing here answers a question in one team with another team's
facts. The target answers from the rows it received, in its own scope, with its own disclosure gate.
A live question fanned out to several teams and merged is deliberately not offered: it brings partial
failure and slower answers, and it waits for a deployment that asks for it.

It is also not a team hierarchy. There is no notion of team rank, and a grant is never implied by one.
A head office sees a region's table because the region published it, and for no other reason.

## The pieces

| Piece | What it is |
|---|---|
| `PublicationGrant` | Source team and table, target team and table, a visibility level, and two consents. |
| `FactPublicationTarget` | The target's declaration of a consolidation table and the hierarchy level the origin team occupies. |
| `IFactPublication` | Propose, consent, withdraw, list, publish, refresh. Every member takes the caller's minted scope. |
| `FactTeamPublication` | The egress door the source's rows leave through. |
| `ResolvedScopePair` | Two minted scopes held together, for the one sanctioned cross-scope write. |
| `FactPublicationJobs` | The publication job (source scope) and the refresh job (target scope). |

## The flow

1. **Declare the consolidation table.** It is an ordinary declared fact table whose subject hierarchy
   has the origin team as its **root level**, so every row's path is the origin team followed by the
   source row's path. "Most elastic SKU in the North" is then a path-prefix population query.

   ```fsharp skip=fragment
   app
   |> FactsCompose.withFactStore
   |> FactsCompose.withFactTableWriter
   |> FactsCompose.withFactPublication
       (FactPublicationConfig.create [ FactPublicationTarget.create "group-sales" "region" ])
   ```

   A declaration whose origin level is not the root level, or whose table is not declared, fails at
   startup and names the table.

2. **Propose a grant.** An owner of either team proposes it, in a request resolved to their own team.
   Proposing records no consent.

3. **Consent on both sides.** Each team's owner consents in a request resolved to that team. The owner
   is the platform's resolved request viewer, never an argument. Each consent is audited in that
   team's scope. **A grant with one consent publishes nothing.**

4. **Publish.** Publication is a job in the **source** team's scope, scheduled with the source's minted
   scope (`FactPublicationJobs.schedulePublication`). It reads the source table in the source's own
   scope and passes every cell through the disclosure gate at `FactTeamPublication`, so the source
   team controls what leaves. A row with any cell the gate denies is absent from the publication and
   counted as withheld.

5. **Write across, once.** The one sanctioned seam takes the source's minted scope, the scope the
   target owner consented in, and the grant. It checks both scopes against the grant, and writes one
   run of the target's table. Nothing else in the fact tier holds two teams' scopes at once, and the
   test pack enumerates the seam's callers.

6. **Refresh.** A refresh job in the **target** team's scope (`FactPublicationJobs.scheduleRefresh`)
   rewrites the consolidation table from the origins still in force.

## What the target receives

- **A writer run.** The rows arrive as a committed run of the declared table: a watermark, a change
  summary, and history by transaction time, exactly as any table the target writes itself.
- **Origin team and origin run.** The origin team heads each row's path. Each fact's evidence names
  the origin team, the source table's committed run, and the publication run.
- **`Imported` provenance.** Each fact's method is `Imported` with a reference naming the grant
  (`PublicationGrant.certificateRef`). It is stable for the grant's life, so a republished value
  supersedes the previous one in one lineage and an unchanged value is written once.
- **Disclosure that narrows and never widens.** Each cell's disclosure is the floor of what the source
  published and what the target table declares (`Disclosure.floor`, the certificate-import rule). A
  `Restricted` source cell stays `Restricted` in a `Surfaceable` target column; a target column
  declared `Internal` makes every imported cell `Internal`.

## Visibility

Two controls apply, and both belong to the source team.

- **Its gate.** The `FactTeamPublication` door is its own, so an operator narrows what one team may
  publish to another without narrowing what its own members, the model or an export see. A policy that
  permits "everywhere" by listing the older doors does not permit this one until it is added.
- **Its output level** (Phase 896). A publication's audience is the target team, wider than whoever
  scheduled it, so the gate decides a `Restricted` cell for the least-privileged viewer of the source
  team. Under `TeamAdmins` or `PlatformAdmins`, restricted output stays home.

The grant's **visibility level** uses the federation vocabulary: `AggregatesOnly`, `ViewOnly`, `Full`.
A declared fact table is a population of computed metric values, so publishing one needs
`AggregatesOnly`, which is what a consolidation requires by default. A target may declare a higher
requirement (`FactPublicationTarget.Requires`), and a grant below it is refused by name. The level is
an authority check. It never widens a fact's disclosure.

## Withdrawal

Either team's owner may withdraw a grant. The target's rows from that origin are removed at the
target's next refresh, or at the next publication into the same table. Every fact minted from them is
superseded, in its own lineage, by an absence naming the withdrawal and the grant.

## Where grants live

A grant holds the scope the target owner consented in, and a minted scope is a process value. Since
Phase 935 grants **survive a restart**. Every owner act writes the grant to durable storage (the
platform-reserved `_platform` container, under `_fact-publication/grants/`), and each consent is
stored with a token. The platform's `ScopeCarrier` issued that token from the scope the consenting
owner's request resolved to. It is sealed under the deployment's DataProtection key ring, for this
grant, this side and this consent. When the service is built after a restart, the platform redeems
the target's token into the scope the seam writes under. It checks the source's token without
holding the scope. A consolidation then keeps receiving, and neither owner has to consent again.

A consent whose token does not redeem is dropped on restore. That covers a token that is missing,
altered, issued under another key ring, or copied from another grant or consent. An audit record in
both teams' scopes names why. The grant is then not in force, so the target's next refresh withdraws
the origin with a named reason. This is the same fail-closed direction a restart took before. The
token binds the grant's teams, tables and visibility and the consent itself. So an edit of those
fields in the store drops the consent rather than re-pointing it. A withdrawal is persisted with no
token at all.

A record replayed from an earlier state is dropped on restore too (Phase 964). Its tokens were
issued by this deployment for this grant and these consents, so they still redeem; what gives it
away is the audit trail. Every owner act writes `FactPublicationConsented` or
`FactPublicationRevoked` in the acting team's scope after the record is persisted, so restore reads
those two event types in both teams' scopes and compares them with the record. A record that lacks
an act the trail holds, such as a copy put back from before a withdrawal, is older than that act. Its
consents are dropped, and the audit record in both teams' scopes names the act it predates. The
check reads the trail by team id, as the audit records are written, so the service still holds one
scope per grant and the seam stays the only place that holds two. A deleted record is not caught:
it restores nothing, which leaves the grant out of force.

The composition's `FactsCompose.withFactPublication` builds the durable service whenever the
platform's carrier is composed. The carrier is composed beside the DataProtection key ring in every
`ServerApp`. A service built directly with `FactPublication.createWith` still holds grants in
process. `FactPublication.createDurable` is the durable form.

## Signing between teams of one deployment

| Profile | Requires |
|---|---|
| `RecordedProvenance` (default) | Recorded provenance: the grant, the origin team, the origin run and the two audit records, which cite each other. One deployment is one trust boundary. |
| `SignedCertificate` (regulated) | The source also seals each run's manifest (grant, origin team, source table, origin run, a digest of the rows) with the deployment's `IArtefactSigner`, and the seam verifies it with `IArtefactVerifier` before writing. A run that cannot be signed or verified writes nothing. |

Choose the regulated profile with `FactPublicationConfig.signed`. Composing it without a signer and a
verifier fails at startup.

## Audit

Every record rides the fact store's `_facts` source module.

| Event | Written in | Carries |
|---|---|---|
| `FactPublicationProposed` | the proposer's team | the grant and the owner |
| `FactPublicationConsented` | the consenting team | the grant and the owner |
| `FactPublicationRevoked` | the withdrawing team | the grant and the owner |
| `FactPublicationRunPublished` | the source | the run id, origin run, target run, rows published and withheld, and the target record's id |
| `FactPublicationRunReceived` | the target | the same run id, and the source record's id |
| `FactPublicationRunRefused` | the source | why; nothing was written to the target |
| `FactPublicationRefreshed` | the target | the run, and the origins removed |

The source's own gate additionally audits every cell it denied at the publication door.

## Composition

`FactsCompose.withFactPublication` registers the service and the two job handlers on the composed
scheduler. It registers handlers only: a publication is scheduled by the source team's owner, and a
refresh by the target's. A deployment that does not call it is unchanged, and one that calls it but
records no grant writes nothing anywhere. Under `NoFactStore` it returns the app unchanged.

## Verifying

```text
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 897"
```

The pack proves the consolidated view across two origins, the refusals without a grant in force, the
gate at the publication door, the output-level rule, the narrowing rule, withdrawal, both audit
records, both signing profiles, and the seam's single holder.
