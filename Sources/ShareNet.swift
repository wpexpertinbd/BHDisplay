import Foundation
import Network
import CryptoKit

// Keyboard & mouse sharing — TCP session (handshake + encrypted frames), listener, and UDP discovery.
// Everything here runs on one serial queue; callbacks are delivered on that queue.

/// Thread-safety: all mutable state is touched only on `queue`; public methods hop onto it.
final class ShareSession: @unchecked Sendable {
    enum Role { case dialer, listener }
    private enum State { case hello, auth, open, closed }

    let role: Role
    let queue: DispatchQueue
    private let conn: NWConnection
    private let identity: ShareIdentity
    private let ephemeral = P256.KeyAgreement.PrivateKey()
    private var myHello = Data()
    private var peerHelloRaw = Data()
    private var transcript = Data()
    private var state = State.hello
    private var sendCipher: RecordCipher?
    private var recvCipher: RecordCipher?
    private var lastReceive = Date()
    private var timer: DispatchSourceTimer?

    private(set) var peer: Hello?
    private(set) var pairCode = ""
    var remoteHost: String {
        if case .hostPort(let h, _) = conn.endpoint { return "\(h)" }
        return "?"
    }
    var peerFingerprint: Data { peer.map { Data(SHA256.hash(data: $0.identityKey)) } ?? Data() }

    var onReady: ((ShareSession) -> Void)?
    var onMessage: ((ShareSession, ShareMsg) -> Void)?
    var onClose: ((ShareSession, String) -> Void)?

    init(connection: NWConnection, role: Role, identity: ShareIdentity, queue: DispatchQueue) {
        self.conn = connection; self.role = role; self.identity = identity; self.queue = queue
    }

    static func dial(host: String, port: UInt16, identity: ShareIdentity, queue: DispatchQueue) -> ShareSession {
        let c = NWConnection(host: NWEndpoint.Host(host), port: NWEndpoint.Port(rawValue: port)!, using: tcpParameters())
        return ShareSession(connection: c, role: .dialer, identity: identity, queue: queue)
    }

    static func tcpParameters() -> NWParameters {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true                     // input events are tiny and latency-sensitive
        tcp.connectionTimeout = 5
        let p = NWParameters(tls: nil, tcp: tcp)
        p.allowLocalEndpointReuse = true
        return p
    }

    func start() {
        conn.stateUpdateHandler = { [weak self] st in
            guard let self else { return }
            switch st {
            case .ready:
                if self.role == .dialer { self.sendHello() }
                self.readFrame()
            case .failed(let e): self.closeNow("connection failed: \(e)")
            case .cancelled: self.closeNow("connection closed")
            default: break
            }
        }
        conn.start(queue: queue)
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 2, repeating: 2)
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        timer = t
    }

    func send(_ msg: ShareMsg) {
        queue.async { [weak self] in
            guard let self, self.state == .open, var c = self.sendCipher else { return }
            guard let body = try? c.seal(msg.encoded) else { self.closeNow("encryption failed"); return }
            self.sendCipher = c
            self.writeFrame(body)
        }
    }

    func close(_ reason: String) {
        queue.async { [weak self] in self?.closeNow(reason) }
    }

    private func closeNow(_ reason: String) {
        guard state != .closed else { return }
        state = .closed
        timer?.cancel(); timer = nil
        conn.cancel()
        onClose?(self, reason)
        onClose = nil; onMessage = nil; onReady = nil
    }

    // MARK: private

    private func tick() {
        let idle = Date().timeIntervalSince(lastReceive)
        switch state {
        case .open:
            if idle > 6 { closeNow("peer stopped responding") } else { send(.ping) }
        case .hello, .auth:
            if idle > 10 { closeNow("handshake timed out") }
        case .closed: break
        }
    }

    private func sendHello() {
        myHello = Hello(deviceID: identity.deviceID, identityKey: identity.publicKey,
                        ephemeralKey: ephemeral.publicKey.x963Representation,
                        nonce: Data.random(32), name: identity.name).encoded
        writeFrame(myHello)
    }

    private func writeFrame(_ body: Data) {
        var w = WireWriter(); w.u32(UInt32(body.count)); w.bytes(body)
        conn.send(content: w.data, completion: .contentProcessed { [weak self] err in
            if let err { self?.closeNow("send failed: \(err)") }
        })
    }

    private func readFrame() {
        conn.receive(minimumIncompleteLength: 4, maximumLength: 4) { [weak self] head, _, done, err in
            guard let self, self.state != .closed else { return }
            guard err == nil, let head, head.count == 4 else { self.closeNow(done ? "peer closed" : "receive failed"); return }
            var r = WireReader(head)
            let n = Int((try? r.u32()) ?? 0)
            guard n >= 1, n <= BHDS.maxFrame else { self.closeNow("bad frame length"); return }
            self.conn.receive(minimumIncompleteLength: n, maximumLength: n) { body, _, _, err in
                guard self.state != .closed else { return }
                guard err == nil, let body, body.count == n else { self.closeNow("receive failed"); return }
                self.lastReceive = Date()
                do { try self.handle(Data(body)) } catch { self.closeNow("\(error)"); return }
                if self.state != .closed { self.readFrame() }
            }
        }
    }

    private func handle(_ frame: Data) throws {
        switch state {
        case .hello:
            let h = try Hello.decode(frame)
            guard h.deviceID != identity.deviceID else { throw WireError.bad("connected to itself") }
            peer = h; peerHelloRaw = frame
            if role == .listener { sendHello() }
            transcript = role == .dialer
                ? Handshake.transcript(dialerHello: myHello, listenerHello: frame)
                : Handshake.transcript(dialerHello: frame, listenerHello: myHello)
            writeFrame(try Handshake.sign(identity, role: role == .dialer ? 0 : 1, transcript: transcript))
            state = .auth
        case .auth:
            guard let p = peer,
                  Handshake.verify(signature: frame, identityKey: p.identityKey,
                                   role: role == .dialer ? 1 : 0, transcript: transcript)
            else { throw WireError.bad("peer failed authentication") }
            let keys = try Handshake.deriveKeys(ephemeral: ephemeral, peerEphemeral: p.ephemeralKey,
                                                transcript: transcript, isDialer: role == .dialer)
            sendCipher = RecordCipher(key: keys.send)
            recvCipher = RecordCipher(key: keys.receive)
            pairCode = keys.pairCode
            state = .open
            onReady?(self)
        case .open:
            guard var c = recvCipher else { throw WireError.bad("no session key") }
            let plain = try c.open(frame)
            recvCipher = c
            let msg = try ShareMsg.decode(plain)
            if msg == .ping { send(.pong) }
            onMessage?(self, msg)
        case .closed: break
        }
    }
}

