# AgentDesk

Lokales Human-in-the-Loop-Postfach für ChatGPT Desktop und Codex. Fragen und
Aufgaben sind dauerhaft; Nachrichten gehen bewusst über `codex queue` an
bestehende Thread-IDs. Kein Modell-API-Aufruf, kein OpenAI-/Azure-Key erforderlich.

## Benutzen

Öffne **http://127.0.0.1:5137**.

- **Fragen:** Auswahl oder Freitext, Ziele, eingeschätzte Auswirkungen und Folgeprompts.
- **Codex-Threads:** native Metadaten, konkrete IDs, Projektpfade, Status und letzte
  begrenzte Zusammenfassung. 1–8 Empfänger auswählen und eine Nachricht senden.
- **Gemeinsame Aufgaben:** Operator oder konkreter Agent-Thread als Verantwortlicher;
  offene, laufende, blockierte und abgeschlossene Aufgaben mit Ergebnisnachweis.
- **Antwort an Codex senden:** Antwort prüfen, Empfänger wählen und bewusst absenden.
  Ein Queue-Beleg bedeutet angenommen, nicht gelesen oder erledigt.

`queued`, `failed` und `uncertain` sind getrennte Varianten. Bei unklarer Zustellung
nicht automatisch erneut senden. Ein Request-Key verhindert doppelte Zustellungen.

## Build und Tests

Voraussetzungen: .NET 11 SDK, Node.js 24+ und npm. Verifiziert mit
`11.0.100-rc.3.26522.148` und C# 15. Kein erzwungenes Upgrade vorhandener SDKs.

```sh
npm ci
npm run build:ui
dotnet clean AgentDesk.slnx
dotnet build AgentDesk.slnx
dotnet test --solution AgentDesk.slnx --no-build --report-trx --report-trx-filename AgentDesk.Tests.trx --results-directory .local/test-results
python3 scripts/verify-union-coverage.py
python3 -m unittest discover -s tests -v
node --test tests/inbox-ui.test.mjs
```

Für HTTP-Tests muss die veröffentlichte App in `.local/runtime` aktuell sein:

```sh
./scripts/build.sh
npm test
```

`AgentDesk.Tests` verwendet die vom Nutzer gewählte **MSTest.Sdk 4.5.1**,
Microsoft.Testing.Platform, den bestehenden eigenen Programmeinstieg und
Method-Level-Parallelisierung. `global.json` wählt den nativen MTP-Testmodus.

Die C#-Tests prüfen reine Zustandsentscheidungen, explizite JSON-Verträge und FakeTimeProvider.
HTTP/MCP-Tests erhalten pro Test eine eigene temporäre Datenbank, einen Fake-Codex-Socket
und ein Fake-CLI; sie erreichen keine echten Agenten. Cleanup wird bereits beim Setup
registriert. UI-Tests laufen mit jsdom und prüfen auch Anzahl und Nutzdaten der Requests.
Der Compiler-Test lässt eine temporäre Projektkopie absichtlich an CS8509 scheitern.

## Lokal betreiben

```sh
./scripts/build.sh
./scripts/install-service.sh
```

Der macOS-Benutzerdienst `local.agentdesk` startet bei Anmeldung und
hält den Webserver unabhängig von kurzlebigen MCP- oder Terminalprozessen bereit.
Logs und Runtime liegen unter `.local/`. `stop-server.sh` stoppt den Dienst;
`uninstall-service.sh` entfernt nur seinen Login-Eintrag.

**v0.3 hat absichtlich ein neues Datenformat.** Die Standarddatenbank ist jetzt
`~/Library/Application Support/QuestionInbox/inbox-v3.db`. Die ältere v0.2-Datei
`questions.db` wird nicht importiert oder verändert. Die damaligen Einträge waren
vom Nutzer als veränderbare Prototypdaten freigegeben. Eigene v0.2-Datenbankpfade
müssen für v0.3 ebenfalls auf eine neue Datei zeigen. Native Codex-Verläufe sind
von diesem Wechsel nicht betroffen.

## MCP verbinden

In ChatGPT Desktop unter **Plugins → Add → Add MCP server**:

| Feld | Wert auf diesem Computer |
| --- | --- |
| Name | `question-inbox` |
| Command | `/bin/sh` |
| Argument | `/Users/alexandernachtmann/RiderProjects/AgentDesk/scripts/start-mcp.sh` |

Oder über die lokale Codex-Konfiguration:

```sh
codex mcp add question-inbox -- /bin/sh /Users/alexandernachtmann/RiderProjects/AgentDesk/scripts/start-mcp.sh
```

Den Server nicht doppelt verbinden. Nach Änderungen an Tools/Schema gegebenenfalls
MCP neu laden oder einen neuen Chat öffnen. Die Plugin-Quelle unter
`plugins/question-inbox/` bündelt dieselbe Verbindung mit einem Workflow-Skill.
Sie ist bewusst an dieses lokale Repository gebunden.

