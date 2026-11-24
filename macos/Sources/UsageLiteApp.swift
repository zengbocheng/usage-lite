import AppKit
import SwiftUI

@MainActor
private final class QuotaStore: ObservableObject {
    @Published private(set) var data = QuotaData.empty
    @Published private(set) var now = Date()
    @Published private(set) var refreshing = false
    @Published private(set) var failed = false

    init() {
        Task { [weak self] in
            while !Task.isCancelled, self != nil {
                await self?.refresh()
                try? await Task.sleep(for: .seconds(60))
            }
        }
    }

    func refresh() async {
        guard !refreshing else { return }
        refreshing = true
        defer { refreshing = false; now = Date() }
        do {
            data = try await Task.detached(priority: .utility) { try QuotaData.read() }.value
            failed = false
        } catch {
            failed = true
        }
    }
}

@MainActor
private struct UsageLiteApp: App {
    @AppStorage("menuBarProvider") private var provider = Provider.codex
    @StateObject private var store = QuotaStore()

    var body: some Scene {
        MenuBarExtra {
            QuotaPanel(data: store.data, now: store.now, provider: $provider,
                       refreshing: store.refreshing, failed: store.failed,
                       refresh: { Task { await store.refresh() } }, quit: { NSApp.terminate(nil) })
        } label: {
            Text(store.data[provider].label(provider, at: store.now)).monospacedDigit()
        }
        .menuBarExtraStyle(.window)
    }
}

@main
struct EntryPoint {
    @MainActor static func main() {
        do {
            switch Array(CommandLine.arguments.dropFirst()) {
            case []:
                UsageLiteApp.main()
            case let args where args.count == 2 && args[0] == "--export-previews":
                try Previews.export(to: URL(fileURLWithPath: args[1], isDirectory: true))
            case let args where args.count == 2 && args[0] == "--smoke-fixture":
                let data = try QuotaData.read(fixture: args[1])
                let now = Date()
                guard data.codex.label(.codex, at: now) == "Codex: 76%",
                      data.claude.label(.claude, at: now) == "Claude: 90% / 5%",
                      Quota.empty.label(.codex, at: now) == "Codex: —" else {
                    throw CocoaError(.fileReadCorruptFile)
                }
                print("Quota sources, bundled reader and menu bar labels passed")
            default:
                exit(2)
            }
        } catch {
            FileHandle.standardError.write(Data("Usage Lite: \(error)\n".utf8))
            exit(1)
        }
    }
}
