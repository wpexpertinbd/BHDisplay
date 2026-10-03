import Foundation
import CryptoKit

// Keyboard & mouse sharing — wire format, identity, handshake and record encryption.
// The protocol is specified in docs/SHARING-PROTOCOL.md; this file and the Windows app's
// ShareCore.cs implement the same spec and must stay byte-for-byte compatible.

enum BHDS {
    static let tcpPort: UInt16 = 24860
    static let udpPort: UInt16 = 24861
    static let maxFrame = 1_048_576
    static let maxClipboard = 256 * 1024
    static let version: UInt8 = 2

    /// A peer-supplied name for logs and dialogs: no control or invisible formatting characters (newlines,
    /// right-to-left overrides) that could forge log lines or disguise the name.
    static func cleanName(_ s: String) -> String {
        let kept = s.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) && $0.properties.generalCategory != .format }
        let t = String(String.UnicodeScalarView(kept)).trimmingCharacters(in: .whitespaces)
        return t.isEmpty ? "unnamed computer" : t
    }
}

enum WireError: Error, CustomStringConvertible {
    case short, bad(String)
    var description: String {
        switch self { case .short: return "truncated message"; case .bad(let s): return s }
    }
}

struct WireWriter {
    private(set) var data = Data()
    mutating func u8(_ v: UInt8) { data.append(v) }
    mutating func u16(_ v: UInt16) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func u32(_ v: UInt32) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func u64(_ v: UInt64) { withUnsafeBytes(of: v.bigEndian) { data.append(contentsOf: $0) } }
    mutating func i16(_ v: Int16) { u16(UInt16(bitPattern: v)) }
    mutating func f32(_ v: Float) { u32(v.bitPattern) }
    mutating func bytes(_ d: Data) { data.append(d) }
}

struct WireReader {
    let data: Data
    private var off: Int
    init(_ d: Data) { data = Data(d); off = 0 }      // re-base so indices start at 0
    var remaining: Int { data.count - off }
    mutating func bytes(_ n: Int) throws -> Data {
        guard n >= 0, remaining >= n else { throw WireError.short }
        defer { off += n }
        return data.subdata(in: off..<(off + n))
    }
    mutating func u8() throws -> UInt8 { try bytes(1)[0] }
    mutating func u16() throws -> UInt16 { try bytes(2).reduce(0) { $0 << 8 | UInt16($1) } }
    mutating func u32() throws -> UInt32 { try bytes(4).reduce(0) { $0 << 8 | UInt32($1) } }
    mutating func i16() throws -> Int16 { Int16(bitPattern: try u16()) }
    mutating func f32() throws -> Float { Float(bitPattern: try u32()) }
    mutating func rest() -> Data { (try? bytes(remaining)) ?? Data() }
}

// MARK: - Identity

/// Secrets kept in the login Keychain, readable without a prompt only by this app (its code signature):
/// other programs running as the same user can't copy the identity key or add themselves to the paired list.
enum ShareKeychain {
    private static let service = "com.biswashost.bhdisplay.sharing"

    static func read(_ account: String) -> Data? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess ? out as? Data : nil
    }

    @discardableResult
    static func write(_ account: String, _ data: Data) -> Bool {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        if SecItemUpdate(q as CFDictionary, [kSecValueData as String: data] as CFDictionary) == errSecSuccess { return true }
        var add = q
        add[kSecValueData as String] = data
        add[kSecAttrLabel as String] = "BHDisplay keyboard & mouse sharing"
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
    }
}

/// Long-term device identity: a random device id and a P-256 signing key, created once and kept in the
/// Keychain. (Earlier builds kept it in a 0600 file; it is moved into the Keychain and the file deleted.)
final class ShareIdentity: @unchecked Sendable {   // immutable after init
    let deviceID: Data
    let signingKey: P256.Signing.PrivateKey
    let name: String
    var publicKey: Data { signingKey.publicKey.x963Representation }
    var fingerprint: Data { Data(SHA256.hash(data: publicKey)) }

    private init(deviceID: Data, key: P256.Signing.PrivateKey, name: String) {
        self.deviceID = deviceID; self.signingKey = key; self.name = name
    }

    #if BHDS_TESTS
    static func testing(deviceID: Data, key: P256.Signing.PrivateKey, name: String) -> ShareIdentity {
        ShareIdentity(deviceID: deviceID, key: key, name: name)
    }
    #endif

