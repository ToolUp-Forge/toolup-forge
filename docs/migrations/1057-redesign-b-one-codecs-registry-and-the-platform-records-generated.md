# 1057 — One `Codecs` registry with a posture

## What changes

`Codecs` (ToolUp.Platform.Core, `Shared/Remoting/Codecs.fs`) is the one process-wide table of algebra codecs,
replacing the two tables `RemotingDecoders` and `JsonDecoders` held. A registration is a `Scope`
(`Unscoped`, or `ForRecord "<API record name>"`) and a `Codec<'T>` whose three legs are each optional: a JSON
encoder, a JSON decoder, a MessagePack decoder. A later registration of a leg replaces that leg and leaves the
others. Every host reads a wire type through one seam:

| Seam | Consulted by |
|---|---|
| `Codecs.resolveJson policy scope type` | the server's argument parse (`FableConverters.tryDeserialiseWith`), the client's JSON response, a streamed chunk on the client |
| `Codecs.resolveJsonEncoder policy scope type` | the server's response writer and each streamed chunk |
| `Codecs.resolveMsgPack policy scope type` | the client's binary response (`Remoting.withBinarySerialization`) |

Each answers `Some (Ok codec)` for a registered type (record-scoped first, then unscoped), `None` for a miss
under `DecodePolicy.ReflectionFallback` (the reflective reader or writer, exactly as before), and
`Some (Error refusal)` naming the type for a miss under `DecodePolicy.VerifiedOnly`.

**The server's posture.** `RemotingOptions` gains `DecodePolicy` (default `ReflectionFallback`), set with
`Remoting.withDecodePolicy`. Under `VerifiedOnly` a mount refuses to start, naming every method, leg and type
of the contract with no registered codec (`Proxy.codecMisses`), and the argument seam refuses an unregistered
type by name. `MakeEndpointProps` gains the same field. The boot log carries one more line,
`RemotingDecoderFacet.describeCodecs`, saying how many served records would pass under `VerifiedOnly`.

**The response leg.** The generator gains a `json-encoders` command, which emits encoders for every type the
named API records' methods return. `ToolUp.Remoting.Json.PlatformJsonEncoders` is that emission for the
platform's own records, and `ServerApp` registers it at boot beside the platform's decoders, so a platform
response is written by the algebra. A registered encoder is gated like a decoder: its text must equal the
System.Text.Json writer's, byte for byte, over draws of its own type (`JsonEncoders.gateWith`,
`JsonEncoders.registerVerifiedWith`). An unregistered type keeps the converter set's writer.

**The client.** `RemoteStream.create` takes the element's `System.Type` before its `TypeInfo`, so a streamed
chunk resolves a registered decoder.

## What a consumer does

Nothing, to keep today's behaviour: every unregistered type keeps the reflective path.

- A consumer that registers decoders keeps working: `RemotingDecoders.register` and `JsonDecoders.register` /
  `registerFor` (and the rest of both modules) forward to `Codecs`. Move to the new call before the minor that
  retires the forwarding modules:

  ```diff
  -RemotingDecoders.register<Order> orderDecoder
  +Codecs.register<Order> Scope.Unscoped (Codec.msgPack orderDecoder)
  -JsonDecoders.registerFor<OrderRequest> "IOrdersApi" orderRequestDecoder
  +Codecs.register<OrderRequest> (Scope.ForRecord "IOrdersApi") (Codec.jsonDecoder orderRequestDecoder)
  ```

  Regenerating a decoder module (`decoders`, `json-decoders`) emits the new calls.
- A consumer may run `json-encoders` over its own records and call the module's `registerAll` (or
  `registerAllVerified (JsonEncoders.gateWith FableConverters.decoderOracle) draws seed`) at composition.
- To turn the posture on: register every codec the API's contract needs, then
  `Remoting.withDecodePolicy DecodePolicy.VerifiedOnly`. Read the boot line first; it names what would refuse.
- Code that built a `MakeEndpointProps` or a `RemotingOptions` literal adds `DecodePolicy`.
- Code that called `RemoteStream.create` directly passes the element type:
  `RemoteStream.create url headers body withCredentials decodeErrorOf typeof<'T> typeInfo`.

## Verify

- `dotnet run --project src/ToolUp.Platform.Tests.Remoting/ToolUp.Platform.Tests.Remoting.fsproj -- --filter-test-list "Codecs"`
- the same with `--filter-test-list "Platform JSON encoders"`.
- Boot a deployment and read the `remoting codecs:` line.

## Rollback

Revert the commit. No stored data and no wire format changes: the encoders write the bytes the converter set
wrote.
