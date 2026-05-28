# NOTICE

ToolUp Platform
Copyright 2024–2026 ToolUp Analytics Ltd.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

---

## Curated third-party attributions

This product includes software developed by the following projects. The
exhaustive package-by-package listing — including transitive dependencies
and their licence texts — is regenerated at build time and lives in
`THIRD_PARTY_NOTICES.md`. The curated list below names the major
direct dependencies and their licences for at-a-glance attribution.

### Server-side runtime and framework

- **Giraffe** — F# functional web framework. Apache License 2.0.
  Copyright (c) Dustin Moris Gorski. https://github.com/giraffe-fsharp/Giraffe
- **ASP.NET Core** — Web framework substrate. MIT License.
  Copyright (c) .NET Foundation and Contributors. https://github.com/dotnet/aspnetcore
- **F# Core (FSharp.Core)** — F# runtime and standard library. MIT License.
  Copyright (c) Microsoft Corporation. https://github.com/dotnet/fsharp
- **Newtonsoft.Json** — JSON serialisation. MIT License.
  Copyright (c) 2007 James Newton-King. https://github.com/JamesNK/Newtonsoft.Json

### Client-side runtime and framework

- **Fable** — F# to JavaScript compiler. MIT License.
  Copyright (c) Alfonso Garcia-Caro. https://github.com/fable-compiler/Fable
- **Elmish** — Elm-style MVU library for F#. Apache License 2.0.
  Copyright (c) Eugene Tolmachev. https://github.com/elmish/elmish
- **Feliz** — React bindings for Fable. MIT License.
  Copyright (c) Zaid Ajaj. https://github.com/Zaid-Ajaj/Feliz
- **Fable.Remoting** — Type-safe RPC over HTTP. MIT License.
  Copyright (c) Zaid Ajaj. https://github.com/Zaid-Ajaj/Fable.Remoting
- **React** — UI library. MIT License.
  Copyright (c) Meta Platforms, Inc. and affiliates. https://github.com/facebook/react
- **Vite** — Frontend build tool. MIT License.
  Copyright (c) 2019-present, Yuxi (Evan) You and Vite contributors. https://github.com/vitejs/vite
- **Tailwind CSS** — Utility-first CSS framework. MIT License.
  Copyright (c) Tailwind Labs, Inc. https://github.com/tailwindlabs/tailwindcss

### UI components

- **AG Grid Community** — Data grid component. MIT License.
  Copyright (c) AG Grid Ltd. https://github.com/ag-grid/ag-grid
- **AG Charts Community** — Charting library. MIT License.
  Copyright (c) AG Grid Ltd. https://github.com/ag-grid/ag-charts
- **@dnd-kit** — Drag-and-drop primitives for React. MIT License.
  Copyright (c) Claudéric Demers. https://github.com/clauderic/dnd-kit

Note: ToolUp deployments that opt into the `ToolUp.AgGridEnterprise`
companion package will additionally pull in `ag-grid-enterprise` and
`ag-charts-enterprise`, which are NOT covered by Apache 2.0 — they ship
under AG Grid's commercial licence and require a separately purchased
licence key. The Enterprise companion is opt-in; deployments that omit it
operate on Community-tier components only.

### Cloud storage SDKs (opt-in companions)

- **Azure.Storage.Blobs**, **Azure.Identity**, **Azure.Security.KeyVault.Secrets** — Azure SDKs. MIT License.
  Copyright (c) Microsoft Corporation. https://github.com/Azure/azure-sdk-for-net
- **AWSSDK.S3** — AWS SDK for .NET. Apache License 2.0.
  Copyright (c) Amazon.com, Inc. or its affiliates. https://github.com/aws/aws-sdk-net
- **Google.Cloud.Storage.V1** — Google Cloud Storage SDK. Apache License 2.0.
  Copyright (c) Google LLC. https://github.com/googleapis/google-cloud-dotnet

### Notification, search, and data primitives

- **StackExchange.Redis** — Redis client. MIT License.
  Copyright (c) Stack Exchange. https://github.com/StackExchange/StackExchange.Redis
- **MailKit** — Email transport. MIT License.
  Copyright (c) .NET Foundation and Contributors. https://github.com/jstedfast/MailKit
- **WebPush** — RFC 8030 web push notifications. MPL 2.0.
  Copyright (c) Coen Stevens. https://github.com/web-push-libs/web-push-csharp
- **HNSW** — Hierarchical Navigable Small World graph (vector store). MIT License.
  Copyright (c) Microsoft Corporation. https://github.com/microsoft/HNSW.Net

### Document parsing

- **PdfPig** — PDF parsing library. Apache License 2.0.
  Copyright (c) Eliot Jones. https://github.com/UglyToad/PdfPig
- **DocumentFormat.OpenXml** — Office document parsing. MIT License.
  Copyright (c) Microsoft Corporation. https://github.com/dotnet/Open-XML-SDK

### Numerical computing

- **Math.NET Numerics** — Mathematical primitives. MIT License.
  Copyright (c) 2002-2024 Math.NET. https://github.com/mathnet/mathnet-numerics

### Build tooling and tests

- **Microsoft.Extensions.Logging.Abstractions** — Logging primitives. MIT License.
  Copyright (c) .NET Foundation and Contributors.
- **Expecto** — F# testing framework. Apache License 2.0.
  Copyright (c) Anthony Lloyd, Henrik Feldt, and contributors. https://github.com/haf/expecto
- **FAKE** — F# build automation. Apache License 2.0.
  Copyright (c) Steffen Forkmann and contributors. https://github.com/fsprojects/FAKE
- **Farmer** — F# infrastructure-as-code DSL. MIT License.
  Copyright (c) Compositional IT and contributors. https://github.com/CompositionalIT/farmer
- **Fantomas** — F# code formatter. MIT License.
  Copyright (c) The Fantomas Project. https://github.com/fsprojects/fantomas

---

The licences listed above are reproduced in their entirety in
`THIRD_PARTY_NOTICES.md`, regenerated by `dotnet run -- ThirdPartyNotices`
from the live dependency graph. If you redistribute ToolUp or a derivative
work, you must also retain a copy of the relevant third-party licence
texts as required by each licence.
