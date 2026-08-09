// SPDX-License-Identifier: Apache-2.0
//
// A second, deliberately minimal emitter for the federation-seam
// conformance corpus — written against FEDERATION_WIRE.md alone, in a
// different language and runtime from the reference implementation.
//
// Its job is triangulation, not coverage. One emitter cannot tell its
// own accidents from the protocol: whatever it does becomes "the format"
// simply because it is the only thing producing bytes. So this script
// takes the same reference VALUES, applies the ordering, encoding and
// stamping rules as the specification states them, and compares its
// output byte-for-byte against the committed fixtures. A divergence is
// a specification bug by definition — either the spec failed to state a
// rule, or it stated one the reference emitter does not follow.
//
// Zero dependencies; Node's own crypto and fs only.
//
//   node emit.mjs            # check against the committed corpus
//   node emit.mjs --write    # rewrite the round-trip documents
//
// Scope, stated honestly per family:
//
//   peer-surface / aggregate-surface — full independent derivation: list
//     ordering, the trust-posture floor and its `mixed:` marker, the
//     unanimity rule for vocabulary pins, canonical encoding, and the
//     SHA-256 stamp.
//   pinned-exchange (accept vector) — the projection rules: facet names
//     sorted, booleans lowercased, served contracts reduced to id +
//     versions.
//   attestation — the length-prefixed signing input and its digest,
//     which is a different encoding from the record's JSON and the one
//     an independent signer is most likely to get wrong.
//   contract-invocation — canonical encoding, including documents that
//     ride as embedded strings and the error-code mapping.
//   host-envelope — canonical encoding and stamping only. Its derivation
//     is a host's own business; the wire contract is the document shape.
//   model-execution — canonical encoding, and the two rules this family
//     is the only carrier of: real numbers with a mandatory fractional
//     digit, and map members whose keys are sorted ordinally. Both are
//     places a JS emitter diverges by doing the obvious thing, which is
//     exactly why they are triangulated here rather than trusted.
//
// Reject vectors are not emitted: they are documents an implementation
// must REFUSE, so reproducing their bytes proves nothing. Certify against
// them by feeding each to your own reader and requiring the refusal class
// the manifest names.

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));

// ── canonical encoding ───────────────────────────────────────────────

/** A JSON object whose key order is the declaration order of the shape. */
const obj = (pairs) => "{" + pairs.map(([k, v]) => JSON.stringify(k) + ":" + v).join(",") + "}";
const arr = (items) => "[" + items.join(",") + "]";
const str = (value) => JSON.stringify(value);
const num = (value) => String(value);
const bool = (value) => (value ? "true" : "false");
/** An optional field: absent is `null`, present is the value itself. */
const opt = (value, encode) => (value === null || value === undefined ? "null" : encode(value));
/** A union case with no payload. */
const caseOnly = (name) => JSON.stringify(name);
/** A union case with one payload. */
const caseOf = (name, payload) => obj([[name, payload]]);

const sha256Hex = (text) => createHash("sha256").update(Buffer.from(text, "utf8")).digest("hex");

// ── shared shapes ────────────────────────────────────────────────────

const version = (v) =>
  obj([
    ["Major", num(v.major)],
    ["Minor", num(v.minor)],
  ]);

const compareVersion = (a, b) => a.major - b.major || a.minor - b.minor;
const ordinal = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

const servedContract = (c) =>
  obj([
    ["ContractId", str(c.contractId)],
    ["Versions", arr([...c.versions].sort(compareVersion).map(version))],
    ["Routines", arr([...c.routines].sort(ordinal).map(str))],
  ]);

const consumedContract = (c) =>
  obj([
    ["ContractId", str(c.contractId)],
    ["Versions", arr([...c.versions].sort(compareVersion).map(version))],
    ["CounterpartRole", str(c.counterpartRole)],
  ]);

const trustPosture = (p) =>
  obj([
    ["AuthProfile", str(p.authProfile)],
    ["AudienceBound", bool(p.audienceBound)],
    ["DelegationVerification", str(p.delegationVerification)],
    ["ReplayStance", str(p.replayStance)],
    ["TransportSecurity", str(p.transportSecurity)],
  ]);

const budgets = (b) =>
  obj([
    ["CascadeGuard", str(b.cascadeGuard)],
    ["LongRunningEnabled", bool(b.longRunningEnabled)],
  ]);

// Phase 642 — the data-visibility authority levels, weakest first. The
// ORDER is the specification's, not an accident of declaration: the
// aggregate floor below is a minimum over it, and an emitter that sorted
// these alphabetically would put `Full` below `ViewOnly`.
const authorityLevels = ["AggregatesOnly", "ViewOnly", "Full"];

/**
 * The fail-closed read of a surface's declared level: an absent member,
 * an empty value, or a level this reader does not know all read as the
 * narrowest. Silence is not a grant, and a word a reader cannot enforce
 * is not one either.
 */
