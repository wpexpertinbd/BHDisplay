import Foundation
import CryptoKit
// Interop peer (Swift side). Usage: peer listen <port> | peer dial <host> <port>
// Prints CODE/PEER/GOT lines; sends three messages on ready; exits 0 after receiving three.
setvbuf(stdout, nil, _IOLBF, 0)
let args = CommandLine.arguments
let q = DispatchQueue(label: "peer")
let me = ShareIdentity.testing(deviceID: Data.random(16), key: P256.Signing.PrivateKey(), name: "swift-peer")
var got = 0
func wire(_ s: ShareSession) {
    s.onReady = { s in
        print("CODE \(s.pairCode)"); print("PEER \(s.peer?.name ?? "?")")
        s.send(.move(dx: 7, dy: -9)); s.send(.key(usage: 0xE3, down: true)); s.send(.clipboard("ঢাকা ✓ swift"))
    }
    s.onMessage = { s, m in
        if m == .ping || m == .pong { return }
        print("GOT \(m)"); got += 1
        if got == 3 { q.asyncAfter(deadline: .now() + 0.3) { exit(0) } }
    }
    s.onClose = { _, why in print("CLOSED \(why)"); if got < 3 { exit(3) } }
    s.start()
}
let listener = ShareListener()
if args.count >= 3, args[1] == "listen" {
    listener.onSession = { wire($0) }
    listener.onError = { print("ERROR \($0)"); exit(4) }
    listener.start(identity: me, queue: q, port: UInt16(args[2])!)
    print("LISTENING")
} else if args.count >= 4, args[1] == "dial" {
    wire(ShareSession.dial(host: args[2], port: UInt16(args[3])!, identity: me, queue: q))
} else { print("usage"); exit(1) }
q.asyncAfter(deadline: .now() + 15) { print("TIMEOUT"); exit(2) }
dispatchMain()
