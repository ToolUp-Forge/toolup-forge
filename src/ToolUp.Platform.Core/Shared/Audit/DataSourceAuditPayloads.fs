// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── The data-source query audit event ─────────────────────────────────
//
// A query changes no state, so nothing audited it, and "who read what, from
// which source, and how much" was answered, if at all, by the database's own
// log under the service account's name. `DataSourceQueried` is that answer in
// the platform's own trail: one row per statement the platform runs against a
// source, failures and refusals included.
//
// It is emitted by the platform's two call sites, the data ingestor and the
// virtual dataset reader, never by a connector, so a third-party connector is
// covered without changing. It is a kind of the open audit-event registry
// declared by the platform, and never a case of the closed
// `AuditEvent` union. The subject and the scope ride the stored ROW (the
// registry's rule), so the payload does not repeat them.
//
// It carries no parameter value, ever: the statement is identified by a hash
// over its TEXT alone, so two runs of one bound statement with different
// values share a hash and count as one question asked twice, and a parameter
// is named with its kind and nothing else. A source's error message can echo
// the statement and so a value, so a failure records the error's class, never
// its message. The statement's text is recorded only on a source's explicit
// opt-in (`StatementTextAudit`), which is refused where the text may carry a
// value.

/// How the statement reached the source.
[<RequireQualifiedAccess>]
type DataSourceStatementForm =
    /// A string handed to the connector's `Query` (or its native emission):
    /// the connector interprets the text, which may carry a value rendered
    /// into it.
    | Text
    /// A bound statement run through `IBindsStatements.QueryBound`: its
    /// values travel beside the text and never in it.
    | Bound

/// A parameter of a bound statement as an audit row names it: its name and
/// the kind it was bound as, never its value.
type DataSourceQueryParameter = {
    /// The parameter's name, without the `@`.
    Name: string
    /// `DatasetDType.name` of the kind the value was bound as.
    Kind: string
}

/// How a query ended. Each case carries a CLASS, never a message: a source's
/// message can echo the statement, and with it a value.
[<RequireQualifiedAccess>]
type DataSourceQueryOutcome =
    /// The source answered, and the platform accepted the answer.
    | Succeeded
    /// The statement was sent and the query failed: at the source (the
    /// `IngestionError` case name), or in the platform's reading of the
    /// answer (`"Undecodable"`, `"SchemaDrift"`).
    | Failed of failure: string
    /// The platform refused the statement before sending it (the refusal's
    /// case name). Nothing reached the source.
    | Refused of refusal: string

/// One statement the platform ran, or refused to run, against a data
/// source. PII-free and value-free: identity, shape and cardinality only.
/// The subject the query ran for and the scope it ran in are the row's.
type DataSourceQueriedPayload = {
    /// `DataSourceConfig.Id` of the source.
    SourceId: string
    /// `IDataSource.Kind` of the connector the statement went through.
    ConnectorKind: string
    /// Whether the statement was text or bound.
    Form: DataSourceStatementForm
    /// Lowercase-hex SHA-256 over the UTF-8 bytes of the statement's text
    /// alone. For a bound statement that is the text with its `@markers`, so
    /// runs that differ only in their values share it.
    StatementHash: string
    /// The bound parameters, by name and kind, in binding order. Empty for
    /// the text form.
    Parameters: DataSourceQueryParameter list
    /// The statement's text, only when the source opted in
    /// (`StatementTextAudit.Key`); `None` otherwise.
    StatementText: string option
    /// Rows the platform decoded from the answer, where it decodes it (the
    /// virtual reader). `None` where the platform holds the payload opaquely
    /// (the ingestor), and when no answer came back.
    RowCount: int64 option
    /// The answer's size in bytes, when an answer came back.
    ByteCount: int64 option
    /// Lowercase-hex SHA-256 of the answer's bytes, when an answer came back.
    ContentHash: string option
    /// How the query ended.
    Outcome: DataSourceQueryOutcome
}

/// The platform's data-source audit events: kinds of the open registry that
/// the platform itself declares, outside the closed `AuditEvent` union.
type DataSourceAuditEvent =
    /// The platform ran a statement against a data source, or refused to.
    | DataSourceQueried of DataSourceQueriedPayload

/// The kinds the data-source audit family declares. Its codec is the
/// server's (`ToolUp.Platform.Infra.DataSourceQueryAudit.family`), because
/// the payload serialisation every audit kind shares is server-side.
[<RequireQualifiedAccess>]
module DataSourceAuditEvent =

    /// The wire kind of `DataSourceQueried`.
    [<Literal>]
    let DataSourceQueriedKind = "DataSourceQueried"

    /// Every kind the family declares, registered under the platform.
    let kinds: AuditEventKindInfo list = [
        {
            Kind = DataSourceQueriedKind
            Registrant = AuditEventRegistry.PlatformRegistrant
            Severity = Some AuditInformational
        }
    ]

    /// The bound parameters of `statement` as an audit row names them.
    let parameters (statement: BoundStatement) : DataSourceQueryParameter list =
        statement.Parameters
        |> List.map (fun p -> {
            Name = p.Name
            Kind = DatasetDType.name (BoundValue.kind p.Value)
        })

/// A source's opt-in to record its statements' text in the query audit
/// event, declared by the `audit_statement_text` key of its
/// `ConnectionScope`. Off by default. Refused for a source whose statement
/// policy admits raw text, because such text may carry a value rendered into
/// it and an audit row carries no value.
[<RequireQualifiedAccess>]
module StatementTextAudit =

    /// The connection-scope key naming the opt-in.
    [<Literal>]
    let Key = "audit_statement_text"

    /// Read the opt-in out of a `ConnectionScope`, case-insensitively. Absent,
    /// whitespace-only or `false` is `Ok false`. `true` is `Ok true` only
    /// under `statement_policy=bound-only`; under a policy admitting raw
    /// text, or an unreadable one, it is refused by name, and so is any other
    /// value.
    let read (scope: Map<string, string>) : Result<bool, string> =
        match scope.TryFind Key with
        | Some raw when not (String.IsNullOrWhiteSpace raw) ->
            match raw.Trim().ToLowerInvariant() with
            | "false" -> Ok false
            | "true" ->
                match StatementPolicy.read scope with
                | Error reason -> Error reason
                | Ok StatementPolicy.BoundOnly -> Ok true
                | Ok StatementPolicy.RawAllowed ->
                    Error
                        $"ConnectionScope key '%s{Key}=true' is refused for a source whose %s{StatementPolicy.Key} admits raw text: such text may carry a value rendered into it, and an audit row carries no value. Declare %s{StatementPolicy.Key}=%s{StatementPolicy.token StatementPolicy.BoundOnly}, or remove the key"
            | _ -> Error $"ConnectionScope key '%s{Key}' must be one of [true, false]; got '%s{raw}'"
        | _ -> Ok false