const dataVisibility = (s) => (authorityLevels.includes(s.dataVisibility) ? s.dataVisibility : "AggregatesOnly");

const vocabularyPin = (p) =>
  obj([
    ["PackId", str(p.packId)],
    ["Version", version(p.version)],
    ["Hash", str(p.hash)],
  ]);

const comparePin = (a, b) =>
  ordinal(a.packId, b.packId) || a.version.major - b.version.major || a.version.minor - b.version.minor;

const peerSurface = (s) =>
  obj([
    ["Enabled", bool(s.enabled)],
    ["LocalPeerId", opt(s.localPeerId, str)],
    [
      "Serves",
      obj([
        ["Contracts", arr([...s.serves.contracts].sort((a, b) => ordinal(a.contractId, b.contractId)).map(servedContract))],
        ["Endpoints", arr(s.serves.endpoints.map(str))],
      ]),
    ],
    ["Consumes", arr([...s.consumes].sort((a, b) => ordinal(a.contractId, b.contractId)).map(consumedContract))],
    ["TrustPosture", opt(s.trustPosture, trustPosture)],
    ["Budgets", opt(s.budgets, budgets)],
    ["PinnedVocabulary", arr([...s.pinnedVocabulary].sort(comparePin).map(vocabularyPin))],
    ["DataVisibility", str(dataVisibility(s))],
  ]);

/** The export envelope: format version + a stamp over the surface. */
const peerSurfaceExport = (s) => {
  const surface = peerSurface(s);

  return obj([
    ["FormatVersion", num(1)],
    ["SurfaceHash", str(sha256Hex(surface))],
    ["Surface", surface],
  ]);
};

// ── the endpoint set the v1 wire face mounts ─────────────────────────

const endpoints = [
  "POST /peer/v1/{contractId}",
  "GET /peer/v1/capabilities",
  "GET /peer/v1/capabilities/profile",
  "GET /peer/v1/{contractId}/jobs/{jobId}",
];

// ── reference values ─────────────────────────────────────────────────

const v1 = { major: 1, minor: 0 };
const v11 = { major: 1, minor: 1 };

const referencePosture = (transportSecurity) => ({
  authProfile: "jwt-hs256-bearer",
  audienceBound: true,
  delegationVerification: "per-peer-trust-anchor",
  replayStance: "freshness-window",
  transportSecurity,
});

const cascadeGuard = "hop-budget-decrement-with-route-loop-detection";

// Contracts deliberately declared out of order — the emitter, not the
// author, is responsible for the canonical ordering.
const instanceSurface = {
  enabled: true,
  localPeerId: "seller-ssp",
  serves: {
    contracts: [
      {
        contractId: "example.orders",
        versions: [v11, v1],
        routines: ["_platform.peer.example.orders.ReconcileLedger"],
      },
      { contractId: "example.catalogue", versions: [v1], routines: [] },
    ],
    endpoints,
  },
  consumes: [{ contractId: "example.directory", versions: [v1], counterpartRole: "hub" }],
  trustPosture: referencePosture("deployment-managed"),
  budgets: { cascadeGuard, longRunningEnabled: true },
  pinnedVocabulary: [],
  // Declares nothing, so the member is present at the fail-closed
  // default rather than omitted — an omitted member and a declared
  // narrowest level mean the same thing to a READER, and only one of
  // them is a document a stamp can be reproduced from.
  dataVisibility: "AggregatesOnly",
};

// Phase 642 — the same deployment with a declared grant. Separate from
// the instance surface so an emitter that hard-coded the default is
// caught: it would reproduce the instance vector exactly and this one
// not at all.
const authoritySurface = { ...instanceSurface, dataVisibility: "ViewOnly" };

const emptySurface = {
  enabled: false,
  localPeerId: null,
  serves: { contracts: [], endpoints: [] },
  consumes: [],
  trustPosture: null,
  budgets: null,
  pinnedVocabulary: [],
  dataVisibility: "AggregatesOnly",
};

const sharedPin = {
  packId: "example.retail",
  version: { major: 2, minor: 0 },
  hash: "3f786850e387550fdab836ed7e6dc881de23001b",
};

const divergentPin = {
  packId: "example.logistics",
  version: { major: 1, minor: 0 },
  hash: "89e6c98d92887913cadf06b2adb97f26cde4849b",
};

