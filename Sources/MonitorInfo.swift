import Foundation
import IOKit
import CoreGraphics
import IOKit.graphics

/// Facts macOS already knows about the external monitor (EDID + link), no DDC needed.
struct MonitorInfo {
    var name = "External Monitor"
    var serial = "—"
    var manufactured = "—"
    var link = "—"               // physical link into the monitor: "HDMI" / "DP"
    var linkIsHDMI: Bool?        // nil = unknown
    var resolution = "—"
    var firmware = "—"
    var mccs = "—"
    var matchedDDC = false       // true when this info belongs to the display DDC talks to
    var externalCount = 1
    var displayID: CGDirectDisplayID?   // the CoreGraphics display that IS this monitor (for keyboard & mouse sharing)

    /// `identity` comes from the DDC channel's own EDID read; only the framebuffer with the same
    /// manufacturer/product is described, so the window never shows one monitor while driving another.
    static func probe(identity: MonitorIdentity?) -> MonitorInfo {
        var info = MonitorInfo()
        var it = io_iterator_t()
        if IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOMobileFramebufferShim"), &it) == KERN_SUCCESS {
            defer { IOObjectRelease(it) }
            while case let s = IOIteratorNext(it), s != 0 {
                defer { IOObjectRelease(s) }
                // The built-in panel's ProductAttributes carries no ProductName — that is how it is skipped.
                guard let attrs = IORegistryEntryCreateCFProperty(s, "DisplayAttributes" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? [String: Any],
                      let prod = attrs["ProductAttributes"] as? [String: Any],
                      let name = prod["ProductName"] as? String else { continue }
                if let id = identity {
                    guard (prod["LegacyManufacturerID"] as? Int) == Int(id.manufacturer),
                          (prod["ProductID"] as? Int) == Int(id.product) else { continue }
                    info.matchedDDC = true
                }
                info.name = name
                if let sn = prod["AlphanumericSerialNumber"] as? String { info.serial = sn }
                if let y = prod["YearOfManufacture"] as? Int, let w = prod["WeekOfManufacture"] as? Int { info.manufactured = "Week \(w), \(y)" }
                if let t = IORegistryEntryCreateCFProperty(s, "Transport" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? [String: Any],
                   let down = t["Downstream"] as? String {
                    info.link = down
                    info.linkIsHDMI = down.uppercased() == "HDMI"
                }
                break
            }
        }
        info.displayID = displayID(identity: identity)
        info.resolution = info.displayID.flatMap(nativeMode) ?? "—"
        return info
    }

    /// The CG display matching the DDC identity (CGDisplayVendorNumber/ModelNumber carry the same EDID values).
    static func displayID(identity: MonitorIdentity?) -> CGDirectDisplayID? {
        var ids = [CGDirectDisplayID](repeating: 0, count: 8)
        var n: UInt32 = 0
        guard CGGetOnlineDisplayList(8, &ids, &n) == .success else { return nil }
        return ids.prefix(Int(n)).first(where: { id in
            CGDisplayIsBuiltin(id) == 0 && (identity.map {
                CGDisplayVendorNumber(id) == UInt32($0.manufacturer) && CGDisplayModelNumber(id) == UInt32($0.product)
            } ?? true)
        })
    }

    /// The panel's native mode (EDID preferred timing) at its highest refresh rate.
    private static func nativeMode(_ ext: CGDirectDisplayID) -> String? {
        let opts = [kCGDisplayShowDuplicateLowResolutionModes: kCFBooleanTrue] as CFDictionary
        guard let modes = CGDisplayCopyAllDisplayModes(ext, opts) as? [CGDisplayMode], !modes.isEmpty else { return nil }
        let native = modes.filter { $0.ioFlags & UInt32(kDisplayModeNativeFlag) != 0 }
        let pool = native.isEmpty ? modes : modes.filter { m in native.contains { $0.pixelWidth == m.pixelWidth && $0.pixelHeight == m.pixelHeight } }
        let best = pool.max { a, b in
            (a.pixelWidth * a.pixelHeight, a.refreshRate) < (b.pixelWidth * b.pixelHeight, b.refreshRate)
        }!
        return "\(best.pixelWidth) × \(best.pixelHeight) at \(Int(best.refreshRate.rounded()))Hz"
    }
}
