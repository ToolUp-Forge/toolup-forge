// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 432 — component secret & config requirements ──────────────
//
// What a composed component NEEDS in order to work: the credentials it
// resolves from `ISecretStore` and the config knobs it binds — as
// **names and classes, never values**. Keyed by the same stable Phase 279
// `ComponentId` the manifest (Phase 280), preflight (Phase 281),
// component config (Phase 289) and capability descriptor (Phase 282)
// correlate against, so "what does this component require" joins to
// "what did this deployment compose" by id.
//
// Today a missing credential surfaces as a cryptic mid-request failure
// in whichever component first reaches for it — a null-reference, a 401
// from a provider, an empty connection string three layers down. The
// requirements manifest closes that structurally: the Server-tier
// presence preflight (`ComponentRequirementsPreflight`, in
// `ToolUp.Platform.Server`) checks every REQUIRED secret resolves and
// every REQUIRED knob binds *before* the app serves, and aggregates the
// misses into one typed startup report naming the `ComponentId` and the
// requirement.
//
// **Names and classes only — a requirement never carries a value.**
// There is deliberately no field on any type here that could hold a
// secret's value, so no report, log line, or introspection surface built
// from these types can leak one. The presence probe tests existence and
// discards what it read; `SecretRequirement.describe` renders key +
// class. That property is asserted by the test pack, not merely
// intended.
//
// **Derived where possible, declared only at the seam that cannot be
// derived (432.B).** A component that already binds config by id through
// its Phase 289 `ComponentConfig` section needs to declare nothing —
// `ComponentRequirementsPreflight.fromComponentConfig` derives its knob
// requirements from the section itself, so the requirement set cannot
// drift from what the component actually binds. Secrets have no such
// registration to read (a companion resolves them from `ISecretStore` in
// its own `create`), so those are the residual DECLARED half: a companion
// declares them alongside its Phase 282 `CompanionCapability`, keyed by
// the same `ComponentId`, in a `RequirementsSignature`.
//
// **Why a sidecar map rather than a fourth field on
// `CompanionCapability`.** Adding a field to an F# record changes its
// constructor signature — a breaking change for every consumer that
// constructs one, and a removal under the public-API baseline gate. This
// is the same call Phase 585 made for `ClassifiedCompositionRule`: a
// separate shape, keyed by the same id, that reads as one descriptor
// when a tool joins them. `CapabilitySignature` and
// `RequirementsSignature` are parallel `Map<ComponentId, _>` projections
// of the same component set.
//
// **Zero cost when undeclared (GP 11 / GP 13).** The identity value is
// the EMPTY requirement set: a component that declares nothing requires
// nothing, an empty signature makes both preflight validators no-ops
// (they are not even registered), and a bare
// `ServerApp.empty |> ServerApp.run` deployment is byte-for-byte what it
// was. Absence is the no-op; declaration is the opt-in.
//
// **Generic substrate (GP 1).** No vendor or domain vocabulary — only
// `ComponentId`, small closed DUs, and strings. Fable-safe (no BCL
// surface beyond `System.String`), so a scaffolding / authoring tool on
// either tier reads the same shapes.

/// The kind of credential a `SecretRequirement` names. A closed,
/// vendor-neutral classification an operator can act on ("this component
/// wants an API key" vs "…a signing key") without the SDK knowing which
/// provider issued it. `OpaqueSecret` is the honest fallback for a
/// credential that fits none of the named classes — never a default
/// chosen to avoid thinking about it.
type SecretClass =
    /// A provider / service API key or token (`ANTHROPIC_API_KEY`, an SMS
    /// gateway token, a webhook delivery token).
    | ApiKeySecret
    /// A symmetric or private signing key — anything whose compromise
    /// forges identity or authenticity (HMAC keys, JWT signing material,
    /// artefact-signing keys).
    | SigningKeySecret
    /// A database / broker / storage connection string, which typically
    /// embeds its own credentials.
    | ConnectionStringSecret
    /// A certificate or key-pair blob (PEM / PFX material).
    | CertificateSecret
    /// A shared secret used to VERIFY an inbound webhook's signature (the
    /// counterpart the sender signs with).
    | WebhookSigningSecret
    /// A credential that fits none of the named classes.
    | OpaqueSecret

