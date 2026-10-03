import Foundation
import CryptoKit

// Run: swiftc -D BHDS_TESTS ../../Sources/ShareCore.swift *.swift -o /tmp/sct && /tmp/sct
var failures = 0, passes = 0
func check(_ ok: Bool, _ name: String) { if ok { passes += 1 } else { failures += 1; print("FAIL:", name) } }

func device(_ name: String) -> (Data, P256.Signing.PrivateKey) { (Data.random(16), P256.Signing.PrivateKey()) }

struct Side {
    let id: Data; let sk: P256.Signing.PrivateKey; let eph = P256.KeyAgreement.PrivateKey(); let name: String
    var hello: Hello { Hello(deviceID: id, identityKey: sk.publicKey.x963Representation,
                             ephemeralKey: eph.publicKey.x963Representation, nonce: Data.random(32), name: name) }
}

// 0. Self-check: a known-good roundtrip of the wire helpers, so a broken rig can't pass.
var w = WireWriter(); w.u16(0xBEEF); w.i16(-2); w.f32(0.25); w.u32(7)
var r = WireReader(w.data)
check((try? r.u16()) == 0xBEEF && (try? r.i16()) == -2 && (try? r.f32()) == 0.25 && (try? r.u32()) == 7, "wire roundtrip")

// 1. Honest handshake: both sides derive identical keys and the same pairing code.
let (aid, ask) = device("A"), (bid, bsk) = device("B")
let A = Side(id: aid, sk: ask, name: "MacBook"), B = Side(id: bid, sk: bsk, name: "BH-H-W11")
let hA = A.hello.encoded, hB = B.hello.encoded
check((try? Hello.decode(hA))?.name == "MacBook", "hello decode")
let T = Handshake.transcript(dialerHello: hA, listenerHello: hB)
let kA = try! Handshake.deriveKeys(ephemeral: A.eph, peerEphemeral: B.eph.publicKey.x963Representation, transcript: T, isDialer: true)
let kB = try! Handshake.deriveKeys(ephemeral: B.eph, peerEphemeral: A.eph.publicKey.x963Representation, transcript: T, isDialer: false)
check(kA.pairCode == kB.pairCode && kA.pairCode.count == 6, "same 6-digit pair code")
var sendA = RecordCipher(key: kA.send), recvB = RecordCipher(key: kB.receive)
let msgs: [ShareMsg] = [.move(dx: -5, dy: 300), .key(usage: 0x04, down: true), .clipboard("héllo ✓"), .enter(edge: 0, position: 0.5)]
check(msgs.allSatisfy { m in (try? ShareMsg.decode(recvB.open(sendA.seal(m.encoded)))) == m }, "encrypted message roundtrip")

// 2. Authentication: a valid signature verifies; wrong role / wrong transcript / wrong key do not.
let sigA = try! Handshake.sign(ShareIdentityShim.make(A), role: 0, transcript: T)
check(Handshake.verify(signature: sigA, identityKey: A.sk.publicKey.x963Representation, role: 0, transcript: T), "valid auth verifies")
check(!Handshake.verify(signature: sigA, identityKey: A.sk.publicKey.x963Representation, role: 1, transcript: T), "role mismatch rejected")
check(!Handshake.verify(signature: sigA, identityKey: A.sk.publicKey.x963Representation, role: 0, transcript: Data(T.reversed())), "other transcript rejected")
check(!Handshake.verify(signature: sigA, identityKey: B.sk.publicKey.x963Representation, role: 0, transcript: T), "other identity rejected")

// 3. Man in the middle: M talks to A as "B" and to B as "A" with its own ephemerals → the codes differ.
let M1 = Side(id: bid, sk: P256.Signing.PrivateKey(), name: "BH-H-W11"), M2 = Side(id: aid, sk: P256.Signing.PrivateKey(), name: "MacBook")
let tA = Handshake.transcript(dialerHello: hA, listenerHello: M1.hello.encoded)
let tB = Handshake.transcript(dialerHello: M2.hello.encoded, listenerHello: hB)
let codeA = try! Handshake.deriveKeys(ephemeral: A.eph, peerEphemeral: M1.eph.publicKey.x963Representation, transcript: tA, isDialer: true).pairCode
let codeB = try! Handshake.deriveKeys(ephemeral: B.eph, peerEphemeral: M2.eph.publicKey.x963Representation, transcript: tB, isDialer: false).pairCode
check(codeA != codeB || codeA == "skip", "MITM produces different codes (1e-6 false-pass chance)")

// 4. Tampering / replay / reordering all fail to decrypt.
var s2 = RecordCipher(key: kA.send), r2 = RecordCipher(key: kB.receive)
var frame = try! s2.seal(ShareMsg.key(usage: 0x04, down: true).encoded); frame[0] ^= 1
check((try? r2.open(frame)) == nil, "tampered frame rejected")
var s3 = RecordCipher(key: kA.send), r3 = RecordCipher(key: kB.receive)
let f1 = try! s3.seal(Data([1])), f2 = try! s3.seal(Data([2]))
check((try? r3.open(f2)) == nil, "reordered frame rejected")
var r4 = RecordCipher(key: kB.receive); _ = try? r4.open(f1)
check((try? r4.open(f1)) == nil, "replayed frame rejected")
var wrongDir = RecordCipher(key: kB.send)
var sA = RecordCipher(key: kA.send); let f9 = try! sA.seal(Data([9]))
check((try? wrongDir.open(f9)) == nil, "direction keys differ")

// 5. Hostile input: bad edge/button, oversized name, invalid curve point.
check((try? ShareMsg.decode(Data([0x10, 9, 0, 0, 0, 0]))) == nil, "edge > 3 rejected")
check((try? ShareMsg.decode(Data([0x21, 9, 1]))) == nil, "button 9 rejected")
var badHello = WireWriter(); badHello.bytes(Data("BHDS".utf8)); badHello.u8(1); badHello.bytes(aid)
badHello.bytes(Data(repeating: 4, count: 65)); badHello.bytes(Data(repeating: 4, count: 65)); badHello.bytes(Data(count: 32)); badHello.u8(0)
check((try? Hello.decode(badHello.data)) == nil, "invalid P-256 point rejected")
check(ShareMsg.move(dx: 1, dy: 1).carriesInput && !ShareMsg.ping.carriesInput && !ShareMsg.pairConfirm.carriesInput, "input classification")
if case .enter(_, let p) = try! ShareMsg.decode(Data([0x10, 1]) + withUnsafeBytes(of: Float(7).bitPattern.bigEndian) { Data($0) }) { check(p == 1, "position clamped") }

print("\(passes) passed, \(failures) failed")
exit(failures == 0 ? 0 : 1)
