# Phase 824: a peer import's scope is the platform's mint, pinned

**Who this is for:** a deployment that serves certificate-verified fact imports (Phase 683) over a peer contract, or
that calls `IFactImportDoor.Import` itself. **No public surface moved**: no type, member or baseline changed, and
nothing persisted or on the wire changes. The phase pins, with tests, a rule the import door already followed, and
writes it down where a deployment reads it.

## What changes

1. **The scope a fact lands in is always the one its caller was handed.** `IFactImportDoor` has exactly one `Import`,
   and its scope is a `ResolvedScope`. There is no string form, so neither the offer, the certificate nor a peer call
   can name the shard: an import lands in, and is audited under, the scope the door was given, never the shard the
   certificate was issued from. A test now fails if a string overload returns.
2. **A peer call carries no scope.** The call context a peer dispatch closure receives, which the wire body carries,
   has no scope member, so a scope asserted by the calling deployment has nowhere to ride. A contract-pack test pins
   this for every `IPlatformPeer` binding.
3. **The peer host mints no scope for a peer call.** Its validated principal names the calling deployment and that
   deployment's user, which this deployment's resolver has no standing to place in a shard here.

## Do this

Nothing, if your deployment already resolves the receiving scope itself. If a deployment serves imports over a peer
contract, resolve the receiving scope from a principal **you declared**, through the platform's
`DeclaredPrincipalResolver` (the declared-principal shape is in
[1029-one-declared-principal-shape.md](1029-one-declared-principal-shape.md)), and hand the `ResolvedScope` it yields
to `Import`. Do not derive it from the peer principal or from anything in the request.

## Verify

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "certificate-verified fact import"`
runs the import cases, including the two that pin the scope rule.

## Rollback

Nothing to roll back: the phase adds tests and documentation only.
