# forge#1073: the admin page kit and the admin sub-models

**What changes.** `ToolUp.Platform.UI` carries the shapes every admin page draws, and the SDK's admin
modules are rebuilt on them:

| Shape | Signature |
|---|---|
| tab bar | `Tabs.bar : ('T * string) list -> 'T -> ('T -> unit) -> ReactElement` (was the client-tier `Layout.Tabs.tabGroup`, now deleted) |
| overlay | `Modal.overlay : title -> body -> actions -> ReactElement` |
| confirmation | `Modal.confirm : ConfirmMessages -> prompt -> onConfirm -> onCancel -> busy:bool -> ReactElement` |
| stateful buttons | `Forms.Button.primaryWith` / `secondaryWith` / `dangerWith : ButtonState -> string -> (unit -> unit) -> ReactElement`, `ButtonState = Enabled \| Disabled \| Busy of label`, `Forms.Button.stateOf` |
| typed table | `Data.table : Column<'row> list -> 'row list -> ReactElement`, `Column<'row> = { Header; Align; Cell }` |
| tokens | `Tokens.Text.muted` / `success` / `danger`, `Tokens.Bg.subtle` / `success` / `danger` |

Every string the kit shows is the caller's. Only `Data.table` breaks: the rest is additive, and a
consumer module adopts it when it next touches its view.

**`Data.table` takes typed columns.** It took header strings and rows of pre-rendered cells; it now
takes one `Column<'row>` per column and the row values themselves. The codemod
(`dotnet run --project Build.fsproj -- Codemod`) reports each old call as `1073-data-table-columns`;
there is no mechanical rewrite, because the cell renderer has to be written over the row type.

```diff
-Data.table [ "Region"; "Spend" ] [
-    for r in rows -> [ Html.text r.Region; Html.text (string r.Spend) ]
-]
+Data.table
+    [
+        { Header = "Region"; Align = Data.Left; Cell = fun r -> Html.text r.Region }
+        { Header = "Spend"; Align = Data.Right; Cell = fun r -> Html.text (string r.Spend) }
+    ]
+    rows
```

**`Layout.Tabs` is gone.** Replace `Layout.Tabs.tabGroup tabs selected onChange` with
`Tabs.bar tabs selected onChange` (same arguments, `open Toolup.UIToolkit`). `Layout.Tabs.tab` and
`Layout.Tabs.container` had no callers and have no successor.

**The SDK's admin modules' public types moved.** A consumer that only composes them (`create`,
`moduleId`, `adminTile`) changes nothing. A consumer or test that builds their `Model` or sends their
`Msg` directly reads the module: the modal and wizard states are sub-models (`TeamManagerUI`'s
`IssueByEmail`, `TransferOwnership` and `RevokeByEmail`; `PlatformAdminUI`'s `AssignConfirm` and
`ConfirmDeleteTeam`; `MappingDataManagerUI`'s `Wizard`), each with its own `Model`, `Msg` and `update`,
held in the parent as an option and reached through one parent message with `Cmd.map`. The directory
lookup three modules copied is one sub-model, `UserDirectory` (`Directory: UserDirectory.Model`,
`DirectoryMsg`).

**Verify.** `dotnet build` the consumer; the codemod's report lists each `Data.table` site left.

**Rollback.** Pin the previous SDK version.
