# ToolUp.AI.Wire

The portable JSON substrate for building and reading wire payloads,
compilable to **both** .NET and [Fable](https://fable.io) (browser). It
depends on `FSharp.Core` + `Fable.Core` only — no `System.Net.Http`, no
`System.Text.Json` — so one mapping can serve a server host and a browser
host without re-deriving the translation.

## What it gives you

- **`JsonValue`** (namespace `ToolUp.Remoting.Json`) — the one closed JSON
  value model: `JsonValue.Null` / `Bool` / `Number` / `String` / `Array` /
  `Object`. A number carries its token **as written** (`Number of lexical:
  string`), so an `int64` past 2^53 or a decimal written to thirty places
  survives a round trip. Object members are an ordered array, so
  serialization preserves insertion order and is byte-stable.
- **Builders** — `jnull` / `jbool` / `jint` / `jint64` / `jdecimal` / `jnum` /
  `jstr` / `jarr` / `jobj`, auto-opened with the namespace.
- **Total accessors** — `tryMember` / `tryItem` / `asString` / `asBool` /
  `asInt` / `asInt64` / `asDecimal` / `asFloat` / `asNumberText` / `asArray` /
  `asObject`. Every one returns `option` and never throws; the numeric ones
  parse the token on demand.
- **The text seam** — `JsonHost.serialize` (the one canonical writer,
  identical on every host), `JsonHost.escape` (its string escape, for JSON
  written by hand) and `JsonHost.parse` (the portable `JsonText` parser on
  both hosts), returning `JsonValue option`. `JsonText.tryParse` returns the
  refusal (`ToolUp.Remoting.DecodeError`) instead.
- **`JsonEncode`** — the typed encoders a generated remoting proxy writes
  its arguments through, over the same model and writer.
- **The connector contract** (`Contract.fs`, namespace `ToolUp.Platform.AI`)
  — the portable `IAIProvider` value types a browser host can reference
  directly: `AIProviderMessage` / `AIProviderToolCall` /
  `AIProviderToolResult` / `AIProviderToolDef` / `AIProviderResponse` /
  `TokenUsage` / `AIProviderCapabilities` / `AIProviderError` /
  `AIContentPart` (+ `ImageSource` / `ImagePayload`) and their companion
  modules. They keep the `ToolUp.Platform.AI` namespace so a `ToolUp.Platform`
  consumer sees them unchanged; the `IAIProvider` interface itself stays in
  `ToolUp.Platform.Core` (it references the server-tier `RetryPolicy`).

## Example

```fsharp
open ToolUp.AI.Wire
open ToolUp.Remoting.Json

let body =
    jobj
        [ "model", jstr "example-model"
          "stream", jbool true
          "messages", jarr [ jobj [ "role", jstr "user"; "content", jstr "hi" ] ] ]

let json = JsonHost.serialize body
// {"model":"example-model","stream":true,"messages":[{"role":"user","content":"hi"}]}

let model =
    JsonHost.parse json
    |> Option.bind (JsonValue.tryMember "model")
    |> Option.bind JsonValue.asString
// Some "example-model"
```

## Byte-stable output

`JsonHost.serialize` is a hand-rolled canonical writer rather than a passthrough
to two different host JSON engines, so its bytes are identical on .NET and
Fable for the same `JsonValue`. Object members emit in model order, a number
emits as its token, and output is compact (no insignificant whitespace).

Licensed under Apache-2.0.
