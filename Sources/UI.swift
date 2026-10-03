import SwiftUI

private let accent = Color(red: 0.20, green: 0.33, blue: 0.62)   // ViewSonic-style navy

struct Card<Content: View, Trailing: View>: View {
    let title: String, icon: String
    @ViewBuilder var trailing: Trailing
    @ViewBuilder var content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.system(size: 16, weight: .medium)).frame(width: 22)
                Text(title).font(.system(size: 16, weight: .semibold))
                Spacer()
                trailing
            }
            .padding(.horizontal, 16).padding(.vertical, 13)
            Divider()
            content.padding(16).frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }
}
extension Card where Trailing == EmptyView {
    init(title: String, icon: String, @ViewBuilder content: () -> Content) {
        self.init(title: title, icon: icon, trailing: { EmptyView() }, content: content)
    }
}

struct SliderRow: View {
    let icon: String, label: String
    @Binding var value: Double
    var range: ClosedRange<Double> = 0...100
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: icon).frame(width: 22)
            Text(label).frame(width: 120, alignment: .leading)
            Slider(value: $value, in: range).tint(accent)
            Text("\(Int(value))").monospacedDigit().frame(width: 34, alignment: .trailing)
        }
        .font(.system(size: 13))
    }
}

struct ContentView: View {
    @ObservedObject var m = MonitorModel.shared
    @State private var confirmReset = false

