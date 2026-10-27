# Async-Leitlinien für das Postfach

Die vom Nutzer am 2026-10-27 bereitgestellten neun unterschiedlichen Artikel
wurden gelesen. Der Builder-Artikel war in der ursprünglichen Liste doppelt.
Diese Datei bewahrt nur Referenzen und projektbezogene Entscheidungen, keine
Kopie der Artikel oder Benchmark-Ergebnisse.

| Referenz | Konsequenz für dieses Projekt |
| --- | --- |
| [Async method builders and where the allocations go](https://www.meziantou.net/async-method-builders-and-where-the-allocations-go.htm) | Keine eigenen oder gepoolten Builder ohne nachgewiesenen Engpass. Netzwerk- und Prozesspfade verwenden weiter `Task`. |
| [Async streams: IAsyncEnumerable and await foreach](https://www.meziantou.net/async-streams-iasyncenumerable-and-await-foreach.htm) | SSE-Iteratoren reichen Cancellation weiter und tragen `EnumeratorCancellation`. Kein synchrones Blockieren zur Stream-Verarbeitung. |
| [Synchronization in async code: lock and SemaphoreSlim](https://www.meziantou.net/synchronization-in-async-code-lock-and-semaphoreslim.htm) | Datenbankzugriff mit `SemaphoreSlim(1,1)`, Erwerb vor `try`, Freigabe in `finally`. Keine Netzwerkzugriffe unter der Datenbanksperre. |
| [Composing async work: WhenAll, WhenAny and WhenEach](https://www.meziantou.net/composing-async-work-whenall-whenany-and-wheneach.htm) | Aufgaben vor `WhenAll` materialisieren, Einzelergebnisse erhalten. Maximal acht Empfänger je Batch und vier gleichzeitig laufende Versandprozesse insgesamt. |
| [Cancellation: CancellationToken end to end](https://www.meziantou.net/cancellation-cancellationtoken-end-to-end.htm) | Lesezugriffe folgen dem Request-Token. Ein bereits dauerhaft reservierter Versand wird bewusst vom Browser-Disconnect entkoppelt, bleibt aber pro Prozess zeitlich begrenzt. Ein Timeout beim Senden bedeutet unklare Zustellung, nicht sicheres Nicht-Senden. |
| [IAsyncDisposable and await using](https://www.meziantou.net/iasyncdisposable-and-await-using.htm) | RPC-Verbindungen schließen ihre WebSocket-Ausgabe mit begrenzter Wartezeit und geben Transportressourcen auch bei Fehlern frei. Kein Blockieren auf asynchroner Freigabe. |
| [Task vs ValueTask and IValueTaskSource](https://www.meziantou.net/task-vs-valuetask-and-ivaluetasksource.htm) | `ValueTask` nur an passenden API-Grenzen, etwa `DisposeAsync`; keine pauschale Umstellung der Serviceverträge. |
| [SynchronizationContext, ConfigureAwait and deadlocks](https://www.meziantou.net/synchronizationcontext-configureawait-and-deadlocks.htm) | Anwendungscode bleibt durchgehend asynchron; keine `.Result`/`.Wait()`-Brücken. Kein pauschales `ConfigureAwait(false)` als vermeintliches Tuning der ASP.NET-Core-App. |
| [ExecutionContext: what flows across an await](https://www.meziantou.net/executioncontext-what-flows-across-an-await.htm) | Herkunft wird als explizite verifizierte Daten übergeben, nicht aus einem globalen oder threadlokalen „aktuellen Agenten“ geraten. Keine Chatverläufe in `AsyncLocal` halten. |

Die Artikel liefern Review-Fragen; die konkreten Verträge werden gegen die
installierte .NET-/Codex-Version und die Tests geprüft. Timingwerte aus fremden
Benchmarks sind keine Performance-Messung dieser Anwendung.

Provider-spezifische Ergänzung aus InspectCode und `ref`: Bei
Microsoft.Data.Sqlite ist ein vorhandener Async-Overload kein Nachweis für
asynchrone I/O. Die [offizielle Dokumentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)
empfiehlt synchrone Aufrufe mit WAL. Deshalb liegen die Datenbankoperationen in
synchronen Hilfsmethoden unter einem asynchron erworbenen Gate.
