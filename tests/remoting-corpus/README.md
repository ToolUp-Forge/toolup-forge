# Remoting wire corpus — pinned fixtures

Committed encodings of the API-shaped values declared in
`src/ToolUp.Platform.Tests/Remoting/WireCorpus.fs`. Two files per case:

| File | What it is |
|---|---|
| `<case>.msgpack` | the bytes the shipped MsgPack writer emits for that value |
| `<case>.json` | the text the shipped System.Text.Json converter set emits for it |

The **expected value** a fixture decodes to is not a third file: it is the F# declaration in
`WireCorpus.fs`, which is compiled into both consuming hosts. A canonical-JSON rendering of the
expected value was considered and rejected — it would be a second transcription of the same fact,
free to drift from the declaration, and weaker than the declaration as an oracle. One declaration,
two encodings.

## Who reads these

* `src/ToolUp.Platform.Tests/Remoting/MsgPackRoundTripTests.fs` — asserts the writer still emits
  these bytes, and (unconditionally) that these bytes decode to the declared values.
* `src/ToolUp.Platform.Tests/Remoting/StjRoundTripTests.fs` — the same two claims for the JSON text.
* `src/ToolUp.AI.Client.Tests/RemotingCorpusParityTests.fs` — decodes the `.msgpack` files under
  **Fable**, so the .NET and JavaScript readers are held to one contract. It reads the cross-host
  subset; cases outside it name a measured host divergence in `WireCorpus.recordedDivergences`.

## Regenerating

    $env:TOOLUP_REMOTING_CORPUS_REFRESH = "1"
    dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -c Release
    dotnet src/ToolUp.Platform.Tests/bin/Release/net10.0/ToolUp.Platform.Tests.dll `
        --filter "ToolUp.Platform.Tests.Remoting" --sequenced

Then review the diff and commit it. A re-pin is a deliberate act: these bytes and this text are
what non-F# clients of the wire see, so a change here is a wire change whether or not both of our
own ends still agree.

**`-c Release` is load-bearing, not habit.** Under a build with the F# optimiser OFF — which is
what `verify.ps1` produces — the MsgPack writer emits short strings and decimals from a popped
stack frame and is not a function of its input; two runs produce two different byte strings.
Pinning from such a build would commit noise. The measurement, its controls and the regime probe
the suite runs before it asserts anything are in `WireCorpus.fs` under
"The writer's regime, measured before anything is asserted".

## Adding a case

Declare it in `WireCorpus.pinnedCases`, regenerate as above, commit the value and its two files
together. The suites check both directions — a fixture with no case and a case with no fixture are
each a failure — so a rename is one commit or none.
