import Foundation
import CryptoKit
// Loopback test of ShareSession/ShareListener: real TCP on 127.0.0.1:24860.
// Run: swiftc -D BHDS_TESTS ../../Sources/ShareCore.swift ../../Sources/ShareNet.swift main.swift -o /tmp/snt && /tmp/snt
let q = DispatchQueue(label: "test")
let A = ShareIdentity.testing(deviceID: Data.random(16), key: P256.Signing.PrivateKey(), name: "A-dialer")
let B = ShareIdentity.testing(deviceID: Data.random(16), key: P256.Signing.PrivateKey(), name: "B-listener")
var codes: [String] = [], gotAtB: [ShareMsg] = [], gotAtA: [ShareMsg] = [], closed: [String] = []
var listenerSession: ShareSession?
let done = DispatchSemaphore(value: 0)

let L = ShareListener()
L.onError = { print("listener error:", $0); exit(2) }
L.onSession = { s in
    listenerSession = s
    s.onReady = { s in codes.append("B:" + s.pairCode + ":" + (s.peer?.name ?? "")) }
    s.onMessage = { s, m in
        if m == .ping || m == .pong { return }
        gotAtB.append(m)
        if case .clipboard = m { s.send(.monitorPorts(mac: 0x12, other: 0x0F)) }
    }
    s.start()
}
L.start(identity: B, queue: q)

q.asyncAfter(deadline: .now() + 0.3) {
    let d = ShareSession.dial(host: "127.0.0.1", port: BHDS.tcpPort, identity: A, queue: q)
    d.onReady = { s in
        codes.append("A:" + s.pairCode + ":" + (s.peer?.name ?? ""))
        s.send(.move(dx: 3, dy: -4)); s.send(.key(usage: 0x04, down: true)); s.send(.clipboard("hi ✓"))
    }
    d.onMessage = { s, m in
        if m == .ping || m == .pong { return }
        gotAtA.append(m); s.close("test done")
    }
    d.onClose = { _, r in closed.append(r); done.signal() }
    d.start()
    withExtendedLifetime(d) {}
    objc_setAssociatedObject(L, "d", d, .OBJC_ASSOCIATION_RETAIN)
}
let ok = done.wait(timeout: .now() + 8) == .success
q.sync {}
var fails = 0
func check(_ c: Bool, _ n: String) { if !c { fails += 1; print("FAIL:", n) } else { print("ok  ", n) } }
check(ok, "session completed within 8 s")
check(codes.count == 2, "both sides authenticated (\(codes))")
let ca = codes.first { $0.hasPrefix("A:") }?.split(separator: ":"), cb = codes.first { $0.hasPrefix("B:") }?.split(separator: ":")
check(ca?[1] == cb?[1], "same pairing code on both ends")
check(ca?[2] == "B-listener" && cb?[2] == "A-dialer", "each side learned the other's name")
check(gotAtB == [.move(dx: 3, dy: -4), .key(usage: 0x04, down: true), .clipboard("hi ✓")], "listener got messages in order: \(gotAtB)")
check(gotAtA == [.monitorPorts(mac: 0x12, other: 0x0F)], "dialer got reply: \(gotAtA)")
L.stop()
print(fails == 0 ? "ALL PASSED" : "\(fails) FAILED"); exit(fails == 0 ? 0 : 1)
