# forge#1254: declared pages, settings-read data sources, reference data and OIDC admission are combinators

**Ships in:** ToolUp.PublicRendering (`DeclaredPage`, `ServedNarrative`, `DeclaredPages`,
`PublicRenderingCompose.withDeclaredPages`), ToolUp.Platform.Server (`ServerApp.withDataSourcesFromSettings`,
`DataSourceFromSettings`, `ServerApp.withReferenceData`, `ReferenceDataset`, `IReferenceData`) and
ToolUp.AuthProviders.Oidc (`OidcAdmission.layered`, `AdmissionSettings`; auth-providers line 0.39.2).

**Breaking for one literal:** `PublicRenderingOptions` gains `DeclaredPages`, so a full record literal adds the field.
`PublicRenderingOptions.create` builds it. Nothing changes for a deployment that calls none of the four.

**Affected:** a consuming application that carries its own content-API decorator for declared routes, its own
start-up seeder of data-source configs, its own reference-data parser and hash check, or its own layering of tenants,
roles and aliases onto the OIDC configuration.

## The change

Delete the four files or functions that carry that runtime. Keep the declarations as data, and add the compose lines:

```diff
-module App.PublishedPages   // the decorator, the matcher, the capture predicates
-module App.DataSources      // the reader and the hosted seeder
-module App.ReferenceData    // the CSV parser, the verifier, the registry
-let admission () = ...      // the settings parser and the layering
+let pages: DeclaredPage list = [ (* route, title, layout, audience, captures, body, narrative *) ]
+let sources: DataSourceFromSettings list = [ (* id, kind, scope setting, connection settings, key setting *) ]
+let datasets: ReferenceDataset list = [ (* id, source, content hash, columns *) ]
+let settings: AdmissionSettings = { (* the names of the settings *) }

 ServerApp.empty
+|> ServerApp.withReferenceData datasets
+|> ServerApp.withDataSourcesFromSettings sources
 |> PublicRenderingCompose.withPublicRendering (
     PublicRenderingOptions.create ()
     |> PublicRenderingOptions.withLayout (LayoutName "report") reportLayout
+    |> PublicRenderingCompose.withDeclaredPages pages
 )

-AuthProvider.fromEnv logger (fun l config -> OidcAuthProvider.fromConfig l (admit config))
+AuthProvider.fromEnv logger (fun l config -> OidcAuthProvider.fromConfig l (OidcAdmission.layered settings config))
```

Connectors stay yours to register, as `IDataSource` services: only the application knows which companions it takes.
A served narrative names its publishing principal's user id as data (`ServedNarrative.PublishingPrincipal`), where the
copy looked it up itself. Read a verified dataset through `IReferenceData.Dataset id`, from the container.

## What each one refuses

- **A declared page:** a layout nobody registered, a repeated route, and a predicate on a capture its route lacks,
  all at compose. Literal routes now match first whatever the order they were declared in.
- **A settings-read source:** set in part (naming the missing setting), an unset scope setting, a kind with no
  composed connector, and a `CredentialNeed.Required` connector with no key. Every refusal fails the start, and
  nothing is seeded. None of its connection settings set is declared-but-disabled, and logged.
- **Reference data:** a hash, header, cell or field-count mismatch, a file that is not UTF-8, a missing file and a
  repeated id, all named together when `withReferenceData` is called.
- **Admission:** publication readers with no member rule (AZ-11), a directory group aliased to two roles, a malformed
  alias, and an unset reader-group or required setting.

`DeclaredDelivery.withDeclaredDataSources` (ToolUp.DataSources.Common) is unchanged and is a different thing: a config
known in code, whose delivery forge schedules under a principal. `ServerApp.withDataSourcesFromSettings` reads the config
from settings, seeds it, and schedules nothing.

## Verify and roll back

`dotnet build`, then start the application with one data-source setting unset: the start names it. A page with a
misspelt layout stops the composition rather than answering 500. To roll back, restore the deleted files and remove
the compose lines; no stored data changes shape.
