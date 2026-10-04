import Foundation
import CoreGraphics

/// Turns this Mac's output to the shared monitor off while that monitor shows the other computer, so the Mac
/// behaves as a single-screen MacBook (windows move to the MacBook screen; nothing hides on an invisible display).
///
/// macOS has no public call for this; the window server's SLSConfigureDisplayEnabled (SkyLight) is the
/// mechanism other display utilities use. It is looked up at run time — if it is ever missing, the feature
/// simply does nothing. Changes are committed with `.forAppOnly`, but macOS does NOT reliably restore a display
/// turned off this way when the app ends (seen on macOS 27), so the ID is also saved: it is turned back on when
/// BHDisplay quits, and on the next launch if BHDisplay ended without doing so.
enum DisplayPower {
    private typealias EnableFn = @convention(c) (CGDisplayConfigRef?, CGDirectDisplayID, Bool) -> CGError
    private static let enableFn: EnableFn? = {
        guard let h = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW),
              let f = dlsym(h, "SLSConfigureDisplayEnabled") else { return nil }
        return unsafeBitCast(f, to: EnableFn.self)
    }()

    static var available: Bool { enableFn != nil }
    /// The display we turned off (nil = none). Lives only as long as this process, like the change itself.
    private(set) static var disabledID: CGDirectDisplayID? {
        didSet {
            if let id = disabledID { UserDefaults.standard.set(Int(id), forKey: savedKey) }
            else { UserDefaults.standard.removeObject(forKey: savedKey) }
        }
    }
    private static let savedKey = "displayTurnedOff"

    /// A display we turned off in an earlier run that was never turned back on: turn it on now.
    /// The monitor we once turned off is connected but inactive (e.g. replugged while off): turn it on.
    /// Only that model, never another display. True when something was turned on.
    @discardableResult
    static func reenableRemembered() -> Bool {
        guard let fn = enableFn, disabledID == nil,
              let vendor = UserDefaults.standard.object(forKey: "displayTurnedOffVendor") as? Int,
              let model = UserDefaults.standard.object(forKey: "displayTurnedOffModel") as? Int,
              let h = dlopen("/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight", RTLD_NOW),
              let f = dlsym(h, "SLSGetDisplayList") else { return false }
        typealias ListFn = @convention(c) (UInt32, UnsafeMutablePointer<CGDirectDisplayID>, UnsafeMutablePointer<UInt32>) -> CGError
        var ids = [CGDirectDisplayID](repeating: 0, count: 16); var n: UInt32 = 0
        guard unsafeBitCast(f, to: ListFn.self)(16, &ids, &n) == .success else { return false }
        var turnedOn = false
        for id in ids.prefix(Int(n)) where CGDisplayIsBuiltin(id) == 0 && !isActive(id)
            && Int(CGDisplayVendorNumber(id)) == vendor && Int(CGDisplayModelNumber(id)) == model {
            if apply({ fn($0, id, true) }) { turnedOn = true }
        }
        return turnedOn
    }

    static func restoreLeftover() {
        guard let fn = enableFn, disabledID == nil, let saved = UserDefaults.standard.object(forKey: savedKey) as? Int else { return }
        guard let id = CGDirectDisplayID(exactly: saved) else { UserDefaults.standard.removeObject(forKey: savedKey); return }
        if !isActive(id) { _ = apply({ fn($0, id, true) }) }
        UserDefaults.standard.removeObject(forKey: savedKey)
    }

    static func isActive(_ id: CGDirectDisplayID) -> Bool {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16); var n: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &n) == .success else { return false }
        return ids.prefix(Int(n)).contains(id)
    }

    /// Never turns off the last active display or a built-in one.
    @discardableResult
    static func turnOff(_ id: CGDirectDisplayID) -> Bool {
        guard let fn = enableFn, disabledID == nil, CGDisplayIsBuiltin(id) == 0, isActive(id) else { return false }
        var ids = [CGDirectDisplayID](repeating: 0, count: 16); var n: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &n) == .success, n >= 2 else { return false }
        guard apply({ fn($0, id, false) }) else { return false }
        disabledID = id
        // Remember WHICH monitor we turn off (EDID vendor/model): if it is unplugged and replugged while off, macOS keeps it
        // off under a new display ID — reenableRemembered() finds it again by model.
        UserDefaults.standard.set(Int(CGDisplayVendorNumber(id)), forKey: "displayTurnedOffVendor")
        UserDefaults.standard.set(Int(CGDisplayModelNumber(id)), forKey: "displayTurnedOffModel")
        return true
    }

    @discardableResult
    static func turnOn() -> Bool {
        guard let fn = enableFn, let id = disabledID else { return true }
        guard apply({ fn($0, id, true) }) else { return false }
        disabledID = nil
        return true
    }

    private static func apply(_ body: (CGDisplayConfigRef?) -> CGError) -> Bool {
        var cfg: CGDisplayConfigRef?
        guard CGBeginDisplayConfiguration(&cfg) == .success else { return false }
        guard body(cfg) == .success else { CGCancelDisplayConfiguration(cfg); return false }
        return CGCompleteDisplayConfiguration(cfg, .forAppOnly) == .success
    }
}