    static var legacyFileURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("BHDisplay", isDirectory: true).appendingPathComponent("identity")
    }

    private static func parse(_ d: Data, name: String) -> ShareIdentity? {
        guard d.count == 48, let key = try? P256.Signing.PrivateKey(rawRepresentation: d.subdata(in: 16..<48)) else { return nil }
        return ShareIdentity(deviceID: d.subdata(in: 0..<16), key: key, name: name)
    }

    static func loadOrCreate() throws -> ShareIdentity {
        let name = String((Host.current().localizedName ?? "Mac").prefix(64))
        if let d = ShareKeychain.read("identity"), let id = parse(d, name: name) { return id }
        // Move an identity from the old file into the Keychain (keeps existing pairings working).
        let file = legacyFileURL
        if let d = try? Data(contentsOf: file), let id = parse(d, name: name) {
            guard ShareKeychain.write("identity", d) else { throw WireError.bad("cannot save the sharing key in the Keychain") }
            try? FileManager.default.removeItem(at: file)
            return id
        }
        var id = Data(count: 16)
        guard id.withUnsafeMutableBytes({ SecRandomCopyBytes(kSecRandomDefault, 16, $0.baseAddress!) }) == errSecSuccess
        else { throw WireError.bad("no randomness") }
        let key = P256.Signing.PrivateKey()
        guard ShareKeychain.write("identity", id + key.rawRepresentation) else { throw WireError.bad("cannot save the sharing key in the Keychain") }
        return ShareIdentity(deviceID: id, key: key, name: name)
    }
}

extension Data {
    var hex: String { map { String(format: "%02x", $0) }.joined() }
    static func random(_ n: Int) -> Data {
        var d = Data(count: n)
        _ = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, n, $0.baseAddress!) }
        return d
    }
}

// MARK: - Handshake

struct Hello {
    let deviceID: Data      // 16
    let identityKey: Data   // 65, X9.63
    let ephemeralKey: Data  // 65, X9.63
    let nonce: Data         // 32
    let name: String

    var encoded: Data {
        var w = WireWriter()
        w.bytes(Data("BHDS".utf8)); w.u8(BHDS.version)
        w.bytes(deviceID); w.bytes(identityKey); w.bytes(ephemeralKey); w.bytes(nonce)
        let n = Data(name.utf8).prefix(64)
        w.u8(UInt8(n.count)); w.bytes(n)
        return w.data
    }

    static func decode(_ d: Data) throws -> Hello {
        var r = WireReader(d)
        guard try r.bytes(4) == Data("BHDS".utf8) else { throw WireError.bad("not a BHDisplay peer") }
        guard try r.u8() == BHDS.version else { throw WireError.bad("unsupported protocol version") }
        let id = try r.bytes(16), ik = try r.bytes(65), ek = try r.bytes(65), nonce = try r.bytes(32)
        let n = Int(try r.u8())
        guard n <= 64 else { throw WireError.bad("name too long") }
        let name = BHDS.cleanName(String(decoding: try r.bytes(n), as: UTF8.self))
        guard r.remaining == 0 else { throw WireError.bad("trailing bytes in HELLO") }
        // Validate both keys are real P-256 points before anything uses them.
        _ = try P256.Signing.PublicKey(x963Representation: ik)
        _ = try P256.KeyAgreement.PublicKey(x963Representation: ek)
        return Hello(deviceID: id, identityKey: ik, ephemeralKey: ek, nonce: nonce, name: name)
    }
}

struct HandshakeKeys {
    let send: SymmetricKey
    let receive: SymmetricKey
    let pairCode: String
}

enum Handshake {
    static func transcript(dialerHello: Data, listenerHello: Data) -> Data {
        Data(SHA256.hash(data: Data("BHDS-v2".utf8) + dialerHello + listenerHello))
    }

    /// The listener's commitment to its HELLO, sent before it sees the dialer's: neither side can then choose
    /// its HELLO to steer the pairing code (an attacker in the middle could otherwise make both codes match).
    static func commitment(listenerHello: Data) -> Data {
        Data(SHA256.hash(data: Data("BHDS-v2 commit".utf8) + listenerHello))
    }

    static func authMessage(role: UInt8, transcript t: Data) -> Data { Data("BHDS-auth".utf8) + [role] + t }

    static func sign(_ id: ShareIdentity, role: UInt8, transcript t: Data) throws -> Data {
        try id.signingKey.signature(for: authMessage(role: role, transcript: t)).rawRepresentation
    }

    static func verify(signature: Data, identityKey: Data, role: UInt8, transcript t: Data) -> Bool {
        guard signature.count == 64,
              let key = try? P256.Signing.PublicKey(x963Representation: identityKey),
              let sig = try? P256.Signing.ECDSASignature(rawRepresentation: signature) else { return false }
        return key.isValidSignature(sig, for: authMessage(role: role, transcript: t))
    }

    static func deriveKeys(ephemeral: P256.KeyAgreement.PrivateKey, peerEphemeral: Data,
                           transcript t: Data, isDialer: Bool) throws -> HandshakeKeys {
        let peer = try P256.KeyAgreement.PublicKey(x963Representation: peerEphemeral)
        let z = try ephemeral.sharedSecretFromKeyAgreement(with: peer)
        let ikm = z.withUnsafeBytes { SymmetricKey(data: Data($0)) }
        let k = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, salt: t, info: Data("BHDS-v2 keys".utf8), outputByteCount: 64)
            .withUnsafeBytes { Data($0) }
        let d2l = SymmetricKey(data: k.subdata(in: 0..<32)), l2d = SymmetricKey(data: k.subdata(in: 32..<64))
        let p = HKDF<SHA256>.deriveKey(inputKeyMaterial: ikm, salt: t, info: Data("BHDS-v2 pair".utf8), outputByteCount: 4)
            .withUnsafeBytes { Data($0) }
        let code = p.reduce(UInt32(0)) { $0 << 8 | UInt32($1) } % 1_000_000
        return HandshakeKeys(send: isDialer ? d2l : l2d, receive: isDialer ? l2d : d2l,
                             pairCode: String(format: "%06u", code))
    }
}

