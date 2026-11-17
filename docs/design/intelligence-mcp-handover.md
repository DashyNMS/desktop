# Intelligence MCP handover: Desktop acceptance

Recorded 16 November 2026 for epic #588 (Mobile batches 1–6, DashyNMS/mobile#236), delivered in #597 with DashyNMS/core#89. This page records what was decided, what was checked and what remains. Each item says which kind of evidence backs it: an automated test, CI, or a manual check.

## Decision: keep the bundled MCP process (#595)

Desktop keeps running the standalone DashyNMS MCP server as a private child process over stdio. It doesn't adopt `DashyNMS.Agent.Shared` in-process, and it doesn't reference `DashyNMS.Agent` or call `AddDashyNmsAgent`.

- **User benefit.** Every P1 fix in this epic was possible on the process path. Shared would bring typed results and one process fewer. Users wouldn't notice either, so neither justifies a migration now.
- **Runtime.** Desktop targets .NET 9 and Shared targets .NET 10, so Desktop can't consume it directly. Adopting it means moving Desktop to .NET 10 first. That is a separate decision with its own Release, trimming and platform checks.
- **Packaging.** The process is already a self-contained single-file executable per platform. It now ships with its licence, `NOTICES.md` and a verified `third-party/` inventory (#589). In-process adoption would move those dependencies into the app's own graph.
- **Maintenance.** The process boundary keeps LibreNMS, Unimus and Graylog credentials out of the app's address space for tool work. Desktop's own boundaries then stay small: settings in, text out, a restart when settings change. Shared would add host-boundary adapters per tool and generation-scoped clients.

Revisit this when Desktop moves to .NET 10, or when a feature needs typed results in the UI.

### Unimus short hostnames

Desktop saves LibreNMS's discovery domain (**Settings › Unimus**, `UnimusSettings.MyDomain`), and its own config-backup pages use it. MCP #148 added the matching option only for in-process hosts (`AgentOptions.UnimusMyDomain`), and the standalone server has no setting for it. Matching is needed: a LibreNMS device named `core-sw1` can't match a Unimus `core-sw1.example.net` without the domain.

This is **explicitly deferred** to DashyNMS/mcp#157. That issue asks for a documented standalone setting, with fixtures covering backup, diff, scoped recent changes and `what_changed` under full, summary and off policies. Desktop invents no variable of its own. Once a reviewed MCP commit exists, Desktop pins it and passes the saved domain under the documented name. Its server identity already covers every value it passes, so a change restarts the server.

Remaining risk until then: Intelligence's config tools can miss Unimus devices that need the domain to match, while the desktop pages find them.

## Baseline (#589)

| | Before | Now |
|---|---|---|
| Bundled MCP | `61161c4` | `8f32942c194132fa7152f1e0f8e8448be7a3ac8e` (the reviewed source, MCP `1.1.0`) |
| Core in the MCP executable | n/a | `2.0.0-ci.64` (MCP's own pin; a separate process, so no shared graph with the app) |
| Core in the app | `2.0.0-ci.59` | `2.0.0-ci.71` (includes DashyNMS/core#89) |

- **Every packaging path** pins the same commit: `dev-build.yml`, `mcp-bundle.yml` and both jobs of `package-build.yml`.
- **mcp#156** (VLAN and rule details on LibreNMS 26.11, needed by #587) is still open, so no newer compatibility fix is lost by this pin. #587 moves the pin again once that is published.
- **Licences.** MCP is MIT-licensed from `4312052`. `Publish-Mcp.ps1` copies `LICENSE` and `NOTICES.md`, and fills `third-party/` with MCP's own `collect-dependency-notices.py`:
  - Supplements are read byte-exact from git, so a CRLF checkout can't fail their hashes.
  - Only packages that put runtime files in the executable are included: 33 at this pin. `Microsoft.NET.ILLink.Tasks` is a build-only analyzer, so it's left out.
  - The self-contained .NET runtime pack's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` are included too.
  - `McpBundleTests.The_bundle_carries_the_network_tools_licence_and_every_dependency_notice` checks every inventory entry on the installed Windows, macOS and Linux packages.
- **Tools.** `McpBundleTests` checks that all 24 allowlisted tools are offered with descriptions and object schemas, and calls each with an ordinary question against the demo over real stdio:
  - Results are JSON within `MCP_MAX_RESULT_BYTES`.
  - Unconfigured Unimus and Graylog tools say so.
  - The demo draws SVG graphs, which `graph` refuses, so that call only checks that the refusal is a fixed message with no token or address. Raster graphs are covered by the PDF check below and by real servers.

## Child issues and evidence

| Issue | Outcome | Evidence |
|---|---|---|
| #589 baseline | Pinned, notices bundled, all tools exercised | `McpBundleTests` (7 cases), the Native network tools workflow on five runtimes |
| #590 sensor thresholds | Fixed: every saved bound and the override reach the server as invariant `MCP_*` values from one snapshot. Invalid values stop the start with a message naming only the section. A saved change starts a new server and discards pending results. | `McpSensorAssessmentTests`: mapping, `de-DE` culture, NaN/∞, misordering, identity. Bundle parity with the Health tab under five threshold scenarios, override on and off, device limits and unassessed readings. |
| #591 Spotlight alerts | Fixed: all-state lookup as in mcp#154, exact ID, source, parent and rule checks, and the returned ID checked | `IntelligenceSpotlightAlertTests` over real HTTP: 26.9 `/alerts` and 26.10 `/faults` in every state, colliding IDs, mismatches, refused reads, cancellation. `IntelligenceObjectChatTests`: chat, draft and Spotlight kept on failure or a switch. |
| #592 uncertainty | Audited: Desktop and Core forward tool text unchanged. One gap fixed: withheld images are now noted to the model. | Core `IntelligenceToolEvidenceTests`: byte-for-byte payloads, replay, refusal vs zero, unassessed sensors, truncation, images. Desktop `McpToolResultTests` and a bundle check that unconfigured sources arrive as unknown. |
| #593 retirement | Fixed: each server is a generation. Sign-out, a switch (same URL with another token, A→B→A) or a saved setting retires it at once. Its lookups return a fixed "discarded" result. An old failure never stops a newer server. Unresponsive servers aren't reused, and a stop during start wins. | `McpToolHostGenerationTests` (deterministic, 14 cases, including a sign-out or switch during startup, a timed-out lookup and lookups alongside a failed one) |
| #594 trust boundary | Fixed in Core: instructions state checked kinds and IDs only, so Spotlight names never reach them. Names returned by lookups still reach the model as tool data, as PRIVACY.md says. Desktop has no `FilterName`. | Core `IntelligenceSpotlightTrustTests` (exact request body and roles, hostile and invalid identities, replay). Desktop: hostile names for every kind, including a restored chat. |
| #595 integration path | Decision above. Unimus domain deferred to mcp#157. | This page |
| #596 chat comparison | Two defects fixed: an unfinished code fence swallowed the rest of a Markdown export, and a starter asked for an unsupported fleet uptime ranking. The rest already behaved correctly and now has regression tests. | `MarkdownFenceTests`, `IntelligenceStarterQuestionsTests`, `IntelligenceSingleChatTests` (deleted and temporary chats never written back), `IntelligenceChatExportTests` (links survive a move), `IntelligenceObjectChatTests`. PDF inspected below. |

### Prompt trust, in detail

- **System instructions:** `device device_id=12; device group group_id=3; sensor sensor_id=73 on device_id=12; alert alert_id=5 on device_id=12 (rule_id=4)`. Built only from a fixed vocabulary and integers, and bounded to Desktop's 32-object Spotlight.
- **User message:** the question only.
- **Tool outputs:** network names. The base instructions already treat these as data.

Redaction and identity validation stay independent of this. Spotlight objects are still rechecked on the current connection before they're added.

### Native PDF check

A chat with two raster graphs, an answer stopped inside a `~~~` block holding a three-backtick line, and a later question was written with `IntelligenceChatPdfWriter`, rasterised with Poppler and inspected page by page. Both graphs render in their frames. The code block keeps the inner fence and the `# not a heading` line as code. The later question, heading and bullets render as ordinary text.

## Native runtime checks

| Platform | How | Result |
|---|---|---|
| Windows x64 | Local build of MCP `8f32942` with notices; all bundle, parity and generation tests | Passed |
| Windows x64, macOS Intel and Apple Silicon, Linux x64 and ARM64 | Native network tools workflow on #597: native publish, then `McpBundleTests` against the executable inside the published app | See #597's checks |
| All five, packaged | Native packages workflow on the PR branch (preview build): installer, `.dmg`, AppImage, with `McpBundleTests` against the installed bundle | See #597 |

Still manual:
- A signed, notarised macOS preview opened through Gatekeeper.
- Intelligence against a real LibreNMS 26.10 server with Unimus and Graylog.
- A small set of real model answers about hostile names, missing sources and capped histories. This needs a ChatGPT account, and model wording isn't deterministic, so the automated tests assert the exact model input instead.

## Upstream limits not fixed here

The handover doesn't fix these, and Desktop doesn't claim them as fixed:

- **DashyNMS/mcp#121:** tools reporting ignored and unknown device states.
- **DashyNMS/mcp#71:** `health_overview` across a large network without global read.
- **DashyNMS/mcp#70:** event-log windows on busy networks only reach back a few hours.

When a source is missing or sampled, the tools say so, and the model is told to report it as unknown. Neither the app nor the model treats it as healthy, empty or complete.