/// Accepts incoming sessions on the sharing port.
final class ShareListener: @unchecked Sendable {
    private var listener: NWListener?
    var onSession: ((ShareSession) -> Void)?
    var onError: ((String) -> Void)?

    func start(identity: ShareIdentity, queue: DispatchQueue) {
        do {
            let l = try NWListener(using: ShareSession.tcpParameters(), on: NWEndpoint.Port(rawValue: BHDS.tcpPort)!)
            l.newConnectionHandler = { [weak self] c in
                self?.onSession?(ShareSession(connection: c, role: .listener, identity: identity, queue: queue))
            }
            l.stateUpdateHandler = { [weak self] st in
                if case .failed(let e) = st { self?.onError?("can't listen on port \(BHDS.tcpPort): \(e)") }
            }
            l.start(queue: queue)
            listener = l
        } catch { onError?("can't listen on port \(BHDS.tcpPort): \(error)") }
    }

    func stop() { listener?.cancel(); listener = nil }
}

/// UDP broadcast "here I am" beacons. Informational only — sessions authenticate everything.
final class ShareDiscovery: @unchecked Sendable {
    struct Beacon { let deviceID: Data; let host: String; let port: UInt16; let fingerprintPrefix: Data; let name: String }

    private var fd: Int32 = -1
    private var readSource: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    var onBeacon: ((Beacon) -> Void)?

    func start(identity: ShareIdentity, queue: DispatchQueue) {
        fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &yes, socklen_t(MemoryLayout<Int32>.size))
        _ = fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK)
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = BHDS.udpPort.bigEndian
        addr.sin_addr.s_addr = INADDR_ANY
        let bound = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard bound == 0 else { Darwin.close(fd); fd = -1; return }

        let rs = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        rs.setEventHandler { [weak self] in self?.receive(ownID: identity.deviceID) }
        rs.resume()
        readSource = rs

        var w = WireWriter()
        w.bytes(Data("BHDS1".utf8)); w.bytes(identity.deviceID); w.u16(BHDS.tcpPort)
        w.bytes(identity.fingerprint.prefix(8))
        let n = Data(identity.name.utf8).prefix(64); w.u8(UInt8(n.count)); w.bytes(n)
        let beacon = w.data
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: 2)
        t.setEventHandler { [weak self] in self?.broadcast(beacon) }
        t.resume()
        timer = t
    }

    func stop() {
        timer?.cancel(); timer = nil
        readSource?.cancel(); readSource = nil
        if fd >= 0 { Darwin.close(fd); fd = -1 }
    }

    private func broadcast(_ payload: Data) {
        guard fd >= 0 else { return }
        var to = sockaddr_in()
        to.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        to.sin_family = sa_family_t(AF_INET)
        to.sin_port = BHDS.udpPort.bigEndian
        to.sin_addr.s_addr = INADDR_BROADCAST
        _ = payload.withUnsafeBytes { buf in
            withUnsafePointer(to: &to) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, buf.baseAddress, buf.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
        }
    }

    private func receive(ownID: Data) {
        var buf = [UInt8](repeating: 0, count: 512)
        var from = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        while true {
            let n = withUnsafeMutablePointer(to: &from) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) }
            }
            guard n > 0 else { return }
            var r = WireReader(Data(buf[0..<n]))
            guard (try? r.bytes(5)) == Data("BHDS1".utf8),
                  let id = try? r.bytes(16), id != ownID,
                  let port = try? r.u16(), let fp = try? r.bytes(8),
                  let nl = try? r.u8(), nl <= 64, let nameData = try? r.bytes(Int(nl)) else { continue }
            var ip = from.sin_addr
            var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            inet_ntop(AF_INET, &ip, &text, socklen_t(INET_ADDRSTRLEN))
            onBeacon?(Beacon(deviceID: id, host: String(cString: text), port: port, fingerprintPrefix: fp,
                             name: String(decoding: nameData, as: UTF8.self)))
        }
    }
}