const members = [
  {
    peerId: "member-north",
    surface: {
      enabled: true,
      localPeerId: "member-north",
      serves: {
        contracts: [
          {
            contractId: "example.orders",
            versions: [v1, v11],
            routines: ["_platform.peer.example.orders.ReconcileLedger"],
          },
        ],
        endpoints,
      },
      consumes: [],
      trustPosture: referencePosture("deployment-managed"),
      budgets: { cascadeGuard, longRunningEnabled: true },
      pinnedVocabulary: [sharedPin, divergentPin],
      dataVisibility: "AggregatesOnly",
    },
  },
  {
    peerId: "member-south",
    surface: {
      enabled: true,
      localPeerId: "member-south",
      serves: {
        contracts: [{ contractId: "example.catalogue", versions: [v1], routines: [] }],
        endpoints,
      },
      consumes: [],
      trustPosture: referencePosture("tls-required"),
      budgets: { cascadeGuard, longRunningEnabled: true },
      pinnedVocabulary: [sharedPin],
      dataVisibility: "AggregatesOnly",
    },
  },
  {
    peerId: "member-internal",
    surface: {
      enabled: true,
      localPeerId: "member-internal",
      serves: {
        contracts: [{ contractId: "example.settlement", versions: [v1], routines: [] }],
        endpoints,
      },
      consumes: [],
      trustPosture: referencePosture("deployment-managed"),
      budgets: { cascadeGuard, longRunningEnabled: true },
      pinnedVocabulary: [],
      dataVisibility: "AggregatesOnly",
    },
  },
];

// ── the aggregate derivation, as the specification states it ─────────

/** A string facet floors to unanimity, else to a sorted `mixed:` marker. */
const floorFacet = (values) => {
  const distinct = [...new Set(values)].sort(ordinal);
  if (distinct.length === 0) return "";
  if (distinct.length === 1) return distinct[0];
  return "mixed:" + distinct.join("|");
};

const deriveAggregate = (groupPeerId, exposure) => {
  const routes = exposure
    .map(({ contractId, owner }) => {
      const serving = members.filter((m) => m.surface.serves.contracts.some((c) => c.contractId === contractId));
      const chosen = owner ? serving.find((m) => m.peerId === owner) : serving.length === 1 ? serving[0] : undefined;
      if (!chosen) throw new Error(`exposure for '${contractId}' does not resolve to exactly one member`);
      const served = chosen.surface.serves.contracts.find((c) => c.contractId === contractId);
      return { contractId, versions: served.versions, owner: chosen };
    })
    .sort((a, b) => ordinal(a.contractId, b.contractId));

  const exposing = [...new Set(routes.map((r) => r.owner.peerId))].sort(ordinal).map((id) => members.find((m) => m.peerId === id));

  // The gateway edge participates in the floor: the group's face is the
  // weaker of what the edge enforces and what stands behind it.
  const edge = referencePosture("deployment-managed");
  const postures = [edge, ...exposing.map((m) => m.surface.trustPosture)];

  const servedAnywhereInGroup = new Set(members.flatMap((m) => m.surface.serves.contracts.map((c) => c.contractId)));

  // A pack carries only where EVERY exposing member pins it identically.
  const pinKey = (p) => `${p.packId} ${p.version.major}.${p.version.minor} ${p.hash}`;

  const pins = [...new Set(exposing.flatMap((m) => m.surface.pinnedVocabulary.map((p) => p.packId)))]
    .map((packId) => exposing.map((m) => m.surface.pinnedVocabulary.find((p) => p.packId === packId)))
    .filter((held) => held.every(Boolean) && new Set(held.map(pinKey)).size === 1)
    .map((held) => held[0]);

  return {
    enabled: true,
    localPeerId: groupPeerId,
    serves: {
      contracts: routes.map((r) => ({ contractId: r.contractId, versions: r.versions, routines: [] })),
      endpoints,
    },
    consumes: exposing
      .flatMap((m) => m.surface.consumes)
      .filter((c) => !servedAnywhereInGroup.has(c.contractId)),
    trustPosture: {
      authProfile: floorFacet(postures.map((p) => p.authProfile)),
      audienceBound: postures.every((p) => p.audienceBound),
      delegationVerification: floorFacet(postures.map((p) => p.delegationVerification)),
      replayStance: floorFacet(postures.map((p) => p.replayStance)),
      transportSecurity: floorFacet(postures.map((p) => p.transportSecurity)),
    },
    budgets: {
      cascadeGuard: floorFacet([cascadeGuard, ...exposing.map((m) => m.surface.budgets.cascadeGuard)]),
      // A BOOLEAN facet conjoins over the exposing members: the group
      // dispatches long-running work only where every member it fronts
      // does. (This emitter carried a hardcoded `false` until Phase 638,
      // which is what the group and solo fixtures caught — the gateway
      // used to forward only the invoke leg, and the rule moved when
      // handle translation landed. A divergence between the two emitters
      // is a specification defect by definition, and this one was.)
      longRunningEnabled: exposing.every((m) => m.surface.budgets.longRunningEnabled),
    },
    pinnedVocabulary: pins,
    // Phase 642 — the authority floor: the NARROWEST level the gateway
    // edge and every exposing member grants. A minimum rather than a
    // `mixed:` marker, because the levels are ordered and a floor over an
    // ordered set is a value a counterparty can act on; `mixed:` exists
    // only where a divergence has no computable floor.
    dataVisibility: authorityLevels[
      Math.min(
        ...[{ dataVisibility: "AggregatesOnly" }, ...exposing.map((m) => m.surface)].map((s) =>
          authorityLevels.indexOf(dataVisibility(s)),
        ),
      )
    ],
  };
};