/// Whether a requirement's absence is fatal or merely degrading.
/// `RequiredRequirement` fails preflight (the component cannot work
/// without it); `OptionalRequirement` warns and continues (the component
/// degrades — an unconfigured optional sink, a fallback provider).
type RequirementNecessity =
    /// Absence aborts startup — the component cannot function.
    | RequiredRequirement
    /// Absence logs a preflight warning and continues — the component
    /// degrades rather than fails.
    | OptionalRequirement

/// One credential a component resolves from `ISecretStore`, named by its
/// scope + key and classified — **never its value**. There is no field
/// here a value could occupy; that is the point.
type SecretRequirement = {
    /// The `ISecretStore` scope the key is resolved under — the reserved
    /// `_platform` scope (`ComponentRequirements.PlatformScope`) for
    /// deployment-level credentials, or a scope-derived container name.
    Scope: string
    /// The key the secret is stored under. A NAME, resolved by the
    /// presence probe; the value it resolves to is never retained.
    Key: string
    /// What kind of credential this is.
    Class: SecretClass
    /// Whether absence is fatal (`RequiredRequirement`) or degrading.
    Necessity: RequirementNecessity
    /// One line saying what the component does with it — the sentence an
    /// operator reads in the preflight report to know what to go and
    /// provision.
    Purpose: string
}

/// The declared type of a config knob — what an authoring tool renders
/// and what a validator would parse the bound value as. Best-effort when
/// derived from a Phase 289 section's declared default (a section is a
/// `Map<string,string>`, so the type is inferred from the default's
/// shape); exact when a component declares it.
type ConfigKnobType =
    | StringKnob
    | IntKnob
    | BoolKnob
    | UriKnob
    /// A duration, in whatever textual form the component parses.
    | DurationKnob
    /// A closed set of admissible values (the dropdown case).
    | EnumKnob of values: string list
    /// Type unknown — derived from a knob with no default to infer from,
    /// or declared as free-form. Never a validation claim.
    | OpaqueKnob

/// Whether a config knob carries a default or the deployment must supply
/// it — the "default-or-required" axis. A knob with a default always
/// binds, so it is never a preflight failure; a knob with none must be
/// bound by the deployment before the app serves.
type ConfigDefault =
    /// No default — the deployment MUST supply a value. Absence is a
    /// preflight error.
    | NoKnobDefault
    /// A declared default the knob falls back to. Absence is fine.
    | KnobDefault of value: string

/// One config knob a component binds: its path (the Phase 289
/// `ComponentConfig` key, addressable as
/// `TOOLUP_COMPONENT__<id>__<key>`), its type, and whether it defaults or
/// is required.
type ConfigRequirement = {
    /// The knob path — the key within the component's own config section.
    Path: string
    KnobType: ConfigKnobType
    /// Whether the knob defaults or the deployment must supply it.
    Default: ConfigDefault
    /// One line saying what the knob controls.
    Purpose: string
}

/// Everything one composed component requires: the credentials it
/// resolves and the knobs it binds, keyed by its stable `ComponentId`.
/// The empty set (`ComponentRequirements.none`) is the identity a
/// component that declares nothing contributes.
type ComponentRequirements = {
    /// The component these requirements belong to.
    Component: ComponentId
    Secrets: SecretRequirement list
    Config: ConfigRequirement list
}

/// Every composed component's requirements, keyed by stable
/// `ComponentId` — the parallel of Phase 282's `CapabilitySignature` over
/// the same id space. An ABSENT id requires nothing (the identity), so an
/// undeclared component never adds a preflight check (GP 11).
type RequirementsSignature = Map<ComponentId, ComponentRequirements>

[<RequireQualifiedAccess>]
module SecretClass =

    /// A stable, human-readable token for a secret class — for preflight
    /// reports, manifests, and authoring tools. Never positional; matches
    /// the DU case.
    let toWireString (cls: SecretClass) : string =
        match cls with
        | ApiKeySecret -> "api-key"
        | SigningKeySecret -> "signing-key"
        | ConnectionStringSecret -> "connection-string"
        | CertificateSecret -> "certificate"
        | WebhookSigningSecret -> "webhook-signing-secret"
        | OpaqueSecret -> "opaque-secret"

