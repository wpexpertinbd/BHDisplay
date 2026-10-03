import AppKit
import Combine
import CryptoKit

/// Keyboard & mouse sharing — "input follows the shared monitor".
/// - Shared monitor shows THIS Mac: the Mac keeps its input; the peer (whose screen isn't visible) takes over
///   the Mac entirely (ENTER edge 4).
/// - Shared monitor shows the PEER: the monitor's area on this Mac's desktop IS the peer — moving the pointer
///   onto it hands keyboard & mouse to the peer; the peer's pointer leaving toward us comes back beside it.
///   If this Mac has no other screen (lid closed), all of its input goes to the peer.
/// Never changes the monitor input (that stays BHDisplay's menu / shortcut).
@MainActor
final class ShareController: NSObject, ObservableObject, ShareCaptureDelegate {
    static let shared = ShareController()

    struct Device: Identifiable, Equatable { let id: String; let name: String; let host: String; let port: UInt16; let fpPrefix: Data; var lastSeen: Date }

    @Published private(set) var running = false
    @Published private(set) var status = "Off"
    @Published private(set) var needsAccessibility = false
    @Published private(set) var discovered: [Device] = []
    @Published private(set) var connectedName: String?
    @Published private(set) var controlling = false                // this Mac's input goes to the peer
    @Published private(set) var controlled = false                 // the peer's input drives this Mac
    @Published var enabled: Bool { didSet { UserDefaults.standard.set(enabled, forKey: "shareEnabled"); enabled ? start() : stop() } }
    @Published var swapCmdCtrl: Bool { didSet { UserDefaults.standard.set(swapCmdCtrl, forKey: "shareSwapCmdCtrl"); capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl } }
    @Published private(set) var paired: [String: String]

    private let q = DispatchQueue(label: "com.biswashost.bhdisplay.share")
    private var identity: ShareIdentity?
    private let listener = ShareListener()
    private let discovery = ShareDiscovery()
    private let capture = ShareCapture()
    private let emulator = ShareEmulator()
    private let clipboard = ShareClipboard()
    private var session: ShareSession?
    private var pending: [ObjectIdentifier: ShareSession] = [:]
    private var pairing: (session: ShareSession, local: Bool, remote: Bool)?
    private var dialing: Set<String> = []
    private var lastPairPrompt = Date.distantPast
    private var peerHosts: [String: String] = UserDefaults.standard.dictionary(forKey: "sharePeerHosts") as? [String: String] ?? [:]
    private var timers: [Timer] = []
    private var inputWatch: AnyCancellable?
    private var displayWatch: AnyCancellable?
    private var showsPeer = false                    // the shared monitor currently shows the peer
    private var suppressUntil = Date.distantPast     // after ⌃⌥⌘Esc: don't re-capture for a moment

    private override init() {
        let d = UserDefaults.standard
        enabled = d.bool(forKey: "shareEnabled")
        swapCmdCtrl = d.object(forKey: "shareSwapCmdCtrl") as? Bool ?? true
        paired = d.dictionary(forKey: "sharePaired") as? [String: String] ?? [:]
        super.init()
        capture.delegate = self
        capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl
        emulator.onLeave = { [weak self] pos in MainActor.assumeIsolated { self?.peerPointerLeft(position: pos) } }
    }

    func bootstrap() { if enabled { start() } }

    // MARK: lifecycle

