import Foundation
import SwiftUI
import AppKit

/// VCP codes used by the app. Standard MCCS codes are confirmed from the XG2409A capabilities string.
enum VCP {
    static let input: UInt8       = 0x60
    static let brightness: UInt8  = 0x10
    static let contrast: UInt8    = 0x12
    static let sharpness: UInt8   = 0x87
    static let volume: UInt8      = 0x62
    static let mute: UInt8        = 0x8D   // 1 = muted, 2 = sound on
    static let colorPreset: UInt8 = 0x14
    static let red: UInt8         = 0x16
    static let green: UInt8       = 0x18
    static let blue: UInt8        = 0x1A
    static let viewMode: UInt8    = 0xDC
    static let autoDetect: UInt8  = 0x33   // Setup Menu → Auto Detect: 2 = On, 1 = Off (found by OSD diff, write verified)
    static let blueLight: UInt8   = 0xE2   // ViewSonic-specific; checked against the OSD
    static let firmware: UInt8    = 0xC9
    static let mccsVersion: UInt8 = 0xDF
    static let factoryReset: UInt8 = 0x04
}

struct Choice: Identifiable, Hashable { let id: UInt16; let name: String }

/// Values the XG2409A advertises; names matched to the OSD (see project notes before changing).
enum Choices {
    static let colorTemp: [Choice] = [
        .init(id: 0x01, name: "sRGB"),
        .init(id: 0x08, name: "Bluish"),
        .init(id: 0x06, name: "Cool"),
        .init(id: 0x05, name: "Native"),
        .init(id: 0x04, name: "Warm"),
        .init(id: 0x0B, name: "User Color"),
    ]
    static let viewMode: [Choice] = [
        .init(id: 0x00, name: "Standard"),
        .init(id: 0x30, name: "FPS Game"),
        .init(id: 0x31, name: "RTS Game"),
        .init(id: 0x32, name: "MOBA Game"),
        .init(id: 0x03, name: "Movie"),
        .init(id: 0x33, name: "Web"),
        .init(id: 0x34, name: "Text"),
        .init(id: 0x35, name: "MAC"),
        .init(id: 0x36, name: "Mono"),
        .init(id: 0x3E, name: "Custom"),
    ]
    static func name(_ list: [Choice], _ v: UInt16) -> String {
        list.first { $0.id == v }?.name ?? String(format: "0x%02X", v)
    }
}

/// All DDC traffic goes through one serial queue. Slider writes are coalesced so dragging
/// sends only the latest value instead of flooding the monitor's slow DDC bus.
final class DDCWorker {
    private let q = DispatchQueue(label: "com.biswashost.bhdisplay.ddc", qos: .userInitiated)
    private var ddc: DDC?
    /// Monitor the window was last refreshed from. Queued writes are dropped if a different
    /// monitor is found when they are sent (hot-plug between reading and writing).
    private var expected: MonitorIdentity?
    private let lock = NSLock()
    private var pending: [UInt8: (value: UInt16, repeats: Int, target: MonitorIdentity?)] = [:]
    private var order: [UInt8] = []
    private var scheduled = false
    var onError: ((String?) -> Void)?

    private func device() -> DDC? {
        if ddc == nil { ddc = DDC.firstExternal() }
        return ddc
    }

    func set(_ code: UInt8, _ value: UInt16, repeats: Int = 1, delay: Double = 0.05) {
        lock.lock()
        if pending[code] == nil { order.append(code) }
        pending[code] = (value, repeats, expected)   // aimed at the monitor the user is looking at now
        let start = !scheduled
        scheduled = true
        lock.unlock()
        if start { q.asyncAfter(deadline: .now() + delay) { self.flush() } }
    }

    private func flush() {
        lock.lock()
        let batch = order.compactMap { c in pending[c].map { (c, $0) } }
        pending.removeAll(); order.removeAll(); scheduled = false
        lock.unlock()
        var err: String?
        // Fresh EDID read, compared with the monitor each command was aimed at when it was queued.
        if let d = device(), batch.contains(where: { $0.1.target != nil }) {
            let now = d.currentIdentity()
            if batch.contains(where: { $0.1.target != nil && $0.1.target != now }) {
                ddc = nil
                let e = "A different monitor is connected now — nothing was sent. Press Refresh."
                DispatchQueue.main.async { self.onError?(e) }
                return
            }
        }
        for (code, w) in batch {
            guard let d = device() else { err = DDCError.noExternalDisplay.description; break }
            do { try d.write(code, w.value, repeats: w.repeats) }
            catch { err = "\(error)"; ddc = nil; break }
        }
        let e = err
        DispatchQueue.main.async { self.onError?(e) }
    }

