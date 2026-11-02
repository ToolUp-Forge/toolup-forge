# Stored-page routes — a reader's page and a request set over HTTP

**Ships in:** ToolUp.Platform.Server, ToolUp.Facts.Presentation and ToolUp.Facts.Presentation.Server 0.39.0. Rides
the 0.39.0 draft. The class here is additive: two routes, a request-set codec and the functions behind them. Nothing
existing changes shape.

**Affected:** a deployment that composes the page store (`PageDocumentStoreCompose.withPageDocumentStore`, Phase 1165)
or the page request resolvers (`PresentationCompose.withPageRequests`, Phase 1166). Each step now also mounts one
route. A deployment composing neither mounts nothing.

**First adopter:** diametrical#292, which loads its stored page and re-resolves its requests through these routes
rather than writing its own. <!-- OSS-BOUNDARY-EXEMPT: a bare roadmap citation (side#number); the boundary admits the ordinal. -->

## What changes

### The page route — `GET /api/_pages/{module}/{page}`

`withPageDocumentStore` mounts `PageDocumentRoute.handler`, once however often the step is called. The route folds
the page for the caller through `IPageDocumentStore.ReadFolded`: the platform layer, then the caller's team's layer,
then the caller's own layer where the deployment enables it. The caller's scope is the one the platform resolved for
the request (`ScopeResolution.forRequest`), and the access context is the request's own. Nothing on the wire names a
scope or a principal.

A **200** carries one canonical JSON object (`Canonical.write Rendering.CrossHost`: members sorted, no whitespace):

| Member | What it is |
|---|---|
| `kind`, `version` | `"toolup.storedPage"`, `1` |
| `module`, `page` | the page's id |
| `document` | the folded tree's canonical wire JSON, as a string, byte for byte what `FoldedPage.Document` holds |
| `contentHash` | SHA-256 (lowercase hex) of `document`'s UTF-8 bytes |
| `heads` | each folded layer's op-stream head, by layer name (`platform`, `team`, `individual`) |
| `sidecar` | the sidecar saved with the layer that last moved the page, base64 of the bytes as saved |
| `sidecarLayer` | that layer's name |
| `conflicts` | how many stored ops did not apply in this reader's fold |
| `detached` | whether the page is detached from its compiled default |

The layer that last moved the page is the uppermost folded layer holding ops, else the platform layer: a layer with
no ops leaves the document as the layer below it saved it, and so leaves the read-set. `sidecar` and `sidecarLayer`
are both absent when that layer saved no sidecar.

A refusal carries `{"refused":{"code":…,"reason":…}}`. `code` is `PageStoreError.code`:

| Status | `code` | When |
|---|---|---|
| 403 | `scope-refused` | the reader is outside the page's gate: an anonymous scope, or a team scope the caller is not acting in |
| 404 | `not-seeded` | no page of that id is stored |
| 400 | `invalid-page-ref` | the module or page is not a safe identifier |
| 503 | `permissions-unavailable`, `storage` | the store could not be read; it fails closed |
| 500 | `stored-layer-unreadable` | a stored layer is malformed |

### The resolution route — `POST /api/_facts/page-requests`

`withPageRequests` mounts `PageRequestRoute.handler`, under a composed fact store only, and once. The body is a
request set (`PageRequestSet`, ToolUp.Facts.Presentation, both compilers):

```json
{"requests":{"<key>":{"dependsOn":["…"],"kind":"presentation"|"pipeline","payload":{…}},…}}
```

Each request is the canonical form Phase 1166 published (`PageRequest.toJVal`). The set carries no other member. A
set naming one, such as a `scope`, is refused by that member's name: a request resolves in the caller's own scope,
for the caller's own principal.

Every request is answered through the caller's `PageRequestResolvers.ForRequest` resolver. That is the resolver the
in-process path uses, judged at the `FactBrowse` door and cached in the caller's session. A **200** carries one answer
per key posted:

```json
{"resolutions":{"<key>":{"resolved":<resolution>}|{"refused":{"cause":"…","reason":"…"}},…}}
```

`<resolution>` is `PageResolution.toJVal`, so `Canonical.write Rendering.CrossHost` over it gives exactly the bytes
`PageResolution.toJson` gives for the in-process call. `cause` is `PageRefusal.cause`, for example `unknown-table`,
`unknown-kind`, `malformed-request`, `withheld`, `no-facts` or `read-refused`. One request refused does not refuse the
others.

A set refused whole carries `{"refused":{"cause":…,"reason":…}}`. It is a 400 `malformed-request` for a body that is
not a request set or names a member the set does not carry. It is a 400 `budget` for more than
`PageRequestRoute.MaxRequests` (100) requests, and a 503 `no-fact-store` when the caller's deployment composes no fact
store.

### The gate both routes run under

Both routes run under the surface requirement the composition declares for their paths. When it declares none, the
strict `SurfaceRequirement.userOrTeam` fallback applies: anonymous callers and claim bearers are refused before the
handler runs. A deployment that serves pages to claim bearers declares that requirement for the path itself.

## What to do

Nothing, unless you render a stored page from a browser. Then the hosting tier's `StoredView.load` takes the page from
the GET route, and its injected resolvers take their answers from the POST route.

**The page.** forge's page store keeps the tree and ONE opaque sidecar per layer (Phase 1165). It does not hold a
`PageDocument` or a `SignedPage`: forge's pin of the UI language package predates both, and the store never reads
the sidecar. The deployment that signs a page defines the sidecar's encoding: the page's request map and provenance, and
the `SignedPage` over them. Its `loadPage` builds the `StoredDocument` from the two members:

```fsharp skip=fragment
let loadPage () = async {
    let! envelope = getJson (sprintf "/api/_pages/%s/%s" moduleName pageName)   // the 200 above
    // `document` is the tree's wire JSON; `sidecar` is base64 of what this deployment saved with the layer.
    let! requests, provenance, signed = readSidecar (Convert.FromBase64String envelope.sidecar)
    let! tree = decodeTree envelope.document
    return Ok { Document = { Tree = tree; Requests = requests; Provenance = provenance }; Signed = signed }
}
StoredView.load verifier resolvers loadPage
```

The served page is never re-signed. If an upper layer's ops no longer apply over a moved layer below, the read-set
saved with that layer no longer describes the fold, and `StoredView` refuses the page by name (`Drift` /
`BadSignature`). It is never shown unverified.

**The requests.** Register one `StoredPageResolver` per kind (`presentation`, `pipeline`). Each posts its request
under its key and reads that key's answer:

```fsharp skip=fragment
let resolveVia (request: StoredPageRequest) = async {
    let body =
        sprintf """{"requests":{%s:{"dependsOn":%s,"kind":%s,"payload":%s}}}""" (quote request.Key) … // canonical JSON
    let! answer = postJson "/api/_facts/page-requests" body
    match answer.resolutions.[request.Key] with
    | Resolved r -> return Ok { Value = decodeTable r.table; Schema = None }   // the Column table, rows absent when withheld
    | Refused(cause, reason) -> return Error(sprintf "%s: %s" cause reason)
}
```

A parameter change reaches the server as the request the client posts. The route does not read `Changes`.

## Verification

- `ToolUp.Platform.Tests` "Page store and layer fold / the page route": a seeded page with a team save is served to
  a team member as the in-process fold, byte for byte, with the team layer's sidecar. An anonymous reader is refused
  403 `scope-refused`, an unknown page 404 `not-seeded`, and the step mounts the route once.
- "page request resolvers / the resolution route": a presentation and a pipeline request posted together each answer
  the bytes a fresh in-process resolver gives. An undeclared table is refused `unknown-table` under its key while the
  other request resolves. A set naming `scope` is refused by that name. The route is mounted once, and not at all
  without a fact store.

## Rollback

Drop the two routes by not composing the steps. That also drops the store and the resolvers, so to keep those and
lose only the routes, pin the previous draft. Nothing is persisted by either route.