// ── pinned exchange ──────────────────────────────────────────────────

const pinnedSurface = (counterpartyId, source, pinnedAt, surface) => {
  const facetValue = (v) => (typeof v === "boolean" ? (v ? "true" : "false") : String(v));

  const facets = Object.entries({
    AuthProfile: surface.trustPosture.authProfile,
    AudienceBound: surface.trustPosture.audienceBound,
    DelegationVerification: surface.trustPosture.delegationVerification,
    ReplayStance: surface.trustPosture.replayStance,
    TransportSecurity: surface.trustPosture.transportSecurity,
  })
    .map(([Facet, value]) => ({ Facet, Value: facetValue(value) }))
    .sort((a, b) => ordinal(a.Facet, b.Facet));

  return obj([
    ["CounterpartyId", str(counterpartyId)],
    ["Source", str(source)],
    ["FormatVersion", num(1)],
    ["SurfaceHash", str(sha256Hex(peerSurface(surface)))],
    ["PinnedAt", str(pinnedAt)],
    [
      "Serves",
      arr(
        [...surface.serves.contracts]
          .sort((a, b) => ordinal(a.contractId, b.contractId))
          .map((c) =>
            obj([
              ["ContractId", str(c.contractId)],
              ["Versions", arr([...c.versions].sort(compareVersion).map(version))],
            ]),
          ),
      ),
    ],
    [
      "TrustFacets",
      arr(
        facets.map((f) =>
          obj([
            ["Facet", str(f.Facet)],
            ["Value", str(f.Value)],
          ]),
        ),
      ),
    ],
    // Phase 642 — normalised at PINNING time: a pin is a document the
    // consumer has already read, so the fail-closed reading happens once
    // here rather than at every later check.
    ["DataVisibility", str(dataVisibility(surface))],
  ]);
};

// ── attestation ──────────────────────────────────────────────────────

/** `{utf8ByteLength}:{value}\n` — the length prefix is what makes the
 * signing input injection-proof. */
const field = (value) => `${Buffer.byteLength(value, "utf8")}:${value}\n`;

const unixSeconds = (iso) => String(Math.floor(Date.parse(iso) / 1000));

const approvalActions = { TemplateProposed: "Proposed", TemplateReviewed: "Reviewed", TemplateApproved: "Approved", TemplateRevoked: "Revoked" };

const approvalRecordJson = (r) =>
  obj([
    ["TemplateId", str(r.templateId)],
    ["TemplateVersion", str(r.templateVersion)],
    ["ActingPeerId", str(r.actingPeerId)],
    ["CounterpartyPeerId", str(r.counterpartyPeerId)],
    ["Action", caseOnly(r.action)],
    ["IssuedAt", str(r.issuedAt)],
    ["NotBefore", str(r.notBefore)],
    ["ExpiresAt", opt(r.expiresAt, str)],
    ["Signature", str(r.signature)],
  ]);

const approvalSigningInput = (r) =>
  field("toolup.cleanroom.approval/1") +
  field(r.templateId) +
  field(r.templateVersion) +
  field(r.actingPeerId) +
  field(r.counterpartyPeerId) +
  field(approvalActions[r.action]) +
  field(unixSeconds(r.issuedAt)) +
  field(unixSeconds(r.notBefore)) +
  field(r.expiresAt ? unixSeconds(r.expiresAt) : "none");

/** The record's content address: its signing input PLUS its signature. */
const approvalRecordId = (r) =>
  createHash("sha256")
    .update(Buffer.concat([Buffer.from(approvalSigningInput(r), "utf8"), Buffer.from(r.signature, "utf8")]))
    .digest("hex");

const approval = {
  templateId: "example.cohort-report",
  templateVersion: "sha256:4a44dc15364204a80fe80e9039455cc1608281820fe2b24f1e5233ade6af1dd5",
  actingPeerId: "seller-ssp",
  counterpartyPeerId: "buyer-acme",
  action: "TemplateApproved",
  issuedAt: "2026-07-16T09:30:00+00:00",
  notBefore: "2026-07-16T09:30:00+00:00",
  expiresAt: null,
  signature: "c2lnbmF0dXJlLXBsYWNlaG9sZGVy",
};

const revocation = {
  ...approval,
  action: "TemplateRevoked",
  issuedAt: "2026-08-15T09:30:00+00:00",
  notBefore: "2026-08-15T09:30:00+00:00",
  expiresAt: "2026-10-14T09:30:00+00:00",
  signature: "cmV2b2NhdGlvbi1wbGFjZWhvbGRlcg",
};

// ── contract invocation ──────────────────────────────────────────────

