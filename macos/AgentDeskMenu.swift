import AppKit
import Combine
import Foundation
import ServiceManagement
import SwiftUI

@main
struct AgentDeskMenu: App {
    @StateObject private var controller = ServerController()

    var body: some Scene {
        MenuBarExtra("AgentDesk", systemImage: controller.status.symbol) {
            Text(controller.activity ?? controller.status.title)
            Divider()
            Button("AgentDesk öffnen") { controller.openDesk() }
                .keyboardShortcut("o")
                .disabled(controller.busy)
            Button("Server starten") { controller.start() }
                .disabled(controller.busy || controller.status != .stopped)
            Button("Server stoppen") { controller.stop() }
                .disabled(controller.busy || controller.status != .running)
            Divider()
            Toggle("Menüleiste bei Anmeldung starten", isOn: Binding(
                get: { controller.startsAtLogin },
                set: { controller.setLoginItem($0) }
            ))
            Button("Logs anzeigen") { controller.openLogs() }
            Button("Status aktualisieren") { Task { await controller.refresh() } }
                .disabled(controller.busy)
            Divider()
            Button("Menüleisten-App beenden") { NSApplication.shared.terminate(nil) }
                .disabled(controller.busy)
            Button("AgentDesk vollständig beenden") { controller.stop(quit: true) }
                .keyboardShortcut("q")
                .disabled(controller.busy || controller.status == .checking)
        }
        .menuBarExtraStyle(.menu)

        Window("AgentDesk", id: "controls") {
            VStack(alignment: .leading, spacing: 18) {
                Label("AgentDesk", systemImage: controller.status.symbol)
                    .font(.largeTitle.bold())
                Text(controller.activity ?? controller.status.title)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("server-status")
                HStack {
                    Button("AgentDesk öffnen") { controller.openDesk() }
                        .buttonStyle(.borderedProminent)
                    if controller.status == .running {
                        Button("Server stoppen") { controller.stop() }
                    } else {
                        Button("Server starten") { controller.start() }
                            .disabled(controller.status != .stopped)
                    }
                }
                .disabled(controller.busy)
                Text("Du kannst dieses Fenster schließen. Das Postfach-Symbol oben in der Menüleiste bleibt erreichbar.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Divider()
                HStack {
                    Button("Logs anzeigen") { controller.openLogs() }
                    Spacer()
                    Button("Vollständig beenden") { controller.stop(quit: true) }
                        .disabled(controller.busy || controller.status == .checking)
                }
            }
            .padding(24)
            .frame(width: 430)
        }
        .windowResizability(.contentSize)
        .defaultLaunchBehavior(.presented)
    }
}

private enum ServerStatus: Equatable {
    case checking, running, stopped, occupied, unavailable

    var title: String {
        switch self {
        case .checking: "Status wird geprüft …"
        case .running: "Läuft · 127.0.0.1:5137"
        case .stopped: "Server ist gestoppt"
        case .occupied: "Port 5137 wird von einem anderen Dienst verwendet"
        case .unavailable: "Server antwortet nicht"
        }
    }

    var symbol: String {
        switch self {
        case .running: "tray.full.fill"
        case .checking, .stopped: "tray"
        case .occupied, .unavailable: "exclamationmark.triangle"
        }
    }
}

private struct MenuError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

@MainActor
private final class ServerController: ObservableObject {
    @Published private(set) var status = ServerStatus.checking
    @Published private(set) var activity: String?
    @Published private(set) var startsAtLogin = SMAppService.mainApp.status == .enabled

    var busy: Bool { activity != nil }
    private let deskURL = URL(string: "http://127.0.0.1:5137/")!
    private var checking = false

