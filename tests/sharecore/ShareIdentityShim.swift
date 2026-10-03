import CryptoKit
import Foundation
// Test-only: build a ShareIdentity-like signer for a test Side (the real init is private + file-backed).
enum ShareIdentityShim {
    static func make(_ s: Side) -> ShareIdentity { ShareIdentity.testing(deviceID: s.id, key: s.sk, name: s.name) }
}
