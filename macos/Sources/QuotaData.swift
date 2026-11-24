import Foundation

enum Provider: String, CaseIterable, Identifiable {
    case codex = "Codex"
    case claude = "Claude"

    var id: Self { self }
    var cardTitle: String { self == .claude ? "Claude Code" : rawValue }
}

struct QuotaWindow: Decodable {
    let remaining: Double
    let resetAt: Date?
    let title: String

    func isActive(at now: Date) -> Bool { resetAt.map { $0 > now } ?? true }
    var percent: String { "\(Int(remaining.rounded(.toNearestOrEven)))%" }
}

struct Quota: Decodable {
    let session: QuotaWindow?
    let weekly: QuotaWindow?
    let observedAt: Date?
    let note: String

    static let empty = Self(session: nil, weekly: nil, observedAt: nil, note: "尚无本地额度记录")

    func label(_ provider: Provider, at now: Date) -> String {
        let windows = [session, weekly].compactMap { $0 }.filter { $0.isActive(at: now) }
        let values = windows.map(\.percent).joined(separator: " / ")
        return "\(provider.rawValue): \(values.isEmpty ? "—" : values)"
    }
}

struct QuotaData: Decodable {
    let codex: Quota
    let claude: Quota

    static let empty = Self(codex: .empty, claude: .empty)

    subscript(provider: Provider) -> Quota { provider == .codex ? codex : claude }

    static func read(fixture: String? = nil) throws -> Self {
        let executable = Bundle.main.bundleURL.appendingPathComponent("Contents/Helpers/Reader/usage-lite-reader")
        let process = Process()
        process.executableURL = executable
        if let fixture { process.arguments = ["--fixture-root", fixture] }
        let pipe = Pipe()
        process.standardOutput = pipe
        try process.run()
        let bytes = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw CocoaError(.fileReadUnknown) }

        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .millisecondsSince1970
        return try decoder.decode(Self.self, from: bytes)
    }
}
