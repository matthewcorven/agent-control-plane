# Spike: `Microsoft.AspNetCore.Components.AI` in the `InteractiveAuto` client (issue #3)

**Verdict: GO.** `Microsoft.AspNetCore.Components.AI` `11.0.0-preview.7.26425.128` constructs,
renders, streams and completes a turn in **both** phases of `InteractiveAuto` (server circuit
first, WebAssembly takeover on the second visit). No `Components.Web` version pin was needed.

## Working package matrix (.NET 11 RC2)

| Package | Version | Where |
|---|---|---|
| `Microsoft.AspNetCore.Components.AI` | `11.0.0-preview.7.26425.128` | `Client.Wasm` |
| `Microsoft.AspNetCore.Components.Web` | `11.0.0-rc.2.26504.105` (framework pin, untouched) | `Client.Wasm` |
| `Microsoft.Extensions.AI` | `10.11.0` client / `10.10.0` server | `Client.Wasm`, `Server` |
| `Microsoft.Agents.AI` | `1.23.0` | `Client.Wasm`, `Server` |
| `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` | `1.23.0-preview.260928.1` | `Server` |
| `AGUI.Abstractions` / `AGUI.Client` | `1.0.0` / `1.0.0` | `Server` / `Client.Wasm` |

The package declares `Microsoft.AspNetCore.Components.Web 11.0.0-rc.1.26425.128` as a
dependency; the solution-wide `11.0.0-rc.2.26504.105` wins by NuGet's nearest-wins rule and no
`ExcludeAssets="all"` workaround is required.

## Evidence

Probe page `src/Client.Wasm/Components/Pages/SpikeAi.razor` (`@page "/spike-ai"`, throwaway)
hosts `<ChatPage Agent="@_agent" />` with an in-process `SpikeEchoChatClient : IChatClient` that
streams Markdown in 12-character chunks.

| Observation | Server phase (1st visit) | WASM phase (2nd visit) |
|---|---|---|
| Page marker | `server` (`OperatingSystem.IsBrowser() == false`) | `wasm` |
| `ChatPage` renders (`sc-ai-root sc-ai-chat-page`) | yes | yes |
| Send button + textarea wired | yes | yes |
| Turn completes, user + assistant messages render | yes | yes |
| Streamed deltas accumulate into one message | yes | yes |
| Console / page errors | none | none |

`ai-chat.css` ships as a static web asset (`_content/Microsoft.AspNetCore.Components.AI/ai-chat.css`)
and is **not** auto-linked. `src/Client/Components/App.razor` now links it through `@Assets[...]`
so fingerprints and the import map resolve it. Without the link the components render unstyled.

## Negative findings (important for #6–#8)

1. **No `[ToolBlock]` attribute and no source generator ship in this package.** The nupkg contains
   only `lib/net11.0/Microsoft.AspNetCore.Components.AI.dll`, `staticwebassets/ai-chat.css`, and
   targets that only import `Microsoft.AspNetCore.StaticWebAssets.targets`. There is no
   `analyzers/` folder and `strings` on the assembly finds no `ToolBlock`. The `[ToolBlock]`
   generator is an AgenticUI concept, not part of the ASP.NET Core preview. Issue #8 must build
   block rendering from the shipped primitives instead:
   - `ContentBlockHandler<T>` + `UIAgentOptions.AddBlockHandler<T>(handler)` to map `AIContent`
     into a custom `ContentBlock`
   - `BlockRenderer<T>` with `ListContext` / `When` / `ChildContent` for the Razor side
   - `MessageListContext.RenderBlock(ContentBlock)` for imperative dispatch
   - `BlockMappingContext.MarkHandled(...)` / `MarkUpdateHandled()` to claim content
2. **The components do not parse Markdown into `RichTextContent` on their own.** Streaming plain
   Markdown text yields one `RichContentBlock` per response whose `RawText` keeps the raw source;
   the default renderer emits `sc-ai-rich-text__paragraph` per newline-separated chunk, so
   `**bold**`, `- item`, fenced code, `>` and tables stay literal. Issue #7 must produce
   `RichTextContent(text, nodes)` with a `RichTextNode` tree itself (the node types are all
   present: `HeadingNode`, `ParagraphNode`, `ListNode`/`ListItemNode`, `TableNode`/`TableRowNode`/
   `TableCellNode`, `CodeBlockNode`, `InlineCodeNode`, `StrongNode`, `EmphasisNode`,
   `StrikethroughNode`, `LinkNode`, `ImageNode`, `BlockQuoteNode`, `ThematicBreakNode`,
   `TextNode`, `LineBreakNode`, footnotes, definition lists, `HtmlNode`).
3. **`UIAgent` is provider-neutral.** It takes an `IChatClient` and the assembly references no
   `Microsoft.Agents.AI`. `AGUIChatClient.AsAIAgent()` returns an `AIAgent`, which is **not** an
   `IChatClient`, so #6 needs an adapter (or a direct `IChatClient` over the AG-UI transport)
   rather than passing the existing `AIAgent` through.
4. **Correction from #9: server state mapping APIs exist in `AGUI.Server 1.0.0`, not the
   hosting assembly inspected by this spike.** `AGUIStreamOptions` supports `MapResult`,
   `MapResultAsStateSnapshot`, and `MapResultAsStateDelta`. The convenience methods require
   JSON results; they do not turn a `{id,status}` DTO into RFC 6902 operations. #9 registers
   endpoint metadata mapping full snapshots and actual JSON Patch arrays, verified over SSE.
5. **Correction from #9: `ChatOptions.TryGetRunAgentInput` exists in `AGUI.Server`.**
   It recovers the originating input retained in `AdditionalProperties["agui_input"]`.
   `RunAgentInput` itself lives in `AGUI.Abstractions`, including `State` and `Resume`.
6. **Correction from #9: `ApprovalRequiredAIFunction` exists in
   `Microsoft.Extensions.AI.Abstractions 10.10.1`, namespace `Microsoft.Extensions.AI`.**
   The server emits approval interrupts and accepts typed AG-UI resumes. MAF response binding
   requires retained session state; the default no-op session store cannot safely resume.
   #9 uses the user-approved expiring, cookie-isolated in-memory store without disabling
   binding. The approved predictive adapter uses the same accept/reject transport.

These corrections establish **server** APIs, not missing **client** APIs. They do not make
`UIAgent<TState>`, `RegisterUIAction`, or the proposed Components.AI approval/state APIs
available. #10 must integrate the approved transport adapter and caller-cookie continuity;
see [Server scenario protocol](README.md#server-scenario-protocol-9).

`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` `1.27.0-preview.261030.1` is published; a bump is
worth evaluating in #5 but is not required by this spike.

## Consequences for the plan

- #4/#5 (provider abstraction, Foundry) are unaffected — they sit behind `IChatClient`.
- #6 must introduce an `IChatClient`-shaped path to the AG-UI backend.
- #7 must own Markdown → `RichTextContent` conversion.
- #8 must implement block renderers without a source generator.
- #10 can rely on `InteractiveAuto`; no per-page rendermode override is needed.
