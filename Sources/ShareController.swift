import AppKit
import Carbon
import IOKit.pwr_mgt
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
    /// Speed of the other computer's mouse on this Mac (1 = default curve).
    @Published var peerMouseSpeed: Double { didSet { UserDefaults.standard.set(peerMouseSpeed, forKey: "sharePeerMouseSpeed"); emulator.speed = peerMouseSpeed } }
    @Published var peerScrollSpeed: Double { didSet { UserDefaults.standard.set(peerScrollSpeed, forKey: "sharePeerScrollSpeed"); emulator.scrollSpeed = peerScrollSpeed } }
    @Published var swapCmdCtrl: Bool { didSet { UserDefaults.standard.set(swapCmdCtrl, forKey: "shareSwapCmdCtrl"); capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl } }
    @Published private(set) var paired: [String: String]
    private var pairedLoaded = false

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
    private var promptOpen = false                      // never stack pairing prompts
    private var peerHosts: [String: String] = UserDefaults.standard.dictionary(forKey: "sharePeerHosts") as? [String: String] ?? [:]
    private var timers: [Timer] = []
    private var inputWatch: AnyCancellable?
    private var displayWatch: AnyCancellable?
    private var showsPeer = false                    // the shared monitor currently shows the peer
    private var suppressUntil = Date.distantPast     // after ⌃⌥⌘Esc: don't re-capture for a moment
    private var lastHandover = Date.distantPast       // no bouncing straight back across the boundary
    private var lastSecureNotice = Date.distantPast

    private override init() {
        let d = UserDefaults.standard
        enabled = d.bool(forKey: "shareEnabled")
        swapCmdCtrl = d.object(forKey: "shareSwapCmdCtrl") as? Bool ?? true
        peerMouseSpeed = min(max(d.object(forKey: "sharePeerMouseSpeed") as? Double ?? 1, 0.5), 3)
        peerScrollSpeed = min(max(d.object(forKey: "sharePeerScrollSpeed") as? Double ?? 1, 0.5), 5)
        let p = ShareController.loadPaired()
        paired = p ?? [:]
        pairedLoaded = p != nil
        super.init()
        capture.delegate = self
        capture.swapCmdCtrl = swapCmdCtrl; emulator.swapCmdCtrl = swapCmdCtrl; emulator.speed = peerMouseSpeed; emulator.scrollSpeed = peerScrollSpeed
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
        ensurePairedLoaded()
        do { identity = try ShareIdentity.loadOrCreate() } catch {
            status = "Sharing can't start yet: \(error)"
            ShareLog.write(status)
            q.asyncAfter(deadline: .now() + 10) { [weak self] in Task { @MainActor in if self?.enabled == true, self?.running == false { self?.start() } } }
            return
        }
        guard let identity else { return }
        guard capture.start() else { needsAccessibility = true; status = "Needs Accessibility permission"; return }
        LegacyCleanup.removeOldSharingJob()
        ShareLog.write("sharing on (\(identity.name))")

        listener.onSession = { [weak self] s in
            // Sharing is between two computers: a connection from this Mac itself is never a peer.
            if ShareController.isLocalHost(s.remoteHost) { s.close("refused: connection from this Mac"); return }
            self?.q.async { self?.adopt(s) }
        }
        listener.onError = { [weak self] e in Task { @MainActor in self?.listenerFailed(e) } }
        listener.start(identity: identity, queue: q)
        discovery.onBeacon = { [weak self] b in Task { @MainActor in self?.beacon(b) } }
        discovery.start(identity: identity, queue: q)
        running = true
        timers = [
            Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.ensurePairedLoaded(); self?.reconnectKnownPeers() } },
            // Secure keyboard entry switched on while forwarding: keys would go to the Mac app — stop forwarding.
            Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, self.controlling, IsSecureEventInputEnabled() else { return }
                    ShareLog.write("secure keyboard entry turned on: keyboard back to this Mac")
                    self.stopControlling(warpTo: self.layout()?.besideShared(0.5))
                }
            },
            // Notice switches made with the monitor's own buttons or by its Auto Detect.
            Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { if self?.session != nil { MonitorModel.shared.refresh(full: false) } }
            },
        ]
        MonitorModel.shared.askPeerToSwitch = { [weak self] code in
            guard let self, let s = self.session else { return false }
            ShareLog.write("asked \(self.connectedName ?? "the peer") to switch the monitor to \(MonitorInput.name(for: code))")
            s.send(.switchRequest(UInt8(truncatingIfNeeded: code)))
            return true
        }
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
        MonitorModel.shared.askPeerToSwitch = nil
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

    /// While the shared monitor shows this Mac, the other computer sits beyond the outer edge of our desktop on
    /// the shared monitor's side: its pointer enters there and leaves back through it.
    private func outerLayout() -> ShareLayout {
        _ = layout()                                               // refreshes lastSharedOnRight
        return ShareLayout(shared: nil, others: ShareScreens.displays(), sharedOnRight: lastSharedOnRight)
    }

    /// Re-derive the mode from what the shared monitor shows; `announce` tells the peer about a change.
    private func monitorInputChanged(announce: Bool) {
        let m = MonitorModel.shared
        guard running, let input = m.input, MonitorInput.isValid(input), let mac = m.macInput else { return }
        let nowShowsPeer = input != mac
        if announce { session?.send(.monitorShows(UInt8(truncatingIfNeeded: input))) }
        let changed = nowShowsPeer != showsPeer
        if changed {
            showsPeer = nowShowsPeer
            ShareLog.write("monitor shows \(nowShowsPeer ? "the other computer" : "this Mac") (\(MonitorInput.name(for: input)))")
        }
        applyMode(modeChanged: changed)
    }

    /// `modeChanged`: the monitor just changed between the two computers. Only then is a peer that is
    /// controlling this Mac sent back — re-applying the same mode (display turned off, screens rearranged)
    /// must not drop a pointer that crossed over on purpose.
    private func applyMode(modeChanged: Bool = false) {
        let lay = layout()
        guard showsPeer, let lay else {
            // Shared monitor shows this Mac: keep our input; stop forwarding if we were.
            capture.watch = nil
            if controlling { stopControlling(warpTo: lay?.shared.map { CGPoint(x: $0.midX, y: $0.midY) }) }
            updateStatus(); return
        }
        // Shared monitor shows the peer.
        if controlled && modeChanged {                              // the screen layout changed under the peer's pointer
            emulator.leave(); controlled = false
            session?.send(.leave(edge: 4, position: 0.5))           // tell it, or it keeps swallowing its own keyboard/mouse
            ShareLog.write("monitor changed: \(connectedName ?? "peer") gets its keyboard/mouse back")
        }
        if lay.others.isEmpty {
            capture.watch = nil                                     // lid closed: no screen of our own to cross from
        } else {
            capture.watch = lay
            // Our pointer was left on the shared monitor, which now shows the peer: bring it onto our own screen
            // instead of handing the peer our keyboard/mouse — the Mac only gives them away when the user
            // deliberately moves the pointer across.
            if !controlling, modeChanged, let p = CGEvent(source: nil)?.location, let s = lay.shared, s.contains(p) {
                let own = lay.others.reduce(CGRect.null) { $0.union($1) }
                CGWarpMouseCursorPosition(CGPoint(x: own.midX, y: own.midY))
            }
        }
        updateStatus()
    }

    @discardableResult
    private func startControlling(position: Float, takeover: Bool, layout lay: ShareLayout) -> Bool {
        guard running, let s = session, Date() > suppressUntil else { return false }
        // Secure keyboard entry (a password field, Terminal's Secure Keyboard Entry) hides key events from us:
        // forwarding then would send the mouse across but type into the Mac app. Don't start.
        if IsSecureEventInputEnabled() {
            if Date().timeIntervalSince(lastSecureNotice) > 10 {
                lastSecureNotice = Date()
                ShareLog.write("not crossing: secure keyboard entry is on in a Mac app")
                status = "Can't share the keyboard while a password field or Secure Keyboard Entry is active on this Mac"
            }
            return false
        }
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
        guard running, let identity, !ShareController.isLocalHost(b.host) else { return }
        let key = b.deviceID.hex
        if isPaired(prefix: b.fingerprintPrefix) {
            discovered.removeAll { $0.id == key }
            if session == nil, identity.deviceID.lexicographicallyPrecedes(b.deviceID), !dialing.contains(b.host), dialing.count < 4 {
                dial(host: b.host, port: BHDS.tcpPort)              // only ever our own port, whatever a beacon claims
            }
        } else if let i = discovered.firstIndex(where: { $0.id == key }) {
            discovered[i].lastSeen = Date()
        } else if discovered.count < 16 {                        // bounded: beacons are unauthenticated
            discovered.append(Device(id: key, name: b.name, host: b.host, port: b.port, fpPrefix: b.fingerprintPrefix, lastSeen: Date()))
            ShareLog.write("found \(b.name) at \(b.host)")
        }
        discovered.removeAll { Date().timeIntervalSince($0.lastSeen) > 10 }
        updateStatus()
    }

    private func dial(host: String, port: UInt16) {
        guard let identity, !ShareController.isLocalHost(host) else { return }
        dialing.insert(host)
        let s = ShareSession.dial(host: host, port: port, identity: identity, queue: q)
        q.asyncAfter(deadline: .now() + 10) { [weak self] in Task { @MainActor in self?.dialing.remove(host) } }
        q.async { self.adopt(s) }
    }

    private func reconnectKnownPeers() {
        guard running, session == nil, pairing == nil else { return }
        for (fp, host) in peerHosts where paired[fp] != nil && !dialing.contains(host) { dial(host: host, port: BHDS.tcpPort) }
    }

    /// New pairings are accepted only for 2 minutes after the user asks for one on THIS computer — a stranger
    /// on the network can never make a pairing prompt appear out of the blue.
    @Published private(set) var pairArmedUntil = Date.distantPast
    var pairingArmed: Bool { Date() < pairArmedUntil }
    func armPairing() {
        pairArmedUntil = Date().addingTimeInterval(120)
        ShareLog.write("ready to pair a new computer for 2 minutes")
        updateStatus()
        q.asyncAfter(deadline: .now() + 121) { [weak self] in Task { @MainActor in self?.updateStatus() } }
    }

    func pair(with d: Device) { armPairing(); dial(host: d.host, port: BHDS.tcpPort) }

    func connect(toHost host: String) {
        guard running, !host.isEmpty else { return }
        armPairing()
        dial(host: host, port: BHDS.tcpPort)
        status = "Connecting to \(host)…"
    }

    func forget(_ fingerprintHex: String) {
        guard ensurePairedLoaded() else { status = "Paired computers can't be read right now — try again"; return }
        let before = paired
        paired.removeValue(forKey: fingerprintHex)
        guard ShareController.savePaired(paired) else {
            paired = before; status = "Couldn't save — the computer is still paired. Try again"; ShareLog.write(status); return
        }
        peerHosts.removeValue(forKey: fingerprintHex)
        UserDefaults.standard.set(peerHosts, forKey: "sharePeerHosts")
        if session?.peerFingerprint.hex == fingerprintHex { session?.close("forgotten") }
    }

    /// The paired list lives in a private file. nil = it exists but can't be read right now: then NOTHING is
    /// written, so a passing problem can never replace the saved pairings with an empty list.
    private static func loadPaired() -> [String: String]? {
        switch ShareStore.read("paired.json") {
        case .found(let d):
            guard let p = try? JSONDecoder().decode([String: String].self, from: d) else {
                // Damaged (not just unreadable): keep a copy aside and start empty — pair again once.
                ShareStore.setAside("paired.json")
                ShareLog.write("paired.json was damaged — kept as paired.json.damaged-*; pair your computer again")
                return [:]
            }
            return p.filter { Data(hexString: $0.key)?.count == 32 }
        case .unreadable(let why):
            ShareLog.write("paired computers can't be read yet (\(why))")
            return nil
        case .notFound:
            return [:]
        }
    }

    /// True if `host` is one of this Mac's own addresses (any interface). Software running on this Mac can only
    /// connect from these, so refusing them means it can never use BHDisplay to type or click here.
    /// Loopback or one of this Mac's own addresses.
    nonisolated static func isLocalHost(_ host: String) -> Bool {
        host.hasPrefix("127.") || host.hasPrefix("::1") || host.hasPrefix("::ffff:127.") || host == "localhost" || isOwnAddress(host)
    }

    nonisolated static func isOwnAddress(_ host: String) -> Bool {
        let h = host.split(separator: "%").first.map(String.init) ?? host
        let bare = h.hasPrefix("::ffff:") ? String(h.dropFirst(7)) : h
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0, let first = list else { return true }   // can't tell → treat as local (refuse)
        defer { freeifaddrs(list) }
        for p in sequence(first: first, next: { $0.pointee.ifa_next }) {
            guard let sa = p.pointee.ifa_addr else { continue }
            var buf = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            let len = socklen_t(sa.pointee.sa_family == UInt8(AF_INET6) ? MemoryLayout<sockaddr_in6>.size : MemoryLayout<sockaddr_in>.size)
            guard sa.pointee.sa_family == UInt8(AF_INET) || sa.pointee.sa_family == UInt8(AF_INET6),
                  getnameinfo(sa, len, &buf, socklen_t(buf.count), nil, 0, NI_NUMERICHOST) == 0 else { continue }
            let addr = String(cString: buf).split(separator: "%").first.map(String.init) ?? ""
            if addr == bare { return true }
        }
        return false
    }

    @discardableResult
    private static func savePaired(_ p: [String: String]) -> Bool {
        guard let d = try? JSONEncoder().encode(p) else { return false }
        return ShareStore.write("paired.json", d)
    }

    /// Loads the paired list if an earlier attempt failed. False = still unreadable (don't change it then).
    @discardableResult
    private func ensurePairedLoaded() -> Bool {
        if pairedLoaded { return true }
        guard let p = ShareController.loadPaired() else { return false }
        paired = p; pairedLoaded = true
        return true
    }

    nonisolated private func adopt(_ s: ShareSession) {
        s.onReady = { [weak self] s in Task { @MainActor in self?.ready(s) } }
        s.onMessage = { [weak self] s, m in Task { @MainActor in self?.received(m, from: s) } }
        s.onClose = { [weak self] s, why in Task { @MainActor in self?.closed(s, why) } }
        Task { @MainActor in
            // At most a few handshakes at a time: strangers can't pile up connections.
            guard self.pending.count < 8 else { s.close("too many connections"); return }
            self.pending[ObjectIdentifier(s)] = s
            s.start()
        }
    }

    private func isPreferred(_ s: ShareSession) -> Bool {
        guard let me = identity?.deviceID, let peer = s.peer?.deviceID else { return true }
        return (s.role == .dialer) == me.lexicographicallyPrecedes(peer)
    }

    private func ready(_ s: ShareSession) {
        guard running else { s.close("not running"); return }
        // Never from this Mac, in either direction: local software could otherwise answer our own dial on one of
        // our addresses and pose as the paired computer.
        guard !ShareController.isLocalHost(s.remoteHost) else { s.close("refused: this Mac's own address"); return }
        let fp = s.peerFingerprint.hex
        if paired[fp] != nil {
            pending.removeValue(forKey: ObjectIdentifier(s))
            if let old = session, old !== s {
                // Both sides may dial at once: keep the one dialed by the lower device id (both apply this rule).
                guard isPreferred(s) else { s.close("duplicate connection"); return }
                // The old session's close callback will find it is no longer current and do nothing,
                // so release everything it carried now — never keep capturing for a session that is gone.
                if controlling { capture.end(warpTo: layout()?.besideShared(0.5)); controlling = false }
                if controlled { emulator.leave(); controlled = false }
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
        guard pairingArmed else { s.close("not accepting new pairings"); return }
        guard pairing == nil, !promptOpen, Date().timeIntervalSince(lastPairPrompt) > 5 else { s.close("busy pairing"); return }
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
        let sameName = paired.values.contains(name)
            ? "\n\n⚠️ A computer named “\(name)” is already paired — this is a DIFFERENT computer using that name."
            : ""
        NSApp.activate()
        let a = NSAlert()
        a.messageText = "Pair with “\(name)”?"
        a.informativeText = """
        \(name) (\(s.remoteHost)) wants to share keyboard and mouse with this Mac.

        Pairing code:  \(code)

        Pair only if the other computer shows exactly the same code. Once paired, its keyboard and mouse can control this Mac.\(sameName)
        """
        a.addButton(withTitle: "Pair")
        a.addButton(withTitle: "Don't Pair")
        a.alertStyle = .warning
        // No key answers "Pair" (a stray Return must never pair), and it can't be clicked for the first 1.5 s.
        let pairButton = a.buttons[0]
        pairButton.keyEquivalent = ""
        a.buttons[1].keyEquivalent = "\u{1b}"
        pairButton.isEnabled = false
        let enable = Timer(timeInterval: 1.5, repeats: false) { _ in pairButton.isEnabled = true }
        RunLoop.main.add(enable, forMode: .modalPanel)
        promptOpen = true
        let ok = a.runModal() == .alertFirstButtonReturn
        promptOpen = false
        enable.invalidate()
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
        pairArmedUntil = .distantPast
        guard ensurePairedLoaded() else { s.close("paired computers can't be read right now"); status = "Couldn't save the pairing — try again"; return }
        let before = paired
        paired[s.peerFingerprint.hex] = s.peer?.name ?? "Computer"
        guard ShareController.savePaired(paired) else {
            paired = before; s.close("pairing couldn't be saved"); status = "Couldn't save the pairing — try again"; ShareLog.write(status); return
        }
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
        guard s === session else {
            if case .ping = m {} else if case .pong = m {} else { ShareLog.write("ignored a message from a connection that is not the active one") }
            return
        }
        switch m {
        case .enter, .move, .button, .scroll, .key: if m.isEnter || controlled { userIsActive() }
        default: break
        }
        switch m {
        case .enter(let edge, let pos):
            if controlling { capture.end(warpTo: nil); controlling = false }
            // Shows the peer: its pointer comes off the shared monitor onto our screens. Shows this Mac: it comes
            // in at the outer edge on the shared monitor's side. (Edge 4 = old take-over: pointer stays put.)
            emulator.enter(position: pos, layout: edge == 4 ? nil : showsPeer ? (layout() ?? outerLayout()) : outerLayout())
            controlled = true
            lastHandover = Date()
            ShareLog.write("← \(connectedName ?? "peer") is controlling this Mac (\(edge == 4 ? "take-over" : "came across"))")
        case .leave(_, let pos):
            if controlling {
                capture.end(warpTo: showsPeer ? layout()?.besideShared(pos) : nil)
                controlling = false
                lastHandover = Date()
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
        case .switchAccepted(let code):
            ShareLog.write("\(connectedName ?? "peer") is switching the monitor to \(MonitorInput.name(for: UInt16(code)))")
            MonitorModel.shared.peerAcceptedSwitch(UInt16(code))
        case .monitorShows(let code):
            ShareLog.write("peer says the monitor shows \(MonitorInput.name(for: UInt16(code)))")
            if MonitorInput.isValid(UInt16(code)), MonitorModel.shared.input != UInt16(code) {
                MonitorModel.shared.adoptInput(UInt16(code))       // the peer switched it
            }
        default: break
        }
        updateStatus()
    }

    /// Input typed in by BHDisplay doesn't count as "someone is here" for macOS, so a sleeping display stays dark
    /// while the other computer's mouse moves on it. Declaring user activity (Apple's API for exactly this) wakes the
    /// display and keeps it awake while that keyboard/mouse is in use. At most every 2 s.
    private var userActivityID: IOPMAssertionID = 0
    private var lastUserActivity = Date.distantPast
    private func userIsActive() {
        guard Date().timeIntervalSince(lastUserActivity) > 2 else { return }
        lastUserActivity = Date()
        IOPMAssertionDeclareUserActivity("BHDisplay: keyboard/mouse of the paired computer" as CFString, kIOPMUserActiveLocal, &userActivityID)
    }

    private func peerPointerLeft(position: Float) {
        // No time guard here: the emulator has already let go, so the peer MUST be told, or it keeps sending into nothing.
        guard controlled, let s = session else { return }
        let lay = showsPeer ? (layout() ?? outerLayout()) : outerLayout()
        controlled = false
        lastHandover = Date()
        ShareLog.write("→ \(connectedName ?? "peer")'s pointer went back to it")
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
            guard showsPeer, Date().timeIntervalSince(lastHandover) > 0.25, let lay = layout() else { return false }
            lastHandover = Date()
            let hadPeer = controlled
            if controlled { emulator.leave(); controlled = false }   // our own mouse wins: the peer's pointer was here
            let ok = startControlling(position: position, takeover: false, layout: lay)
            // We let go of the peer's pointer: if we didn't take over its screen instead, tell it, or it keeps
            // capturing into nothing.
            if !ok && hadPeer { session?.send(.leave(edge: 4, position: 0.5)) }
            return ok
        }
    }

    nonisolated func captured(_ msg: ShareMsg) {
        MainActor.assumeIsolated { if controlling { session?.send(msg) } }
    }

    /// ⌃⌥⌘Esc from the Carbon hot key (works even under secure keyboard entry, where the event tap sees no keys).
    func takeBack() {
        guard controlling else { return }
        suppressUntil = Date().addingTimeInterval(5)
        stopControlling(warpTo: layout()?.besideShared(0.5))
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
        if pairingArmed && pairing == nil {
            status = "Ready to pair — choose “Pair” on the other computer now (2 minutes)"
        } else if let n = connectedName {
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
    /// "Keep a Log" (menu) — on unless the user turned it off.
    static var enabled: Bool {
        get { UserDefaults.standard.object(forKey: "logEnabled") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "logEnabled") }
    }
    /// Opens the log (Console, or the app set for .log files).
    static func open() {
        if !FileManager.default.fileExists(atPath: url.path) { write("log opened") }
        NSWorkspace.shared.open(url)
    }
    static func write(_ line: String) {
        guard enabled else { return }
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
