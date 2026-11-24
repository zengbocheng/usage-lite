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
                Picker("菜单栏显示", selection: $provider) {
                    ForEach(Provider.allCases) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .frame(width: 142)
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
            .controlSize(.small)
        }
        .padding(16)
        .frame(width: 360)
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
                    Text("本地记录 · \(at.formatted(.dateTime.month().day().hour().minute()))")
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
                Text("重置 · \(reset.formatted(.dateTime.month().day().hour().minute()))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