[<RequireQualifiedAccess>]
module ConfigKnobType =

    /// A stable, human-readable token for a knob type. An `EnumKnob`
    /// renders its admissible values, which are declared vocabulary, not
    /// deployment data.
    let toWireString (knob: ConfigKnobType) : string =
        match knob with
        | StringKnob -> "string"
        | IntKnob -> "int"
        | BoolKnob -> "bool"
        | UriKnob -> "uri"
        | DurationKnob -> "duration"
        | EnumKnob values -> "enum(" + String.concat "|" values + ")"
        | OpaqueKnob -> "opaque"

[<RequireQualifiedAccess>]
module SecretRequirement =

    /// A required credential: absence fails preflight.
    let required (scope: string) (key: string) (cls: SecretClass) (purpose: string) : SecretRequirement = {
        Scope = scope
        Key = key
        Class = cls
        Necessity = RequiredRequirement
        Purpose = purpose
    }

    /// An optional credential: absence warns at preflight and the
    /// component degrades.
    let optional (scope: string) (key: string) (cls: SecretClass) (purpose: string) : SecretRequirement = {
        Scope = scope
        Key = key
        Class = cls
        Necessity = OptionalRequirement
        Purpose = purpose
    }

    /// Whether absence of this credential is fatal.
    let isRequired (req: SecretRequirement) : bool = req.Necessity = RequiredRequirement

    /// Render a requirement as `scope/key (class)` — **name and class
    /// only**. Every report path renders through this, so no report can
    /// carry a value: there is none on the record to render.
    let describe (req: SecretRequirement) : string =
        req.Scope + "/" + req.Key + " (" + SecretClass.toWireString req.Class + ")"

[<RequireQualifiedAccess>]
module ConfigRequirement =

    /// A knob the deployment must supply — no default, so absence fails
    /// preflight.
    let required (path: string) (knobType: ConfigKnobType) (purpose: string) : ConfigRequirement = {
        Path = path
        KnobType = knobType
        Default = NoKnobDefault
        Purpose = purpose
    }

    /// A knob with a declared default — it always binds, so it is never a
    /// preflight failure.
    let defaulted
        (path: string)
        (knobType: ConfigKnobType)
        (defaultValue: string)
        (purpose: string)
        : ConfigRequirement =
        {
            Path = path
            KnobType = knobType
            Default = KnobDefault defaultValue
            Purpose = purpose
        }

    /// Whether the deployment must supply this knob (no declared
    /// default).
    let isRequired (req: ConfigRequirement) : bool = req.Default = NoKnobDefault

    /// Render a requirement as `path (type)` — name and type only. A
    /// knob's DEFAULT is declared vocabulary (it lives in the component's
    /// own source), not deployment data, but it is still omitted here:
    /// the report's job is to name what is missing, and a default value
    /// in a startup log is noise at best.
    let describe (req: ConfigRequirement) : string =
        req.Path + " (" + ConfigKnobType.toWireString req.KnobType + ")"

