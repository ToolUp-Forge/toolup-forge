# Getting started with the data platform template

`platformsdk-dataplatform` scaffolds a whole repository for "your daily report as data": an
application that takes a daily extract, writes it into fact tables, has a model write the day's
report from those facts only, and publishes the report to the readers allowed to see it. Every part
is an SDK seam you can replace; the template wires them together once, with synthetic data, so the
first run works.

## What it composes

| Concern | What the scaffold uses | Docs |
|---|---|---|
| AI, retrieval and the knowledge base | `RAGServerApp` with Claude (strictly bring-your-own-key, the key read from `ISecretStore`), the local embedder, the HNSW vector store, and the knowledge base's AI tools | [knowledge-portal](../platform/knowledge-portal.md) |
| The fact tier | `RAGServerApp.withFacts FactsCompose.withFactTier`: the fact store and the fact-table writer in one call | [facts](../platform/facts.md), [fact-publication](../platform/fact-publication.md) |
| The data module | `DailyTrade`: an `IDataSource` over uploaded deliveries, a reader that validates the whole delivery or rejects it, and two declared fact tables with their metrics and subjects; it fills the client shell's data-manager slot | [modules](../platform/modules.md) |
| The daily report | a `GroundedNarrativeDefinition` composed with `GroundedNarratives.compose` over `NarrativeGrounding.factTools`, run under the scope of the request that delivered the data | [grounded-narratives](../platform/grounded-narratives.md) |
| The report pages | `insights/latest` and `insights/{date}`, declared in front of PublicRendering's content API with `PageAudience.ScopeGated [ "report-reader" ]`, rendering the stored `NarrativeDocument` | [gated-ssr](../platform/gated-ssr.md) |
| Identity | generic OIDC (`TOOLUP_AUTH_MODE=oidc`) with interactive sign-in for the pages; forge's dev-only header identity for a local run | [gated-ssr](../platform/gated-ssr.md#readers-governed-by-the-identity-providers-groups) |

The views are forge-native: the module's own `ClientView.fs`, and the report rendered by
PublicRendering from the narrative. The scaffold depends on `ToolUp.*` packages alone.

**Why the report is trustworthy.** The model is offered only the fact tools, and its answer is
checked before publication: a figure that is not a reference to a fact the scope holds, or that
misstates one, refuses the whole report, and the refusal is logged. The scaffold's end-to-end test
proves the happy path; changing the scripted model to state a different figure turns it red with
the offending span named.

## Scaffold it

```powershell
dotnet new install .\templates\platformsdk-dataplatform
dotnet new platformsdk-dataplatform -n TradingInsights `
    --port-server 5010 --port-client-vite 8100
cd TradingInsights
pwsh ./run.ps1
```

`run.ps1` restores the tools, checks formatting, builds the solution and runs the end-to-end test:
the synthetic delivery is uploaded through the module's API, both fact tables are written, the
report is generated with every number resolving to a fact, and the gated page refuses an anonymous
reader while serving one with access. The two ports follow the PortGuard contract (see
[`templates/README.md`](../../templates/README.md#port-clash-enforcement)); `--sdk-version` pins the
`ToolUp.*` packages and `--feed` names a local folder feed for unreleased packs.

## Run it locally with the synthetic data

```powershell
pwsh ./run.ps1 -Run
```

`-Run` starts the server with `--local`, which applies the local run's settings
(`src/<AppName>-Server/LocalRun.fs`): a bootstrap team named `Trading`, a secrets master key kept
under `data/`, and the acknowledgements forge's dev-only header identity needs to start a team
deployment. Set `TOOLUP_INITIAL_PLATFORM_ADMIN` to the user id you will sign in as (for example
`analyst`); that user owns the team.

1. Start the client (`cd src/<AppName>-Client; npm install; dotnet fable watch -o output --run npx vite`)
   and open it on the Vite port as your user.
2. Add an Anthropic key in the app's AI provider settings. The key goes to the secret store; it is
   never read from configuration.
3. Upload `data/sample/daily-trade.synthetic.csv` from the "Daily trading" data manager. The result
   names the tables written and the report's outcome.
4. Read the report on the server port at `/insights/latest`. A browser page request carries no header
   identity, so with the dev identity read it with the header instead:
   `Invoke-WebRequest http://localhost:5010/insights/latest -Headers @{ 'X-User-Id' = 'analyst' }`.

## Point it at a real source

- **The data.** Replace `UploadedDeliverySource` in `src/Modules/DailyTrade/Server.fs` with a
  connector to your system: any `IDataSource`, for example one of the `ToolUp.DataSources.*`
  packages. Change the reader to your extract's columns, and declare each output as a fact table
  with its metrics and subjects; the platform checks the declarations when the server starts. Keep
  `Disclosure = FactTableDisclosure.Surfaceable` for figures the report may publish, or mark a column
  `Restricted` to keep it behind a disclosure policy.
- **The calendar.** Replace the synthetic fiscal calendar with your organisation's, and update
  `ReferenceData.FiscalCalendarSha256` in the same commit: the server refuses to start on a calendar
  whose bytes do not match.
- **The readers.** Set `TOOLUP_AUTH_MODE=oidc`, `TOOLUP_OIDC_ISSUER`, `TOOLUP_OIDC_AUDIENCE` and the
  sign-in endpoints (`OIDC_AUTHORIZATION_ENDPOINT`, `OIDC_TOKEN_ENDPOINT`, `OIDC_REDIRECT_URI`,
  `OIDC_CLIENT_SECRET` for a confidential client), and map the directory group whose members read the
  report to the `report-reader` role. For readers who must never be app users, switch the pages to a
  publication audience.
- **Storage and secrets.** Swap the two lines in `Companions.fs` for a cloud object store and a vault
  companion. The server's startup preflight names every production posture still missing; read its
  warnings before deploying.

## See also

- [`templates/README.md`](../../templates/README.md) — every template, and how each is gated.
- [`docs/platform/grounded-narratives.md`](../platform/grounded-narratives.md) — the gate, the run and
  its triggers in full.
