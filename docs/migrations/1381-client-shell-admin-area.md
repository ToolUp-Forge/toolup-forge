# A composition declares the Administration area: `clientShell.adminArea`

**Ships in:** the composition profile `server-app-host` 7.6. Profile-only and additive: no package changes, and the
runtime it binds to (`ShellChrome.AdminArea`, Phase 567 / Phase 1074) ships in ToolUp.Platform.Client 0.42.1. A 7.5
composition carries no `adminArea`, so it encodes byte for byte as before and is valid at 7.6 with the same verdict.

**Affected:** a composed application that wants administrators to work in the product by default and reach the
administration modules through one role-gated switcher; an evaluator or tool that reads the profile's `clientShell`.

## What changes

1. **The member.** `clientShell` (§P3.1) gains the OPTIONAL `adminArea`, a profile token with the closed vocabulary
   §P2.26: one token, `separate`. Absent is today's behaviour, the administration modules inline on the rail. A token
   outside the vocabulary is refused as `token-unregistered`; no composition rule is added.
2. **What `separate` obliges a host to do.** Render the product area by default; give the area switcher only to a caller
   the administration groups' own role predicates admit (a caller they refuse sees the product area alone and no
   switcher); keep the active team across a flip between the areas; flip to the administration area when a deep link
   opens an administration route. The member says nothing about who administers, which stays the deployment's roles.
3. **The binding.** `spec/surface-binding.json` binds the member to `ClientConfig.Chrome.AdminArea`: `separate` is
   `ChromeFill.On ()`, absent is `ChromeFill.Off`.
4. **Vectors.** `client-shell-admin-area` (round trip) and `reject-token-unregistered-admin-area`, in the fixture
   manifest.

## Adopting it

In the composition, on the `clientShell` node (the `client-shell-admin-area` vector is the full example):

```json
{ "kind": "clientShell", "id": "clientShell", "home": [ … ], "adminArea": "separate", "children": [] }
```

An evaluator that reduces `clientShell` to a client entry sets the chrome field when the member is present:

```fsharp skip=fragment
let config = { config with Chrome = { config.Chrome with AdminArea = ChromeFill.On () } }
```

A hand-written client needs nothing from this phase: it sets `Chrome.AdminArea` directly, as before.

## Verifying

`dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Composition profile"`: the binding check over
`browserHistory` and `adminArea`, its planted-mismatch cases, and the two new vectors through the profile conformance
suite. Read the case count, not only the exit code: a filter that matches nothing reports success.

## Rolling back

Remove `adminArea` from the composition, or pin the profile at 7.5; the client then renders administration inline.
