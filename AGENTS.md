# AgentDesk

AgentDesk is the local operator workspace for durable questions, native Codex
messaging and shared tasks. Keep project-specific knowledge here; reusable skills
are maintained in the shared `nami-plugin` source, not copied into this repository.
Central marketplace registration and installation are managed separately; do not
register another marketplace as part of ordinary AgentDesk work.

For application changes read `.agents/skills/agentdesk-integration/SKILL.md`.
Use `README.md` for operation and `docs/union-architecture.md` for the domain model.

Run `./scripts/build.sh` and `npm test` for affected application changes.
Tests must use their isolated databases and fake Codex transport. Real messages
to existing threads require task-specific user authorization.

Keep the existing database and stable HTTP/MCP contracts. Do not edit Codex's
private state, import complete chat histories, or alter global model settings.
