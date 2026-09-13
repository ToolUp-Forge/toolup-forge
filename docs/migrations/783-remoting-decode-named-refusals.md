# Phase 783 — remoting decode failures are named refusals

**Applies to:** any deployment using `ToolUp.Remoting` (i.e. any consumer of
`ToolUp.Platform.Server` / `ToolUp.Platform.Client`).
**Breaking:** no. Additive on the wire, additive on the public surface.
**Action required:** none to keep working. One optional client-side branch.

## What changes

A request whose **arguments do not decode** used to be indistinguishable from a
handler bug: the decoder threw, the dispatcher's error adapter folded the throw
into `Errors.unhandled`, and the client saw

```
HTTP 500  {"error":"Error occured while running the function Foo","ignored":true,"handled":false}
```

It now arrives as a named refusal in the Phase 69e categorised envelope:

```
HTTP 400
{"error":{"methodName":"Foo",
          "decodeError":{"expected":"Int32","found":"String (…)","path":["Foo","args[0]","count"]},
          "message":"expected Int32 at Foo.args[0].count, got String (…)"},
 "ignored":false,"handled":true,"category":"validation","__schema_version":1}
```

Five request shapes are covered: a mistyped field, a body that is not a JSON
array, a body that is not JSON at all, too few arguments, too many arguments.
The handler never runs in any of them. **Well-formed traffic is byte-identical**
— the success envelope, the status code and the response bytes are unchanged.

## What a consumer sees change

| Before | After |
|---|---|
| HTTP 500 on a malformed request | **HTTP 400** |
| `category` absent (`Errors.unhandled` is uncategorised) | `category: "validation"` |
| `error` is a prose string | `error` is an object with `methodName`, `decodeError`, `message` |

If you alert on 5xx from the remoting routes, malformed-input noise **leaves**
that signal — a 5xx from these routes is now a genuine server fault. If you
branch on `category` already (the Phase 69e path), a decode refusal arrives on
the branch you have, with no new code.

The refusal carries no handler state and no server internals — it names the
declared parameter type, the JSON shape received, and the field path. It is
emitted **after** authentication and rate-limiting, so an unauthenticated
caller cannot use it to probe method shapes.

## The client-side branch (optional)

`ProxyRequestException` gains `DecodeError: ToolUp.Remoting.DecodeError option`.
The pre-783 three-argument constructor is unchanged, so existing construction
sites compile as they are.

```fsharp skip=fragment
| Error ex ->
    match ex with
    | :? ProxyRequestException as pex ->
        match pex.DecodeError with
        | Some decode ->
            // The request shape was wrong — a bug in THIS client, or a
            // client/server version skew. `decode.Path` / `.Expected` /
            // `.Found` are structured; no message-text parsing.
            { model with Error = Some(DecodeError.render decode) }, Cmd.none
        | None -> model, Cmd.ofMsg (GenericFailure ex.Message)
    | _ -> model, Cmd.ofMsg (GenericFailure ex.Message)
```

`DecodeError` is `Some` for two situations and no others: the server refused to
decode the request, or — on a proxy built with
`Remoting.withBinarySerialization` — **this client could not decode the
server's reply**. A Phase 69e attribute-validation failure keeps
`DecodeError = None`: a decoded value failing a rule is a different thing from a
value that never decoded, and the two stay distinguishable.

## Server-side surface

- `ToolUp.Remoting.DecodeError` — `{ Path: string list; Expected: string; Found: string }`,
  with `DecodeError.render`, `.create`, `.at`, `.under`. Lives in
  `ToolUp.Platform.Core` so all three tiers share one vocabulary.
- `InvocationResult.DecodeRefused of error * functionName` — a new case. **A
  custom adapter that matches on `InvocationResult` exhaustively will get an
  incomplete-match warning until it handles it**; route it to
  `Errors.categorised ErrorCategory.Validation` + 400, as the shipped Giraffe
  and ASP.NET Core adapters do.
- `MsgPack.Read.Reader.TryRead: Type -> Result<obj, DecodeError>` — the refusing
  entry. `Reader.Read` is unchanged and still throws, so existing callers need
  no edit (GP 11); what it throws is now `DecodeException` carrying the
  structured refusal instead of a bare `Exception` carrying prose.
- `FableConverters.tryDeserialiseElement` / `tryDeserialise<'T>` — the one seam
  the dispatcher decodes through.
- `ParsingArgumentsError.ofDecodeError` — the pre-783 stringly-typed shape,
  produced from the structured one rather than authored alongside it.

## Adoption

Flip your `sdk-adoption.json` record for this refactor:

```json
{ "refactor": "783", "status": "adopted", "sha": "<your commit>" }
```

Adopting is just taking the SDK version — nothing in your code has to change.
Use `"status": "n-a"` with a reason if your deployment does not use
`ToolUp.Remoting`. The matrix regenerates from your manifest; never hand-edit it.

## Not in this phase

The MsgPack reader and the System.Text.Json converter set still refuse
*internally* by throwing; this phase moved the **boundary**, so every throw is
converted to a named refusal at one seam per wire. Rewriting the decoders
themselves around `Result` is Phase 785, and the totality theorem over the
result is Phase 787.
