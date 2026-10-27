---
name: question-inbox
description: Store durable questions for the human to answer later, open the questions inbox, or retrieve their answers across ChatGPT and Codex turns.
---

Use the question-inbox MCP tools for durable asynchronous questions.

- Use `ask_question` to create a question. It returns immediately; do independent work afterward.
- Include the actual project name, a stable task identifier in `conversationId`, helpful context, and options where useful. Set `blocking` only when the next dependent step needs the answer. The flag is descriptive, not a scheduler.
- Use a unique `requestKey` for a new question and reuse it on retries. Do not reuse a key for a different question.
- Keep returned question IDs. At the next natural continuation, read the relevant answers with `get_question` or filtered `list_questions`.
- Never poll repeatedly or sleep merely to keep a question alive. If all remaining work depends on an answer, explain the dependency and finish the turn.
- Pending, dismissed, missing, or elapsed time is not approval. Never answer on the human's behalf. Native command and file approvals stay with the host.
- Use `open_question_inbox` to show the interactive view. If unavailable, the browser view is at http://127.0.0.1:5137.
- A saved answer does not automatically wake a finished chat. The human may pass it to the chat from the app view or ask you to continue. Read the stored answer before continuing.
- Supply `originThreadId` only from your verified current `CODEX_THREAD_ID`. The service resolves the actual workspace, session, parent/fork references and transcript path from the running Codex server. A persistent MCP server's own environment is not the identity of every caller.
- For a decision, prefer 1-3 `choices`, each with `label`, estimated `impact`, and optional `followUpPrompt`. Explain uncertainty; these are predictions, not guaranteed outcomes. Add `goal` to state the intended result.
- Use `list_work_items`, `create_work_item`, and `update_work_item` to maintain compact operator/agent responsibilities and actual results. A work item does not start an agent. Never mark an item done without result evidence.
- Native thread inspection uses `list_codex_threads` / `read_codex_thread`. Read bounded summaries only when relevant; do not import entire transcripts.
- v0.3 uses discriminated objects: question `state.kind`, owner `{kind:operator}` or `{kind:agent,threadId}`, and work state `{kind:todo|in_progress|blocked|done}` with note/reason/evidence appropriate to the case. Decisions are `{kind:answer,text}` or `{kind:dismiss}`. Do not send the old flat status/answer/owner fields.
- Operator-driven dispatch sends through `codex queue` to explicit IDs. A `queued` receipt is not proof of processing. `uncertain` delivery must never be automatically retried.

This local plugin uses the built runtime in the AgentDesk repository on this computer. It does not require or export an OpenAI API key.
