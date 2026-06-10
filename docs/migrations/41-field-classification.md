# Migration — Phase 41: `IFieldClassification` data-classification substrate

**Status:** new opt-in substrate in `ToolUp.Platform.{Core,Server}`. No
consumer is *required* to act — a deployment that registers no
classifications behaves exactly as before (GP 11). This doc is the "how
to switch it on" guide.

## What changes

New types + interface, additive only:
- `Core`: `ClassificationLevel` (Public / Confidential / Financial /
  Regulatory / Pii / Spi), `FieldClassification`, `FieldHit`,
  `FieldClassification.attach<'Entity>`.
- `Server`: `IFieldClassifier` + `DefaultFieldClassifier`,
  `ClassificationGate` (`ClassificationDecision` / `ClassificationPolicy`
  / `defaultPolicy` / `redactFields` / `recordWrites`).
- Two new audit cases (`ClassifiedFieldRead` / `ClassifiedFieldWritten`)
  under `_platform.classification`.

## Diff to apply (opt-in)

Declare classifications in module code and build a classifier:

```fsharp
open ToolUp.Platform

let classifications =
    FieldClassification.attach<Customer> [ "Email", Pii; "RevenueUsd", Financial ]
    @ [ FieldClassification.create "Customer" "Email" Pii
        |> FieldClassification.withAuditOnRead
        |> FieldClassification.withEncryptionKey "pii-kek" ]

let classifier = DefaultFieldClassifier.create classifications
```

Gate a read path (redacts fields the caller may not see, emits audit):

```fsharp
let! safe =
    ClassificationGate.redactFields
        classifier ClassificationGate.defaultPolicy audit ctx "Customer" fieldMap
```

Grant a caller the `PiiReader` capability through the existing RBAC map:
`ModulePermissions = Map [ "_classification.Pii", [ Read ] ]` (or
`PlatformAdmin`, which satisfies every level).

## Verification steps

- `dotnet build` clean.
- A `Customer.Email : Pii` field is redacted to `[redacted]` for a
  non-`PiiReader`, intact for a `PiiReader` / `PlatformAdmin`.
- A `ClassifiedFieldRead` audit row appears under `_platform.classification`
  for `AuditOnRead` fields, carrying entity + field-path + level + caller +
  redacted flag — never the value.
- `classifier.LookupBySubject subjectId` returns the `Pii`/`Spi` fields
  (the DSAR Article 15 input).

## Rollback

Register no classifications (or remove the `DefaultFieldClassifier` /
gate calls). No persisted state.

## Deferred follow-ups

- Config-store JSON hydration (`_platform/classification/{entityName}.json`)
  over the same registry.
- Mounting the gate as a Giraffe middleware wrapper over
  `makePermissionGuardedApi` (today the gate is a composable function).
- The Phase 22 entity-store decorator consuming
  `DefaultFieldClassifier.encryptionKeyFor` for per-classification KEKs.