const rootRequestId = "0f9a4c22-6b1e-4d3a-9d61-2f0c8b7a5e11";

const callContext = obj([
  [
    "Peer",
    obj([
      ["PeerId", str("buyer-acme")],
      ["DisplayName", str("Acme demand-side")],
    ]),
  ],
  [
    "User",
    caseOf(
      "Direct",
      obj([
        ["Subject", str("user-1874")],
        ["Issuer", str("buyer-acme")],
        ["DisplayName", str("Ada Lovelace")],
      ]),
    ),
  ],
  ["ContractVersion", version(v11)],
  ["Route", arr([str("buyer-acme")])],
  ["RootRequestId", str(rootRequestId)],
  ["ParentRequestId", "null"],
  ["HopsRemaining", num(4)],
]);

const invocationRequest = () => {
  const payload = obj([
    ["Context", callContext],
    ["Arguments", str('["order-42",{"Quantity":3}]')],
  ]);

  return obj([
    ["JsonRpc", str("2.0")],
    ["Method", str("PlaceOrder")],
    ["Params", str(payload)],
    ["Id", str(rootRequestId)],
  ]);
};

const invocationResponse = () =>
  obj([
    ["JsonRpc", str("2.0")],
    ["Result", str('{"OrderId":"order-42","Accepted":true}')],
    ["Error", "null"],
    ["Id", str(rootRequestId)],
  ]);

const failure = (code, message, data) =>
  obj([
    ["JsonRpc", str("2.0")],
    ["Result", "null"],
    [
      "Error",
      obj([
        ["Code", num(code)],
        ["Message", str(message)],
        ["Data", str(data)],
      ]),
    ],
    ["Id", str(rootRequestId)],
  ]);

const invocationErrors = () =>
  arr([
    failure(-32000, "Peer unauthorized: missing bearer token", caseOf("PeerUnauthorized", str("missing bearer token"))),
    failure(-32001, "Contract not found: example.unknown", caseOf("PeerContractNotFound", str("example.unknown"))),
    failure(-32601, "Method not found: NoSuchMethod", caseOf("PeerMethodNotFound", str("NoSuchMethod"))),
    failure(
      -32002,
      "Contract version mismatch: v1.1",
      caseOf("PeerVersionMismatch", arr([version(v11), arr([version(v1)])])),
    ),
    failure(
      -32003,
      "Peer loop detected: buyer-acme -> broker-mid -> buyer-acme",
      caseOf("PeerLoopDetected", arr([str("buyer-acme"), str("broker-mid"), str("buyer-acme")])),
    ),
    failure(-32004, "Peer hop limit exceeded", caseOnly("PeerHopLimitExceeded")),
    failure(-32006, "Peer transport error: connection reset", caseOf("PeerTransport", str("connection reset"))),
    failure(
      -32005,
      "Peer handler error: downstream ledger unavailable",
      caseOf("PeerHandler", str("downstream ledger unavailable")),
    ),
    failure(
      -32700,
      "Peer (de)serialization error: unexpected end of JSON input",
      caseOf("PeerDeserialization", str("unexpected end of JSON input")),
    ),
    failure(
      -32007,
      "Peer request too large: the receiver accepts at most 8388608 bytes of request body",
      // 64-bit integers ride as sign-prefixed strings — see the
      // canonical-encoding section of the specification.
      caseOf("PeerRequestTooLarge", str("+8388608")),
    ),
    failure(
      -32008,
      "Peer clean-room gate 'example.cohort-report' withheld this answer: it did not clear the receiver's privacy floor",
      caseOf("PeerCleanRoomWithheld", str("example.cohort-report")),
    ),
  ]);

const jobPoll = () =>
  arr([
    caseOnly("Pending"),
    caseOf("Completed", str('{"Reconciled":118}')),
    caseOf("Failed", caseOf("PeerHandler", str("ledger snapshot expired"))),
  ]);

// ── host envelope ────────────────────────────────────────────────────

const componentId = (value) => obj([["ComponentId", str(value)]]);