    /// Reads each code; a code the monitor refuses is simply absent from the result.
    func read(_ codes: [UInt8], info: Bool, done: @escaping ([UInt8: (current: UInt16, max: UInt16)], MonitorInfo?, String?) -> Void) {
        q.async {
            var out: [UInt8: (current: UInt16, max: UInt16)] = [:]
            var err: String?
            let d = self.device()
            var mi: MonitorInfo?
            if info {
                mi = MonitorInfo.probe(identity: d?.identity)
                mi?.externalCount = DDC.externalCount()
                self.lock.lock(); self.expected = d?.identity; self.lock.unlock()
            }
            if let d {
                for c in codes {
                    do { out[c] = try d.read(c) }
                    catch { err = "\(error)" }
                }
                if out.isEmpty { self.ddc = nil }
            } else { err = DDCError.noExternalDisplay.description }
            if !out.isEmpty { err = nil }
            DispatchQueue.main.async { done(out, mi, err) }
        }
    }
}

@MainActor
final class MonitorModel: ObservableObject {
    static let shared = MonitorModel()

    @Published var error: String?
    @Published var loading = false
    @Published var input: UInt16?
    @Published var brightness = 0.0
    @Published var contrast = 0.0
    @Published var sharpness = 0.0
    @Published var blueLight = 0.0
    @Published var blueLightLocked = false
    private var blueLightCheck: DispatchWorkItem?
    @Published var volume = 0.0
    @Published var red = 0.0
    @Published var green = 0.0
    @Published var blue = 0.0
    @Published var muted = false
    @Published var colorPreset: UInt16?
    @Published var viewMode: UInt16?
    @Published var autoDetect: Bool?
    @Published var info = MonitorInfo()

    /// Port the Mac is plugged into — learned automatically, remembered across launches.
    @Published var macInput: UInt16? {
        didSet { UserDefaults.standard.set(macInput.map { Int($0) }, forKey: "macInput") }
    }
    /// Last input the monitor showed that was NOT the Mac (the other computer).
    @Published var otherInput: UInt16? {
        didSet { UserDefaults.standard.set(otherInput.map { Int($0) }, forKey: "otherInput") }
    }

    let io = DDCWorker()
    /// When each feature was last changed from the app. A refresh that started earlier must not
    /// overwrite it with the value it read before the change.
    private var lastLocalChange: [UInt8: Date] = [:]

    private func send(_ code: UInt8, _ value: UInt16, repeats: Int = 1, delay: Double = 0.05) {
        lastLocalChange[code] = Date()
        io.set(code, value, repeats: repeats, delay: delay)
    }

    private init() {
        macInput = Self.storedInput("macInput")
        otherInput = Self.storedInput("otherInput")
        io.onError = { [weak self] e in self?.error = e }
    }

    /// Preferences are user-editable (`defaults write`): accept only an input this monitor has.
    private static func storedInput(_ key: String) -> UInt16? {
        guard let i = UserDefaults.standard.object(forKey: key) as? Int,
              let v = UInt16(exactly: i), MonitorInput.isValid(v) else { return nil }
        return v
    }

    func refresh(full: Bool = true) {
        guard !loading else { return }
        loading = true
        let started = Date()
        let codes: [UInt8] = full
            ? [VCP.input, VCP.brightness, VCP.contrast, VCP.sharpness, VCP.blueLight, VCP.volume, VCP.mute,
               VCP.colorPreset, VCP.red, VCP.green, VCP.blue, VCP.viewMode, VCP.autoDetect, VCP.firmware, VCP.mccsVersion]
            : [VCP.input]
        io.read(codes, info: full) { [weak self] v, mi, err in
            guard let self else { return }
            self.loading = false
            self.error = err
            if let mi { self.info = mi }
            // Drop readings of anything the user changed while this refresh was in flight.
            var v = v
            for (c, t) in self.lastLocalChange where t > started { v[c] = nil }
            func d(_ c: UInt8) -> Double? { v[c].map { Double($0.current) } }
            if let x = v[VCP.input] { self.input = x.current & 0xFF }
            if let x = d(VCP.brightness) { self.brightness = x }
            if let x = d(VCP.contrast) { self.contrast = x }
            if let x = d(VCP.sharpness) { self.sharpness = x }
            if let x = d(VCP.blueLight) { self.blueLight = x }
            if let x = d(VCP.volume) { self.volume = x }
            if let x = v[VCP.mute] { self.muted = x.current == 1 }
            if let x = v[VCP.colorPreset] { self.colorPreset = x.current & 0xFF }
            if let x = d(VCP.red) { self.red = x }
            if let x = d(VCP.green) { self.green = x }
            if let x = d(VCP.blue) { self.blue = x }
            if let x = v[VCP.viewMode] { self.viewMode = x.current & 0xFF }
            if let x = v[VCP.autoDetect] { self.autoDetect = x.current == 2 }
            if let x = v[VCP.firmware] { self.info.firmware = "\(x.current >> 8).\(String(format: "%02d", x.current & 0xFF))" }
            if let x = v[VCP.mccsVersion] { self.info.mccs = "\(x.current >> 8).\(x.current & 0xFF)" }
            self.learnPorts()
        }
    }

