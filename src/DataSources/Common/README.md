# ToolUp.DataSources.Common

The vendor-free support layer shared by the `ToolUp.DataSources.*` `IDataSource` companions. **BCL
only** — no cloud SDK, no database driver (GP 1). A consumer never references this package directly;
it arrives transitively with whichever connector they compose.

Six connectors needed the same five things, and duplicating them six times would have meant six
places for the CSV escaping or the credential-fallback order to drift:

| Module | What it owns |
|---|---|
| `Errors` | Classifies a connector exception onto the SDK's `IngestionError` taxonomy — transport-shaped exceptions to `SourceUnreachable`, everything else to `UnexpectedFailure`. `Errors.guard` wraps a body so nothing thrown escapes an `IDataSource` method, and an already-typed `Error` passes through unreclassified. |
| `ConnectionScope` | Typed, failure-explicit reads out of the free-form `DataSourceConfig.ConnectionScope` map. `require` / `optional` / `optionalOr` / `optionalInt` / `optionalBool` / `optionalEnum`. A missing key is `SchemaMismatch` naming the key; a *present but unparseable* value is an error rather than a silent fallback, so a mistyped port cannot quietly become the default. |
| `Credentials` | The credential thunk. `resolve` prefers `DataSourceCallContext.Credential` (which the shipped `DataIngestor` pre-resolves) and otherwise reads `ISecretStore.GetSecret(ScopeId, CredentialKey)` — **on every call**, so a rotated secret takes effect without reconstructing the connector. `resolveOptional` is the same thing for connectors whose auth can legitimately fall back to an ambient provider chain. |
| `SqlIdentifier` | Identifier safety for the connectors that build catalogue queries by hand: `isSafe` / `require` / `quoteLiteral`. See below. |
| `CredentialJson` | Parses a JSON-object credential blob into a case-insensitive `key → string` map, with `tryFind` accepting several spellings of a key (vendors are not consistent about `accessKeyId` vs `aws_access_key_id`). |
| `Csv` | RFC 4180 emission — the uniform `IDataSource.Query` wire format for the family. `renderValue` / `escapeField` / `renderRow` / `toBytes`, plus `ofReader` to drain a `DbDataReader` (`System.Data.Common` is BCL, so the ADO-shaped and API-shaped connectors share it). |
| `TypeMap` | Native-type-name → coarse `ColumnType` classification: `normalise`, the `ansi` fallback table, and `classify` to compose a connector's own overrides in front of it. |

## Why one CSV format for the whole family

`IDataSource.Query` returns opaque bytes and the ingestor stores them through `IDataObjectStore`
without looking inside, so the format is the connector's to choose. Choosing the *same* one
everywhere means a module that reads one warehouse's output reads all of them.

CSV specifically: `ToolUp.Tabular` already reads it, and Athena stages result sets to S3 as CSV
natively — so for at least one connector the wire format is a re-emission rather than a translation.
Types are recovered from `GetSchema`, not from the payload.

Two details that are load-bearing rather than incidental:

- **Values render invariant-culture.** A connector running on a comma-decimal host would otherwise
  corrupt every number in the payload, silently and unrecoverably. `DateTime` renders ISO-8601
  round-trip (`"O"`), `byte[]` base64, `bool` as `true`/`false`, `NULL` and `DBNull` as the empty
  field.
- **No BOM.** A UTF-8 BOM appears as a stray character in the first header cell for every naive
  downstream parser.

## Why identifiers are validated rather than parameterised

`SqlIdentifier` exists because the eight backends across this family do not share one parameter marker
— `@p`, `:p`, `{p:String}`, `?` — and a connector whose safety depended on getting four markers right
in eighteen places is a connector waiting to be wrong.

Catalogue queries therefore interpolate schema and table names, and every interpolated identifier is
first validated against `^[A-Za-z_][A-Za-z0-9_$]{0,127}$` and **refused** as `SchemaMismatch` if it
does not match. Quoted identifiers containing spaces, dots or Unicode are refused rather than escaped
— deliberately narrower than what any backend permits. `quoteLiteral` doubles single quotes on top, as
a second and independent line of defence.

## Testing

Everything here is a total function or an in-process async, so all of it is covered by the always-on
arm of `src/ToolUp.DataSources.Tests` — no credential, no server, no network.
