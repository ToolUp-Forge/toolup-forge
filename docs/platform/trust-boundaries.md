# Trust boundaries — what the platform does not claim

<!-- GENERATED from `TrustBoundaries.all` (src/ToolUp.Platform.Core/Shared/Types/TrustBoundaryTypes.fs). Do not edit by hand: change the entry, then regenerate with TOOLUP_REGEN_TRUST_BOUNDARIES=1 and the Phase 777 tests. -->

Every place something untrusted crosses into the platform, in one closed list, each in the same four parts: what crosses, what mediates it, what is claimed, and what is not. Each entry names the mechanism that bounds it by a page in this repository. The deployment verification report carries the same list as its trust-boundaries section on every run, with what is in force in that deployment beside each entry.

Claim states: **contained** under a composition profile, within the limits its not-claimed line states; **not claimed**; or a **stated hatch**, declared openly and not contained.

| Boundary | Claim |
|---|---|
| [A hostile host with root](#hostile-host-with-root) | not-claimed |
| [Custom route handlers](#custom-routes) | contained-under-standard |
| [Hand-resolved substrate](#hand-resolved-substrate) | contained-under-verified |
| [Outbound egress](#outbound-egress) | contained-under-verified |
| [Native companion parsers on untrusted input](#native-companion-parsers) | contained-under-verified |
| [Remoting decoders on untrusted client bytes](#remoting-decoders) | contained-under-verified |
| [Model intent](#model-intent) | not-claimed |
| [Statistical disclosure floors](#statistical-disclosure-floors) | not-claimed |
| [Advisory renderer defaults](#advisory-renderer-defaults) | stated-hatch |

<a id="hostile-host-with-root"></a>

## A hostile host with root

- **Claim state:** not-claimed
- **Crosses:** Everything in the running process: memory, keys, loaded binaries, configuration and the sealed deploy record are readable and writable by whoever holds root or administrative control of the host, its hypervisor or its container runtime.
- **Mediates:** The boot seal records the verification level the boot was credited with, inside the signed bytes, and a declared floor refuses a start below it under the verified profile. A record alone is credited with at most isolated-signer; runtime-quoted can only come from a runtime attestor's observation, and the in-tree attestor is the unattested one, so no in-tree deployment is observed above attributed or isolated-signer.
- **Claim:** Nothing. The verification level says under what environment guarantee a boot verified and where that answer came from; it does not make the host trustworthy, and with the in-tree attestor it never reports more than the record alone supports.
- **Not claimed:** Any property of a deployment whose host is hostile: the confidentiality of keys and data in memory, and the integrity of the process image, the seal, the audit sinks and this report itself. A host that can rewrite the process can rewrite what the process says about itself.
- **Bounded by:** narrowed by Phase 774 (docs/migrations/774-verification-level.md)

<a id="custom-routes"></a>

## Custom route handlers

- **Claim state:** contained-under-standard
- **Crosses:** A module's own HTTP handlers, at every request they serve. A handler is a closure, so its reach cannot be enumerated from its registration.
- **Mediates:** A route that declares an effect envelope (`ServerModule.withRouteEffect`) runs inside it: a host-capability invocation made while it runs goes through the capability gate's `guardInvoke` with the route's envelope, and an outbound call through the platform client factory is held to the route's egress grant, which narrows the installed egress policy and never widens it. A `Pure` envelope permits neither. Refusals reach the audit path through the route-effect observer.
- **Claim:** Under every composition profile, a route that declares its effects cannot reach a host capability or the network through the platform beyond what it declared.
- **Not claimed:** An undeclared route: it is labelled, not contained, and runs exactly as it did before. Code a declared route runs outside the platform's mediation, such as its own socket, its own HttpClient or a capability registry of its own making, is outside both checks.
- **Bounded by:** contained by Phase 773 (docs/migrations/773-declared-route-effect-containment.md)

<a id="hand-resolved-substrate"></a>

## Hand-resolved substrate

- **Claim state:** contained-under-verified
- **Crosses:** A module's code resolving a substrate service (a store, the job scheduler, the audit log) from the container by hand rather than declaring it.
- **Mediates:** When the composition confines its modules (`SeamAuthorityEnforcement.confine` or `confineAudited`), each module's route handlers and AI tools resolve services through a seam-aware provider that refuses a substrate seam outside the module's grant and records every seam it hands over. The verification report overlays that observed reach on the declared and derived reach, and names a component whose observed reach exceeds its derived one.
- **Claim:** In a composition that confines its modules, a module's route handlers and AI tools cannot obtain a substrate seam outside their grant through the request's services, and what they did obtain is observed.
- **Not claimed:** A composition that does not call confine or confineAudited. A provider or substrate instance a module captured outside a request: at composition, from the root container, or before the confining wrapper existed. A job handler or hosted service that closes over the root provider; an in-tree reporting module's render job is one. Query handlers, job handlers, configuration migrators and vectorisation handlers, which are handed no provider and reach only what they closed over.
- **Bounded by:** contained by Phase 770 (docs/migrations/770-seam-aware-service-provider.md)

<a id="outbound-egress"></a>

## Outbound egress

- **Claim state:** contained-under-verified
- **Crosses:** Outbound HTTP from the process to any origin.
- **Mediates:** The installed `IEgressPolicy`, applied by the handler in front of every client the platform factory hands out: the named `IHttpClientFactory` client and `PlatformHttpClient`. A verified composition that declares nothing resolves to deny-all. A denial names the origin, never a path, and is audited. The static analyser's inventory lists every in-tree client constructed outside the factory, so the bypass set is counted rather than assumed.
- **Claim:** Every outbound HTTP call made through the platform client factory reaches only an origin the installed policy permits; under the verified profile an undeclared origin is denied.
- **Not claimed:** Clients constructed outside the factory: a module's own HttpClient, and the companions that reference only the Core tier and so cannot reach the factory yet (the HashiCorp Vault and GCP Secret Manager secret stores, the Stripe billing client, the Whisper and Azure Speech transcription providers, and the Entra and Google directory providers). Every non-HTTP egress: SMTP, message brokers, database drivers, raw sockets. What a permitted call carries.
- **Bounded by:** contained by Phase 772 (docs/migrations/772-server-egress-policy.md); kept honest by Phase 776 (src/ToolUp.Remoting.Analyzers/analyzer-inventory.json); moves when Phase 969 lands (docs/migrations/969-core-only-companions-egress.md)

<a id="native-companion-parsers"></a>

## Native companion parsers on untrusted input

- **Claim state:** contained-under-verified
- **Crosses:** Untrusted bytes, such as an uploaded document or image, reaching memory-unsafe native code through a companion's P/Invoke surface.
- **Mediates:** The companion isolation seam runs the native call in a separate worker process with a wall-clock timeout and a memory cap: a kernel-enforced job object on Windows, plus a resident-set sampler and a managed heap limit everywhere. A parser fault is then a typed failure of one call rather than code execution in the host. The verified profile refuses in-process isolation at composition.
- **Claim:** Under the verified profile, a companion path that parses untrusted bytes runs out of process, bounded in time and memory.
- **Not claimed:** The worker's own system access: it runs as the same OS user on the same host, and confining it further is a container the deployment composes. A hard bound on native allocation on a non-Windows host, which is the container runtime's cgroup. The media transcode launch, already a child process, which does not yet run under the isolation limits. Native libraries an application consumes outside the platform's own paths.
- **Bounded by:** contained by Phase 687 (docs/companions/native-boundary-isolation.md)

<a id="remoting-decoders"></a>

## Remoting decoders on untrusted client bytes

- **Claim state:** contained-under-verified
- **Crosses:** client bytes on both remoting wires, at every request
- **Mediates:** the closed value model with length and depth bounds (786); total combinators that accept the declared type or refuse by name (785); the wire corpus (784) and the extracted-model differential host (787)
- **Claim:** no client bytes can drive the decode edge to divergence, unbounded allocation, or a silently mistyped value; every decode is an accept of the declared type or a named refusal routed to the validation envelope before any handler or pre-flight stage runs
- **Not claimed:** authorisation, tenancy or session integrity of a decoded value; the bytes-to-value pass (bounded, not proved); semantic validity (Phase 69e's domain); the STJ path for any argument type not registered (799 — under `Verified`, no platform record is left in that set: 842); the reflection fallback for any record not opted in
- **Bounded by:** contained by Phase 785 (docs/platform/remoting-decoder-algebra.md); contained by Phase 799 (docs/migrations/799-json-wire-decoder-algebra.md); contained by Phase 801 (docs/migrations/801-safe-decoder-adoption.md); narrowed by Phase 786 (docs/platform/remoting-decoder-algebra.md); kept honest by Phase 787 (proofs/README.md)

<a id="model-intent"></a>

## Model intent

- **Claim state:** not-claimed
- **Crosses:** The output of a language model, text and tool calls alike, shaped by its prompt, by retrieved content and by anything an attacker can place in either.
- **Mediates:** What a model's tool calls can do is bounded by controls on the tools rather than on the model: each tool's declared effect class and envelope, the module's seam grant, the caller's own authorisation, and the disclosure gate on what reaches the model.
- **Claim:** Nothing about what a model intends or will produce. The controls bound what an admitted tool call can do; they do not decide whether the model should have asked.
- **Not claimed:** That a model follows its instructions, resists prompt injection, or reports truthfully. A tool call the controls admit is admitted whoever asked for it.
- **Bounded by:** narrowed by Phase 793 (docs/migrations/793-tool-effect-class.md)

<a id="statistical-disclosure-floors"></a>

## Statistical disclosure floors

- **Claim state:** not-claimed
- **Crosses:** Aggregates, derived figures and retrieved passages computed over individual-level data, read by someone entitled to the aggregate but not to the rows beneath it.
- **Mediates:** Disclosure is a declared classification enforced at the boundary: the fact disclosure gate checks each fact's declared policy against the caller's purpose and scope on every call, including the retrieval of passages derived from facts. A declassification routine can be held to a cumulative budget, which is opt-in.
- **Claim:** Nothing about inference. The platform enforces declared disclosure classes; it is not an inference-control regime.
- **Not claimed:** Minimum cell sizes, small-count suppression, differencing and linkage across several permitted aggregates, or any statistical floor on what can be inferred from what was disclosed. A deployment that needs one supplies it in its own query layer.
- **Bounded by:** narrowed by Phase 675 (docs/migrations/675-declassification-budgets.md)

<a id="advisory-renderer-defaults"></a>

## Advisory renderer defaults

- **Claim state:** stated-hatch
- **Crosses:** Content a declarative UI host renders: custom content identified by a content hash, and keys a host writes into shared view state.
- **Mediates:** Two defaults are advisory by design, so that an upgrade does not break a working deployment: content whose hash does not verify warns and still renders, and protection of host-written keys attaches to a reserved prefix rather than to the key's sensitivity. Each has a stricter opt-in.
- **Claim:** Stated, not contained: both defaults fail open, and the hardening path for each is opt-in.
- **Not claimed:** That content with an unverified hash was not rendered, or that a sensitive key written outside the reserved prefix is protected. The defaults belong to the declarative UI layer, outside this repository, whose escape-hatch inventory records both with their hardening paths.
- **Bounded by:** recorded upstream as entry 900
