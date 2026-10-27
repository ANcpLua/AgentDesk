# v0.3: functional core, typed variants

The application owns its workflow cases. Native Codex metadata remains a bounded
external protocol; native statuses are not forced into a closed set we do not own.

## Core

- `QueueOutcome` is a C# 15 `readonly union`: `QueueAccepted`, `QueueRejected`,
  `QueueUncertain`. Expected native outcomes are values, not an exception plus
  a Boolean that changes its meaning.
- `DomainResult<T>` is a `readonly union`: `Succeeded<T>`, `InvalidInput`,
  `StateConflict`. `Map` and `Bind` preserve errors and skip success callbacks.
  They do not catch arbitrary callback exceptions or promise allocation freedom.
- `QuestionRules` and `WorkRules` are pure. They receive values and the desired
  timestamp, and return new immutable records or an explicit failure.
- HTTP, SQLite, native Codex calls and the clock live outside those functions.
  `DomainBoundary` translates domain errors at the HTTP/MCP boundary.

## JSON and storage

Workflow families use C# 15 `closed record` roots and sealed cases because they
share an explicit polymorphic JSON contract. Every case uses a stable `kind` tag.
Concrete cases reject unmapped fields. Required payloads are marked as such.

```json
{ "kind": "pending" }
```

```json
{ "kind": "answered", "text": "Option A", "at": "2026-10-27T12:00:00+00:00" }
```

```json
{ "kind": "queued", "messageId": "native-message-id" }
```

An operator owner has only `{ "kind": "operator" }`; an agent owner additionally
requires `threadId`. A completed task requires `evidence`; a blocked task requires
`reason`. There are no parallel `status`/`error`/`answer` fields that can contradict
one another. Choices are a single array of `label`, optional `impact` and optional
`followUpPrompt`.

`RequiredDiscriminatorResolver` makes a missing `kind` an input error at the HTTP
boundary. Without that explicit metadata rule, the default serializer can throw
`NotSupportedException` for an abstract base and ASP.NET Core can report `500`.
Both direct serializer checks and real endpoint checks cover this boundary.

v0.3 uses `QuestionInbox/inbox-v3.db`. The earlier v0.2 prototype database is not
imported. This intentional schema reset was authorized by the user; no permanent
legacy adapter is kept. Existing native Codex conversations are unrelated to it.

## Exhaustiveness and default values

Every owned union/closed-family match is a switch expression, with all known
cases and an explicit null/default rejection where required. Production builds
promote `CS8509`, `CS8655`, and `CS8846` to errors. No catch-all hides future cases.

`scripts/verify-union-coverage.py` copies the production project into a temporary
directory, removes the `QueueRejected` match arm, and requires the real build to
fail with **error CS8509**. Runtime tests reject default unions separately; the
compiler cannot be relied on to diagnose every default/null hole.

## Time and tests

`TimeProvider.System` is registered once. Services, native request deadlines and
the inbox SSE timer receive the provider. Domain functions still take `now` explicitly.
`FakeTimeProvider` tests advance timestamps and timer streams without sleeping;
separate reads for origin verification and question creation remain separate.

Tests use the user's `AgentDesk.Tests`, `MSTest.Sdk/4.5.1`, its existing custom MTP
entry point and method-level parallelization. New tests do not need a model or
API key. The existing HTTP/MCP integration fixtures use an isolated database,
fake native socket and fake CLI, while browser tests use jsdom.

The union and MSTest references used for this design are the user-provided local
skills in `nami-plugin/plugins/nami/skills/csharp-union-types` and `skills/mstest`.
No full transcript or copied external skill bundle is required to understand it.
