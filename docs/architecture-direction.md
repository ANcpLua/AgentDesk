# AgentDesk: Architektur und nächste Ausbaustufe

Stand: 27. Oktober 2026. Mit dem Nutzer vereinbarte Richtung; die unten als
geplant bezeichneten Komponenten sind noch nicht implementiert.

## Produktziel

AgentDesk ist der persönliche Arbeitsplatz für die Zusammenarbeit zwischen
Operator und Codex-Agenten. Fragen, Entscheidungen und Aufgaben überleben einen
Agent-Turn, einen UI-Wechsel und einen Neustart. Der Operator kann nachdenken,
während Agenten an unabhängigen Aufgaben weiterarbeiten.

Die Oberfläche soll schön, persönlich und austauschbar sein. Unterschiedliche
Ansichten verwenden denselben dauerhaften Kern. Agenten dürfen konkrete
Verbesserungen vorschlagen; Beobachtungen werden nicht automatisch zu
Benutzervorlieben, Berechtigungen oder Änderungen an der Anwendung.

## Vereinbarte Zuständigkeiten

| Bereich | Entscheidung | Status |
| --- | --- | --- |
| Anwendungslogik | C# 15 / .NET 11; Functional Core, Imperative Shell | Vorhanden |
| Verbindlicher Zustand | SQLite für Fragen, Antworten, Aufgaben und Zustellbelege | Vorhanden |
| MCP-Server | C# im bestehenden Host; keine zweite Geschäftslogik in TypeScript | Vorhanden |
| Oberfläche | React 19, TypeScript, Tailwind 4 | Geplant; aktuell direktes TypeScript/DOM |
| ChatGPT-Ansicht | MCP Apps; Apps SDK UI als Komponentenbasis | MCP-App vorhanden; React-Umbau geplant |
| Persönliche Gestaltung | Geeignete React Bits Pro App-UI-/Agent-Komponenten | Geplant |
| Sitzungsanalyse | DuckDB als rekonstruierbare Analyse-Datenbank | Geplant |
| macOS-Integration | Dünne SwiftUI-Menüleisten-App für Öffnen, Start und Stop | Vorhanden |

Der in Go geschriebene TypeScript-Compiler ist kein Grund, das Backend in Go
umzuschreiben. Auch Rust, MAUI oder Aspire sind keine Voraussetzung für diese
Ausbaustufe. Aspire bleibt eine mögliche Hilfe für Entwicklung und Diagnose.
Die MCP-Server-Sprache ist unabhängig von der Sprache der Oberfläche.

Die native Steuerung liegt unter `macos/AgentDeskMenu.swift`. Sie öffnet die
Weboberfläche, steuert den bestehenden Backend-Dienst und zeigt dessen Health-
Status. Sie ersetzt weder den Web-Client noch den C#-Kern. Installation und
Lebenszyklus sind im README beschrieben.

## Zustand und Seiteneffekte

Das Gesamtsystem ist ausdrücklich zustandsbehaftet. Reine Regeln berechnen aus
Zustand, Eingabe und expliziter Zeit eine Entscheidung. Die äußere Anwendung
übernimmt Datenbankzugriffe, Prozessaufrufe, HTTP, MCP und Ereigniszustellung.

Die vorhandenen Union-Verträge und injizierter TimeProvider bleiben die Basis.
Ein vollständiger Event-Sourcing-Umbau ist nicht beschlossen. Insbesondere ist
ein Codex-Transkript kein Ersatz für die verbindlichen AgentDesk-Daten.

Browser und ChatGPT-Ansicht greifen auf denselben Kern zu. UI-Zustand wie
Auswahl, Entwurf oder aufgeklappte Karte ist vom dauerhaften Fachzustand getrennt.
Eine geschlossene Oberfläche oder MCP-Verbindung schließt keine offene Frage.

## MCP: geprüfter Stand und Grenzen

AgentDesk referenziert `ModelContextProtocol.AspNetCore` 2.3.0. Die offizielle
C#-SDK-Linie ab 2.0 unterstützt `2026-07-28` und ältere Protokollrevisionen.
Das offizielle TypeScript-SDK unterstützt ebenfalls beide Generationen.
Ein Wechsel der Server-Sprache ist für die neue Revision nicht nötig.

Lokal geprüft am 27. Oktober 2026:

- Installierte CLI: `codex-cli 0.164.0`.
- Die Binary enthält `2026-07-28`, `server/discover` und die neue Metadatenkennung.
- `codex features list` meldete `mcp_2026_07_28` und
  `codex_apps_mcp_2026_07_28` als `under development`, jeweils `false`.
- Die vorhandenen HTTP-/stdio-Akzeptanztests verwenden `2025-11-25`.

Das belegt Implementierung und lokale CLI-Konfiguration, keinen erfolgreichen
End-to-End-Test der neuen Revision. Die tatsächlich verwendete Revision einer
ChatGPT-App-Verbindung wurde nicht gemessen. Host-Overrides können abweichen.
Es wurden keine globalen Flags oder bestehenden MCP-Verbindungen geändert.

`2026-07-28` verwendet einen zustandslosen Protokollkern mit `server/discover`
und Metadaten pro Anfrage. Das entfernt nicht den Fachzustand von AgentDesk.
Vor einer Aktivierung einen gezielten Verbindungstest durchführen und die
tatsächliche Revision erfassen. Optionale Fähigkeiten wie Tasks, Elicitation
und UI separat anhand der Host-Fähigkeiten prüfen; ein Versionsdatum allein
beweist sie nicht. Eigene dauerhafte Fragen bleiben unabhängig davon nutzbar.