const hostEnvelope = () =>
  obj([
    ["EnvelopeSchemaVersion", num(1)],
    [
      "EnvelopePlatform",
      obj([
        ["Package", str("example.host")],
        ["Version", str("1.0.0.0")],
        ["Assembly", str("example.host, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")],
      ]),
    ],
    [
      "EnvelopeCapabilities",
      arr(
        [
          { kind: "module", ids: ["module:orders"] },
          { kind: "datatype", ids: ["datatype:SalesData"] },
        ].map((l) =>
          obj([
            ["LayerKind", str(l.kind)],
            ["LayerCount", num(l.ids.length)],
            ["LayerIds", arr(l.ids.map(str))],
          ]),
        ),
      ),
    ],
    [
      "EnvelopeSlots",
      arr([
        obj([
          ["OfferSlot", componentId("companion:IAuditSink")],
          ["OfferInterface", str("IAuditSink")],
          ["OfferCardinality", caseOnly("MultiImpl")],
          ["OfferState", caseOnly("FilledSlot")],
          ["OfferImpls", arr([str("archive")])],
        ]),
        obj([
          ["OfferSlot", componentId("companion:IVectorStore")],
          ["OfferInterface", str("IVectorStore")],
          ["OfferCardinality", caseOnly("SingleImpl")],
          ["OfferState", caseOnly("OpenSlot")],
          ["OfferImpls", arr([])],
        ]),
      ]),
    ],
    [
      "EnvelopeModules",
      arr([
        obj([
          ["Module", str("Orders")],
          ["Component", componentId("module:orders")],
          [
            "Provides",
            arr([
              obj([
                ["Field", str("DataTypes")],
                ["Kind", str("datatype")],
                ["Key", str("SalesData")],
                ["Label", str("Sales data")],
                ["Slot", componentId("datatype:SalesData")],
              ]),
            ]),
          ],
          [
            "Needs",
            arr([
              obj([
                ["Field", str("NeedsData")],
                ["Kind", str("substrate")],
                ["Key", str("IDataObjectStore")],
                ["Label", str("")],
                ["Slot", "null"],
              ]),
            ]),
          ],
          [
            "Opaque",
            arr([
              obj([
                ["Field", str("Routes")],
                ["Kind", str("route")],
                ["Count", num(2)],
                ["Reason", str("handler composition is opaque to reflection")],
              ]),
            ]),
          ],
          [
            "Coverage",
            arr(
              [
                ["DataTypes", "ProvidesFacet"],
                ["NeedsData", "NeedsFacet"],
              ].map(([f, facet]) =>
                obj([
                  ["Field", str(f)],
                  ["Origin", str("server")],
                  ["Facet", caseOnly(facet)],
                ]),
              ),
            ),
          ],
          ["Unclassified", arr([])],
          ["Stale", arr([])],
          ["ClientDescribed", bool(false)],
        ]),
      ]),
    ],
    [
      "EnvelopeKnobs",
      arr([
        obj([
          ["KnobName", str("PeerSubstrate")],
          ["KnobAdmissible", arr([str("EnabledPeerSubstrate"), str("NoPeerSubstrate")])],
          ["KnobResolved", str("EnabledPeerSubstrate")],
        ]),
      ]),
    ],
    [
      "EnvelopeRoutes",
      arr([
        obj([
          ["RouteKey", str("/api/orders/")],
          ["RouteOwner", str("Orders")],
          ["RouteAdmits", str("UserKind")],
          ["RouteExact", bool(false)],
        ]),
      ]),
    ],
  ]);

const hostEnvelopeStamp = () =>
  obj([
    ["StampSchemaVersion", num(1)],
    ["StampPlatformVersion", str("1.0.0.0")],
    ["StampContentHash", str(sha256Hex(hostEnvelope()))],
  ]);

// ── model execution ──────────────────────────────────────────────────
//
// The one family that carries real numbers. They are JSON numbers in
// shortest round-trip decimal form and ALWAYS carry a fractional part —
// `5.0`, never `5` — which is the divergence a JS emitter walks into
// first, because `String(5)` is `"5"`.

const real = (v) => (Number.isInteger(v) ? v.toFixed(1) : String(v));

/** A 64-bit integer: sign-prefixed decimal string (§3.1 rule 7). */
const int64 = (v) => str((v < 0 ? "" : "+") + String(v));

/** A map member: a JSON object whose keys are sorted ORDINALLY — the one
 * place member order is not the shape's declaration order. */
const mapOf = (entries, encode) =>
  obj([...entries].sort((a, b) => ordinal(a[0], b[0])).map(([k, v]) => [k, encode(v)]));

const profileVersion = 1;

const vintageRef = (v) =>
  obj([
    ["DatasetId", str(v.datasetId)],
    ["Version", num(v.version)],
  ]);

const requestEnvelope = (operation, assertedScope, body) =>
  obj([
    ["ProfileVersion", num(profileVersion)],
    ["Operation", str(operation)],
    ["AssertedScope", opt(assertedScope, str)],
    // The operation's own document rides EMBEDDED — a string whose
    // content is itself canonical JSON (§3.1 rule 12).
    ["Body", str(body)],
  ]);

const answered = (body) => caseOf("Answered", str(body));
const refused = (refusal) => caseOf("Refused", refusal);

const gateRequest = (g) =>
  obj([
    ["Name", str(g.name)],
    ["Threshold", real(g.threshold)],
    ["Direction", str(g.direction)],
  ]);

const gateVerdict = (g) =>
  obj([
    ["Name", str(g.name)],
    ["Threshold", real(g.threshold)],
    ["Direction", str(g.direction)],
    ["Observed", real(g.observed)],
    ["Passed", bool(g.passed)],
  ]);