## v0.3-Werkzeuge und Daten

`ask_question` nimmt `choices` als ein einziges Array entgegen:

```json
{
  "prompt": "Welchen nächsten Schritt möchtest du?",
  "originThreadId": "<eigene verifizierte Thread-UUID>",
  "goal": "Den Ablauf mit wenig Kontextballast prüfen.",
  "choices": [
    {"label": "Einzeltest", "impact": "Wenig Aufwand", "followUpPrompt": "Prüfe einen ausgewählten Thread."},
    {"label": "Vergleich", "impact": "Mehr parallele Arbeit", "followUpPrompt": "Vergleiche die ausgewählten Threads."}
  ]
}
```

Die Antwort ist ein eigener Fall, z. B. `state: {"kind":"answered","text":"Einzeltest","at":"..."}`.
`submit_question_answer` nimmt `decision: {"kind":"answer","text":"Einzeltest"}`
oder `decision: {"kind":"dismiss"}` plus `expectedVersion` entgegen.

`dispatch_to_codex` nimmt `content: {"kind":"message","text":"..."}` oder
`content: {"kind":"answer","questionId":"...","note":"..."}` entgegen.
Die Herkunft einer weitergereichten Antwort kommt aus der gespeicherten Frage.

Weitere Werkzeuge: `list_questions`, `get_question`, `open_question_inbox`,
`list_codex_threads`, `read_codex_thread`, `list_dispatches`, `list_work_items`,
`create_work_item`, `update_work_item`. Vollständige HTTP-Beispiele stehen in
`AgentDesk/AgentDesk.http`.

Die MCP-App nutzt den standardisierten Host-Bridge; die Browseransicht verwendet
SSE. Der Zustand liegt im Backend. Die Darstellung der eingebetteten Ansicht
muss zusätzlich in der jeweiligen ChatGPT-Version geprüft werden.

## Architektur

`Domain/` enthält C#-15-Unions (`QueueOutcome`, `DomainResult<T>`), geschlossene
JSON-Varianten und reine `QuestionRules`/`WorkRules`. `Questions/` und `Workspace/`
führen I/O aus. `Codex/` liest die vorhandene native WebSocket-Schnittstelle und
verwendet das installierte `codex queue`; `Infrastructure/` enthält HTTP-/JSON-Grenzen.
`ui/contracts.ts` bildet dieselben Varianten als TypeScript-Discriminated-Unions ab.

TimeProvider ist injiziert. FakeTimeProvider-Tests verschieben Zeit und Timer;
reine Regeln bekommen den Zeitpunkt als Wert. Exhaustiveness-Diagnosen CS8509,
CS8655 und CS8846 sind im Produktionsprojekt Fehler.

## Konfiguration

- `QUESTION_INBOX_DB`: absolute v0.3-Datenbankdatei.
- `QUESTION_INBOX_URL`: Loopback-URL für Launcher/MCP, Standard `http://127.0.0.1:5137`.
- `QUESTION_INBOX_DOTNET`: dotnet-Pfad für den Launcher.
- `QUESTION_INBOX_CODEX`: Codex-Binary, Standard `/opt/homebrew/bin/codex`.
- `QUESTION_INBOX_CODEX_SOCKET`: lokaler App-Server-Socket; standardmäßig
  `$CODEX_HOME/app-server-control/app-server-control.sock` mit `~/.codex` als Fallback.
- `QUESTION_INBOX_QUEUE_TIMEOUT_SECONDS`: 1–60 Sekunden, Standard 20.
- `QUESTION_INBOX_CONTENT_ROOT`: Verzeichnis mit `wwwroot` bei manuellem Start.

Dies ist ein lokaler Einbenutzerdienst. Keine privaten Codex-Datenbanken werden
verändert, keine ganzen Chats kopiert und keine nativen Freigaben ersetzt.

## Wissen für weitere Agenten

- [Vereinbarte Architektur und nächste Ausbaustufe](docs/architecture-direction.md)
- [Projekt-Skill](.agents/skills/agentdesk-integration/SKILL.md)
- [Union-Architektur](docs/union-architecture.md)
- [Async-Entscheidungen](docs/async-notes.md)
- [Handoff](HANDOFF.md)

Allgemeine Skills werden in der bestehenden `nami-plugin`-Quelle gepflegt und
über den zentral verwalteten Marketplace bereitgestellt. Registrierung und
Installation werden getrennt von diesem Projekt verwaltet.
AgentDesk enthält keine zweite Kopie der MSTest-/Assertions-/Union-Skills.
Die appgebundene Plugin-Quelle bleibt unter `plugins/question-inbox/`; ihre
MCP- und Ressourcenkennungen bleiben für bestehende Verbindungen stabil.
