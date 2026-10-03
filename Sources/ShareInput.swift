import AppKit
import CoreGraphics
import ApplicationServices

// Keyboard & mouse sharing — capturing local input (event tap) and replaying the peer's input (CGEvent posting).

/// Injected events carry this in kCGEventSourceUserData so our own tap ignores them (no feedback loops).
let kShareInjectedTag: Int64 = 0x4248_4453   // "BHDS"


// MARK: - Key map: macOS virtual key codes ↔ USB HID usage IDs (Keyboard/Keypad page 0x07)

enum ShareKeyMap {
    /// (mac kVK, HID usage). Source: Apple HIToolbox kVK_* constants and the USB HID Usage Tables.
    static let pairs: [(UInt16, UInt16)] = [
        (0x00, 0x04), (0x0B, 0x05), (0x08, 0x06), (0x02, 0x07), (0x0E, 0x08), (0x03, 0x09), (0x05, 0x0A), (0x04, 0x0B),
        (0x22, 0x0C), (0x26, 0x0D), (0x28, 0x0E), (0x25, 0x0F), (0x2E, 0x10), (0x2D, 0x11), (0x1F, 0x12), (0x23, 0x13),
        (0x0C, 0x14), (0x0F, 0x15), (0x01, 0x16), (0x11, 0x17), (0x20, 0x18), (0x09, 0x19), (0x0D, 0x1A), (0x07, 0x1B),
        (0x10, 0x1C), (0x06, 0x1D),
        (0x12, 0x1E), (0x13, 0x1F), (0x14, 0x20), (0x15, 0x21), (0x17, 0x22), (0x16, 0x23), (0x1A, 0x24), (0x1C, 0x25),
        (0x19, 0x26), (0x1D, 0x27),
        (0x24, 0x28), (0x35, 0x29), (0x33, 0x2A), (0x30, 0x2B), (0x31, 0x2C), (0x1B, 0x2D), (0x18, 0x2E), (0x21, 0x2F),
        (0x1E, 0x30), (0x2A, 0x31), (0x29, 0x33), (0x27, 0x34), (0x32, 0x35), (0x2B, 0x36), (0x2F, 0x37), (0x2C, 0x38),
        (0x39, 0x39),
        (0x7A, 0x3A), (0x78, 0x3B), (0x63, 0x3C), (0x76, 0x3D), (0x60, 0x3E), (0x61, 0x3F), (0x62, 0x40), (0x64, 0x41),
        (0x65, 0x42), (0x6D, 0x43), (0x67, 0x44), (0x6F, 0x45),
        (0x72, 0x49), (0x73, 0x4A), (0x74, 0x4B), (0x75, 0x4C), (0x77, 0x4D), (0x79, 0x4E),
        (0x7C, 0x4F), (0x7B, 0x50), (0x7D, 0x51), (0x7E, 0x52),
        (0x47, 0x53), (0x4B, 0x54), (0x43, 0x55), (0x4E, 0x56), (0x45, 0x57), (0x4C, 0x58),
        (0x53, 0x59), (0x54, 0x5A), (0x55, 0x5B), (0x56, 0x5C), (0x57, 0x5D), (0x58, 0x5E), (0x59, 0x5F), (0x5B, 0x60),
        (0x5C, 0x61), (0x52, 0x62), (0x41, 0x63), (0x0A, 0x64), (0x51, 0x67),
        (0x69, 0x68), (0x6B, 0x69), (0x71, 0x6A), (0x6A, 0x6B), (0x40, 0x6C), (0x4F, 0x6D), (0x50, 0x6E), (0x5A, 0x6F),
        (0x4A, 0x7F), (0x48, 0x80), (0x49, 0x81),
        (0x5E, 0x87), (0x5D, 0x89), (0x68, 0x90), (0x66, 0x91), (0x5F, 0x85),
        (0x3B, 0xE0), (0x38, 0xE1), (0x3A, 0xE2), (0x37, 0xE3), (0x3E, 0xE4), (0x3C, 0xE5), (0x3D, 0xE6), (0x36, 0xE7),
    ]
    static let toHID: [UInt16: UInt16] = Dictionary(pairs.map { ($0.0, $0.1) }, uniquingKeysWith: { a, _ in a })
    static let toMac: [UInt16: UInt16] = Dictionary(pairs.map { ($0.1, $0.0) }, uniquingKeysWith: { a, _ in a })

