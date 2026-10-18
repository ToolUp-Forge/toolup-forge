namespace ToolUp.Remoting.Analyzers

// =============================================================================
// Phase 776 — the rule set's metadata, and the document generated from it
// =============================================================================
//
// One record per diagnostic the package can raise. The rule reference
// (`docs/platform/analyzer-rules.md`) is RENDERED from this list by
// `renderDocument`, and the `Analyzers` leg of `VerifyAll` fails when the
// committed document differs from the render — so the published rule set and
// the code that enforces it cannot drift. Regenerate with:
//
//     dotnet run --project src/ToolUp.Remoting.Analyzers.Tests -- --emit-rules-doc docs/platform/analyzer-rules.md
//
// FSharp.Core only: nothing here needs the compiler.

module Rules =

    type RuleInfo = {
        /// The diagnostic code, e.g. `TUA001`.
        Id: string
        Title: string
        /// `Error` or `Warning` — the severity the editor / CLI host reports.
        Severity: string
        /// Off unless the consumer opts in.
        OptIn: bool
        /// Why the rule exists — what goes wrong when the code it flags ships.
        Rationale: string
        /// The form the rule steers to. Every message names it, so an author
        /// (or a code generator) learns the fix from the diagnostic itself.
        Sanctioned: string
        /// The runtime check this rule is the compile-time half of.
        RuntimeHalf: string
        /// The SDK version the rule first shipped in.
        Since: string
    }

    let all: RuleInfo list = [
        {
            Id = "TUR0001"
            Title = "Remoting API method without an authorisation classification"
            Severity = "Error"
            OptIn = false
            Rationale =
                "The Remoting dispatcher refuses to start when an API-record method carries none of the authorisation classifications. Finding it at edit time is strictly cheaper than finding it at server start."
            Sanctioned =
                "Classify the method: `[<RequiresRole>]`, `[<RequiresClaim>]`, `[<TenantScoped>]`, `[<AllowAnonymous>]` or `[<PublicEndpoint>]`. The codefix inserts the fail-closed placeholder `[<AllowAnonymous>]`, which must be replaced deliberately."
            RuntimeHalf = "The dispatcher's startup authorisation classifier."
            Since = "0.7.0"
        }
        {
            Id = "TUR0002"
            Title = "Unaudited Remoting method whose input carries a `[<PiiSafe>]` field"
            Severity = "Warning"
            OptIn = true
            Rationale =
                "Personal data flows through the method and nothing records that it did. Off by default: set `TOOLUP_REMOTING_ANALYZER_AUDIT=1` in the analyser host's environment to enable it."
            Sanctioned =
                "Annotate the method with `[<Audit(...)>]`. The codefix inserts `[<Audit(\"Custom:TODO\")>]` as a placeholder."
            RuntimeHalf = "The audit pipeline's per-method annotation."
            Since = "0.7.0"
        }
        {
            Id = "TUA001"
            Title = "Container resolution outside service registration"
            Severity = "Warning"
            OptIn = false
            Rationale =
                "A module that resolves services from the container by hand (`GetService`, `GetRequiredService`, `GetServices`, the keyed forms, or `HttpContext.RequestServices`) reaches substrate its declared seam grant never names, so the grant describes what the module declares rather than what it does."
            Sanctioned =
                "Resolve inside a registration factory (the lambda handed to `AddSingleton` / `AddScoped` / `AddTransient` / `AddKeyed*` / `TryAdd*` / `AddHostedService`) and pass the dependency to the handler as a value. An `IServiceProvider` implementation delegating from its own `GetService` member is not flagged."
            RuntimeHalf =
                "The seam-aware service provider, which refuses a resolution outside the module's grant at runtime."
            Since = "0.28.0"
        }
        {
            Id = "TUA002"
            Title = "`HttpClient` constructed without the platform egress handler"
            Severity = "Warning"
            OptIn = false
            Rationale =
                "A hand-built `HttpClient` sends its requests around the platform's outbound-call seam, so the egress policy, its audit and its deny decisions never see them."
            Sanctioned =
                "`PlatformHttpClient.create surface`, or `new HttpClient(PlatformHttpClient.wrap surface handler)` when a custom handler is needed. A construction whose arguments name `PlatformHttpClient` is not flagged."
            RuntimeHalf = "The server-side egress policy handler on the outbound-call seam."
            Since = "0.28.0"
        }
        {
            Id = "TUA003"
            Title = "`Async.RunSynchronously` outside a program entry point"
            Severity = "Warning"
            OptIn = false
            Rationale =
                "Blocking a thread-pool thread on an asynchronous computation starves the pool under load and turns a slow dependency into a stalled server; on a request path it is never correct."
            Sanctioned =
                "Stay asynchronous: compose with `let!` / `do!` in an `async` or `task` expression and return the computation. Blocking is sanctioned only inside an `[<EntryPoint>]` function."
            RuntimeHalf = "None: a blocked request thread has no runtime refusal, which is why this rule exists."
            Since = "0.28.0"
        }
    ]

    let tryFind (id: string) =
        all |> List.tryFind (fun r -> r.Id = id)

    /// The message an analyser reports for a rule: the title, then the
    /// sanctioned alternative, so the fix travels with the finding.
    let message (id: string) =
        match tryFind id with
        | Some r -> sprintf "%s: %s. Sanctioned form: %s" r.Id r.Title r.Sanctioned
        | None -> id

    /// The rule reference, rendered. Line endings are `\n`; the committed
    /// document is compared after normalising them.
    let renderDocument () : string =
        let sb = System.Text.StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line "# Analyzer rules"
        line ""

        line
            "<!-- GENERATED from src/ToolUp.Remoting.Analyzers/Rules.fs. Do not edit by hand: the `Analyzers` leg of VerifyAll fails when this file differs from the render. Regenerate with `dotnet run --project src/ToolUp.Remoting.Analyzers.Tests -- --emit-rules-doc docs/platform/analyzer-rules.md`. -->"

        line ""

        line
            "The `ToolUp.Remoting.Analyzers` package is the SDK's static analyser rule set: [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) analysers that read the untyped syntax tree, so they need no project options and run in the editor, on the CLI and in CI. It is versioned with the SDK. Each rule is the compile-time half of a runtime check, so a violation is caught twice, at the earlier of the two, and every message names the sanctioned alternative."

        line ""
        line "## Rules"
        line ""
        line "| Id | Title | Severity | Since |"
        line "|---|---|---|---|"

        for r in all do
            let sev = if r.OptIn then r.Severity + " (opt-in)" else r.Severity
            line (sprintf "| `%s` | %s | %s | %s |" r.Id r.Title sev r.Since)

        for r in all do
            line ""
            line (sprintf "### %s — %s" r.Id r.Title)
            line ""
            line (sprintf "- **Severity:** %s%s" r.Severity (if r.OptIn then " (opt-in)" else ""))
            line (sprintf "- **Since:** %s" r.Since)
            line (sprintf "- **Rationale:** %s" r.Rationale)
            line (sprintf "- **Sanctioned alternative:** %s" r.Sanctioned)
            line (sprintf "- **Runtime half:** %s" r.RuntimeHalf)

        line ""
        line "## Wiring the analysers into a consumer project"
        line ""
        line "Reference the package; it contributes no compile or runtime surface:"
        line ""
        line "```xml"
        line "<ItemGroup>"

        line
            "  <PackageReference Include=\"ToolUp.Remoting.Analyzers\" Version=\"$(ToolUpSdkVersion)\" PrivateAssets=\"all\" />"

        line "</ItemGroup>"
        line "```"
        line ""

        line
            "Then point the analyser host at the package's `analyzers/dotnet/fs` directory. With the `FSharp.Analyzers.Build` MSBuild integration (and the `fsharp-analyzers` tool installed), set the `FSharpAnalyzersOtherFlags` property and run the `AnalyzeFSharpProject` target:"

        line ""
        line "```xml"
        line "<PropertyGroup>"

        line
            "  <FSharpAnalyzersOtherFlags>--analyzers-path &quot;$(PkgToolUp_Remoting_Analyzers)/analyzers/dotnet/fs&quot;</FSharpAnalyzersOtherFlags>"

        line "</PropertyGroup>"
        line "```"
        line ""

        line
            "`$(PkgToolUp_Remoting_Analyzers)` is defined when the reference carries `GeneratePathProperty=\"true\"`. From the command line the same run is:"

        line ""
        line "```pwsh"
        line "dotnet tool install --global fsharp-analyzers"
        line "fsharp-analyzers --project MyProject.fsproj --analyzers-path <restored-package>/analyzers/dotnet/fs"
        line "```"
        line ""
        line "## How this repository applies the rules"
        line ""

        line
            "The `Analyzers` leg of `VerifyAll` parses every tracked `.fs` file under `src/` and runs the `TUA` rules over it. The committed inventory `src/ToolUp.Remoting.Analyzers/analyzer-inventory.json` declares the paths out of scope (test packs, benchmarks, tools) and carries each known site as a per-file count with a one-line reason. A site the inventory does not account for is a finding, and so is an inventory entry the tree no longer needs, so the inventory can only be tightened deliberately. Any finding fails the leg."

        sb.ToString()