    func start() {
        guard !running else { return }
        if !ShareCapture.hasPermission {
            needsAccessibility = true
            status = "Needs Accessibility permission"
            ShareCapture.requestPermission()
            return
        }
        needsAccessibility = false
        do { identity = try ShareIdentity.loadOrCreate() } catch { status = "Can't create identity: \(error)"; return }
        guard let identity else { return }
        guard capture.start() else { needsAccessibility = true; status = "Needs Accessibility permission"; return }
        LegacyCleanup.removeOldSharingJob()
        ShareLog.write("sharing on (\(identity.name))")

        listener.onSession = { [weak self] s in self?.q.async { self?.adopt(s) } }
        listener.onError = { [weak self] e in Task { @MainActor in self?.listenerFailed(e) } }
        listener.start(identity: identity, queue: q)
        discovery.onBeacon = { [weak self] b in Task { @MainActor in self?.beacon(b) } }
        discovery.start(identity: identity, queue: q)
        running = true
        timers = [
            Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.reconnectKnownPeers() } },
            // Notice switches made with the monitor's own buttons or by its Auto Detect.
            Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { if self?.session != nil { MonitorModel.shared.refresh(full: false) } }
            },
        ]
        inputWatch = MonitorModel.shared.$input.removeDuplicates().dropFirst().sink { [weak self] _ in
            Task { @MainActor in self?.monitorInputChanged(announce: true) }
        }
        displayWatch = MonitorModel.shared.$macDisplayOff.removeDuplicates().dropFirst().sink { [weak self] off in
            Task { @MainActor in ShareLog.write(off ? "Mac display to the monitor turned off" : "Mac display to the monitor turned on"); self?.applyMode() }
        }
        monitorInputChanged(announce: false)
        updateStatus()
    }

    func stop() {
        guard running || needsAccessibility else { return }
        running = false
        timers.forEach { $0.invalidate() }; timers = []
        inputWatch = nil; displayWatch = nil
        capture.stop()
        emulator.leave()
        controlling = false; controlled = false
        discovery.stop(); listener.stop()
        session?.close("sharing turned off"); session = nil
        for s in pending.values { s.close("sharing turned off") }
        pending.removeAll(); pairing = nil
        discovered.removeAll(); connectedName = nil
        status = "Off"
        ShareLog.write("sharing off")
    }

    /// The port can be briefly unavailable (e.g. TIME_WAIT for ~30 s after a quick restart): retry, don't give up.
    private func listenerFailed(_ e: String) {
        ShareLog.write(e + " — retrying in 5 s")
        listener.stop()
        q.asyncAfter(deadline: .now() + 5) { [weak self] in
            Task { @MainActor in
                guard let self, self.running, let identity = self.identity else { return }
                self.listener.start(identity: identity, queue: self.q)
            }
        }
    }

    func retryAccessibility() { if ShareCapture.hasPermission { needsAccessibility = false; start() } else { ShareCapture.requestPermission() } }

    // MARK: shared-monitor mode

    /// The shared monitor's place among this Mac's displays (nil if it isn't attached).
    private var lastSharedOnRight = true

    private func layout() -> ShareLayout? {
        if let off = DisplayPower.disabledID {
            // Our output to the shared monitor is off: cross at the edge of our screens that faced it.
            _ = off
            return ShareLayout(shared: nil, others: ShareScreens.displays(), sharedOnRight: lastSharedOnRight)
        }
        guard let id = MonitorModel.shared.info.displayID ?? MonitorInfo.displayID(identity: nil), DisplayPower.isActive(id) else { return nil }
        let shared = CGDisplayBounds(id)
        let others = ShareScreens.displays().filter { $0 != shared }
        let own = others.reduce(CGRect.null) { $0.union($1) }
        lastSharedOnRight = others.isEmpty || shared.midX >= own.midX
        return ShareLayout(shared: shared, others: others, sharedOnRight: lastSharedOnRight)
    }

    /// Re-derive the mode from what the shared monitor shows; `announce` tells the peer about a change.
    private func monitorInputChanged(announce: Bool) {
        let m = MonitorModel.shared
        guard running, let input = m.input, MonitorInput.isValid(input), let mac = m.macInput else { return }
        let nowShowsPeer = input != mac
        if announce { session?.send(.monitorShows(UInt8(truncatingIfNeeded: input))) }
        if nowShowsPeer != showsPeer {
            showsPeer = nowShowsPeer
            ShareLog.write("monitor shows \(nowShowsPeer ? "the other computer" : "this Mac") (\(MonitorInput.name(for: input)))")
        }
        applyMode()
    }

    private func applyMode() {
        let lay = layout()
        guard showsPeer, let lay else {
            // Shared monitor shows this Mac: keep our input; stop forwarding if we were.
            capture.watch = nil
            if controlling { stopControlling(warpTo: lay?.shared.map { CGPoint(x: $0.midX, y: $0.midY) }) }
            updateStatus(); return
        }
        // Shared monitor shows the peer.
        if controlled { emulator.leave(); controlled = false }       // the peer is visible now; it keeps its input
        if lay.others.isEmpty {
            capture.watch = nil                                     // no screen of our own: everything goes to the peer
            if !controlling { startControlling(position: 0.5, takeover: true, layout: lay) }
        } else {
            capture.watch = lay
            // If our pointer is already on the shared monitor, the user is "on" the peer now.
            if !controlling, let p = CGEvent(source: nil)?.location, let s = lay.shared, s.contains(p) {
                startControlling(position: lay.position(p), takeover: false, layout: lay)
            }
        }
        updateStatus()
    }

    @discardableResult
    private func startControlling(position: Float, takeover: Bool, layout lay: ShareLayout) -> Bool {
        guard running, let s = session, Date() > suppressUntil else { return false }
        if let text = clipboard.takeOutgoing() { s.send(.clipboard(text)) }
        s.send(.enter(edge: takeover ? 4 : (lay.sharedOnRight ? 0 : 1), position: position))
        capture.begin(parkAt: takeover ? nil : lay.besideShared(position))
        controlling = true
        ShareLog.write("→ controlling \(connectedName ?? "peer") (\(takeover ? "take-over" : "pointer moved onto the shared monitor"))")
        updateStatus()
        return true
    }

    private func stopControlling(warpTo p: CGPoint?) {
        guard controlling else { return }
        session?.send(.releaseAll)
        session?.send(.leave(edge: 4, position: 0.5))
        capture.end(warpTo: p)
        controlling = false
        ShareLog.write("← stopped controlling the peer")
        updateStatus()
    }

    // MARK: discovery & connections

    private func isPaired(prefix: Data) -> Bool { paired.keys.contains { Data(hexString: $0)?.prefix(8) == prefix } }

    private func beacon(_ b: ShareDiscovery.Beacon) {
        guard running, let identity else { return }
        let key = b.deviceID.hex
        if isPaired(prefix: b.fingerprintPrefix) {
            discovered.removeAll { $0.id == key }
            if session == nil, identity.deviceID.lexicographicallyPrecedes(b.deviceID), !dialing.contains(b.host) {
                dial(host: b.host, port: b.port)
            }
        } else if let i = discovered.firstIndex(where: { $0.id == key }) {
            discovered[i].lastSeen = Date()
        } else {
            discovered.append(Device(id: key, name: b.name, host: b.host, port: b.port, fpPrefix: b.fingerprintPrefix, lastSeen: Date()))
            ShareLog.write("found \(b.name) at \(b.host)")
        }
        discovered.removeAll { Date().timeIntervalSince($0.lastSeen) > 10 }
        updateStatus()
    }

    private func dial(host: String, port: UInt16) {
        guard let identity else { return }
        dialing.insert(host)
        let s = ShareSession.dial(host: host, port: port, identity: identity, queue: q)
        q.asyncAfter(deadline: .now() + 10) { [weak self] in Task { @MainActor in self?.dialing.remove(host) } }
        q.async { self.adopt(s) }
    }

    private func reconnectKnownPeers() {
        guard running, session == nil, pairing == nil else { return }
        for (fp, host) in peerHosts where paired[fp] != nil && !dialing.contains(host) { dial(host: host, port: BHDS.tcpPort) }
    }

    func pair(with d: Device) { dial(host: d.host, port: d.port) }

    func connect(toHost host: String) {
        guard running, !host.isEmpty else { return }
        dial(host: host, port: BHDS.tcpPort)
        status = "Connecting to \(host)…"
    }

    func forget(_ fingerprintHex: String) {
        paired.removeValue(forKey: fingerprintHex)
        UserDefaults.standard.set(paired, forKey: "sharePaired")
        peerHosts.removeValue(forKey: fingerprintHex)
        UserDefaults.standard.set(peerHosts, forKey: "sharePeerHosts")
        if session?.peerFingerprint.hex == fingerprintHex { session?.close("forgotten") }
    }

    nonisolated private func adopt(_ s: ShareSession) {
        s.onReady = { [weak self] s in Task { @MainActor in self?.ready(s) } }
        s.onMessage = { [weak self] s, m in Task { @MainActor in self?.received(m, from: s) } }
        s.onClose = { [weak self] s, why in Task { @MainActor in self?.closed(s, why) } }
        Task { @MainActor in self.pending[ObjectIdentifier(s)] = s }
        s.start()
    }

    private func isPreferred(_ s: ShareSession) -> Bool {
        guard let me = identity?.deviceID, let peer = s.peer?.deviceID else { return true }
        return (s.role == .dialer) == me.lexicographicallyPrecedes(peer)
    }

    private func ready(_ s: ShareSession) {
        guard running else { s.close("not running"); return }
        let fp = s.peerFingerprint.hex
        if paired[fp] != nil {
            pending.removeValue(forKey: ObjectIdentifier(s))
            if let old = session, old !== s {
                // Both sides may dial at once: keep the one dialed by the lower device id (both apply this rule).
                guard isPreferred(s) else { s.close("duplicate connection"); return }
                old.close("duplicate connection")
            }
            session = s
            connectedName = s.peer?.name
            peerHosts[fp] = s.remoteHost
            UserDefaults.standard.set(peerHosts, forKey: "sharePeerHosts")
            discovered.removeAll { $0.fpPrefix == s.peerFingerprint.prefix(8) }
            ShareLog.write("connected to \(s.peer?.name ?? "?") at \(s.remoteHost)")
            sendMonitorPorts()
            if let input = MonitorModel.shared.input { s.send(.monitorShows(UInt8(truncatingIfNeeded: input))) }
            applyMode()
            updateStatus()
            return
        }
        guard pairing == nil, Date().timeIntervalSince(lastPairPrompt) > 5 else { s.close("busy pairing"); return }
        lastPairPrompt = Date()
        pairing = (s, false, false)
        q.asyncAfter(deadline: .now() + 60) { [weak self] in
            Task { @MainActor in if self?.pairing?.session === s { s.close("pairing timed out") } }
        }
        promptPairing(s)
    }

    private func promptPairing(_ s: ShareSession) {
        let name = s.peer?.name ?? "another computer"
        let code = s.pairCode.prefix(3) + " " + s.pairCode.suffix(3)
        NSApp.activate()
        let a = NSAlert()
        a.messageText = "Pair with “\(name)”?"
        a.informativeText = """
        \(name) (\(s.remoteHost)) wants to share keyboard and mouse with this Mac.

        Pairing code:  \(code)

        Pair only if the other computer shows exactly the same code. Once paired, its keyboard and mouse can control this Mac.
        """
        a.addButton(withTitle: "Pair")
        a.addButton(withTitle: "Don't Pair")
        a.alertStyle = .warning
        let ok = a.runModal() == .alertFirstButtonReturn
        guard pairing?.session === s else { return }
        if ok {
            pairing?.local = true
            s.send(.pairConfirm)
            finishPairingIfBoth()
        } else {
            s.send(.pairReject)
            s.close("pairing declined")
        }
    }

    private func finishPairingIfBoth() {
        guard let p = pairing, p.local, p.remote else { return }
        let s = p.session
        pairing = nil
        paired[s.peerFingerprint.hex] = s.peer?.name ?? "Computer"
        UserDefaults.standard.set(paired, forKey: "sharePaired")
        ShareLog.write("paired with \(s.peer?.name ?? "?")")
        ready(s)
    }

    private func closed(_ s: ShareSession, _ why: String) {
        pending.removeValue(forKey: ObjectIdentifier(s))
        if pairing?.session === s { pairing = nil }
        guard session === s else { return }
        session = nil
        connectedName = nil
        if controlling { capture.end(warpTo: layout()?.besideShared(0.5)); controlling = false }   // never leave input swallowed
        if controlled { emulator.leave(); controlled = false }
        ShareLog.write("disconnected: \(why)")
        status = "Disconnected (\(why))"
        q.asyncAfter(deadline: .now() + 2) { [weak self] in Task { @MainActor in self?.updateStatus() } }
    }

    // MARK: messages

    private func received(_ m: ShareMsg, from s: ShareSession) {
        if m == .pairConfirm, pairing?.session === s { pairing?.remote = true; finishPairingIfBoth(); return }
        if m == .pairReject, pairing?.session === s { s.close("pairing declined on the other computer"); return }
        guard s === session else { return }
        switch m {
        case .enter(let edge, let pos):
            if controlling { capture.end(warpTo: nil); controlling = false }
            // Take-over (edge 4), or the shared monitor shows this Mac: the pointer stays where it is.
            emulator.enter(position: pos, layout: edge == 4 || !showsPeer ? nil : layout())
            controlled = true
            ShareLog.write("← \(connectedName ?? "peer") is controlling this Mac (\(edge == 4 ? "take-over" : "came across"))")
        case .leave(_, let pos):
            if controlling {
                capture.end(warpTo: showsPeer ? layout()?.besideShared(pos) : nil)
                controlling = false
                ShareLog.write("← back on this Mac")
            } else if controlled {
                emulator.leave(); controlled = false
                ShareLog.write("peer stopped controlling this Mac")
            }
        case .move(let dx, let dy): if controlled { emulator.move(dx: dx, dy: dy) }
        case .button(let b, let d): if controlled { emulator.button(b, down: d) }
        case .scroll(let dx, let dy): if controlled { emulator.scroll(dx: dx, dy: dy) }
        case .key(let u, let d): if controlled { emulator.key(usage: u, down: d) }
        case .releaseAll: emulator.releaseAll()
        case .clipboard(let text): clipboard.apply(text)
        case .switchRequest(let code):
            // The other computer asks us to switch (we may need to turn our output back on first).
            if MonitorInput.isValid(UInt16(code)) { ShareLog.write("switch requested by peer: \(MonitorInput.name(for: UInt16(code)))"); MonitorModel.shared.switchTo(UInt16(code)) }
        case .monitorShows(let code):
            if MonitorInput.isValid(UInt16(code)), MonitorModel.shared.input != UInt16(code) {
                MonitorModel.shared.adoptInput(UInt16(code))       // the peer switched it
            }
        default: break
        }
        updateStatus()
    }

    private func peerPointerLeft(position: Float) {
        guard controlled, let s = session, let lay = layout() else { return }
        controlled = false
        ShareLog.write("→ pointer moved onto the shared monitor: back to \(connectedName ?? "peer")")
        if let text = clipboard.takeOutgoing() { s.send(.clipboard(text)) }
        s.send(.leave(edge: lay.sharedOnRight ? 1 : 0, position: position))
        updateStatus()
    }

    private func sendMonitorPorts() {
        let m = MonitorModel.shared
        guard let mac = m.macInput else { return }
        let other = m.otherInput ?? (mac == 0x0F ? 0x12 : 0x0F)
        session?.send(.monitorPorts(mac: UInt8(truncatingIfNeeded: mac), other: UInt8(truncatingIfNeeded: other)))
    }

    // MARK: ShareCaptureDelegate (called on the main run loop by the event tap)

    nonisolated func captureEnteredShared(position: Float) -> Bool {
        MainActor.assumeIsolated {
            guard showsPeer, !controlled, let lay = layout() else { return false }
            return startControlling(position: position, takeover: false, layout: lay)
        }
    }

    nonisolated func captured(_ msg: ShareMsg) {
        MainActor.assumeIsolated { if controlling { session?.send(msg) } }
    }

    nonisolated func captureLocalHotkey(_ keyCode: UInt16) {
        MainActor.assumeIsolated {
            switch Int(keyCode) {
            case 0x35:                                           // ⌃⌥⌘Esc: take this Mac's input back
                suppressUntil = Date().addingTimeInterval(5)
                stopControlling(warpTo: layout()?.besideShared(0.5))
            case 0x01: MonitorModel.shared.toggleMacOther()      // S
            case 0x12, 0x13, 0x14:                               // 1 2 3
                let i = Int(keyCode) - 0x12
                if MonitorInput.all.indices.contains(i) { MonitorModel.shared.switchTo(MonitorInput.all[i].id) }
            default: break
            }
        }
    }

    private func updateStatus() {
        guard running else { return }
        if let n = connectedName {
            status = controlling ? "Typing on \(n) — ⌃⌥⌘Esc takes it back"
                : controlled ? "\(n)'s keyboard & mouse are on this Mac"
                : showsPeer ? "Connected to \(n) — move onto the monitor to use it" : "Connected to \(n)"
        } else if paired.isEmpty {
            status = discovered.isEmpty ? "Looking for BHDisplay on your other computer…" : "Found a computer — pair it below"
        } else {
            status = "Waiting for \(paired.values.sorted().joined(separator: ", "))…"
        }
    }
}

/// Small rolling log of sharing events: ~/Library/Logs/BHDisplay/sharing.log (restarted past 256 KB).
enum ShareLog {
    static var url: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/BHDisplay/sharing.log")
    }
    private static let fmt: DateFormatter = { let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"; return f }()
    static func write(_ line: String) {
        let u = url
        try? FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let size = (try? FileManager.default.attributesOfItem(atPath: u.path))?[.size] as? Int, size > 256 * 1024 {
            try? FileManager.default.removeItem(at: u)
        }
        let data = Data("\(fmt.string(from: Date())) \(line)\n".utf8)
        if let h = try? FileHandle(forWritingTo: u) { h.seekToEndOfFile(); h.write(data); try? h.close() }
        else { try? data.write(to: u) }
    }
}

extension Data {
    init?(hexString s: String) {
        guard s.count % 2 == 0 else { return nil }
        var d = Data(capacity: s.count / 2), i = s.startIndex
        while i < s.endIndex {
            let j = s.index(i, offsetBy: 2)
            guard let b = UInt8(s[i..<j], radix: 16) else { return nil }
            d.append(b); i = j
        }
        self = d
    }
}
