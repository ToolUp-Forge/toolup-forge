# Phase 923 — a data-source query audit event

**What changes for a consumer:** nothing has to. Every addition is optional, and a deployment composed through
`ServerApp` records the new event without an edit. Read on if you construct a `VirtualDatasetReader` or a
`DataIngestor` yourself, or if you want a source's statement text in the trail.

## What the event is

`DataSourceQueried` is one audit row per statement the platform runs against a data source, failures and refusals
included: the source's id, the connector's kind, whether the statement was text or bound, a SHA-256 over the
statement's **text alone**, the bound parameters by name and kind, the size and hash of the answer, the rows the
platform decoded (where it decodes them), and the outcome as a class. It never carries a parameter value, and it
carries the statement's text only on the source's opt-in. Two runs of one bound statement with different values share
a hash, so a trail can count one question asked twice. The subject and the scope ride the row, as for every kind.

It is a kind of the open audit-event registry, declared by the platform: `ServerApp` registers
`DataSourceQueryAudit.family` beside the platform's own kinds, and a reader takes it back with
`DataSourceQueryAudit.tryRead`. It is not a case of the closed `AuditEvent` union, so no exhaustive `match` breaks.

## Who records it

| Site | Records | How it gets a log |
|---|---|---|
| the composed `IDataIngestor` | every statement it sends, and a refused opt-in | `ServerApp` passes the deployment's `IAuditLog`; nothing to do |
| `DataIngestor.createAudited …  audit` | the same | your call |
| `DataIngestor.create` / `createWithReaders` | nothing (as before) | they compose `NoOpAuditLog` |
| `VirtualDatasetReader(source, codec, schema, config, audit, subject)` | every `Bind` / `ReadPage` read, run or refused, attributed to `subject` | your call |
| `VirtualDatasetReader(source, codec, schema, config)` | nothing for `Bind` / `ReadPage` (as before) | `NoOpAuditLog` |
| `MaterialiseForHandoff(…, audit, …)` on either reader | its read, in the log it is handed, beside the spill row | the call's `audit` |

A connector is never asked to emit it, so a third-party connector is covered without changing. Under `NoOpAuditLog`
nothing is hashed or serialised (GP 13).

To attribute a virtual read to the person it is for, construct the reader per request with their subject:

```fsharp skip=fragment
let reader =
    VirtualDatasetReader(source, codec, declaredSchema, config, auditLog, AuditSubject.fromSubject subject)
```

## Recording statement text

Set `audit_statement_text=true` in the source's `ConnectionScope`. It is admitted only beside
`statement_policy=bound-only`, where text carries no value. Under a policy that admits raw text, the platform refuses
the statement before sending it: the virtual reader as `VirtualBindingRefusal.StatementTextAuditRefused`, the ingestor
as a failed run, each with a `Refused` row. Any value other than `true` or `false` is refused by name.

## Verification

Read a source through an audited reader and query the trail for `DataSourceQueried`: one row per read, the
`StatementHash` equal to the SHA-256 of the statement text. `VirtualDatasetTests` and `DataIngestorTests` sweep every
payload their scenarios emit for the parameter values they bound.

## Rollback

Construct the reader with four arguments, or `DataIngestor.createWithReaders`; remove `audit_statement_text`.
