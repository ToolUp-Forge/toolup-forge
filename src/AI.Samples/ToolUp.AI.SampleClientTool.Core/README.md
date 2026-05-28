# ToolUp.AI.SampleClientTool.Core

Apache-2.0 reference companion — shared types for the sample client-resident-tool calculator.

**Reference-only.** This package is not a production companion; it exists so the `IClientToolAuthorizer` + `ClientToolDispatch` substrate has an in-tree consumer that exercises the seam end-to-end. It also serves as a binding subject for the `IClientToolDispatchContract` portability pack.

## What's in here

- `CalcRequest { Op: string; A: float; B: float }` — wire shape the model emits.
- `CalcResponse { Result: float }` — wire shape the client handler posts back.
- `CalcOps.compute` — pure arithmetic, shared between the server-side preview and the client-side handler so both agree on division-by-zero (`infinity` / `nan` per IEEE 754, no exception).

## See also

- [`ToolUp.AI.SampleClientTool.Server`](../ToolUp.AI.SampleClientTool.Server/README.md) — server-side compose + tool registration.
- [`ToolUp.AI.SampleClientTool.Client`](../ToolUp.AI.SampleClientTool.Client/README.md) — Fable client-side handler + the ≤10-min companion-authoring walkthrough.
- [`docs/ai/extending.md`](../../../docs/ai/extending.md) — companion-authoring overview.