/// Construction, folding, and lookup over component requirement sets +
/// the `RequirementsSignature` that keys them by `ComponentId`.
module ComponentRequirements =

    /// The reserved `ISecretStore` scope for deployment-level (rather
    /// than per-user / per-team) credentials — the scope essentially every
    /// component-level secret requirement resolves under. Mirrors the
    /// `ISecretStore` scope contract.
    [<Literal>]
    let PlatformScope = "_platform"

    /// The identity: a component that requires nothing. An UNDECLARED
    /// component contributes this, so an empty signature yields no
    /// preflight checks and a pre-432 deployment is byte-for-byte
    /// unchanged (GP 11).
    let none (componentId: ComponentId) : ComponentRequirements = {
        Component = componentId
        Secrets = []
        Config = []
    }

    /// A requirement set with the given secrets + knobs.
    let create
        (componentId: ComponentId)
        (secrets: SecretRequirement list)
        (config: ConfigRequirement list)
        : ComponentRequirements =
        {
            Component = componentId
            Secrets = secrets
            Config = config
        }

    /// Append a secret requirement (fluent declaration helper).
    let withSecret (req: SecretRequirement) (reqs: ComponentRequirements) : ComponentRequirements = {
        reqs with
            Secrets = reqs.Secrets @ [ req ]
    }

    /// Append a config requirement (fluent declaration helper).
    let withConfig (req: ConfigRequirement) (reqs: ComponentRequirements) : ComponentRequirements = {
        reqs with
            Config = reqs.Config @ [ req ]
    }

    /// Whether this component requires nothing at all — the identity.
    let isEmpty (reqs: ComponentRequirements) : bool =
        List.isEmpty reqs.Secrets && List.isEmpty reqs.Config

    /// The credentials whose absence fails preflight.
    let requiredSecrets (reqs: ComponentRequirements) : SecretRequirement list =
        reqs.Secrets |> List.filter SecretRequirement.isRequired

    /// The knobs the deployment must supply (no declared default).
    let requiredConfig (reqs: ComponentRequirements) : ConfigRequirement list =
        reqs.Config |> List.filter ConfigRequirement.isRequired

    /// Merge two requirement sets for the SAME component: DECLARED (the
    /// second argument) wins over DERIVED (the first) on a colliding
    /// secret key or knob path, because a declaration carries the real
    /// class / type / purpose that derivation could only infer. Everything
    /// non-colliding is kept, derived-first so the order is stable.
    ///
    /// Merging sets for two different components keeps the FIRST
    /// argument's id — callers key by id (`addTo` / `mergeInto`), so this
    /// case does not arise in the signature paths.
    let merge (derived: ComponentRequirements) (declared: ComponentRequirements) : ComponentRequirements =
        let declaredKeys =
            declared.Secrets |> List.map (fun s -> s.Scope, s.Key) |> Set.ofList

        let declaredPaths = declared.Config |> List.map _.Path |> Set.ofList

        {
            Component = derived.Component
            Secrets =
                (derived.Secrets
                 |> List.filter (fun s -> not (declaredKeys.Contains((s.Scope, s.Key)))))
                @ declared.Secrets
            Config =
                (derived.Config |> List.filter (fun c -> not (declaredPaths.Contains c.Path)))
                @ declared.Config
        }

    /// The empty signature — no component requires anything. Both
    /// preflight validators degrade to no-ops against it (GP 13).
    let emptySignature: RequirementsSignature = Map.empty

    /// Build a signature from a requirement list, merging any two entries
    /// that share a `ComponentId` (later entries are the declared half).
    let signatureOf (entries: ComponentRequirements seq) : RequirementsSignature =
        entries
        |> Seq.fold
            (fun acc reqs ->
                match Map.tryFind reqs.Component acc with
                | Some existing -> Map.add reqs.Component (merge existing reqs) acc
                | None -> Map.add reqs.Component reqs acc)
            Map.empty

    /// Add / merge one component's requirements into a signature.
    let addTo (reqs: ComponentRequirements) (signature: RequirementsSignature) : RequirementsSignature =
        match Map.tryFind reqs.Component signature with
        | Some existing -> Map.add reqs.Component (merge existing reqs) signature
        | None -> Map.add reqs.Component reqs signature

    /// Merge a whole declared signature onto a derived one — the 432.B
    /// fold: derivation first, declaration on top.
    let mergeSignature (derived: RequirementsSignature) (declared: RequirementsSignature) : RequirementsSignature =
        declared
        |> Map.toSeq
        |> Seq.map snd
        |> Seq.fold (fun acc reqs -> addTo reqs acc) derived

    /// Look a component's requirements up, falling back to the identity
    /// (`none`) for an undeclared component — so an absent id never adds a
    /// check (GP 11).
    let resolve (signature: RequirementsSignature) (componentId: ComponentId) : ComponentRequirements =
        signature |> Map.tryFind componentId |> Option.defaultValue (none componentId)

    /// Every declared requirement set in the signature, ordered by
    /// `ComponentId` so reports are deterministic.
    let all (signature: RequirementsSignature) : ComponentRequirements list =
        signature
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun r -> ComponentId.value r.Component)

    /// Whether any component in the signature declares a secret
    /// requirement — the gate on registering the secret-presence probe at
    /// all (GP 13).
    let anySecrets (signature: RequirementsSignature) : bool =
        signature |> Map.exists (fun _ reqs -> not (List.isEmpty reqs.Secrets))

    /// Whether any component in the signature declares a config
    /// requirement the deployment must supply — the gate on registering
    /// the knob-binding check at all (GP 13).
    let anyRequiredConfig (signature: RequirementsSignature) : bool =
        signature
        |> Map.exists (fun _ reqs -> reqs.Config |> List.exists ConfigRequirement.isRequired)