// Gates declared out of order on purpose: the EMITTER owns the ordinal
// sort, so two modellers asking for the same gates in different orders
// produce the same document.
const submission = {
  vintage: { datasetId: "weekly-panel", version: 7 },
  specPayload: '{"link":"log","terms":["price","promo"]}',
  specHash: "sha256:1b4f0e9851971998e732078544c96b36c3d01cedf7caa332359d6f1d83567014",
  providerKind: "reference-regression",
  seed: 20260716,
  gates: [
    { name: "vif-max", threshold: 5.0, direction: "AtMost" },
    { name: "holdout-r2", threshold: 0.6, direction: "AtLeast" },
  ],
  // The submitter's own declared class. A closed vocabulary of stable
  // lowercase labels, so a caller that is not an F# deployment can emit
  // one; an absent or unrecognised value reads as "human".
  submitterClass: "agent",
};

const submissionBody = (s) =>
  obj([
    ["Vintage", vintageRef(s.vintage)],
    ["SpecPayload", str(s.specPayload)],
    // Submitter-minted and opaque: carried verbatim, never re-derived.
    ["SpecHash", str(s.specHash)],
    ["ProviderKind", str(s.providerKind)],
    ["Seed", int64(s.seed)],
    ["Gates", arr([...s.gates].sort((a, b) => ordinal(a.name, b.name)).map(gateRequest))],
    ["SubmitterClass", str(s.submitterClass)],
  ]);

const outcome = {
  compositeKeyHash: "sha256:60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752",
  specHash: submission.specHash,
  datasetVersion: "consortium-north/weekly-panel@v7",
  seed: submission.seed,
  providerId: "reference-regression",
  providerVersion: "1.4.0",
  artifactId: "artifact-8821",
  artifactContentHash: "sha256:fcde2b2edba56bf408601fb721fe9b5c338d10ee429ea04fae5511b68fbf8fb9",
  diagnostics: [
    ["holdout-r2", 0.71],
    ["aic", 812.5],
    ["vif-max", 3.25],
  ],
  gateVerdicts: [
    { name: "vif-max", threshold: 5.0, direction: "AtMost", observed: 3.25, passed: true },
    { name: "holdout-r2", threshold: 0.6, direction: "AtLeast", observed: 0.71, passed: true },
  ],
  status: "Approved",
  annotations: [["batch", "wave-3"]],
  registeredAt: "2026-07-16T10:15:00+00:00",
};

const outcomeBody = (o) =>
  obj([
    ["CompositeKeyHash", str(o.compositeKeyHash)],
    ["SpecHash", str(o.specHash)],
    ["DatasetVersion", str(o.datasetVersion)],
    ["Seed", int64(o.seed)],
    ["ProviderId", str(o.providerId)],
    ["ProviderVersion", str(o.providerVersion)],
    ["ArtifactId", str(o.artifactId)],
    ["ArtifactContentHash", str(o.artifactContentHash)],
    ["Diagnostics", mapOf(o.diagnostics, real)],
    ["GateVerdicts", arr([...o.gateVerdicts].sort((a, b) => ordinal(a.name, b.name)).map(gateVerdict))],
    ["Status", str(o.status)],
    ["Annotations", mapOf(o.annotations, str)],
    ["RegisteredAt", str(o.registeredAt)],
  ]);

const cell = (c) =>
  obj([
    ["Label", str(c.label)],
    ["Count", num(c.count)],
    ["Value", opt(c.value, real)],
  ]);

const aggregate = (a) =>
  obj([
    ["Shape", caseOnly(a.shape)],
    ["Cells", arr(a.cells.map(cell))],
  ]);

const governedDiagnostics = [
  {
    shape: "Histogram",
    cells: [
      { label: "price|promo", count: 182, value: 0.42 },
      { label: "price|seasonality", count: 182, value: 0.18 },
    ],
  },
  { shape: "Aggregate", cells: [{ label: "observed-weeks", count: 182, value: 0.97 }] },
  {
    shape: "Histogram",
    cells: [
      { label: "adstock-decay-0.3", count: 182, value: 0.55 },
      { label: "adstock-decay-0.6", count: 182, value: 0.31 },
    ],
  },
];