## DuckDB und ausgewählte Sitzungsdaten

SQLite bleibt zuständig für den aktuellen Zustand und Transaktionen des
Postfachs. DuckDB ergänzt diesen Zustand um abgeleitete, neu berechenbare
Auswertungen. Die Analyse darf weder Fragen beantworten noch Berechtigungen
erteilen oder Zustellstatus erraten.

Geplanter Datenfluss:

```text
Codex-API / ausgewählte JSONL -> Importadapter -> DuckDB -> Analyseansichten
AgentDesk-Zustand in SQLite ----------------------^             |
                                                               v
                                                   React / MCP über AgentDesk
```

Die native API bleibt die erste Wahl für laufenden Codex-Zustand. JSONL ist eine
zusätzliche historische Quelle, kein stabiles Frontend-Protokoll. Der Import
wird auf ausdrücklich ausgewählte Sitzungen begrenzt; kein pauschaler Import
vollständiger Chatarchive. Die bisherige Projektregel bleibt bestehen.

Der spätere Importadapter soll:

- Nur vollständige Datensätze verarbeiten; eine noch unvollständig geschriebene
  letzte Zeile beim nächsten Durchlauf erneut versuchen.
- Quelle, Position, verfügbare Thread-/Turn-IDs und Importversion erhalten.
- Wiederholte Importe idempotent behandeln und überlappende Ereignisabbildungen
  nicht als zusätzliche Benutzeraktionen zählen.
- Unterschiedliche Ereignistypen explizit normalisieren; unbekannte Typen
  sichtbar lassen und fehlende Werte nicht als Nullmessungen interpretieren.
- Bei Tokenmetriken zwischen kumulativen Zählern und Einzelwerten unterscheiden.
- Fehler kenntlich machen; keine pauschale Fehlerunterdrückung beim JSON-Lesen.

DuckDB kann NDJSON direkt lesen, einschließlich verschachtelter JSON-Werte.
Zunächst sind kleine, nachvollziehbare Projektionen ausreichend. Ein
AgentDesk-Prozess besitzt die schreibende Analyse-Verbindung; UI und Agenten
verwenden dessen Schnittstellen statt eigener konkurrierender Dateischreiber.
Analyseabfragen sollen den operativen Postfachpfad nicht blockieren.

## Erste hilfreiche Auswertungen

Der nächste sinnvolle vertikale Ausschnitt ist eine Sitzungs-Timeline mit
offenen Entscheidungen und wiederholten Arbeitsschritten. Sie verbindet
vorhandene AgentDesk-Fragen mit eindeutig zuordenbaren Codex-Ereignissen.

Weitere Kandidaten sind mehrfach gestellte Fragen, wiederkehrende Toolfehler,
angenommene oder korrigierte Vorschläge und Aufgaben mit ungeklärtem Ergebnis.
Jede angezeigte Erkenntnis soll Herkunft und Beobachtungszeitraum zeigen.
Korrelationen zwischen Modell, Projekt und Ergebnis sind keine kausalen Beweise.

Der Operator wird nicht über vermutete Persönlichkeit oder Antworttempo
bewertet. Eine lange Antwortpause belegt weder Unsicherheit noch Ablehnung.
Eine hilfreiche Rückfrage wäre: »Du hast bei drei ähnlichen Entscheidungen
zuerst eine Vorschau verlangt. Soll AgentDesk sie künftig direkt anbieten?«
Erst die Bestätigung wird als editierbare Präferenz gespeichert. Eine abgeleitete
Beobachtung bleibt als solche erkennbar und kann verworfen werden.

## Oberfläche und Lizenzen

Apps SDK UI liefert React-Komponenten für ChatGPT-nahe Darstellung; dieselbe
Geschäftslogik bleibt auch in der normalen Browseransicht erreichbar. React Bits
Pro kann passende App-Shell-, Agent-Activity- und Approval-Komponenten ergänzen.
Die konkrete Integration ist noch nicht begonnen.

Der Nutzer hat eine Ultimate-Lizenz angegeben. Lizenzschlüssel gehören nur in
die lokale autorisierte Umgebung. Bezahlte Rohkomponenten, Agent-Kit-Skills und
Vorlagen nicht als wiederverwendbares öffentliches Paket oder Marketplace
veröffentlichen. Bisher wurden keine dieser Assets in AgentDesk übernommen.

## Quellen

- [C#-SDK: Protokollkompatibilität](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/versioning.md)
- [TypeScript-SDK: Protokollgenerationen](https://ts.sdk.modelcontextprotocol.io/v2/protocol-versions)
- [MCP-Spezifikation 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28)
- [Codex: MCP-Konformitätstests](https://github.com/openai/codex/blob/main/scripts/mcp_conformance/README.md)
- [DuckDB: JSON/NDJSON](https://duckdb.org/docs/2.0/data/json/loading_json)
- [DuckDB: Nebenläufigkeit](https://duckdb.org/docs/2.0/connect/concurrency)
- [Apps SDK UI](https://github.com/openai/apps-sdk-ui)
- [ChatGPT-UI-Integration](https://developers.openai.com/plugins/build/chatgpt-ui)
- [React Bits Pro: App UI](https://pro.reactbits.dev/docs/app-ui)
- [React Bits Pro: Lizenz](https://pro.reactbits.dev/license)
