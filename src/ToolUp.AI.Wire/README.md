# ToolUp.AI.Wire

A small, portable JSON value model for building and reading wire payloads,
compilable to **both** .NET and [Fable](https://fable.io) (browser). It
depends on `FSharp.Core` + `Fable.Core` only — no `System.Net.Http`, no
unguarded `System.Text.Json` — so one mapping can serve a server host and a
browser host without re-deriving the translation.

## What it gives you

- **`JsonValue`** — a JSON value DU (`JNull` / `JBool` / `JNumber` /
  `JString` / `JArray` / `JObject`). Object members are an ordered list, so
  serialization preserves insertion order and is byte-stable.
- **Builders** — `jnull` / `jbool` / `jnum` / `jint` / `jstr` / `jarr` /
  `jobj`, auto-opened for terse construction.
- **Total accessors** — `tryField` / `asString` / `asInt` / `asFloat` /
  `asBool` / `asArray` / `asObject` / `tryItem`. Every one returns `option`
  and never throws.
- **A host-bridged seam** — `JsonHost.serialize` (a single portable
  canonical writer, identical on every host) and `JsonHost.parse` (bridged
  to the browser's `JSON.parse` under Fable, to `System.Text.Json`
  otherwise), returning `JsonValue option`.

## Example

```fsharp
open ToolUp.AI.Wire

let body =
    jobj
        [ "model", jstr "example-model"
          "stream", jbool true
          "messages", jarr [ jobj [ "role", jstr "user"; "content", jstr "hi" ] ] ]

let json = JsonHost.serialize body
// {"model":"example-model","stream":true,"messages":[{"role":"user","content":"hi"}]}

let model =
    JsonHost.parse json
    |> Option.bind (JsonValue.tryField "model")
    |> Option.bind JsonValue.asString
// Some "example-model"
```

## Byte-stable output

`JsonHost.serialize` is a hand-rolled canonical writer rather than a passthrough
to two different host JSON engines, so its bytes are identical on .NET and
Fable for the same `JsonValue`. Object keys emit in `JObject` order; output is
compact (no insignificant whitespace).

Licensed under Apache-2.0.