    /// HID usage → the CGEventFlags device-dependent bit for that modifier (left/right distinct).
    static let modifierBits: [UInt16: UInt64] = [
        0xE0: 0x0000_0001 | CGEventFlags.maskControl.rawValue, 0xE4: 0x0000_2000 | CGEventFlags.maskControl.rawValue,
        0xE1: 0x0000_0002 | CGEventFlags.maskShift.rawValue, 0xE5: 0x0000_0004 | CGEventFlags.maskShift.rawValue,
        0xE2: 0x0000_0020 | CGEventFlags.maskAlternate.rawValue, 0xE6: 0x0000_0040 | CGEventFlags.maskAlternate.rawValue,
        0xE3: 0x0000_0008 | CGEventFlags.maskCommand.rawValue, 0xE7: 0x0000_0010 | CGEventFlags.maskCommand.rawValue,
    ]
    /// ⌘ ↔ Ctrl swap (so ⌘C on the Mac keyboard is Ctrl+C on Windows, and Ctrl+C on Windows is ⌘C on the Mac).
    static func swapCmdCtrl(_ u: UInt16) -> UInt16 {
        switch u { case 0xE0: return 0xE3; case 0xE3: return 0xE0; case 0xE4: return 0xE7; case 0xE7: return 0xE4; default: return u }
    }
}

// MARK: - Screen geometry (global CG coordinates: origin top-left of the main display, y down)

enum ShareScreens {
    static func displays() -> [CGRect] {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16); var n: UInt32 = 0
        guard CGGetActiveDisplayList(16, &ids, &n) == .success else { return [] }
        return ids.prefix(Int(n)).map { CGDisplayBounds($0) }
    }

    /// Clamp a point onto the desktop (handles displays of different sizes / gaps between them).
    static func clamp(_ p: CGPoint, within rects: [CGRect]? = nil) -> CGPoint {
        let ds = rects ?? displays()
        if ds.contains(where: { $0.contains(p) }) { return p }
        var best = p, bestDist = CGFloat.greatestFiniteMagnitude
        for d in ds {
            let q = CGPoint(x: min(max(p.x, d.minX), d.maxX - 1), y: min(max(p.y, d.minY), d.maxY - 1))
            let dist = hypot(q.x - p.x, q.y - p.y)
            if dist < bestDist { best = q; bestDist = dist }
        }
        return best
    }
}

/// Where the shared monitor sits among this Mac's displays. "Input follows the monitor": while the shared
/// monitor shows the other computer, its area on this Mac's desktop IS the other computer.
struct ShareLayout {
    /// The shared monitor's bounds on this Mac — nil while this Mac's output to it is turned off
    /// (then the crossing point is the edge of this Mac's own screens that faces where it was).
    let shared: CGRect?
    let others: [CGRect]        // this Mac's other displays (e.g. the MacBook screen); may be empty
    let sharedOnRight: Bool
    static let inset: CGFloat = 24
    private var own: CGRect { others.reduce(CGRect.null) { $0.union($1) } }

    /// The display a vertical position refers to: the shared monitor, or our display at the facing edge.
    private var reference: CGRect {
        if let s = shared { return s }
        let edge = others.filter { sharedOnRight ? $0.maxX >= own.maxX - 0.5 : $0.minX <= own.minX + 0.5 }
        return edge.max { $0.height < $1.height } ?? own
    }