    var body: some View {
        VStack(spacing: 14) {
            header
            HStack(alignment: .top, spacing: 14) {
                VStack(spacing: 14) { monitorCard; inputCard; infoCard }
                    .frame(width: 400)
                VStack(spacing: 14) { displayCard; viewModeCard; colorCard; sharingCard }
                    .frame(width: 430)
            }
            .disabled(m.input == nil && m.error != nil)
            footer
        }
        .padding(.horizontal, 20).padding(.top, 20).padding(.bottom, 12)
        .background(Color(nsColor: .windowBackgroundColor))
        .onAppear { m.refresh() }
        .alert("Reset \(m.info.name) to factory settings?", isPresented: $confirmReset) {
            Button("Reset", role: .destructive) { m.factoryReset() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This restores every setting in the monitor's own menu (picture, colour, sound) to ViewSonic defaults.")
        }
    }

    private var footer: some View {
        HStack(spacing: 6) {
            Text(LocalizedStringKey("BHDisplay v\(Brand.version) · Built by [BiswasHost](\(Brand.website)) · Free & open-source"))
                .tint(accent)
            Spacer()
            Text("Not affiliated with ViewSonic")
        }
        .font(.system(size: 11)).foregroundStyle(.secondary)
    }

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: "house.fill").foregroundStyle(accent)
            Text("Home").font(.system(size: 15, weight: .semibold)).foregroundStyle(accent)
            if m.loading { ProgressView().controlSize(.small).padding(.leading, 6) }
            if let e = m.error {
                Label(e, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange).font(.system(size: 12)).lineLimit(1)
            }
            Spacer()
            Button { m.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            Button("Reset") { confirmReset = true }
                .disabled(!m.info.matchedDDC)
                .help(m.info.matchedDDC ? "Restore the monitor's factory settings" : "Unavailable until the monitor is identified — press Refresh")
        }
    }

    private var monitorCard: some View {
        HStack(spacing: 10) {
            Image(systemName: "display").font(.system(size: 16))
            Text(m.info.name).font(.system(size: 17, weight: .semibold))
            Spacer()
            if m.info.externalCount > 1 {
                Label("\(m.info.externalCount) external displays — controlling this one", systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.orange).font(.system(size: 11))
            } else {
                Text(m.info.link == "—" ? "" : "via \(m.info.link)").foregroundStyle(.secondary).font(.system(size: 12))
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.12)))
    }

    private var inputCard: some View {
        Card(title: "Input Selected", icon: "rectangle.portrait.and.arrow.right", trailing: {
            HStack(spacing: 8) {
                Text("Auto Detect").font(.system(size: 13)).foregroundStyle(.secondary)
                Toggle("", isOn: Binding(get: { m.autoDetect ?? false }, set: { m.setAutoDetect($0) }))
                    .toggleStyle(.switch).labelsHidden().tint(accent)
                    .disabled(m.autoDetect == nil)
                    .help("On: the monitor jumps to another input when the current one has no picture.\nOff: it stays on the input you chose.")
            }
        }) {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 10) {
                    ForEach(MonitorInput.all) { inp in
                        let on = m.input == inp.id
                        Button { m.switchTo(inp.id) } label: {
                            VStack(spacing: 4) {
                                Image(systemName: inp.id == 0x0F ? "cable.connector" : "cable.connector.horizontal")
                                Text(inp.name).font(.system(size: 13, weight: .semibold))
                                Text(inp.id == m.macInput ? "This Mac" : " ")
                                    .font(.system(size: 10)).foregroundStyle(on ? .white.opacity(0.85) : .secondary)
                            }
                            .frame(maxWidth: .infinity).padding(.vertical, 10)
                            .background(RoundedRectangle(cornerRadius: 8).fill(on ? accent : Color.primary.opacity(0.05)))
                            .foregroundStyle(on ? .white : .primary)
                        }
                        .buttonStyle(.plain)
                    }
                }
                if m.macInput != nil {
                    Button { m.toggleMacOther() } label: {
                        Label(m.input == m.macInput ? "Switch to other computer" : "Switch to this Mac",
                              systemImage: "arrow.left.arrow.right").frame(maxWidth: .infinity)
                    }
                    .controlSize(.large)
                }
                if let n = m.notice {
                    Label(n, systemImage: "moon.zzz.fill")
                        .font(.system(size: 11)).foregroundStyle(.orange)
                        .lineLimit(nil).fixedSize(horizontal: false, vertical: true)
                }
                Text("Shortcuts:  ⌃⌥⌘S  Mac ⇄ other\n⌃⌥⌘1  DisplayPort   ⌃⌥⌘2  HDMI 1   ⌃⌥⌘3  HDMI 2")
                    .font(.system(size: 11)).foregroundStyle(.secondary)
                    .lineLimit(nil).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var infoCard: some View {
        Card(title: "Monitor information", icon: "info.circle") {
            Grid(alignment: .leading, horizontalSpacing: 30, verticalSpacing: 14) {
                GridRow { info("Serial Number", Brand.docsMode ? "••••••••••••" : m.info.serial); info("Monitor Name", m.info.name) }
                GridRow { info("Firmware Version", m.info.firmware); info("Recommended Resolution", m.info.resolution) }
                GridRow { info("Mac Connection", m.info.link == "—" ? "—" : "\(m.info.link) → \(m.macInput.map(MonitorInput.name(for:)) ?? "detecting…")")
                          info("Manufactured", m.info.manufactured) }
            }
        }
    }
    private func info(_ k: String, _ v: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(k).font(.system(size: 11)).foregroundStyle(accent)
            Text(v).font(.system(size: 13)).textSelection(.enabled)
        }
        .frame(width: 160, alignment: .leading)
    }

    private var displayCard: some View {
        Card(title: "Display Control", icon: "slider.horizontal.3") {
            VStack(spacing: 14) {
                SliderRow(icon: "sun.max", label: "Brightness", value: m.slider(\.brightness, VCP.brightness))
                SliderRow(icon: "circle.lefthalf.filled", label: "Contrast", value: m.slider(\.contrast, VCP.contrast))
                SliderRow(icon: "triangle", label: "Sharpness", value: m.slider(\.sharpness, VCP.sharpness))
                VStack(alignment: .leading, spacing: 4) {
                    SliderRow(icon: "sun.haze", label: "Blue Light Filter", value: m.blueLightBinding)
                    if m.blueLightLocked {
                        Text("Locked by the monitor in this View Mode — switch View Mode to Standard to adjust.")
                            .font(.system(size: 11)).foregroundStyle(.orange).padding(.leading, 34)
                    }
                }
                HStack(spacing: 12) {
                    Button { m.setMuted(!m.muted) } label: {
                        Image(systemName: m.muted ? "speaker.slash.fill" : "speaker.wave.2.fill").frame(width: 22)
                    }
                    .buttonStyle(.plain).help(m.muted ? "Unmute" : "Mute")
                    Text("Volume").frame(width: 120, alignment: .leading)
                    Slider(value: m.slider(\.volume, VCP.volume), in: 0...100).tint(accent)
                    Text("\(Int(m.volume))").monospacedDigit().frame(width: 34, alignment: .trailing)
                }
                .font(.system(size: 13))
            }
        }
    }

    private var viewModeCard: some View {
        Card(title: "View Mode", icon: "eye") {
            Picker("", selection: Binding(get: { m.viewMode ?? 0xFFFF }, set: { m.setViewMode($0) })) {
                if m.viewMode == nil { Text("—").tag(UInt16(0xFFFF)) }
                ForEach(Choices.viewMode) { Text($0.name).tag($0.id) }
            }
            .labelsHidden()
        }
    }

    @ObservedObject private var share = ShareController.shared
    @ObservedObject private var lanMouse = SharingModel.shared      // fallback only

    private var sharingCard: some View {
        Card(title: "Keyboard & Mouse", icon: "keyboard", trailing: {
            Toggle("", isOn: Binding(get: { share.enabled }, set: { on in
                if on, lanMouse.isOn { lanMouse.set(false) }
                share.enabled = on
            }))
            .toggleStyle(.switch).labelsHidden().tint(accent)
        }) {
            VStack(alignment: .leading, spacing: 8) {
                Text(share.status).font(.system(size: 12)).foregroundStyle(.secondary)
                    .lineLimit(nil).fixedSize(horizontal: false, vertical: true)
                if share.needsAccessibility {
                    HStack {
                        Button("Open Accessibility Settings…") {
                            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
                        }
                        Button("Check Again") { share.retryAccessibility() }
                    }
                    .controlSize(.small)
                }
                if share.enabled && share.running {
                    ForEach(share.discovered) { d in
                        HStack {
                            Image(systemName: "desktopcomputer")
                            Text("\(d.name)  ·  \(d.host)").font(.system(size: 12))
                            Spacer()
                            Button("Pair…") { share.pair(with: d) }.controlSize(.small)
                        }
                    }
                    ForEach(share.paired.sorted(by: { $0.value < $1.value }), id: \.key) { fp, name in
                        HStack {
                            Image(systemName: "checkmark.seal.fill").foregroundStyle(accent)
                            Text(name).font(.system(size: 12))
                            Spacer()
                            Button("Forget") { share.forget(fp) }.controlSize(.small)
                        }
                    }
                    HStack(spacing: 8) {
                        Text("Other computer is on the").font(.system(size: 12))
                        Picker("", selection: Binding(get: { share.side }, set: { share.side = $0 })) {
                            Text("Left").tag(ShareEdge.left)
                            Text("Right").tag(ShareEdge.right)
                        }
                        .pickerStyle(.segmented).labelsHidden().frame(width: 120)
                    }
                    Toggle("⌘ on the Mac = Ctrl on Windows", isOn: Binding(get: { share.swapCmdCtrl }, set: { share.swapCmdCtrl = $0 }))
                        .font(.system(size: 12)).toggleStyle(.checkbox)
                }
                HStack {
                    Text("Moving the mouse never changes the monitor.").font(.system(size: 10)).foregroundStyle(.secondary)
                    Spacer()
                    if !share.enabled && lanMouse.state != .notInstalled {
                        Toggle("Lan Mouse (fallback)", isOn: Binding(get: { lanMouse.isOn }, set: { lanMouse.set($0) }))
                            .toggleStyle(.checkbox).font(.system(size: 10)).disabled(lanMouse.busy)
                    }
                }
            }
        }
        .onAppear { lanMouse.refresh() }
    }

    private var colorCard: some View {
        Card(title: "Color Temperature", icon: "thermometer.medium") {
            VStack(spacing: 12) {
                Picker("", selection: Binding(get: { m.colorPreset ?? 0xFFFF }, set: { m.setColorPreset($0) })) {
                    if m.colorPreset == nil { Text("—").tag(UInt16(0xFFFF)) }
                    ForEach(Choices.colorTemp) { Text($0.name).tag($0.id) }
                }
                .labelsHidden()
                if m.colorPreset == 0x0B {
                    SliderRow(icon: "r.circle", label: "Red", value: m.slider(\.red, VCP.red))
                    SliderRow(icon: "g.circle", label: "Green", value: m.slider(\.green, VCP.green))
                    SliderRow(icon: "b.circle", label: "Blue", value: m.slider(\.blue, VCP.blue))
                }
            }
        }
    }
}
