---
name: agentdesk-integration
description: Extend or debug AgentDesk questions, native Codex dispatch, shared operator tasks, and the TypeScript UI. Use for this application's domain, persistence, HTTP/MCP transport, and lifecycle.
---

# AgentDesk integration

Read the affected source and `README.md`. The domain model is documented in
`docs/union-architecture.md`; project-specific async decisions are in
`docs/async-notes.md`. General MSTest, union and native messaging guidance belongs
to the shared `nami-plugin` source. Use the centrally installed skills; do not
vendor them here or manage marketplace registration from this project.

For the agreed frontend and analytics direction, read
`docs/architecture-direction.md`. React and DuckDB are planned additions, not
current dependencies; retain the existing C# host and authoritative SQLite state.

## Contracts

- Questions, answers, tasks and dispatch receipts live independently of agent turns.
- Stable protocol IDs remain `question-inbox`, `ui://question-inbox/view.html` and
  `QUESTION_INBOX_*`. The displayed product and .NET assemblies are AgentDesk.
- SQLite defaults to `~/Library/Application Support/QuestionInbox/inbox-v3.db`.
  Renaming the product must not reset or migrate this database implicitly.
- Owned domain outcomes use C# 15 unions; JSON families use closed records and
  explicit `kind` tags. External Codex statuses remain an open protocol.
- Concrete JSON cases reject unknown fields and require their payloads. Keep
  `RequiredDiscriminatorResolver` so missing tags yield an HTTP input error.
- Promote CS8509, CS8655 and CS8846 to errors; preserve explicit default rejection.
- The operator chooses 1–8 recipient UUIDs. Reserve dispatch before sending, retain
  per-target results and protect retries with request keys. Global send concurrency is 4.
- `queued` means accepted by Codex, not processed. `uncertain` is never auto-retried.
- Origin metadata is verified through the native read API. Never infer it from
  a display name or a shared MCP process's environment.

## Implementation

- Inject TimeProvider into I/O services and the SSE timer. Pass time as a value
  to pure domain functions and use FakeTimeProvider in tests.
- SQLite helpers are synchronous and execute under the caller's async gate.
  Acquire before try, release in finally, and keep network calls outside the gate.
- Reserved sends deliberately survive browser cancellation. Receipt persistence
  explicitly uses CancellationToken.None; native calls retain bounded deadlines.
- JetBrains UsedImplicitly annotations describe JSON/UI usage; do not delete DTO
  members or globally disable inspections to silence external-usage findings.
- Preserve discriminated TypeScript contracts and draft/focus behavior when
  updating the UI. A DOM test does not prove rendering in the ChatGPT host.

## Verification and operation

The solution contains AgentDesk and AgentDesk.Tests. Tests use MSTest.Sdk 4.5.1,
its existing custom entry point and method-level parallelism.

`./scripts/build.sh` builds the TypeScript UI and publishes the host.
`npm test` runs MSTest, isolated HTTP/MCP tests and jsdom tests.
`scripts/verify-union-coverage.py` checks the compiler's missing-case error.

For inspection use:

```sh
PATH="/usr/local/share/dotnet:$PATH" jb inspectcode AgentDesk.slnx --output=inspectcode.sarif
```

The per-user launchd service is `local.agentdesk`. `install-service.sh` updates it;
`uninstall-service.sh` removes its registration. The database lives outside the
checkout and is retained. See README for the single MCP connection to the app.

`macos/AgentDeskMenu.swift` is a native controller, installed through
`scripts/install-menubar.sh` into `~/Applications/AgentDesk.app`. It reuses the
existing lifecycle scripts and verifies the backend through `/health`. Never
kill an unrelated process just because it holds port 5137. The controller's
optional login item is separate from the backend launchd registration.