    init() {
        Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                if !self.busy { await self.refresh() }
                self.startsAtLogin = SMAppService.mainApp.status == .enabled
                try? await Task.sleep(for: .seconds(5))
            }
        }
    }

    func refresh() async {
        guard !checking else { return }
        checking = true
        defer { checking = false }
        var request = URLRequest(url: deskURL.appendingPathComponent("health"))
        request.timeoutInterval = 2
        request.cachePolicy = .reloadIgnoringLocalCacheData
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            let body = try? JSONDecoder().decode(Health.self, from: data)
            status = (response as? HTTPURLResponse)?.statusCode == 200
                && body?.service == "question-inbox" ? .running : .occupied
        } catch let error as URLError where error.code == .cannotConnectToHost {
            status = .stopped
        } catch {
            status = .unavailable
        }
    }

    func openDesk() {
        if status == .running {
            NSWorkspace.shared.open(deskURL)
        } else {
            start(openAfter: true)
        }
    }

    func start(openAfter: Bool = false) {
        perform("Server startet …") {
            await self.refresh()
            if self.status != .running {
                guard self.status == .stopped else {
                    throw MenuError(message: "\(self.status.title). Es wurde kein zusätzlicher Server gestartet.")
                }
                try await self.runScript("install-service.sh")
                try await self.waitFor(.running)
            }
            if openAfter { NSWorkspace.shared.open(self.deskURL) }
        }
    }

    func stop(quit: Bool = false) {
        perform("Server stoppt …") {
            await self.refresh()
            if self.status != .stopped {
                try await self.runScript("stop-server.sh")
                do {
                    try await self.waitFor(.stopped)
                } catch {
                    throw MenuError(message: "Der Server ist weiterhin erreichbar. Falls er über Rider läuft, beende ihn dort mit ■ Stop. Die Menüleisten-App bleibt geöffnet.")
                }
            }
            if quit { NSApplication.shared.terminate(nil) }
        }
    }

    func setLoginItem(_ enabled: Bool) {
        Task {
            do {
                if enabled {
                    try SMAppService.mainApp.register()
                    if SMAppService.mainApp.status == .requiresApproval {
                        SMAppService.openSystemSettingsLoginItems()
                    }
                } else {
                    try await SMAppService.mainApp.unregister()
                }
            } catch { showError(error) }
            startsAtLogin = SMAppService.mainApp.status == .enabled
        }
    }

    func openLogs() {
        do {
            let directory = try projectRoot().appendingPathComponent(".local/logs", isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            NSWorkspace.shared.open(directory)
        } catch { showError(error) }
    }

    private func perform(_ title: String, operation: @escaping @MainActor () async throws -> Void) {
        guard !busy else { return }
        activity = title
        Task {
            defer { activity = nil }
            do { try await operation() }
            catch { showError(error) }
            await refresh()
        }
    }

    private func waitFor(_ expected: ServerStatus) async throws {
        for _ in 0..<40 {
            await refresh()
            if status == expected { return }
            try await Task.sleep(for: .milliseconds(250))
        }
        throw MenuError(message: "Der Server hat den erwarteten Status nicht erreicht. Details stehen in den Logs.")
    }

    private func projectRoot() throws -> URL {
        guard let path = Bundle.main.object(forInfoDictionaryKey: "AgentDeskProjectRoot") as? String,
              FileManager.default.fileExists(atPath: path + "/scripts/install-service.sh") else {
            throw MenuError(message: "Das AgentDesk-Projekt wurde verschoben. Installiere die Menüleisten-App aus dem neuen Projektordner erneut.")
        }
        return URL(fileURLWithPath: path, isDirectory: true)
    }

    private func runScript(_ name: String) async throws {
        let root = try projectRoot()
        let script = root.appendingPathComponent("scripts").appendingPathComponent(name)
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let process = Process()
            let output = Pipe()
            process.executableURL = URL(fileURLWithPath: "/bin/sh")
            process.arguments = [script.path]
            process.currentDirectoryURL = root
            var environment = ProcessInfo.processInfo.environment
            environment["PATH"] = "/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
            process.environment = environment
            process.standardOutput = output
            process.standardError = output
            process.terminationHandler = { finished in
                let data = output.fileHandleForReading.readDataToEndOfFile()
                let detail = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
                if finished.terminationStatus == 0 {
                    continuation.resume()
                } else {
                    continuation.resume(throwing: MenuError(message: detail.isEmpty ? "Serversteuerung fehlgeschlagen (\(finished.terminationStatus))." : detail))
                }
            }
            do { try process.run() }
            catch { continuation.resume(throwing: error) }
        }
    }

    private func showError(_ error: Error) {
        NSApplication.shared.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = "AgentDesk"
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .warning
        alert.addButton(withTitle: "OK")
        alert.runModal()
    }

    private struct Health: Decodable {
        let service: String
    }
}
