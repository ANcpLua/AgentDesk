# Migration to AgentDesk

Completed on 2026-10-27.

- Production source is in `AgentDesk/`; MSTest source is in `AgentDesk.Tests/`.
- The solution, assemblies, namespaces, scripts and visible product name are AgentDesk.
- The empty Todo API, empty template tests, duplicate appsettings files and old
  timestamp-only SSE demo were removed. The durable inbox SSE endpoint remains.
- The template's `PublishAot` and `InvariantGlobalization` flags were replaced by
  the existing tested managed-host configuration. This migration does not claim
  NativeAOT support for the reflection-based MCP host.
- HTTP/MCP IDs, tool names, `QUESTION_INBOX_*` environment variables, loopback port
  5137 and the external `QuestionInbox/inbox-v3.db` database path are retained.
- The per-user service is `local.agentdesk`; the previous service registration was
  removed. The existing `question-inbox` MCP entry points at the new launcher.
- `AgentDesk` owns only its project integration skill. General assertions, MSTest,
  union and fixture guidance remain in the `nami-plugin` marketplace source.
  The reusable native messaging lessons were extracted to `codex-native-messaging`
  there. No general skill copies are created by AgentDesk's build.
- The old project directory and disposable migration-template backup were deleted
  after verification. The target repository's existing Git and Rider metadata
  were preserved. Experimental transcripts and old build outputs were not imported.

Validation: clean/build and publish succeeded; 46 MSTest, 27 HTTP/MCP and 9 UI tests
passed. The removed 47th MSTest covered only the deleted timestamp demo. The
negative union compiler probe still fails with CS8509 as intended. InspectCode
reports no application findings and one test-project hint. The new HTTP page and
stdio MCP startup were checked; the existing database contents were unchanged.

Build and detailed migration records are disposable artifacts in `.local/migration/`.
Central marketplace registration and installation are managed in the separate
skill-deduplication workflow. This project does not register another marketplace.