/// AES-256-GCM per direction; nonce = 4 zero bytes || 64-bit sequence number.
struct RecordCipher {
    let key: SymmetricKey
    private var seq: UInt64 = 0
    init(key: SymmetricKey) { self.key = key }

    private mutating func nextNonce() throws -> AES.GCM.Nonce {
        var n = Data(count: 4)
        withUnsafeBytes(of: seq.bigEndian) { n.append(contentsOf: $0) }
        seq &+= 1
        return try AES.GCM.Nonce(data: n)
    }
    mutating func seal(_ plain: Data) throws -> Data {
        let box = try AES.GCM.seal(plain, using: key, nonce: try nextNonce())
        return Data(box.ciphertext) + Data(box.tag)    // re-based: the box's parts are slices of one buffer
    }
    mutating func open(_ body: Data) throws -> Data {
        guard body.count >= 16 else { throw WireError.short }
        let b = Data(body)
        let box = try AES.GCM.SealedBox(nonce: try nextNonce(), ciphertext: b.prefix(b.count - 16), tag: b.suffix(16))
        return try AES.GCM.open(box, using: key)
    }
}

// MARK: - Messages

enum ShareMsg: Equatable {
    case ping, pong, pairConfirm, pairReject
    case enter(edge: UInt8, position: Float)
    case leave(edge: UInt8, position: Float)
    case move(dx: Int16, dy: Int16)
    case button(UInt8, down: Bool)
    case scroll(dx: Int16, dy: Int16)
    case key(usage: UInt16, down: Bool)
    case releaseAll
    case clipboard(String)
    case monitorPorts(mac: UInt8, other: UInt8)
    case monitorShows(UInt8)
    case switchRequest(UInt8)
    case unknown(UInt8)

    /// Messages that act on the receiving computer — only honoured from a paired peer.
    var carriesInput: Bool {
        switch self {
        case .enter, .leave, .move, .button, .scroll, .key, .clipboard: return true
        default: return false
        }
    }

    var encoded: Data {
        var w = WireWriter()
        switch self {
        case .ping: w.u8(0x01)
        case .pong: w.u8(0x02)
        case .pairConfirm: w.u8(0x05)
        case .pairReject: w.u8(0x06)
        case .enter(let e, let p): w.u8(0x10); w.u8(e); w.f32(p)
        case .leave(let e, let p): w.u8(0x11); w.u8(e); w.f32(p)
        case .move(let x, let y): w.u8(0x20); w.i16(x); w.i16(y)
        case .button(let b, let d): w.u8(0x21); w.u8(b); w.u8(d ? 1 : 0)
        case .scroll(let x, let y): w.u8(0x22); w.i16(x); w.i16(y)
        case .key(let u, let d): w.u8(0x30); w.u16(u); w.u8(d ? 1 : 0)
        case .releaseAll: w.u8(0x31)
        case .clipboard(let s): w.u8(0x40); w.bytes(Data(s.utf8).prefix(BHDS.maxClipboard))
        case .monitorPorts(let m, let o): w.u8(0x50); w.u8(m); w.u8(o)
        case .monitorShows(let c): w.u8(0x51); w.u8(c)
        case .switchRequest(let c): w.u8(0x52); w.u8(c)
        case .unknown(let t): w.u8(t)
        }
        return w.data
    }

    static func decode(_ d: Data) throws -> ShareMsg {
        var r = WireReader(d)
        let t = try r.u8()
        switch t {
        case 0x01: return .ping
        case 0x02: return .pong
        case 0x05: return .pairConfirm
        case 0x06: return .pairReject
        case 0x10, 0x11:
            let e = try r.u8(), p = try r.f32()
            guard e <= 4, p.isFinite else { throw WireError.bad("bad edge") }   // 4 = take over, no edge
            let pos = min(max(p, 0), 1)
            return t == 0x10 ? .enter(edge: e, position: pos) : .leave(edge: e, position: pos)
        case 0x20: return .move(dx: try r.i16(), dy: try r.i16())
        case 0x21:
            let b = try r.u8(), down = try r.u8() != 0
            guard (1...5).contains(b) else { throw WireError.bad("bad button") }
            return .button(b, down: down)
        case 0x22: return .scroll(dx: try r.i16(), dy: try r.i16())
        case 0x30: return .key(usage: try r.u16(), down: try r.u8() != 0)
        case 0x31: return .releaseAll
        case 0x40:
            let raw = r.rest()
            guard raw.count <= BHDS.maxClipboard else { throw WireError.bad("clipboard too large") }
            return .clipboard(String(decoding: raw, as: UTF8.self))
        case 0x50: return .monitorPorts(mac: try r.u8(), other: try r.u8())
        case 0x51: return .monitorShows(try r.u8())
        case 0x52: return .switchRequest(try r.u8())
        default: return .unknown(t)
        }
    }
}
