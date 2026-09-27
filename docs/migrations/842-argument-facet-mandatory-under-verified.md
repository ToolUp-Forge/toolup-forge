# Phase 842 — The argument facet becomes mandatory under the verified profile

**Applies to:** every deployment running `CompositionProfile.Verified`; any composition root that
reads `RemotingDecoderFacet.inspectServedArguments` / `describeArguments`; any deployment mounting
its own API records under `Verified`.
**Breaking:** no wire change — no byte on the JSON wire moved. Additive surface on
`ToolUp.Platform.Server` (`RemotingDecoderFacet.verifyArguments`, the
`RemotingArgumentDecodersUnregistered` refusal case, `IRemotingArgumentDecoderEvidence` /
`DeploymentVerificationEvidence.withRemotingArgumentDecoders` / `gatherRemotingArgumentDecoders`, a
twelfth report section) and `ToolUp.Platform.Core` (`RemotingArgumentDecoderSection`). Behavioural
change confined to `FacetRequired` on `RemotingDecoderFacet.inspectServedArguments`'s result, which
flips from always `false` to `RemotingDecoderFacet.requiresAlgebraDecoders profile`.
**Action required:** none under `CompositionProfile.Standard` — the facet stays advisory there,
byte-for-byte (GP 11). A root running `CompositionProfile.Verified` that mounts an API record whose
arguments are not fully covered by a registered JSON decoder should read the section below.

## What changes

1. **`FacetRequired` follows the profile, exactly as the response facet has since Phase 801.** Phase
   799 shipped `inspectServedArguments` advisory under every profile (`FacetRequired = false`
   whatever the profile said) because the JSON algebra's first set was hand-written and narrow, and
   a `Verified` deployment that refused on a served record with no JSON decoder would have refused
   nearly every deployment on the day the facet shipped. Phase 841 gave the generator an argument leg
   (`Emit.jsonCompilationUnit` / `Plan.forJsonTypes`), so `PlatformJsonDecoders` now covers every
   platform record's arguments by construction — the same road the response facet travelled between
   Phases 785 and 801. Phase 842 is the same turn for the argument side.
2. **A new, more specific refusal.** `RemotingDecoderFacet.verify` (the response facet's boot check)
   refuses with `RemotingDecodersUnregistered of records: string list` — record names only. The
   argument side gets its own `RemotingDecoderFacet.verifyArguments`, refusing with
   `RemotingArgumentDecodersUnregistered of records: (string * string list) list` — each served
   record paired with the argument type(s) it carries that have no registered decoder. An operator
   reading "IPresenceApi" alone still has to go find what is missing; the pair names what to
   register.
3. **The deployment verification report gains a twelfth section.** `RemotingArgumentDecoderSection`
   (`"remoting-argument-decoders"`) mirrors the existing `RemotingDecoderSection`, over the same
   `RemotingDecoderIntegrity` shape — a composition root supplies it via the new
   `DeploymentVerificationEvidence.withRemotingArgumentDecoders`, exactly as it supplies the response
   section via `withRemotingDecoders`. There is no automatic default the way the response section
   has (derived by `ServerApp.withDeploymentVerificationEvidence` under `Standard` when the root
   supplies none): a root wanting this section composes it explicitly, which is what a `Verified`
   root must already do for the response section — "that profile's refusal is the root's act, and
   the report should carry the facet the refusal was judged on."

## If you run `CompositionProfile.Verified`

The refusal now fires on a **served** record that takes an argument type with no registered JSON
decoder. A root that mounts its own API records and never registered JSON decoders for their
argument types was previously admitted (the facet was advisory everywhere); it is refused now,
naming the record and its uncovered types. Either register a decoder for each type named
(`JsonDecoders.register<'T>`, hand-written in the shape `PlatformJsonDecoders` uses) — a generator
for a consumer's own records is not yet wired to a consumer build (Phase 804 packages that) — or run
`CompositionProfile.Standard`, where the facet stays advisory and the boot line reports the ratio.

## Verification

- `JsonDecoderAlgebraTests`, "Phase 842": a `Verified` deployment serving only platform records
  boots (`verifyArguments` returns `Ok`); one also serving an unregistered consumer record refuses,
  naming the record and its uncovered argument type; a `Standard` deployment is unaffected; the
  deployment verification report renders the ratio on both the fully-covered and the partially-
  covered side.

## Rollback

Revert the phase's commits. No wire bytes changed; `inspectServedArguments` returns to
`FacetRequired = false` under every profile, and a root that composed
`withRemotingArgumentDecoders` simply stops calling it.