    /// Does moving to `p` (by `dx`) mean "onto the other computer"?
    func crossing(_ p: CGPoint, dx: CGFloat) -> Bool {
        if let s = shared { return s.contains(p) }
        return sharedOnRight ? (p.x >= own.maxX - 1 && dx > 0) : (p.x <= own.minX && dx < 0)
    }

    /// Position (0…1) down the shared monitor (or the facing display) for a point.
    func position(_ p: CGPoint) -> Float {
        let r = reference
        return Float(min(max((p.y - r.minY) / max(r.height, 1), 0), 1))
    }

    /// A point on this Mac's own screens, just beside where the shared monitor is, at `position` down it.
    func besideShared(_ position: Float) -> CGPoint {
        let r = reference
        let y = r.minY + CGFloat(min(max(position, 0), 1)) * (r.height - 1)
        let x: CGFloat
        // Land well inside, so a 1-pixel wobble can't send the pointer straight back (no flapping).
        if let s = shared { x = sharedOnRight ? s.minX - ShareLayout.inset : s.maxX + ShareLayout.inset }
        else { x = sharedOnRight ? own.maxX - ShareLayout.inset : own.minX + ShareLayout.inset }
        return ShareScreens.clamp(CGPoint(x: x, y: y), within: others.isEmpty ? shared.map { [$0] } : others)
    }
}

// MARK: - Capture

protocol ShareCaptureDelegate: AnyObject {
    /// The local pointer moved onto the shared monitor (which shows the other computer); true = start capturing.
    func captureEnteredShared(position: Float) -> Bool
    func captured(_ msg: ShareMsg)
    /// ⌃⌥⌘ + key pressed while capturing — handled locally, never forwarded (1/2/3/S, Esc = take input back).
    func captureLocalHotkey(_ keyCode: UInt16)
}

/// One session-level event tap. Watching: notices the pointer moving onto the shared monitor while that
/// monitor shows the other computer. Capturing: swallows all keyboard/mouse input and hands it to the
/// delegate. Requires the Accessibility permission.
final class ShareCapture {
    weak var delegate: ShareCaptureDelegate?
    var swapCmdCtrl = true
    /// Set while the shared monitor shows the other computer and this Mac has other screens; nil = don't watch.
    var watch: ShareLayout?
    private(set) var capturing = false
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var lastFlags: UInt64 = 0
    private var scrollRemainder: (x: Double, y: Double) = (0, 0)

