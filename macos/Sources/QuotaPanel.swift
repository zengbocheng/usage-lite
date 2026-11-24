import SwiftUI

struct QuotaPanel: View {
    let data: QuotaData
    let now: Date
    @Binding var provider: Provider
    var refreshing = false
    var failed = false
    var refresh: () -> Void = {}
    var quit: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Usage Lite").font(.title2.weight(.semibold))
                Spacer()
                HStack(spacing: 2) {
                    ForEach(Provider.allCases) { item in
                        Button(item.rawValue) { provider = item }
                            .buttonStyle(PanelButtonStyle(selected: provider == item))
                            .accessibilityAddTraits(provider == item ? .isSelected : [])
                    }
                }
                .padding(2)
                .background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
                .accessibilityElement(children: .contain)
                .accessibilityLabel("菜单栏显示")
            }
            ForEach(Provider.allCases) { item in
                QuotaCard(provider: item, quota: data[item], now: now)
            }
            if failed {
                Text("本地记录暂时无法读取").foregroundStyle(.secondary).font(.caption)
            }
            HStack {
                Button(action: refresh) {
                    Label("刷新本地记录", systemImage: "arrow.clockwise")
                }
                .disabled(refreshing)
                if refreshing { ProgressView().controlSize(.small) }
                Spacer()
                Button("退出", action: quit)
            }
            .buttonStyle(PanelButtonStyle())
        }
        .padding(16)
        .frame(width: 360)
    }
}

private struct PanelButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var enabled
    var selected = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.subheadline.weight(selected ? .semibold : .regular))
            .padding(.horizontal, 9)
            .padding(.vertical, 5)
            .background(selected ? Color.accentColor.opacity(0.14) : Color.primary.opacity(0.04),
                        in: RoundedRectangle(cornerRadius: 6))
            .opacity(enabled ? (configuration.isPressed ? 0.65 : 1) : 0.45)
    }
}

private struct QuotaCard: View {
    let provider: Provider
    let quota: Quota
    let now: Date

    private var accent: Color {
        provider == .codex ? Color(red: 0.13, green: 0.43, blue: 0.84)
            : Color(red: 0.73, green: 0.35, blue: 0.23)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 7) {
                Circle().fill(accent).frame(width: 7, height: 7).accessibilityHidden(true)
                Text(provider.cardTitle).font(.headline)
                Spacer()
                if let at = quota.observedAt, now.timeIntervalSince(at) > 7200 {
                    Text("较早的记录").font(.caption).foregroundStyle(.secondary)
                }
            }
            if quota.session == nil && quota.weekly == nil {
                Text(quota.note)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                if let session = quota.session { quotaRow(session) }
                if let weekly = quota.weekly { quotaRow(weekly) }
                if let at = quota.observedAt {
                    Text("本地记录 · \(clockTime(at))")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.background.secondary, in: RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(.primary.opacity(0.09)))
    }

    private func quotaRow(_ window: QuotaWindow) -> some View {
        let active = window.isActive(at: now)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(window.title)
                Spacer()
                Text(active ? "剩余 \(window.percent)" : "—").monospacedDigit()
            }
            .font(.subheadline)
            GeometryReader { geometry in
                Capsule().fill(.secondary.opacity(0.18))
                    .overlay(alignment: .trailing) {
                        if active {
                            Capsule().fill(accent).frame(width: geometry.size.width * window.remaining / 100)
                        }
                    }
            }
            .frame(height: 4)
            if !active {
                Text("等待重置后的新记录").font(.caption).foregroundStyle(.secondary)
            } else if let reset = window.resetAt {
                Text("重置 · \(clockTime(reset))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func clockTime(_ date: Date) -> String {
        date.formatted(.dateTime.month().day().hour().minute().locale(Locale(identifier: "zh_CN")))
    }
}
