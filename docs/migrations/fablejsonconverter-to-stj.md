# Migration — `FableJsonConverter` (Newtonsoft) → `FableConverters` (System.Text.Json)

**Released in:** the SDK release that retires the Newtonsoft.Json transitive dependency from `ToolUp.Platform.Server`.

## What changes

`Newtonsoft.Json` is no longer a forge dependency. Every non-Remoting JSON site that previously called `Fable.Remoting.Json.FableJsonConverter` (or its post-Phase-73 alias `ToolUp.Remoting.Json.FableJsonConverter`) now routes through `System.Text.Json` with the F# converter set registered against a `JsonSerializerOptions` instance via `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()`.

The wire format is preserved: the FableConverters STJ port writes the same shape as the Newtonsoft FableJsonConverter for every type the converter set covers (F# Option, discriminated unions, tuples, records, CLIMutable records, lists, maps, sets, DateTime / DateOnly / TimeOnly, decimal, byte[], etc.). Consumer-side JSON blobs persisted under the old converter deserialize cleanly under the new one with no re-write.

## Diff to apply (per consumer)

### Imports

```diff
-open Newtonsoft.Json
-open ToolUp.Remoting.Json
+open System.Text.Json
+open ToolUp.Remoting.Json.SystemTextJson
```

If a file used `Newtonsoft.Json.Linq` (`JObject` / `JArray` / `JToken` / `JsonValue` / `JProperty`), swap to `System.Text.Json.Nodes` (`JsonObject` / `JsonArray` / `JsonNode` / `JsonValue` — same shape with a slightly different API; see "JsonNode equivalents" below).

### Settings construction

```diff
-let private jsonSettings =
-    let s = JsonSerializerSettings()
-    s.Converters.Add(FableJsonConverter())
-    s
+let private jsonOptions = FableConverters.create ()
```

`FableConverters.create ()` returns a fresh `JsonSerializerOptions` with:

- All F# converters registered (Option / DU / tuple / record / CLIMutable / list / Map / Set / decimal / DateTime / DateOnly / TimeOnly / byte[] / DataSet / DataTable / Long / BigInt / TimeSpan / Nullable / Pojo / StringEnum).
- `PropertyNameCaseInsensitive = true` — Newtonsoft-default-compatible camelCase → PascalCase matching for record property reads.
- `NumberHandling = AllowReadingFromString | AllowNamedFloatingPointLiterals` — Fable.SimpleJson emits decimals / NaN as strings; this absorbs them.
- `Encoder = UnsafeRelaxedJsonEscaping` — closest STJ pre-built encoder to Newtonsoft's escape behaviour. The `StringConverter` further pre-escapes only the RFC 8259-required characters and writes via `WriteRawValue` so emoji / supplementary-plane codepoints stay as raw UTF-8 bytes.

Consumers wanting strict-STJ defaults can pass a plain `JsonSerializerOptions()` and call `FableConverters.addTo opts` themselves, then override any of the above settings before the first `Serialize` / `Deserialize` call. After first use, `JsonSerializerOptions` becomes read-only.

### Call sites

```diff
-let json = JsonConvert.SerializeObject(value, jsonSettings)
+let json = JsonSerializer.Serialize(value, jsonOptions)

-let value = JsonConvert.DeserializeObject<'T>(json, jsonSettings)
+let value = JsonSerializer.Deserialize<'T>(json, jsonOptions)
```

For sites that previously used `JsonConvert.SerializeObject(v, Formatting.Indented)`, build an indented options instance:

```fsharp
let private indentedJsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o
```

For sites that previously used `JsonConvert.ToString(s)` to JSON-encode a string (e.g. embedding a single string in a JSON literal), the STJ equivalent is `JsonSerializer.Serialize(s, options)` — same output shape (`"foo"` with quotes).

### `PackageReference`

Remove every `<PackageReference Include="Newtonsoft.Json" />` from consumer fsprojs. The forge SDK no longer brings Newtonsoft transitively, so any consumer fsproj still declaring it would be the only remaining holdout.

## JsonNode equivalents

For consumer code that used Newtonsoft.Json.Linq's mutable tree manipulation, the System.Text.Json.Nodes shape maps directly:

| Newtonsoft.Linq | System.Text.Json.Nodes |
|---|---|
| `JObject.Parse(json)` | `JsonNode.Parse(json) :?> JsonObject` |
| `JObject()` (empty) | `JsonObject()` |
| `JArray(elements)` / `JArray()` | `JsonArray()` (then `.Add(child)`) |
| `JValue(s)` / `JValue.CreateString(s)` | `JsonValue.Create(s)` |
| `JProperty("k", v)` + `obj.Add(prop)` | `obj.["k"] <- v` |
| `obj["key"]` | `obj.["key"]` (returns `JsonNode option`-ish — `null` when absent) |
| `prop.Properties()` | `obj :> IEnumerable<KeyValuePair<string, JsonNode>>` |
| `token.Type` (JTokenType enum) | `node.GetValueKind()` (JsonValueKind enum) |
| `token.Value<string>()` | `node.GetValue<string>()` |
| `token.ToString(Formatting.None)` | `node.ToJsonString()` |
| `token.ToString(Formatting.Indented)` | `node.ToJsonString(JsonSerializerOptions(WriteIndented = true))` |
| `JToken.DeepEquals(a, b)` | `JsonNode.DeepEquals(a, b)` (.NET 8+) |

**Mutation during enumeration**: `JsonObject` throws `InvalidOperationException` if mutated while being enumerated. Snapshot the keys first:

```fsharp
let names = obj |> Seq.map (fun kvp -> kvp.Key) |> Seq.toArray
for name in names do
    let child = obj.[name]
    // mutate obj.[name] here
```

This matters for redaction walks; see `ConfigDriftDetector.fs` / `DiagnosticBundleHandler.fs` for the canonical pattern.

## Verification steps

1. **`dotnet build`** — clean. Any residual `JsonConvert` / `FableJsonConverter()` call surfaces as `FS0039 — value not defined`.
2. **Targeted test pack** — for any persistence path that round-trips F# records / DUs / Options through the converter, run the existing contract test. Round-trip equality fails fast on DateTime / Option / value-type-default regressions.
3. **Full Expecto runners**:
   - `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
   - `dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj`
   - `dotnet run --project src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj`
   All expected green; the only pre-existing flakes the forge baseline carries are noted in `ToolUp-Diametrical/roadmap/TIDY-UP.md`.

## Rollback

The migration is non-reversible at the deps-graph level (Newtonsoft.Json is dropped). Rolling back requires re-adding the package and restoring the legacy converter file. Don't.

## Cross-references

- Workspace [`SDK-ADOPTION.md`](../../../SDK-ADOPTION.md) row for the cross-repo consumer rollout.
- Forge `CLAUDE.md` "Serialisation" subsection — describes the canonical STJ pattern for new code.
- TIDY-UP entry (workspace `ToolUp-Diametrical/roadmap/TIDY-UP.md`) — the original tracker that drove the sweep.
