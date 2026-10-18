// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 777 — trust boundaries as data ─────────────────────────────
//
// What the platform does NOT claim is the most reusable thing it can say,
// and before this phase it said it in prose: a not-proved statement on the
// verification report here, a "what this does not improve" section in a
// platform doc there, a migration note's closing paragraph somewhere else.
// This file makes that list COMPLETE and STRUCTURED: one closed union of
// the boundaries where something untrusted crosses into the platform, and
// for each one the same four parts — what crosses, what mediates it, what
// is claimed, what is not — plus the mechanism that bounds it.
//
// Three decisions are fixed here.
//
//   • The list is CLOSED. `TrustBoundaryId` is a union and `entry` is an
//     exhaustive match over it, so a boundary added to the union does not
//     compile until it has an entry, and the test pack asserts `ids` names
//     every case exactly once. A list that could quietly omit a boundary
//     would be the overclaim this exists to prevent.
//   • Every entry names its MECHANISM by an in-repository reference — the
//     page or migration note that ships the control, the inventory that
//     keeps a hatch honest, the planned landing that will move it. Those
//     references are what make the list unable to lag the code: the test
//     pack fails when a cited page is missing, and when a phase the entry
//     AWAITS has landed its page while the entry still reads as before.
//   • The not-claimed line is never empty and never a one-liner where a
//     longer statement exists elsewhere: the remoting-decoder entry quotes
//     its page's trust-boundary statement verbatim, and the test pack
//     compares the two, so the report and the page cannot drift apart.
//
// Pure data and string building only: this file is Fable-packed with the
// rest of `Shared/`, so an admin surface renders the same list the
// server's verification report carries, and the public page
// `docs/platform/trust-boundaries.md` is generated from `renderMarkdown`.

/// The composition profile a containment claim is stated under — the
/// Core mirror of the Server tier's `CompositionProfile`, which Core
/// cannot name. Its labels match that union's.
[<RequireQualifiedAccess>]
type ContainmentProfile =
    /// In force under every profile: a deployment that declared the
    /// mechanism's input is contained without opting into anything else.
    | Standard
    /// In force under the verified composition profile, or in a
    /// composition that composes the named mechanism itself.
    | Verified

/// What the platform claims about one trust boundary.
[<RequireQualifiedAccess>]
type TrustClaim =
    /// Nothing is claimed. The entry still states what crosses and what,
    /// if anything, narrows the consequence.
    | NotClaimed
    /// The boundary is contained under the named profile, within the
    /// limits the entry's not-claimed line states.
    | ContainedUnder of profile: ContainmentProfile
    /// A declared hatch: stated openly, with its hardening path, and not
    /// contained.
    | StatedHatch

/// What bounds a trust boundary, by in-repository reference.
///
/// A `reference` is a path relative to the repository root. The test pack
/// asserts every `Contains`, `Narrows` and `Evidences` reference exists,
/// and that every `Awaits` reference does NOT yet exist: the landing of
/// that page is the signal the entry must move.
[<RequireQualifiedAccess>]
type TrustMechanism =
    /// A shipped control that contains the boundary under the entry's
    /// claim profile.
    | Contains of phase: int * reference: string
    /// A shipped control that narrows the consequence without containing
    /// the boundary.
    | Narrows of phase: int * reference: string
    /// What keeps the claim honest without being the control: a claims
    /// ladder, an inventory of the exceptions.
    | Evidences of phase: int * reference: string
    /// A planned change that will move this entry when it lands. `landsAt`
    /// is the page its landing creates.
    | Awaits of phase: int * landsAt: string
    /// Recorded outside this repository, by entry number only.
    | Upstream of entry: int
    /// Nothing bounds it. The entry states the boundary and claims nothing.
    | Stated

/// The closed set of trust boundaries.
[<RequireQualifiedAccess>]
type TrustBoundaryId =
    /// A principal holding root or administrative control of the host.
    | HostileHostWithRoot
    /// A module's own HTTP route handlers.
    | CustomRoutes
    /// Module code resolving substrate services from the container by hand.
    | HandResolvedSubstrate
    /// Outbound calls from the process.
    | OutboundEgress
    /// Memory-unsafe native code parsing untrusted bytes.
    | NativeCompanionParsers
    /// The remoting decode edge, where client bytes become typed values.
    | RemotingDecoders
    /// What a language model intends or produces.
    | ModelIntent
    /// Statistical inference from disclosed aggregates.
    | StatisticalDisclosureFloors
    /// The declarative UI layer's advisory rendering defaults.
    | AdvisoryRendererDefaults

