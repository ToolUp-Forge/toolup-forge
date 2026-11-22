# Phase 917: the delegated-query profile

**Who this is for:** a deployment that holds the credential for a data source and wants to answer queries from a
peer that holds none (the **custodian**), and a deployment that sends them (the **requester**). **Additive
surface**: a new contract, `fuaran.delegated-query`, and its handler seam. Nothing changes for a deployment that
does not compose it: no route is mounted and the peer surface is byte-for-byte what it was.

## What changes

1. **`DelegatedQueryPeerContract`** hosts three invocations: `Describe` (the schema this peer may build a query
   against), `Submit` (a query and its bound parameters, answered directly or with a handle) and `Resolve` (a
   handle's state). Forge carries the query, the parameters, the schema and the answer as canonical JSON it hashes
   and never parses. The wire shapes, refusal classes and conformance vectors are the federation wire
   specification's delegated-query profile.
2. **`DelegatedQueryHandler`** is the seam a custodian fills: `Describe` and `Evaluate`, each given the
   validated caller (`DelegatedQueryCaller`: peer, bound scope, effective authority level) and never anything the
   request body asserted. `Evaluate` answers a `DelegatedQueryResult`: an opaque answer, its declared class
   (`Aggregate` or `RowLevel`) and the smallest cohort any figure in it rests on.
3. **The substrate applies the rest.** A `RowLevel` answer needs `Full` authority and an `Aggregate` answer
   `AggregatesOnly`; an answer whose class exceeds the peer's grant is refused before it is written. Every answer
   crosses the clean-room gate: its minimum cohort binds on the declared cohort, the budget is charged per answer
   (and returned for a description, handle or refusal), and rows pass only under a template built with
   `DelegatedQueryProfile.withRowRelease`, whose approval both parties sign. `DelegatedQueryDeps.ClassCheck` is
   where a custodian checks a declared class against its own answer representation.
4. **The queued leg is collected by `Resolve`, never by the transport's job poll**, so a queued result crosses the
   gate and is re-judged against the grant as it stands.
5. **`CleanRoomGate.wrapNoised` is unchanged in behaviour.** Its body moved into an internal `wrapReading`, which
   lets this contract's gate read a declared class instead of a `CohortResult`; no public signature moved.

## Do this

Custodian: register the gated contract with a binding per requester.

```fsharp skip=fragment
let gate: DelegatedQueryGate = {
    Broker = CleanRoomBroker.create ()
    Template = DelegatedQueryProfile.template "delegated-query" floor
    Approval = None
    Budget = Some meter
    Audit = CleanRoomGate.noAudit
}

let deps: DelegatedQueryDeps = {
    ResolveBinding = fun peerId -> async { return bindings |> Map.tryFind peerId }
    Handler = { Describe = describeFor; Evaluate = evaluateFor }
    Leg = DelegatedQueryLeg.Immediate
    ClassCheck = Some checkClass
    Audit = DelegatedQueryPeerContract.noAudit
}

match DelegatedQueryPeerContract.builder gate deps with
| Ok build -> options |> PeerOptions.withContract build
| Error refusals -> failwith (refusals |> List.map DelegatedQueryTemplateRefusal.describe |> String.concat "; ")
```

Requester: declare the contract as consumed, pin the custodian's surface, and call it with the envelope builders.

```fsharp skip=fragment
let request = DelegatedQueryPeerContract.submissionRequest { Query = query; Parameters = parameters }
let args = DelegatedQueryPeerContract.arguments request
// send args to method request.Operation on contract DelegatedQueryProfile.ContractId, then:
match DelegatedQueryPeerContract.readAnswer json with
| Ok(DelegatedQueryAnswer.Answered result) -> use result.Answer
| Ok(DelegatedQueryAnswer.Refused refusal) -> report (DelegatedQueryRefusal.className refusal)
| other -> handle other
```

## Verify

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "delegated-query"`
runs the profile's cases. The corpus vectors run under `--filter-test-list "federation-seam wire conformance"`
with `TOOLUP_FEDERATION_SPEC_DIR` naming a specification checkout that carries the `delegated-query` family.

## Rollback

Remove the `withContract` registration. Nothing is persisted outside the peer job result store, whose delegated-query
records park under their own scope and expire under its retention policy.
