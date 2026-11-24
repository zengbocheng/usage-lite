import AppKit
import SwiftUI

enum Previews {
    @MainActor static func export(to directory: URL) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let now = Date(timeIntervalSince1970: 1_795_500_000)
        let weekly = Quota(session: nil,
                           weekly: QuotaWindow(remaining: 76, resetAt: now.addingTimeInterval(3 * 86400), title: "每周"),
                           observedAt: now, note: "")
        let dual = Quota(session: QuotaWindow(remaining: 90, resetAt: now.addingTimeInterval(7200), title: "5 小时"),
                         weekly: QuotaWindow(remaining: 5, resetAt: now.addingTimeInterval(86400), title: "每周"),
                         observedAt: now, note: "")
        let previews: [(String, QuotaData, Provider)] = [
            ("dual-quota", QuotaData(codex: weekly, claude: dual), .claude),
            ("weekly-only", QuotaData(codex: weekly, claude: .empty), .codex),
            ("no-records", .empty, .codex)
        ]
        for (filename, data, provider) in previews {
            let view = VStack(spacing: 0) {
                Text(data[provider].label(provider, at: now))
                    .monospacedDigit()
                    .padding(.vertical, 8)
                    .frame(maxWidth: .infinity, alignment: .center)
                    .background(.quaternary)
                QuotaPanel(data: data, now: now, provider: .constant(provider))
            }
            .frame(width: 360)
            .background(.background)
            .environment(\.colorScheme, .light)
            .environment(\.locale, Locale(identifier: "zh_CN"))
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            guard let image = renderer.cgImage,
                  let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
                throw CocoaError(.fileWriteUnknown)
            }
            try png.write(to: directory.appendingPathComponent(filename + ".png"), options: .atomic)
        }
    }
}
