# AgentDesk handoff

The application and test projects are `AgentDesk/AgentDesk.csproj` and
`AgentDesk.Tests/AgentDesk.Tests.csproj`, in `AgentDesk.slnx`.
The user-facing name is AgentDesk. Stable MCP/HTTP identifiers and the existing
SQLite location retain `question-inbox` / `QuestionInbox` for compatibility.

Start with `AGENTS.md`, the project integration skill and `README.md`.
Read `docs/architecture-direction.md` for the agreed next stage: retain the C#
MCP/domain host and SQLite, add a React frontend and a separate DuckDB analysis
projection. React and DuckDB are planned, not implemented. A native SwiftUI
menu-bar controller is implemented in `macos/AgentDeskMenu.swift`; install it
with `scripts/install-menubar.sh`. It controls the existing managed backend
without embedding or replacing it. The document records
the locally checked MCP feature flags and the limits on session imports.
General skills are owned by the existing `nami-plugin` marketplace. Do not copy
those skills back into this project. The broad cross-repository dedup inventory
is separate from this application's migration.

The native .NET 11 host serves TypeScript UI, SSE, HTTP and MCP on loopback port
5137. It uses the installed Codex daemon for bounded reads and `codex queue`
for explicit sends. No model API key is needed.

The persistence database lives outside the checkout, at
`~/Library/Application Support/QuestionInbox/inbox-v3.db` by default.
AgentDesk migration preserves it. Never automatically retry uncertain deliveries.

The old template Todo endpoint and timestamp-only SSE demonstration are removed.
The durable inbox SSE endpoint remains. Build output and experimental transcripts
are not production source. Tests use fake native Codex and isolated databases.
