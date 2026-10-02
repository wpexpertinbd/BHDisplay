import Foundation
import IOKit

// Apple Silicon DDC/CI over the DCP AV service. The private symbols are declared in Bridge.h.

enum DDCError: Error, CustomStringConvertible {
    case noExternalDisplay, writeFailed(IOReturn), nullReply, badReply
    var description: String {
        switch self {
        case .noExternalDisplay: return "No external display found"
        case .writeFailed(let r): return String(format: "I2C write failed (0x%08x)", r)
        case .nullReply: return "Monitor refused DDC (turn ON Setup Menu → DDC/CI)"
        case .badReply: return "Unreadable reply from monitor"
        }
    }
}

final class DDC {
    private static let chip: UInt32 = 0x37      // DDC/CI 7-bit address
    private static let dataAddr: UInt32 = 0x51  // host source address / sub-address
    private let av: IOAVService
    private let lock = NSLock()

    /// EDID manufacturer + product code read through this same DDC channel, so the monitor the
    /// UI describes is provably the one the commands go to.
    let identity: MonitorIdentity?

    init(av: IOAVService) {
        self.av = av
        self.identity = Self.readIdentity(av)
    }

    /// Number of external displays on the DCP — the UI warns when it is not exactly one.
    static func externalCount() -> Int { externalServices().count }

    /// First external display on the DCP.
    static func firstExternal() -> DDC? {
        externalServices().first.map { DDC(av: $0) }
    }

    private static func externalServices() -> [IOAVService] {
        var out: [IOAVService] = []
        var it = io_iterator_t()
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("DCPAVServiceProxy"), &it) == KERN_SUCCESS else { return out }
        defer { IOObjectRelease(it) }
        while case let s = IOIteratorNext(it), s != 0 {
            defer { IOObjectRelease(s) }
            let loc = IORegistryEntryCreateCFProperty(s, "Location" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? String
            if loc == "External", let av = IOAVServiceCreateWithService(kCFAllocatorDefault, s) { out.append(av) }
        }
        return out
    }

    /// Re-reads the EDID now — the cached `identity` can be stale after a monitor is swapped on the same port.
    func currentIdentity() -> MonitorIdentity? {
        lock.lock(); defer { lock.unlock() }
        return Self.readIdentity(av)
    }

    private static func readIdentity(_ av: IOAVService) -> MonitorIdentity? {
        var zero: UInt8 = 0
        _ = IOAVServiceWriteI2C(av, 0x50, 0x00, &zero, 0)
        var e = [UInt8](repeating: 0, count: 16)
        guard e.withUnsafeMutableBytes({ IOAVServiceReadI2C(av, 0x50, 0x00, $0.baseAddress!, 16) }) == kIOReturnSuccess,
              e[0...7] == [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00] else { return nil }
        return MonitorIdentity(manufacturer: UInt16(e[8]) << 8 | UInt16(e[9]), product: UInt16(e[11]) << 8 | UInt16(e[10]))
    }

    private func send(_ body: [UInt8]) throws {
        var pkt = [UInt8(0x80 | body.count)] + body
        var chk: UInt8 = 0x6E ^ UInt8(Self.dataAddr)
        for b in pkt { chk ^= b }
        pkt.append(chk)
        let rc = pkt.withUnsafeMutableBytes { IOAVServiceWriteI2C(av, Self.chip, Self.dataAddr, $0.baseAddress!, UInt32($0.count)) }
        if rc != kIOReturnSuccess { throw DDCError.writeFailed(rc) }
    }

    /// Set VCP feature. Input switches go out twice — a monitor can drop the first command after idle.
    func write(_ code: UInt8, _ value: UInt16, repeats: Int = 1) throws {
        lock.lock(); defer { lock.unlock() }
        for i in 0..<max(1, repeats) {
            if i > 0 { usleep(50_000) }
            try send([0x03, code, UInt8(value >> 8), UInt8(value & 0xFF)])
        }
        usleep(50_000) // MCCS minimum gap before the next command
    }

    /// Get VCP feature → (current, max).
    func read(_ code: UInt8) throws -> (current: UInt16, max: UInt16) {
        lock.lock(); defer { lock.unlock() }
        var lastErr: DDCError = .badReply
        for _ in 0..<4 {
            try send([0x01, code])
            usleep(50_000)
            var r = [UInt8](repeating: 0, count: 12)
            let rc = r.withUnsafeMutableBytes { IOAVServiceReadI2C(av, Self.chip, Self.dataAddr, $0.baseAddress!, 12) }
            if rc == kIOReturnSuccess {
                // Reply: 6E 88 02 <result> <code> <type> <maxH> <maxL> <curH> <curL> <chk>; chk = 0x50 ^ bytes 0…9
                var chk: UInt8 = 0x50
                for b in r[0..<10] { chk ^= b }
                if r[0] == 0x6E && r[1] == 0x80 { lastErr = .nullReply }
                else if r[0] == 0x6E && r[1] == 0x88 && r[2] == 0x02 && r[3] == 0x00 && r[4] == code && chk == r[10] {
                    return (UInt16(r[8]) << 8 | UInt16(r[9]), UInt16(r[6]) << 8 | UInt16(r[7]))
                } else { lastErr = .badReply }
            }
            usleep(40_000)
        }
        throw lastErr
    }
}

/// VCP 0x60 values. The XG2409A advertises 60( 0F 11 12) but does NOT follow the MCCS names:
/// its OSD shows "HDMI 1" while DDC reports 0x12 (checked on the monitor 2026-10-03), so 0x11 is HDMI 2.
struct MonitorInput: Identifiable, Hashable {
    let id: UInt16
    let name: String
    static let all: [MonitorInput] = [
        .init(id: 0x0F, name: "DisplayPort"),
        .init(id: 0x12, name: "HDMI 1"),
        .init(id: 0x11, name: "HDMI 2"),
    ]
    static func isValid(_ code: UInt16) -> Bool { all.contains { $0.id == code } }
    static func name(for code: UInt16) -> String {
        all.first { $0.id == code & 0xFF }?.name ?? String(format: "Input 0x%02X", code)
    }
}

struct MonitorIdentity: Equatable {
    let manufacturer: UInt16   // EDID bytes 8–9 (big-endian PNP id) == IORegistry LegacyManufacturerID
    let product: UInt16        // EDID bytes 10–11 (little-endian)   == IORegistry ProductID
}
