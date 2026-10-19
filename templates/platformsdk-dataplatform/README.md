# MyDataPlatform

Your daily report as data. Each day's trading extract is uploaded, validated and written into
**fact tables**; a model writes the day's report **from those facts only**, and the report is refused
if any number in it is not a reference to a fact; and the report is published on **audience-gated
pages** served from the same deployment as the app.

Scaffolded by `dotnet new platformsdk-dataplatform`. The data is synthetic: an invented retailer's
trading extract (`data/sample/daily-trade.synthetic.csv`) and an illustrative fiscal calendar
(`src/MyDataPlatform-Server/reference/fiscal-calendar.csv`), each labelled as such in its first line.

## The shape

| Part | Where | What it is |
|---|---|---|
| The application | `src/MyDataPlatform-Server/Composition.fs` | A RAG app (AI, retrieval, the knowledge base) with the fact tier composed in one call (`RAGServerApp.withFacts FactsCompose.withFactTier`), ending in `ServerApp.run`. |
| Companions | `src/MyDataPlatform-Server/Companions.fs` | Claude, strictly bring-your-own-key (the key is read from the secret store, never from configuration); a local embedder and an HNSW vector store; local blob storage; the encrypted-file secret store. |
| The data module | `src/Modules/DailyTrade/` | An `IDataSource` over uploaded deliveries, a reader that validates the whole delivery or rejects it, and two declared fact tables (`trade.site`, `trade.channel`) with their metrics and subjects. It fills the client shell's data-manager slot and publishes what it ingests. |
| Reference data | `src/MyDataPlatform-Server/ReferenceData.fs` | The fiscal calendar every fact's period is labelled with, verified against a pinned hash before the server starts. |
| The grounded report | `src/MyDataPlatform-Server/Narratives.fs` | The daily report as a grounded narrative run over the fact tools. A delivery triggers it under the scope the platform resolved for that request, so it reads exactly the facts just written. |
| The report pages | `src/MyDataPlatform-Server/PublishedPages.fs` | `insights/latest` and `insights/{date}`, gated to the `report-reader` role and read from the reader's own scope. |
| Identity | `src/MyDataPlatform-Server/Program.fs` | Generic OIDC when `TOOLUP_AUTH_MODE=oidc` (with interactive sign-in for the pages, and role and group claims mapped by the provider); forge's dev-only header identity otherwise. |
| The test | `tests/MyDataPlatform.Tests/` | One end-to-end test: the synthetic delivery is ingested, both tables are written, the report is generated with every number resolving to a fact, and the gated page refuses an anonymous reader. |

**Views are forge-native.** The module's view is its own `ClientView.fs`, and the report pages are
forge's `NarrativeDocument` rendered through PublicRendering, so the scaffold depends on the
`ToolUp.*` packages alone.

## Run it

```powershell
pwsh ./run.ps1          # format check, build, the end-to-end test
pwsh ./run.ps1 -Run     # ...then start the server on http://localhost:5000
```

For a local run with the synthetic data, set a bootstrap team before starting the server; the header
identity then signs you in as its owner:

```powershell
$env:TOOLUP_INITIAL_TEAM_NAME = 'Trading'
$env:TOOLUP_INITIAL_PLATFORM_ADMIN = 'analyst'
pwsh ./run.ps1 -Run
```

Then add your Anthropic key in the app's AI provider settings, upload
`data/sample/daily-trade.synthetic.csv` from the "Daily trading" data manager in the client
(`cd src/MyDataPlatform-Client; npm install; dotnet fable watch -o output --run npx vite`, then
http://localhost:8080 with the dev user `analyst`), and read the report at
http://localhost:5000/insights/latest. With the header identity a browser carries no credential on a
page request, so read the page with the header instead:

```powershell
Invoke-WebRequest http://localhost:5000/insights/latest -Headers @{ 'X-User-Id' = 'analyst' }
```

## Point it at a real source

- **The data.** Replace `UploadedDeliverySource` in `src/Modules/DailyTrade/Server.fs` with a
  connector to your system (an `IDataSource`, for example one of the `ToolUp.DataSources.*`
  packages), and the reader with your extract's columns. Declare each output as a fact table with its
  metrics and subjects; the platform checks the declarations when the server starts.
- **The calendar.** Replace the fiscal calendar with your own and update
  `ReferenceData.FiscalCalendarSha256` in the same commit.
- **Identity.** Set `TOOLUP_AUTH_MODE=oidc`, `TOOLUP_OIDC_ISSUER`, `TOOLUP_OIDC_AUDIENCE`, and the
  sign-in endpoints `OIDC_AUTHORIZATION_ENDPOINT`, `OIDC_TOKEN_ENDPOINT`, `OIDC_REDIRECT_URI` (and
  `OIDC_CLIENT_SECRET` for a confidential client). Map the directory group whose members read the
  report to the `report-reader` role.
- **Storage and secrets.** Swap the two lines in `Companions.fs` for a cloud object store and a vault.

## The docs it builds on

- `docs/platform/facts.md` and `docs/platform/fact-publication.md`: the fact tier and fact tables.
- `docs/platform/grounded-narratives.md`: the grounded run, its gate and its triggers.
- `docs/platform/gated-ssr.md`: page audiences, directory roles and interactive sign-in.
- `docs/platform/knowledge-portal.md`: the knowledge base on a RAG app.
- `docs/platform/modules.md`: the module convention and the data-manager slot.
- `docs/getting-started/dataplatform.md` in the SDK repository: a walk through this scaffold.
