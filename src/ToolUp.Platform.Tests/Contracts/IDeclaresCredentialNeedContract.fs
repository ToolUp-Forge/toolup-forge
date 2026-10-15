module ToolUp.Platform.Tests.Contracts.IDeclaresCredentialNeedContract

open Expecto
open ToolUp.Platform

// ─── IDeclaresCredentialNeed contract pack (Phase 1017) ───────────
//
// Parametrised laws for any connector that declares whether it takes a
// credential. The factory builds a fresh connector (no seeding, no
// network, no secret store read: the declaration is a constant of the
// connector, so nothing here touches `Connect` or `Query`); `expected` is
// the need the binding says the connector has.
//
// The laws:
//   - the declaration is EXPLICIT — the connector implements
//     `IDeclaresCredentialNeed`, rather than reading as `Optional` because
//     `CredentialNeed.declaredBy` defaults a silent connector to `Optional`
//     (an `Optional` assertion over the default would pass vacuously);
//   - `CredentialNeed.declaredBy` answers exactly what the interface says;
//   - the declaration is stable across calls and across instances, because
//     `DataIngestor` reads it once per run and a connector whose answer
//     moved between runs would read the secret store on some and not others.
//
// What `DataIngestor` DOES with each answer is pinned end-to-end beside
// the bindings in `ToolUp.DataSources.Tests` (a NONE connector ingesting
// while the secret store throws; a REQUIRED one failing typed before
// `Connect`). This pack lives in `ToolUp.Platform.Tests` beside the others
// and is source-linked into `ToolUp.DataSources.Tests`, where the shipped
// connectors bind it — the same arrangement as
// `IDeclaresPayloadFormatContract`.

let tests (name: string) (expected: CredentialNeed) (factory: unit -> IDataSource) =

    let declaration (source: IDataSource) =
        match box source with
        | :? IDeclaresCredentialNeed as declared -> Some declared.CredentialNeed
        | _ -> None

    testList $"{name} — IDeclaresCredentialNeed contract" [

        test "declares its credential need explicitly, not by the Optional default" {
            let source = factory ()
            Expect.isSome (declaration source) $"'{source.Kind}' must implement IDeclaresCredentialNeed"
        }

        test "declares the need the binding expects" {
            Expect.equal (declaration (factory ())) (Some expected) "declared need"
        }

        test "CredentialNeed.declaredBy answers what the interface declares" {
            let source = factory ()
            Expect.equal (Some(CredentialNeed.declaredBy source)) (declaration source) "declaredBy agrees"
        }

        test "the declaration is stable across calls and instances" {
            let source = factory ()
            let first = CredentialNeed.declaredBy source

            Expect.equal (CredentialNeed.declaredBy source) first "same instance, second call"
            Expect.equal (CredentialNeed.declaredBy (factory ())) first "a fresh instance"
        }
    ]