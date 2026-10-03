import AppKit
import CryptoKit

/// Keyboard & mouse sharing — ties discovery, sessions, pairing, capture, emulation and clipboard together.
/// Never changes the monitor input (that stays BHDisplay's menu / shortcut).
@MainActor
final class ShareController: NSObject, ObservableObject, ShareCaptureDelegate {
    static let shared = ShareController()

    struct Device: Identifiable, Equatable { let id: String; let name: String; let host: String; let port: UInt16; let fpPrefix: Data; var lastSeen: Date }

    @Published private(set) var running = false
    @Published private(set) var status = "Off"
    @Published private(set) var needsAccessibility = false
    @Published private(set) var discovered: [Device] = []          // unpaired BHDisplay devices on the LAN
    @Published private(set) var connectedName: String?
    @Published private(set) var controlling = false                // this Mac's input goes to the peer
    @Published private(set) var controlled = false                 // the peer's input drives this Mac
    @Published var enabled: Bool { didSet { UserDefaults.standard.set(enabled, forKey: "shareEnabled"); enabled ? start() : stop() } }
    @Published var side: ShareEdge { didSet { UserDefaults.standard.set(Int(side.rawValue), forKey: "shareSide"); capture.edge = side; emulator.edge = side } }
    @Published var swapCmdCtrl: Bool { didSet { UserDefaults.standard.set(swapCmdCtrl, forKey: "shareSwapCmdCtrl"); capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl } }
    /// Paired peers: hex(SHA-256 of identity key) → name. Public values; trust comes from the handshake.
    @Published private(set) var paired: [String: String]

    private let q = DispatchQueue(label: "com.biswashost.bhdisplay.share")
    private var identity: ShareIdentity?
    private let listener = ShareListener()
    private let discovery = ShareDiscovery()
    private let capture = ShareCapture()
    private let emulator = ShareEmulator()
    private let clipboard = ShareClipboard()
    private var session: ShareSession?               // the authenticated, paired session in use
    private var pending: [ObjectIdentifier: ShareSession] = [:]
    private var pairing: (session: ShareSession, local: Bool, remote: Bool)?
    private var pairIntent: Data?                    // fingerprint prefix the user asked to pair with
    private var dialing: Set<String> = []
    private var lastPairPrompt = Date.distantPast

    private override init() {
        let d = UserDefaults.standard
        enabled = d.bool(forKey: "shareEnabled")
        side = ShareEdge(rawValue: UInt8(d.object(forKey: "shareSide") as? Int ?? 1)) ?? .right
        swapCmdCtrl = d.object(forKey: "shareSwapCmdCtrl") as? Bool ?? true
        paired = d.dictionary(forKey: "sharePaired") as? [String: String] ?? [:]
        super.init()
        capture.delegate = self
        capture.edge = side; emulator.edge = side
        capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl
        emulator.onLeave = { [weak self] pos in
            MainActor.assumeIsolated { self?.peerPointerLeft(position: pos) }
        }
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
        // Lan Mouse would capture the same edge — only one sharing tool may run.
        if case .running = InputSharing.state() { InputSharing.disable() }

        listener.onSession = { [weak self] s in self?.q.async { self?.adopt(s) } }
        listener.onError = { [weak self] e in Task { @MainActor in self?.status = e } }
        listener.start(identity: identity, queue: q)
        discovery.onBeacon = { [weak self] b in Task { @MainActor in self?.beacon(b) } }
        discovery.start(identity: identity, queue: q)
        running = true
        updateStatus()
    }

    func stop() {
        guard running || needsAccessibility else { return }
        running = false
        capture.stop()
        emulator.leave()
        controlling = false; controlled = false
        discovery.stop(); listener.stop()
        session?.close("sharing turned off"); session = nil
        for s in pending.values { s.close("sharing turned off") }
        pending.removeAll(); pairing = nil
        discovered.removeAll(); connectedName = nil
        status = "Off"
    }

    func retryAccessibility() { if ShareCapture.hasPermission { needsAccessibility = false; start() } else { ShareCapture.requestPermission() } }

    // MARK: discovery & connections

    private func isPaired(prefix: Data) -> Bool { paired.keys.contains { Data(hexString: $0)?.prefix(8) == prefix } }

    private func beacon(_ b: ShareDiscovery.Beacon) {
        guard running, let identity else { return }
        let key = b.deviceID.hex
        if isPaired(prefix: b.fingerprintPrefix) {
            discovered.removeAll { $0.id == key }
            // Lower device id dials; the other side waits for it.
            if session == nil, identity.deviceID.lexicographicallyPrecedes(b.deviceID), !dialing.contains(key) {
                dial(b)
            }
        } else if let i = discovered.firstIndex(where: { $0.id == key }) {
            discovered[i].lastSeen = Date()
        } else {
            discovered.append(Device(id: key, name: b.name, host: b.host, port: b.port, fpPrefix: b.fingerprintPrefix, lastSeen: Date()))
        }
        discovered.removeAll { Date().timeIntervalSince($0.lastSeen) > 10 }
    }

    private func dial(_ b: ShareDiscovery.Beacon) {
        guard let identity else { return }
        dialing.insert(b.deviceID.hex)
        let s = ShareSession.dial(host: b.host, port: b.port, identity: identity, queue: q)
        let key = b.deviceID.hex
        q.asyncAfter(deadline: .now() + 15) { [weak self] in Task { @MainActor in self?.dialing.remove(key) } }
        q.async { self.adopt(s) }
    }

