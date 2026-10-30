# The remoting defects, and the leftovers the fork carried

**Ships in:** ToolUp.Platform.Core and ToolUp.Platform.Server 0.38.0, the next minor after the tagged 0.37.0. The
change is breaking-class, and the break is confined to public members nothing in this repository called.

**Affected:** code that calls one of the members in the table below; an API record whose `[<Custom>]` validator has
no public parameterless constructor; code that reads a `DecodeError.Path` produced under a map key containing `.`. A
deployment that composes the in-memory stores and declares buildable validators changes nothing.

## Removed members

Each has no replacement unless the table names one: they were documented as not for calling, or had no caller.
`dotnet run --project Build.fsproj -- Codemod` reports every site (rules `1053-*`) and rewrites none.

| Removed | Assembly | What to do |
|---|---|---|
| `ThreadSafeCell` | Server | None. A `Lazy<'T>` covers `computeOnce`. |
| `Reader<'a,'b>`, `Reader`, `ReaderBuilder` | Server | None. Take the `HttpContext` in the function. |
| `Extensions.reader`, `Extensions.resolve`, `Extensions.Remoting.fromReader` | Server | `Remoting.fromContext`, reading services from the context. |
| `Extensions.getService`, the `HttpContext.GetService` extension | Server | Giraffe's `ctx.GetService<'T>()`, or `ctx.RequestServices.GetService`. |
| `ToolUp.Remoting.MsgPack.Write.stackalloc` | Server | None, do not call. It returned a span over a popped frame. Allocate the buffer in the function that reads it. |
| `ParsingArgumentsError`, `ParsingArgumentsError.ofDecodeError` | Server | `DecodeError.render`, which is what it wrapped. |
| `Read.interpretIntegerAs` | Core | `Read.interpretIntegerAsFrom typ 64 n`, which is what it called. |
| `ToolUp.Remoting.MsgPack.Write.Fable` (`writeType`, `writeObject`) | Core | None shipped. No browser path writes MessagePack. The source is `src/ToolUp.AI.Client.Tests/Remoting/MsgPackWrite.fs`; copy that file if you need it. |

`GiraffeUtil.buildFromImplementation` and the obsolete `Project` / `Inject` members of the MessagePack `TypeShape` go
too; all three were internal, so no consumer sees them.

## What changes in behaviour

1. **A `[<Custom>]` validator that cannot be built refuses at compose time.** The dispatcher table build raises
   `ToolUp.Remoting refused to start`, naming the method, the field path, the validator type and the reason. It used
   to compose, and the validator silently never ran. Give the validator a public parameterless constructor, or make
   it implement `IFieldValidator`.
2. **A refusal path keeps a dotted map key together.** A `DecodeError` under a `Map<string,_>` key `a.b` reports
   `['a.b']` as one segment, or `Holds['a.b']` beneath a member. It used to split at the key's dot. A consumer that
   recovers segments by splitting a rendered path on `.` must not split inside a quoted bracket.
3. **`InMemoryRateLimitStore` holds up to `maxBuckets` buckets and never wipes.** It used to hold one fewer, and a
   store of one bucket emptied itself on each new key, so a budget against that key was never spent. The store now
   evicts before it inserts; with nothing left to evict by, it goes transiently over cap.
4. **The in-memory stores share one index.** `InMemoryRateLimitStore`, `InMemoryIdempotencyStore` and
   `InMemoryJobDispatcher` evict through one internal bounded FIFO. An idempotency key stored again keeps its
   original age and adds no second marker; eviction already went by first insertion, so only the stale marker is gone.
5. **A job's terminal test no longer depends on the case order of `JobStatus<'T>`.**

## Verification

`dotnet run --project Build.fsproj -- Codemod` reports no `1053-*` site, and the composed host starts.

## Rollback

Pin the previous version. There is no data to migrate: every store here is in-process.