    /// The Mac's link type (HDMI or DP, from macOS) narrows its port to one family; if the monitor is
    /// currently showing an input of that family, that input is the Mac. Works for HDMI 1 or HDMI 2.
    private func learnPorts() {
        guard let cur = input, MonitorInput.isValid(cur) else { return }
        let isDP = cur == 0x0F
        if let hdmi = info.linkIsHDMI, hdmi != isDP {
            if macInput != cur { macInput = cur }
        } else if cur != macInput {
            otherInput = cur
        }
    }

    @Published var notice: String?
    private var bounceCheck: DispatchWorkItem?
    // Generation counters: a re-check that was already in flight when a newer action started
    // must not overwrite the newer state.
    private var switchGen = 0, blueGen = 0, autoGen = 0

    func switchTo(_ code: UInt16) {
        guard MonitorInput.isValid(code) else { return }
        switchGen += 1
        let gen = switchGen
        if let cur = input, MonitorInput.isValid(cur), cur != code, cur != macInput { otherInput = cur }
        input = code
        notice = nil
        send(VCP.input, code, repeats: 2, delay: 0)
        // With Auto Detect on, an input with no picture is abandoned after ~3s (measured: DP → HDMI 2 → HDMI 1).
        bounceCheck?.cancel()
        let work = DispatchWorkItem { [weak self] in
            self?.io.read([VCP.input], info: false) { r, _, _ in
                guard let self, gen == self.switchGen, let now = r[VCP.input]?.current else { return }
                self.input = now & 0xFF
                if now & 0xFF != code {
                    self.notice = "\(MonitorInput.name(for: code)) has no picture — the computer on it is asleep or off, so the monitor's Auto Detect went back to \(MonitorInput.name(for: now)). Turn Auto Detect off to stay on it."
                    NSSound.beep()
                }
            }
        }
        bounceCheck = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: work)
    }

    /// One-key flip between this Mac and the other computer.
    func toggleMacOther() {
        guard let mac = macInput else { refresh(full: false); return }
        let other = otherInput.flatMap { MonitorInput.isValid($0) && $0 != mac ? $0 : nil } ?? (mac == 0x0F ? 0x12 : 0x0F)
        switchTo(input == mac ? other : mac)
    }

    func label(_ code: UInt16) -> String {
        let port = MonitorInput.name(for: code)
        return code == macInput ? "\(port) · This Mac" : port
    }

    func slider(_ kp: ReferenceWritableKeyPath<MonitorModel, Double>, _ code: UInt8) -> Binding<Double> {
        Binding(get: { self[keyPath: kp] },
                set: { v in self[keyPath: kp] = v.rounded(); self.send(code, UInt16(v.rounded())) })
    }

    /// The XG2409A greys Blue Light Filter out in some View Modes (verified: FPS Game) and silently
    /// ignores the write — so read it back and show the truth instead of a slider that lies.
    var blueLightBinding: Binding<Double> {
        Binding(get: { self.blueLight }, set: { v in
            let want = UInt16(v.rounded())
            self.blueLight = v.rounded()
            self.send(VCP.blueLight, want)
            self.blueGen += 1
            let gen = self.blueGen
            self.blueLightCheck?.cancel()
            let work = DispatchWorkItem {   // the model is an app-lifetime singleton
                self.io.read([VCP.blueLight], info: false) { r, _, _ in
                    guard gen == self.blueGen, let got = r[VCP.blueLight]?.current else { return }
                    self.blueLightLocked = got != want
                    self.blueLight = Double(got)
                }
            }
            self.blueLightCheck = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.6, execute: work)
        })
    }

    /// The monitor is briefly unreadable while it applies this, so confirm from a later read.
    func setAutoDetect(_ on: Bool) {
        autoDetect = on
        autoGen += 1
        let gen = autoGen
        send(VCP.autoDetect, on ? 2 : 1, delay: 0)
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.0) {
            self.io.read([VCP.autoDetect], info: false) { r, _, _ in
                if gen == self.autoGen, let x = r[VCP.autoDetect] { self.autoDetect = x.current == 2 }
            }
        }
    }

    func setMuted(_ m: Bool) { muted = m; send(VCP.mute, m ? 1 : 2) }
    func setColorPreset(_ v: UInt16) { colorPreset = v; send(VCP.colorPreset, v, repeats: 1, delay: 0) }
    func setViewMode(_ v: UInt16) {
        viewMode = v; blueLightLocked = false
        send(VCP.viewMode, v, repeats: 1, delay: 0)
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { self.refresh() }  // a mode changes other values too
    }

    func factoryReset() {
        send(VCP.factoryReset, 1, delay: 0)
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { self.refresh() }
    }
}
