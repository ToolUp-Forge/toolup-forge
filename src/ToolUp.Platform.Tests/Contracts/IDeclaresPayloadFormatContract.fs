module ToolUp.Platform.Tests.Contracts.IDeclaresPayloadFormatContract

open Expecto
open ToolUp.Platform

// ─── IDeclaresPayloadFormat contract pack (Phase 834) ─────────────
//
// Parametrised laws for any connector that declares the format of its
// `IDataSource.Query` bytes. The factory builds a fresh connector (no
// seeding, no network: the declaration is a constant of the connector,
// so nothing here touches `Connect` or `Query`); `expected` is the
// format the binding says the connector emits.
//
// The laws:
//   - the declaration is EXPLICIT — the connector implements
//     `IDeclaresPayloadFormat`, rather than reading as `Csv` because
//     `PayloadFormat.declaredBy` defaults a silent connector to `Csv`
//     (a `Csv` assertion over the default would pass vacuously);
//   - `PayloadFormat.declaredBy` answers exactly what the interface says;
//   - the declaration is stable across calls and across instances;
//   - `PayloadFormat.token` / `ofToken` round-trip it, so the value the
//     ingestor records as `content-format` reads back as the declaration;
//   - it is a format the shipped ingestor stores: `Other` is precisely
//     the case `DataIngestor` refuses at the connector boundary (pinned
//     end-to-end in `DataIngestorTests`), so a shipped connector
//     declaring it could never ingest.
//
// This pack lives in `ToolUp.Platform.Tests` beside the others and is
// source-linked into `ToolUp.DataSources.Tests`, where the cloud and
// file connectors bind it — the same arrangement as
// `TestRegistrationGuard`.

let tests (name: string) (expected: PayloadFormat) (factory: unit -> IDataSource) =

    let declaration (source: IDataSource) =
        match box source with
        | :? IDeclaresPayloadFormat as declared -> Some declared.PayloadFormat
        | _ -> None

    testList $"{name} — IDeclaresPayloadFormat contract" [

        test "declares its payload format explicitly, not by the Csv default" {
            let source = factory ()
            Expect.isSome (declaration source) $"'{source.Kind}' must implement IDeclaresPayloadFormat"
        }

        test "declares the format the binding expects" {
            Expect.equal (declaration (factory ())) (Some expected) "declared format"
        }

        test "PayloadFormat.declaredBy answers what the interface declares" {
            let source = factory ()
            Expect.equal (Some(PayloadFormat.declaredBy source)) (declaration source) "declaredBy agrees"
        }

        test "the declaration is stable across calls and instances" {
            let source = factory ()
            let first = PayloadFormat.declaredBy source

            Expect.equal (PayloadFormat.declaredBy source) first "same instance, second call"
            Expect.equal (PayloadFormat.declaredBy (factory ())) first "a fresh instance"
        }

        test "the recorded token round-trips to the declaration" {
            let format = PayloadFormat.declaredBy (factory ())
            let token = PayloadFormat.token format
            Expect.isNonEmpty token "the content-format token is non-empty"
            Expect.equal (PayloadFormat.ofToken token) format "ofToken (token f) = f"
        }

        test "declares a format the shipped ingestor stores (never Other)" {
            match PayloadFormat.declaredBy (factory ()) with
            | PayloadFormat.Other other ->
                failtest
                    $"declares Other '{other}', which DataIngestor refuses at ingestion — the connector could never ingest"
            | PayloadFormat.Csv
            | PayloadFormat.Json -> ()
        }
    ]