    static var hasPermission: Bool { AXIsProcessTrusted() }
    static func requestPermission() {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary)
    }

    func start() -> Bool {
        guard tap == nil else { return true }
        let types: [CGEventType] = [.mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp,
                                    .otherMouseDown, .otherMouseUp, .leftMouseDragged, .rightMouseDragged,
                                    .otherMouseDragged, .scrollWheel, .keyDown, .keyUp, .flagsChanged]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        guard let t = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                        eventsOfInterest: mask, callback: { _, type, event, refcon in
            let me = Unmanaged<ShareCapture>.fromOpaque(refcon!).takeUnretainedValue()
            return me.handle(type: type, event: event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return false }
        tap = t
        source = CFMachPortCreateRunLoopSource(nil, t, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: t, enable: true)
        return true
    }

    func stop() {
        end(warpTo: nil)
        if let t = tap { CGEvent.tapEnable(tap: t, enable: false); CFMachPortInvalidate(t) }
        if let s = source { CFRunLoopRemoveSource(CFRunLoopGetMain(), s, .commonModes) }
        tap = nil; source = nil
    }

    /// Start forwarding everything. `parkAt`: where to hold this Mac's (hidden) pointer meanwhile.
    private var heldHere: UInt64 = 0            // device modifier bits held when forwarding began
    private var localDown = Set<UInt16>()       // keys down on this Mac while not forwarding
    private var heldKeys = Set<UInt16>()        // of those, still held when forwarding began: they release here
    func begin(parkAt: CGPoint?) {
        guard !capturing else { return }
        capturing = true
        lastFlags = CGEventSource.flagsState(.hidSystemState).rawValue          // physical keys only, not our injections
        heldKeys = localDown.filter { CGEventSource.keyState(.hidSystemState, key: CGKeyCode($0)) }
        heldHere = lastFlags & ShareKeyMap.modifierBits.reduce(0) { $0 | ($1.1 & 0xFFFF) }
        scrollRemainder = (0, 0)
        if let p = parkAt { CGWarpMouseCursorPosition(p) }
        CGAssociateMouseAndMouseCursorPosition(0)      // pointer stays put; deltas keep coming
        ShareCursor.hide()
    }

    func end(warpTo point: CGPoint?) {
        guard capturing else { return }
        capturing = false
        CGAssociateMouseAndMouseCursorPosition(1)
        if let point { CGWarpMouseCursorPosition(point) }
        ShareCursor.show()
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let t = tap { CGEvent.tapEnable(tap: t, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == kShareInjectedTag { return Unmanaged.passUnretained(event) }

        guard capturing else {
            if type == .keyDown { localDown.insert(UInt16(event.getIntegerValueField(.keyboardEventKeycode))) }
            if type == .keyUp { localDown.remove(UInt16(event.getIntegerValueField(.keyboardEventKeycode))) }
            // Only a plain move crosses: dragging with a button held would leave that button "down" here.
            if let layout = watch, type == .mouseMoved,
               layout.crossing(event.location, dx: CGFloat(event.getIntegerValueField(.mouseEventDeltaX))),
               delegate?.captureEnteredShared(position: layout.position(event.location)) == true {
                return nil
            }
            return Unmanaged.passUnretained(event)
        }

        switch type {
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            let dx = event.getIntegerValueField(.mouseEventDeltaX), dy = event.getIntegerValueField(.mouseEventDeltaY)
            if dx != 0 || dy != 0 { delegate?.captured(.move(dx: Int16(clamping: dx), dy: Int16(clamping: dy))) }
        case .leftMouseDown, .leftMouseUp: delegate?.captured(.button(1, down: type == .leftMouseDown))
        case .rightMouseDown, .rightMouseUp: delegate?.captured(.button(2, down: type == .rightMouseDown))
        case .otherMouseDown, .otherMouseUp:
            let n = event.getIntegerValueField(.mouseEventButtonNumber)   // 2 middle, 3 back, 4 forward
            let b: UInt8 = n == 2 ? 3 : n == 3 ? 4 : n == 4 ? 5 : 0
            if b != 0 { delegate?.captured(.button(b, down: type == .otherMouseDown)) }
        case .scrollWheel:
            forwardScroll(event)
        case .keyDown, .keyUp:
            let code = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
            // A key already held on this Mac when forwarding began is released here, and its repeats dropped.
            if heldKeys.contains(code) {
                if type == .keyUp { heldKeys.remove(code); localDown.remove(code); return Unmanaged.passUnretained(event) }
                return nil
            }
            let f = event.flags
            if type == .keyDown, f.contains(.maskControl), f.contains(.maskAlternate), f.contains(.maskCommand) {
                delegate?.captureLocalHotkey(code)          // ⌃⌥⌘ shortcuts stay on this Mac
                return nil
            }
            // Auto-repeat is forwarded as repeated key-downs: Windows does not repeat keys sent with SendInput.
            if let u = ShareKeyMap.toHID[code] {
                delegate?.captured(.key(usage: swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(u) : u, down: type == .keyDown))
            }
        case .flagsChanged:
            let now = event.flags.rawValue, changed = now ^ lastFlags
            lastFlags = now
            if changed & CGEventFlags.maskAlphaShift.rawValue != 0 {     // Caps Lock: toggle it on the other side too
                delegate?.captured(.key(usage: 0x39, down: true)); delegate?.captured(.key(usage: 0x39, down: false))
            }
            // Modifiers already held on this Mac when forwarding began (e.g. ⌃⌥⌘ of a shortcut): their release
            // belongs to this Mac — the peer never saw them pressed, and macOS would keep them "down" otherwise.
            let releasedHere = changed & heldHere & ~now
            if releasedHere != 0 {
                heldHere &= ~releasedHere
                if changed & ~releasedHere & ShareKeyMap.modifierBits.reduce(0, { $0 | ($1.1 & 0xFFFF) }) == 0 {
                    return Unmanaged.passUnretained(event)
                }
            }
            for (usage, bits) in ShareKeyMap.modifierBits {
                let device = bits & 0xFFFF
                if changed & device != 0, releasedHere & device == 0 {
                    let u = swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(usage) : usage
                    delegate?.captured(.key(usage: u, down: now & device != 0))
                }
            }
        default: break
        }
        return nil   // capturing: nothing reaches this Mac
    }

    /// Wheel units: 120 per notch. Trackpads report pixels; ~40 px count as one notch.
    private func forwardScroll(_ e: CGEvent) {
        var x: Double, y: Double
        if e.getIntegerValueField(.scrollWheelEventIsContinuous) != 0 {
            y = Double(e.getIntegerValueField(.scrollWheelEventPointDeltaAxis1)) * 3
            x = Double(e.getIntegerValueField(.scrollWheelEventPointDeltaAxis2)) * 3
        } else {
            y = Double(e.getIntegerValueField(.scrollWheelEventDeltaAxis1)) * 120
            x = Double(e.getIntegerValueField(.scrollWheelEventDeltaAxis2)) * 120
        }
        scrollRemainder.x += x; scrollRemainder.y += y
        let sx = Int16(clamping: Int(scrollRemainder.x)), sy = Int16(clamping: Int(scrollRemainder.y))
        scrollRemainder.x -= Double(sx); scrollRemainder.y -= Double(sy)
        if sx != 0 || sy != 0 { delegate?.captured(.scroll(dx: sx, dy: sy)) }
    }
}

/// Hiding the cursor while another app is frontmost needs the window server's "SetsCursorInBackground"
/// connection property (private SPI, looked up at run time so a missing symbol only means a visible cursor).
enum ShareCursor {
    private typealias ConnFn = @convention(c) () -> Int32
    private typealias SetPropFn = @convention(c) (Int32, Int32, CFString, CFTypeRef) -> Int32
    private static var hidden = false
    private static func allowBackground() {
        guard let h = dlopen(nil, RTLD_NOW),
              let c = dlsym(h, "_CGSDefaultConnection"), let s = dlsym(h, "CGSSetConnectionProperty") else { return }
        let conn = unsafeBitCast(c, to: ConnFn.self)()
        _ = unsafeBitCast(s, to: SetPropFn.self)(conn, conn, "SetsCursorInBackground" as CFString, kCFBooleanTrue)
    }
    static func hide() { guard !hidden else { return }; allowBackground(); CGDisplayHideCursor(CGMainDisplayID()); hidden = true }
    static func show() { guard hidden else { return }; CGDisplayShowCursor(CGMainDisplayID()); hidden = false }
}

// MARK: - Emulation

/// Replays the peer's input on this Mac. Every posted event is tagged so our capture ignores it.
final class ShareEmulator {
    var swapCmdCtrl = true
    /// User multiplier on top of the acceleration curve (0.5…3).
    var speed: Double = 1
    /// User multiplier for the peer's scroll wheel (0.5…5).
    var scrollSpeed: Double = 1
    private var scrollRemainder = (x: 0.0, y: 0.0)
    private var remainder = (x: 0.0, y: 0.0)

    /// The peer sends plain pixel deltas; macOS's own mice get acceleration, so without it the peer's mouse
    /// feels slow here. Small, precise moves stay ~1:1, fast flicks go further.
    private func accelerated(_ dx: Int16, _ dy: Int16) -> CGPoint {
        let mag = hypot(Double(dx), Double(dy))
        let gain = speed * (1 + 0.09 * min(mag, 25))
        let x = Double(dx) * gain + remainder.x, y = Double(dy) * gain + remainder.y
        let ix = x.rounded(.towardZero), iy = y.rounded(.towardZero)
        remainder = (x - ix, y - iy)
        return CGPoint(x: ix, y: iy)
    }
    /// The controlled pointer moved onto the shared monitor (which shows the peer) — return control there.
    var onLeave: ((Float) -> Void)?

    private let source = CGEventSource(stateID: .privateState)
    private var pressedKeys = Set<UInt16>()      // mac key codes
    private var pressedButtons = Set<UInt8>()
    private var flags: UInt64 = 0
    private var lastClick: (button: UInt8, time: TimeInterval, count: Int64, at: CGPoint) = (0, 0, 0, .zero)
    private var leaveLayout: ShareLayout?        // nil = take-over: no hand-back by position
    private(set) var active = false

    private func post(_ e: CGEvent?) {
        guard let e else { return }
        e.setIntegerValueField(.eventSourceUserData, value: kShareInjectedTag)
        e.post(tap: .cghidEventTap)
    }
    private var cursor: CGPoint { CGEvent(source: nil)?.location ?? .zero }

    /// Start replaying. With a layout: the pointer appears beside the shared monitor at `position`, and moving
    /// back onto the shared monitor hands control back. Without (take-over): the pointer stays where it is.
    func enter(position: Float, layout: ShareLayout?) {
        active = true
        leaveLayout = layout
        if let layout {
            let p = layout.besideShared(position)
            CGWarpMouseCursorPosition(p)
            post(CGEvent(mouseEventSource: source, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left))
        }
    }

    func leave() { releaseAll(); active = false; leaveLayout = nil }

    func move(dx: Int16, dy: Int16) {
        guard active else { return }
        let d = accelerated(dx, dy)
        let target = CGPoint(x: cursor.x + d.x, y: cursor.y + d.y)
        if let l = leaveLayout, l.crossing(target, dx: CGFloat(dx)) {
            let pos = l.position(target)
            leave()
            onLeave?(pos)
            return
        }
        let p = ShareScreens.clamp(target, within: leaveLayout.map { $0.others.isEmpty ? ShareScreens.displays() : $0.others })
        let type: CGEventType = pressedButtons.contains(1) ? .leftMouseDragged
            : pressedButtons.contains(2) ? .rightMouseDragged
            : pressedButtons.isEmpty ? .mouseMoved : .otherMouseDragged
        let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: .left)
        e?.setIntegerValueField(.mouseEventDeltaX, value: Int64(d.x))
        e?.setIntegerValueField(.mouseEventDeltaY, value: Int64(d.y))
        post(e)
    }

    func button(_ b: UInt8, down: Bool) {
        guard active else { return }
        let p = cursor
        let (type, cg): (CGEventType, CGMouseButton) = {
            switch b {
            case 1: return (down ? .leftMouseDown : .leftMouseUp, .left)
            case 2: return (down ? .rightMouseDown : .rightMouseUp, .right)
            default: return (down ? .otherMouseDown : .otherMouseUp, CGMouseButton(rawValue: UInt32(b == 3 ? 2 : b == 4 ? 3 : 4))!)
            }
        }()
        let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: cg)
        if down {
            let now = ProcessInfo.processInfo.systemUptime
            let same = lastClick.button == b && now - lastClick.time < NSEvent.doubleClickInterval
                && hypot(p.x - lastClick.at.x, p.y - lastClick.at.y) < 5
            lastClick = (b, now, same ? lastClick.count + 1 : 1, p)
            pressedButtons.insert(b)
        } else { pressedButtons.remove(b) }
        e?.setIntegerValueField(.mouseEventClickState, value: lastClick.count)
        e?.flags = CGEventFlags(rawValue: flags)
        post(e)
    }

    func scroll(dx: Int16, dy: Int16) {
        guard active else { return }
        // Whole notches → line events (like a wheel): 3 lines per notch, as on Windows, times the user's scroll
        // speed. Anything finer → pixel events (like a trackpad), scaled the same way.
        if dy % 120 == 0 && dx % 120 == 0 {
            let y = Double(dy) / 120 * 3 * scrollSpeed + scrollRemainder.y, x = Double(dx) / 120 * 3 * scrollSpeed + scrollRemainder.x
            let iy = y.rounded(.towardZero), ix = x.rounded(.towardZero)
            scrollRemainder = (x - ix, y - iy)
            if iy != 0 || ix != 0 {
                post(CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 2, wheel1: Int32(iy), wheel2: Int32(ix), wheel3: 0))
            }
        } else {
            post(CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2,
                         wheel1: Int32(Double(dy) / 3 * scrollSpeed), wheel2: Int32(Double(dx) / 3 * scrollSpeed), wheel3: 0))
        }
    }

    func key(usage raw: UInt16, down: Bool) {
        guard active else { return }
        let usage = swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(raw) : raw
        guard let code = ShareKeyMap.toMac[usage] else { return }
        if let bits = ShareKeyMap.modifierBits[usage] {
            if down { flags |= bits } else {
                flags &= ~(bits & 0xFFFF)
                // keep the generic bit while the other side's same modifier is still down
                let generic = bits & ~UInt64(0xFFFF)
                let stillDown = ShareKeyMap.modifierBits.contains { $0.key != usage && $0.value & ~UInt64(0xFFFF) == generic && flags & $0.value & 0xFFFF != 0 }
                if !stillDown { flags &= ~generic }
            }
            let e = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: down)
            e?.type = .flagsChanged
            e?.flags = CGEventFlags(rawValue: flags)
            post(e)
        } else {
            let e = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: down)
            e?.flags = CGEventFlags(rawValue: flags)
            post(e)
        }
        if down { pressedKeys.insert(code) } else { pressedKeys.remove(code) }
    }

    /// Lift everything the peer is holding down (on disconnect, leave, or request).
    func releaseAll() {
        for b in pressedButtons { button(b, down: false) }
        for code in pressedKeys {
            if let u = ShareKeyMap.toHID[code] { key(usage: swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(u) : u, down: false) }
        }
        pressedButtons.removeAll(); pressedKeys.removeAll(); flags = 0
    }
}

// MARK: - Clipboard (plain text)

final class ShareClipboard {
    private var syncedChangeCount = NSPasteboard.general.changeCount

    /// Text to send when control moves to the other computer, if it changed since the last sync.
    func takeOutgoing() -> String? {
        let pb = NSPasteboard.general
        guard pb.changeCount != syncedChangeCount else { return nil }
        syncedChangeCount = pb.changeCount
        // Password managers mark copied secrets concealed/transient (nspasteboard.org): never send those.
        let types = Set(pb.types ?? [])
        let secret: [NSPasteboard.PasteboardType] = [.init("org.nspasteboard.ConcealedType"), .init("org.nspasteboard.TransientType"),
                                                    .init("org.nspasteboard.AutoGeneratedType")]
        if secret.contains(where: types.contains) { return nil }
        guard let s = pb.string(forType: .string), !s.isEmpty, s.utf8.count <= BHDS.maxClipboard else { return nil }
        return s
    }

    func apply(_ text: String) {
        let pb = NSPasteboard.general
        guard pb.string(forType: .string) != text else { syncedChangeCount = pb.changeCount; return }
        pb.clearContents()
        pb.setString(text, forType: .string)
        syncedChangeCount = pb.changeCount
    }
}
