# 881 — One JSON value model: a number keeps the digits it was written with

**Class:** breaking (0.x minor). **Affects:** any code written against `ToolUp.AI.Wire.JsonValue` — provider
wire mappers, connector mappers, tests that build or match AI wire JSON. Nothing that only uses the remoting
`ToolUp.Remoting.Json` names changes.

## What changes

- The tree has **one** closed JSON value type, `ToolUp.Remoting.Json.JsonValue`, and it now lives in the
  `ToolUp.AI.Wire` package (FSharp.Core-only, both hosts). `JsonValue`, `JsonText`, `JsonEncode` and
  `ToolUp.Remoting.DecodeError` moved down from `ToolUp.Platform.Core` with their namespaces unchanged, so
  every reference through Core still resolves.
- `ToolUp.AI.Wire.JsonValue` (`JNull | JBool | JNumber of float | JString | JArray of list | JObject of list`)
  is **removed**. Its cases are the qualified `JsonValue.Null | Bool | Number of lexical: string | String |
  Array of JsonValue[] | Object of (string * JsonValue)[]`. A number carries its token text; nothing goes
  through a `float` unless you ask for one.
- `JsonHost.parse` reads with the portable `JsonText` parser on both hosts (no more `JSON.parse` /
  `JsonDocument.GetDouble`); `JsonHost.serialize` is the one writer (`JsonEncode.toText` is the same function)
  and `JsonHost.escape` is its string escape for hand-written JSON.
- Builders stay (`jnull jbool jint jnum jstr jarr jobj`, auto-opened with `ToolUp.Remoting.Json`), plus
  `jint64` and `jdecimal` for exact large ids and money. `jnum` writes exactly the bytes the old writer wrote.
- Accessors move onto the one `JsonValue` module: `tryField` is now `tryMember`; `asFloat`, `asInt`, new
  `asInt64`, `asDecimal`, `asNumberText` parse on demand and return an option; `asArray` / `asObject` return
  arrays.

**Wire bytes do not change** for a number a double represents exactly (the conformance corpus pins the old
writer's bytes for those), and a request body is otherwise byte-identical. A number a double could not hold
(`9007199254740993`, `0.100000000000000000000000000000`, `1e400`) now round-trips instead of being altered.

## The edit, per mapper shape

```diff
 open ToolUp.AI.Wire
+open ToolUp.Remoting.Json
```

```diff
-| JNumber f -> Some f
+| JsonValue.Number _ -> JsonValue.asFloat v          // or asInt / asInt64 / asDecimal / asNumberText
-| JString s -> …            | JObject members -> …           | JArray items -> …
+| JsonValue.String s -> …   | JsonValue.Object members -> …  | JsonValue.Array items -> …
-| JObject [ ("input", v) ] ->
+| JsonValue.Object [| ("input", v) |] ->
-JObject(members @ [ "k", x ])
+JsonValue.Object(Array.append members [| "k", x |])
-JsonValue.tryField "content" root |> Option.bind JsonValue.asArray |> Option.defaultValue []
+JsonValue.tryMember "content" root |> Option.bind JsonValue.asArray |> Option.defaultValue [||]
-JNumber(float id)                     -JNumber amount
+jint64 id                             +jdecimal amount
```

## Verify

1. `dotnet build` your solution: every remaining site is a compile error naming a `J*` case, `tryField`, or a
   list/array mismatch.
2. Run your mapper's golden request fixtures: the bytes must be unchanged.
3. `dotnet run --project src/ToolUp.AI.Wire.Conformance` and the Fable leg pass on both hosts.

## Rollback

Pin the previous `ToolUp.*` release; there is no compatibility shim, by decision (the break lands before 1.0
rather than carrying the float model through a major).