/// One trust boundary in the four-part form: what crosses, what mediates
/// it, what is claimed, what is not — and what bounds it.
type TrustBoundary = {
    Id: TrustBoundaryId
    /// Human title.
    Title: string
    /// What crosses the boundary.
    Crosses: string
    /// What stands between it and the platform.
    Mediates: string
    /// The claim state.
    Claim: TrustClaim
    /// What is claimed, stated in full. For a `NotClaimed` entry, what the
    /// mediation does and does not amount to.
    Statement: string
    /// What is NOT claimed, stated in full. Never empty.
    NotClaimed: string
    /// The mechanisms that bound it. Never empty.
    BoundedBy: TrustMechanism list
}

[<RequireQualifiedAccess>]
module TrustBoundaries =

    /// Stable lowercase wire label for a boundary.
    let label =
        function
        | TrustBoundaryId.HostileHostWithRoot -> "hostile-host-with-root"
        | TrustBoundaryId.CustomRoutes -> "custom-routes"
        | TrustBoundaryId.HandResolvedSubstrate -> "hand-resolved-substrate"
        | TrustBoundaryId.OutboundEgress -> "outbound-egress"
        | TrustBoundaryId.NativeCompanionParsers -> "native-companion-parsers"
        | TrustBoundaryId.RemotingDecoders -> "remoting-decoders"
        | TrustBoundaryId.ModelIntent -> "model-intent"
        | TrustBoundaryId.StatisticalDisclosureFloors -> "statistical-disclosure-floors"
        | TrustBoundaryId.AdvisoryRendererDefaults -> "advisory-renderer-defaults"

    /// Stable lowercase label for a profile — `CompositionProfile.label`'s.
    let profileLabel =
        function
        | ContainmentProfile.Standard -> "standard"
        | ContainmentProfile.Verified -> "verified"

    /// Stable lowercase label for a claim state.
    let claimLabel =
        function
        | TrustClaim.NotClaimed -> "not-claimed"
        | TrustClaim.ContainedUnder profile -> "contained-under-" + profileLabel profile
        | TrustClaim.StatedHatch -> "stated-hatch"

    /// One mechanism, rendered for the report and the public page.
    let renderMechanism =
        function
        | TrustMechanism.Contains(phase, reference) -> sprintf "contained by Phase %d (%s)" phase reference
        | TrustMechanism.Narrows(phase, reference) -> sprintf "narrowed by Phase %d (%s)" phase reference
        | TrustMechanism.Evidences(phase, reference) -> sprintf "kept honest by Phase %d (%s)" phase reference
        | TrustMechanism.Awaits(phase, landsAt) -> sprintf "moves when Phase %d lands (%s)" phase landsAt
        | TrustMechanism.Upstream entry -> sprintf "recorded upstream as entry %d" entry
        | TrustMechanism.Stated -> "nothing bounds it; stated"

    /// Every boundary, in report order. The test pack asserts this names
    /// every `TrustBoundaryId` case exactly once.
    let ids: TrustBoundaryId list = [
        TrustBoundaryId.HostileHostWithRoot
        TrustBoundaryId.CustomRoutes
        TrustBoundaryId.HandResolvedSubstrate
        TrustBoundaryId.OutboundEgress
        TrustBoundaryId.NativeCompanionParsers
        TrustBoundaryId.RemotingDecoders
        TrustBoundaryId.ModelIntent
        TrustBoundaryId.StatisticalDisclosureFloors
        TrustBoundaryId.AdvisoryRendererDefaults
    ]

    /// The entry for a boundary. An exhaustive match, so a new boundary
    /// does not compile without one.
    ///
    /// **A phase that contains a boundary moves its entry in the same
    /// change-set.** The claim state here is the current truth, not the
    /// plan: the report renders it on every run, and the public page is
    /// generated from it.
    let entry (id: TrustBoundaryId) : TrustBoundary =
        match id with
        | TrustBoundaryId.HostileHostWithRoot -> {
            Id = id
            Title = "A hostile host with root"
            Crosses =
                "Everything in the running process: memory, keys, loaded binaries, configuration and the sealed deploy record are readable and writable by whoever holds root or administrative control of the host, its hypervisor or its container runtime."
            Mediates =
                "The boot seal records the verification level the boot was credited with, inside the signed bytes, and a declared floor refuses a start below it under the verified profile. A record alone is credited with at most isolated-signer; runtime-quoted can only come from a runtime attestor's observation, and the in-tree attestor is the unattested one, so no in-tree deployment is observed above attributed or isolated-signer."
            Claim = TrustClaim.NotClaimed
            Statement =
                "Nothing. The verification level says under what environment guarantee a boot verified and where that answer came from; it does not make the host trustworthy, and with the in-tree attestor it never reports more than the record alone supports."
            NotClaimed =
                "Any property of a deployment whose host is hostile: the confidentiality of keys and data in memory, and the integrity of the process image, the seal, the audit sinks and this report itself. A host that can rewrite the process can rewrite what the process says about itself."
            BoundedBy = [ TrustMechanism.Narrows(774, "docs/migrations/774-verification-level.md") ]
          }
        | TrustBoundaryId.CustomRoutes -> {
            Id = id
            Title = "Custom route handlers"
            Crosses =
                "A module's own HTTP handlers, at every request they serve. A handler is a closure, so its reach cannot be enumerated from its registration."
            Mediates =
                "A route that declares an effect envelope (`ServerModule.withRouteEffect`) runs inside it: a host-capability invocation made while it runs goes through the capability gate's `guardInvoke` with the route's envelope, and an outbound call through the platform client factory is held to the route's egress grant, which narrows the installed egress policy and never widens it. A `Pure` envelope permits neither. Refusals reach the audit path through the route-effect observer."
            Claim = TrustClaim.ContainedUnder ContainmentProfile.Standard
            Statement =
                "Under every composition profile, a route that declares its effects cannot reach a host capability or the network through the platform beyond what it declared."
            NotClaimed =
                "An undeclared route: it is labelled, not contained, and runs exactly as it did before. Code a declared route runs outside the platform's mediation, such as its own socket, its own HttpClient or a capability registry of its own making, is outside both checks."
            BoundedBy = [
                TrustMechanism.Contains(773, "docs/migrations/773-declared-route-effect-containment.md")
            ]
          }
        | TrustBoundaryId.HandResolvedSubstrate -> {
            Id = id
            Title = "Hand-resolved substrate"
            Crosses =
                "A module's code resolving a substrate service (a store, the job scheduler, the audit log) from the container by hand rather than declaring it."
            Mediates =
                "When the composition confines its modules (`SeamAuthorityEnforcement.confine` or `confineAudited`), each module's route handlers and AI tools resolve services through a seam-aware provider that refuses a substrate seam outside the module's grant and records every seam it hands over. The verification report overlays that observed reach on the declared and derived reach, and names a component whose observed reach exceeds its derived one."
            Claim = TrustClaim.ContainedUnder ContainmentProfile.Verified
            Statement =
                "In a composition that confines its modules, a module's route handlers and AI tools cannot obtain a substrate seam outside their grant through the request's services, and what they did obtain is observed."
            NotClaimed =
                "A composition that does not call confine or confineAudited. A provider or substrate instance a module captured outside a request: at composition, from the root container, or before the confining wrapper existed. A job handler or hosted service that closes over the root provider; an in-tree reporting module's render job is one. Query handlers, job handlers, configuration migrators and vectorisation handlers, which are handed no provider and reach only what they closed over."
            BoundedBy = [
                TrustMechanism.Contains(770, "docs/migrations/770-seam-aware-service-provider.md")
            ]
          }
        | TrustBoundaryId.OutboundEgress -> {
            Id = id
            Title = "Outbound egress"
            Crosses = "Outbound HTTP from the process to any origin."
            Mediates =
                "The installed `IEgressPolicy`, applied by the handler in front of every client the platform factory hands out: the named `IHttpClientFactory` client and `PlatformHttpClient`. A verified composition that declares nothing resolves to deny-all. A denial names the origin, never a path, and is audited. The static analyser's inventory lists every in-tree client constructed outside the factory, so the bypass set is counted rather than assumed."
            Claim = TrustClaim.ContainedUnder ContainmentProfile.Verified
            Statement =
                "Every outbound HTTP call made through the platform client factory reaches only an origin the installed policy permits; under the verified profile an undeclared origin is denied."
            NotClaimed =
                "Clients constructed outside the factory: a module's own HttpClient, and the companions that reference only the Core tier and so cannot reach the factory yet (the HashiCorp Vault and GCP Secret Manager secret stores, the Stripe billing client, the Whisper and Azure Speech transcription providers, and the Entra and Google directory providers). Every non-HTTP egress: SMTP, message brokers, database drivers, raw sockets. What a permitted call carries."
            BoundedBy = [
                TrustMechanism.Contains(772, "docs/migrations/772-server-egress-policy.md")
                TrustMechanism.Evidences(776, "src/ToolUp.Remoting.Analyzers/analyzer-inventory.json")
                TrustMechanism.Awaits(969, "docs/migrations/969-core-only-companions-egress.md")
            ]
          }
        | TrustBoundaryId.NativeCompanionParsers -> {
            Id = id
            Title = "Native companion parsers on untrusted input"
            Crosses =
                "Untrusted bytes, such as an uploaded document or image, reaching memory-unsafe native code through a companion's P/Invoke surface."
            Mediates =
                "The companion isolation seam runs the native call in a separate worker process with a wall-clock timeout and a memory cap: a kernel-enforced job object on Windows, plus a resident-set sampler and a managed heap limit everywhere. A parser fault is then a typed failure of one call rather than code execution in the host. The verified profile refuses in-process isolation at composition."
            Claim = TrustClaim.ContainedUnder ContainmentProfile.Verified
            Statement =
                "Under the verified profile, a companion path that parses untrusted bytes runs out of process, bounded in time and memory."
            NotClaimed =
                "The worker's own system access: it runs as the same OS user on the same host, and confining it further is a container the deployment composes. A hard bound on native allocation on a non-Windows host, which is the container runtime's cgroup. The media transcode launch, already a child process, which does not yet run under the isolation limits. Native libraries an application consumes outside the platform's own paths."
            BoundedBy = [ TrustMechanism.Contains(687, "docs/companions/native-boundary-isolation.md") ]
          }
        | TrustBoundaryId.RemotingDecoders -> {
            // The four parts are the trust-boundary statement in §7 of
            // `docs/platform/remoting-decoder-algebra.md`, verbatim — the
            // permanent home of the remoting-decoder claim. The test pack
            // compares them, so editing one without the other is red.
            Id = id
            Title = "Remoting decoders on untrusted client bytes"
            Crosses = "client bytes on both remoting wires, at every request"
            Mediates =
                "the closed value model with length and depth bounds (786); total combinators that accept the declared type or refuse by name (785); the wire corpus (784) and the extracted-model differential host (787)"
            Claim = TrustClaim.ContainedUnder ContainmentProfile.Verified
            Statement =
                "no client bytes can drive the decode edge to divergence, unbounded allocation, or a silently mistyped value; every decode is an accept of the declared type or a named refusal routed to the validation envelope before any handler or pre-flight stage runs"
            NotClaimed =
                "authorisation, tenancy or session integrity of a decoded value; the bytes-to-value pass (bounded, not proved); semantic validity (Phase 69e's domain); the STJ path for any argument type not registered (799 — under `Verified`, no platform record is left in that set: 842); the reflection fallback for any record not opted in"
            BoundedBy = [
                TrustMechanism.Contains(785, "docs/platform/remoting-decoder-algebra.md")
                TrustMechanism.Contains(799, "docs/migrations/799-json-wire-decoder-algebra.md")
                TrustMechanism.Contains(801, "docs/migrations/801-safe-decoder-adoption.md")
                TrustMechanism.Narrows(786, "docs/platform/remoting-decoder-algebra.md")
                TrustMechanism.Evidences(787, "proofs/README.md")
            ]
          }
        | TrustBoundaryId.ModelIntent -> {
            Id = id
            Title = "Model intent"
            Crosses =
                "The output of a language model, text and tool calls alike, shaped by its prompt, by retrieved content and by anything an attacker can place in either."
            Mediates =
                "What a model's tool calls can do is bounded by controls on the tools rather than on the model: each tool's declared effect class and envelope, the module's seam grant, the caller's own authorisation, and the disclosure gate on what reaches the model."
            Claim = TrustClaim.NotClaimed
            Statement =
                "Nothing about what a model intends or will produce. The controls bound what an admitted tool call can do; they do not decide whether the model should have asked."
            NotClaimed =
                "That a model follows its instructions, resists prompt injection, or reports truthfully. A tool call the controls admit is admitted whoever asked for it."
            BoundedBy = [ TrustMechanism.Narrows(793, "docs/migrations/793-tool-effect-class.md") ]
          }
        | TrustBoundaryId.StatisticalDisclosureFloors -> {
            Id = id
            Title = "Statistical disclosure floors"
            Crosses =
                "Aggregates, derived figures and retrieved passages computed over individual-level data, read by someone entitled to the aggregate but not to the rows beneath it."
            Mediates =
                "Disclosure is a declared classification enforced at the boundary: the fact disclosure gate checks each fact's declared policy against the caller's purpose and scope on every call, including the retrieval of passages derived from facts. A declassification routine can be held to a cumulative budget, which is opt-in."
            Claim = TrustClaim.NotClaimed
            Statement =
                "Nothing about inference. The platform enforces declared disclosure classes; it is not an inference-control regime."
            NotClaimed =
                "Minimum cell sizes, small-count suppression, differencing and linkage across several permitted aggregates, or any statistical floor on what can be inferred from what was disclosed. A deployment that needs one supplies it in its own query layer."
            BoundedBy = [
                TrustMechanism.Narrows(675, "docs/migrations/675-declassification-budgets.md")
            ]
          }
        | TrustBoundaryId.AdvisoryRendererDefaults -> {
            Id = id
            Title = "Advisory renderer defaults"
            Crosses =
                "Content a declarative UI host renders: custom content identified by a content hash, and keys a host writes into shared view state."
            Mediates =
                "Two defaults are advisory by design, so that an upgrade does not break a working deployment: content whose hash does not verify warns and still renders, and protection of host-written keys attaches to a reserved prefix rather than to the key's sensitivity. Each has a stricter opt-in."
            Claim = TrustClaim.StatedHatch
            Statement = "Stated, not contained: both defaults fail open, and the hardening path for each is opt-in."
            NotClaimed =
                "That content with an unverified hash was not rendered, or that a sensitive key written outside the reserved prefix is protected. The defaults belong to the declarative UI layer, outside this repository, whose escape-hatch inventory records both with their hardening paths."
            BoundedBy = [ TrustMechanism.Upstream 900 ]
          }

    /// Every entry, in report order.
    let all: TrustBoundary list = ids |> List.map entry

    /// The public page, `docs/platform/trust-boundaries.md`. GENERATED from
    /// `all` and compared byte for byte by the test pack, so the page cannot
    /// say something the report does not. LF line endings on every host.
    let renderMarkdown () : string =
        let lines = Collections.Generic.List<string>()
        let line (text: string) = lines.Add text

        line "# Trust boundaries — what the platform does not claim"
        line ""

        line
            "<!-- GENERATED from `TrustBoundaries.all` (src/ToolUp.Platform.Core/Shared/Types/TrustBoundaryTypes.fs). Do not edit by hand: change the entry, then regenerate with TOOLUP_REGEN_TRUST_BOUNDARIES=1 and the Phase 777 tests. -->"

        line ""

        line
            "Every place something untrusted crosses into the platform, in one closed list, each in the same four parts: what crosses, what mediates it, what is claimed, and what is not. Each entry names the mechanism that bounds it by a page in this repository. The deployment verification report carries the same list as its trust-boundaries section on every run, with what is in force in that deployment beside each entry."

        line ""

        line
            "Claim states: **contained** under a composition profile, within the limits its not-claimed line states; **not claimed**; or a **stated hatch**, declared openly and not contained."

        line ""
        line "| Boundary | Claim |"
        line "|---|---|"

        for boundary in all do
            line (sprintf "| [%s](#%s) | %s |" boundary.Title (label boundary.Id) (claimLabel boundary.Claim))

        for boundary in all do
            line ""
            line (sprintf "<a id=\"%s\"></a>" (label boundary.Id))
            line ""
            line (sprintf "## %s" boundary.Title)
            line ""
            line (sprintf "- **Claim state:** %s" (claimLabel boundary.Claim))
            line (sprintf "- **Crosses:** %s" boundary.Crosses)
            line (sprintf "- **Mediates:** %s" boundary.Mediates)
            line (sprintf "- **Claim:** %s" boundary.Statement)
            line (sprintf "- **Not claimed:** %s" boundary.NotClaimed)

            line (sprintf "- **Bounded by:** %s" (boundary.BoundedBy |> List.map renderMechanism |> String.concat "; "))

        String.concat "\n" lines + "\n"