const modelExecutionRefusals = () =>
  arr([
    // A multi-payload union case rides as an ARRAY of its payloads in
    // declaration order (§3.1 rule 11) — the arm most implementations
    // get wrong, because the single-payload arm looks like the rule.
    refused(caseOf("ProfileVersionUnsupported", arr([num(2), num(1)]))),
    refused(caseOf("RowAccessRefused", str("ReadPage"))),
    refused(caseOf("UndeclaredDiagnostic", str("Leverage"))),
    refused(caseOf("ScopeWideningRefused", str("other-tenant"))),
    refused(caseOf("PeerUnbound", str("buyer-acme"))),
    refused(caseOf("RequestUnreadable", str("unexpected end of JSON input"))),
    // The submitter surface's own typed refusal, nested unchanged.
    refused(caseOf("SubmitterRefused", caseOf("UnknownProvider", str("reference-regression")))),
    // Phase 642 — the authority family. The first two are the ladder's
    // two rungs; the third is a disclosure withhold, which names the
    // operation and nothing else because naming the policy across a
    // federation edge would itself be the disclosure.
    refused(caseOf("AuthorityLevelExceeded", arr([str("RenderView"), str("ViewOnly"), str("AggregatesOnly")]))),
    refused(
      caseOf(
        "AuthorityNarrowingRefused",
        arr([str("RenderView"), str("ViewOnly"), str("AggregatesOnly"), str("team:north-analysts")]),
      ),
    ),
    refused(caseOf("EgressWithheld", str("Coverage"))),
  ]);

// ── run ──────────────────────────────────────────────────────────────

const documents = () => {
  const groupSurface = deriveAggregate("consortium-gateway", [
    { contractId: "example.orders", owner: null },
    { contractId: "example.catalogue", owner: "member-south" },
  ]);

  const soloSurface = deriveAggregate("solo-gateway", [{ contractId: "example.orders", owner: "member-north" }]);

  return {
    "peer-surface/instance.json": peerSurfaceExport(instanceSurface),
    "peer-surface/empty.json": peerSurfaceExport(emptySurface),
    "peer-surface/authority-declared.json": peerSurfaceExport(authoritySurface),
    "aggregate-surface/group.json": peerSurfaceExport(groupSurface),
    "aggregate-surface/solo.json": peerSurfaceExport(soloSurface),
    "pinned-exchange/pin.json": pinnedSurface(
      "seller-ssp",
      "peers/seller-ssp.surface.json",
      "2026-07-16T12:00:00+00:00",
      instanceSurface,
    ),
    "attestation/approval.json": approvalRecordJson(approval),
    "attestation/revocation.json": approvalRecordJson(revocation),
    "contract-invocation/request.json": invocationRequest(),
    "contract-invocation/response.json": invocationResponse(),
    "contract-invocation/errors.json": invocationErrors(),
    "contract-invocation/job-poll.json": jobPoll(),
    "host-envelope/envelope.json": hostEnvelope(),
    "host-envelope/stamp.json": hostEnvelopeStamp(),
    "model-execution/submission.json": requestEnvelope("SubmitFit", null, submissionBody(submission)),
    "model-execution/outcome.json": answered(outcomeBody(outcome)),
    "model-execution/diagnostics.json": arr(governedDiagnostics.map((a) => answered(aggregate(a)))),
    "model-execution/refusals.json": modelExecutionRefusals(),
  };
};

const write = process.argv.includes("--write");
const manifest = JSON.parse(readFileSync(join(here, "manifest.json"), "utf8"));
const emitted = documents();
let failures = 0;
let checked = 0;

for (const [file, document] of Object.entries(emitted)) {
  const path = join(here, file);

  if (write) {
    writeFileSync(path, document, "utf8");
    continue;
  }

  const committed = readFileSync(path, "utf8");
  checked += 1;

  if (committed !== document) {
    failures += 1;
    console.error(`FAIL ${file}`);
    console.error(`  committed: ${committed}`);
    console.error(`  emitted  : ${document}`);
  } else {
    console.log(`ok   ${file}`);
  }
}

// The digests the manifest records must also be reproducible from this
// emitter's bytes — otherwise the corpus and its own index disagree.
for (const [file, document] of Object.entries(emitted)) {
  const entry = manifest.vectors.find((v) => v.file === file);
  if (!entry) {
    failures += 1;
    console.error(`FAIL ${file} is not enumerated by the manifest`);
  } else if (!write && sha256Hex(document) !== entry.sha256) {
    failures += 1;
    console.error(`FAIL ${file} digest ${sha256Hex(document)} != manifest ${entry.sha256}`);
  }
}

// Every attestation vector's signing-input digest, recomputed from the
// record rather than read off the JSON.
for (const [id, record] of [
  ["attestation/approval", approval],
  ["attestation/revocation", revocation],
]) {
  const entry = manifest.vectors.find((v) => v.id === id);
  const recomputed = approvalRecordId(record);

  if (!write && entry.digest !== recomputed) {
    failures += 1;
    console.error(`FAIL ${id} signing-input digest ${recomputed} != manifest ${entry.digest}`);
  } else {
    console.log(`ok   ${id} signing-input digest`);
  }
}

if (write) {
  console.log(`wrote ${Object.keys(emitted).length} documents`);
} else if (failures > 0) {
  console.error(`\n${failures} divergence(s) between the two emitters — that is a specification bug.`);
  process.exit(1);
} else if (checked === 0) {
  console.error("no documents were checked");
  process.exit(1);
} else {
  console.log(`\n${checked} documents reproduced byte-identically by an independent emitter.`);
}