    /// User asked to pair with a discovered device.
    func pair(with d: Device) {
        guard let identity else { return }
        pairIntent = d.fpPrefix
        let s = ShareSession.dial(host: d.host, port: d.port, identity: identity, queue: q)
        q.async { self.adopt(s) }
    }

    func forget(_ fingerprintHex: String) {
        paired.removeValue(forKey: fingerprintHex)
        UserDefaults.standard.set(paired, forKey: "sharePaired")
        if session?.peerFingerprint.hex == fingerprintHex { session?.close("forgotten") }
    }

    /// Runs on `q`: wire a new (incoming or outgoing) session's callbacks.
    nonisolated private func adopt(_ s: ShareSession) {
        s.onReady = { [weak self] s in Task { @MainActor in self?.ready(s) } }
        s.onMessage = { [weak self] s, m in Task { @MainActor in self?.received(m, from: s) } }
        s.onClose = { [weak self] s, why in Task { @MainActor in self?.closed(s, why) } }
        Task { @MainActor in self.pending[ObjectIdentifier(s)] = s }
        s.start()
    }

    private func ready(_ s: ShareSession) {
        guard running else { s.close("not running"); return }
        let fp = s.peerFingerprint.hex
        if paired[fp] != nil {
            if let old = session, old !== s { old.close("replaced by a newer connection") }
            pending.removeValue(forKey: ObjectIdentifier(s))
            session = s
            connectedName = s.peer?.name
            discovered.removeAll { $0.fpPrefix == s.peerFingerprint.prefix(8) }
            sendMonitorPorts()
            updateStatus()
            return
        }
        // Unpaired: only one pairing at a time, and at most one prompt every 5 s (no prompt flooding).
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
        guard pairing?.session === s else { return }           // timed out / closed while the alert was open
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
        pairIntent = nil
        paired[s.peerFingerprint.hex] = s.peer?.name ?? "Computer"
        UserDefaults.standard.set(paired, forKey: "sharePaired")
        ready(s)                                               // now a paired session
    }

    private func closed(_ s: ShareSession, _ why: String) {
        pending.removeValue(forKey: ObjectIdentifier(s))
        if pairing?.session === s { pairing = nil }
        guard session === s else { return }
        session = nil
        connectedName = nil
        if controlling { capture.end(at: 0.5); controlling = false }   // never leave this Mac's input swallowed
        if controlled { emulator.leave(); controlled = false }
        status = "Disconnected (\(why))"
        q.asyncAfter(deadline: .now() + 2) { [weak self] in Task { @MainActor in self?.updateStatus() } }
    }

    // MARK: messages

    private func received(_ m: ShareMsg, from s: ShareSession) {
        if m == .pairConfirm, pairing?.session === s { pairing?.remote = true; finishPairingIfBoth(); return }
        if m == .pairReject, pairing?.session === s { s.close("pairing declined on the other computer"); return }
        guard s === session else { return }                    // input only from the paired, active session
        switch m {
        case .enter(_, let pos):
            if controlling { capture.end(at: nil); controlling = false }
            emulator.enter(position: pos)
            controlled = true
        case .leave(_, let pos):
            guard controlling else { return }
            capture.end(at: pos)
            controlling = false
        case .move(let dx, let dy): if controlled { emulator.move(dx: dx, dy: dy) }
        case .button(let b, let d): if controlled { emulator.button(b, down: d) }
        case .scroll(let dx, let dy): if controlled { emulator.scroll(dx: dx, dy: dy) }
        case .key(let u, let d): if controlled { emulator.key(usage: u, down: d) }
        case .releaseAll: emulator.releaseAll()
        case .clipboard(let text): clipboard.apply(text)
        default: break
        }
        updateStatus()
    }

    private func peerPointerLeft(position: Float) {
        guard controlled, let s = session else { return }
        controlled = false
        if let text = clipboard.takeOutgoing() { s.send(.clipboard(text)) }
        s.send(.leave(edge: side.rawValue, position: position))
        updateStatus()
    }

    private func sendMonitorPorts() {
        let m = MonitorModel.shared
        guard let mac = m.macInput else { return }
        let other = m.otherInput ?? (mac == 0x0F ? 0x12 : 0x0F)
        session?.send(.monitorPorts(mac: UInt8(truncatingIfNeeded: mac), other: UInt8(truncatingIfNeeded: other)))
    }

    // MARK: ShareCaptureDelegate (called on the main run loop by the event tap)

    nonisolated func captureEdgeHit(position: Float) -> Bool {
        MainActor.assumeIsolated {
            guard running, let s = session, !controlled else { return false }
            if let text = clipboard.takeOutgoing() { s.send(.clipboard(text)) }
            s.send(.enter(edge: side.opposite.rawValue, position: position))
            controlling = true
            updateStatus()
            return true
        }
    }

    nonisolated func captured(_ msg: ShareMsg) {
        MainActor.assumeIsolated { if controlling { session?.send(msg) } }
    }

    nonisolated func captureLocalHotkey(_ keyCode: UInt16) {
        MainActor.assumeIsolated {
            switch Int(keyCode) {
            case 0x35:                                           // ⌃⌥⌘Esc: take this Mac's input back
                session?.send(.releaseAll)
                capture.end(at: 0.5); controlling = false; updateStatus()
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
            status = controlling ? "Typing on \(n)" : controlled ? "\(n) is typing on this Mac" : "Connected to \(n)"
        } else if paired.isEmpty {
            status = discovered.isEmpty ? "Looking for BHDisplay on your other computer…" : "Found a computer — pair it below"
        } else {
            status = "Waiting for \(paired.values.sorted().joined(separator: ", "))…"
        }
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
