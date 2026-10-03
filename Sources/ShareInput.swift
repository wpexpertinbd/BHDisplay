import AppKit
import CoreGraphics
import ApplicationServices

// Keyboard & mouse sharing — capturing local input (event tap) and replaying the peer's input (CGEvent posting).

/// Injected events carry this in kCGEventSourceUserData so our own tap ignores them (no feedback loops).
let kShareInjectedTag: Int64 = 0x4248_4453   // "BHDS"

enum ShareEdge: UInt8 { case left = 0, right = 1, top = 2, bottom = 3
    var opposite: ShareEdge { switch self { case .left: return .right; case .right: return .left; case .top: return .bottom; case .bottom: return .top } }
}

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
    static var union: CGRect { displays().reduce(CGRect.null) { $0.union($1) } }

    /// The display touching the given outer edge of the desktop that is closest to `y`/`x` along it.
    static func edgeDisplay(_ edge: ShareEdge) -> [CGRect] {
        let u = union, ds = displays()
        switch edge {
        case .left: return ds.filter { $0.minX <= u.minX + 0.5 }
        case .right: return ds.filter { $0.maxX >= u.maxX - 0.5 }
        case .top: return ds.filter { $0.minY <= u.minY + 0.5 }
        case .bottom: return ds.filter { $0.maxY >= u.maxY - 0.5 }
        }
    }

    /// Is `p` on the outer `edge` of the desktop? Returns the position (0…1) along that edge's display.
    static func hit(_ p: CGPoint, edge: ShareEdge) -> Float? {
        for d in edgeDisplay(edge) where d.insetBy(dx: -1, dy: -1).contains(p) {
            switch edge {
            case .left where p.x <= d.minX + 0.5, .right where p.x >= d.maxX - 1:
                return Float((p.y - d.minY) / max(d.height, 1))
            case .top where p.y <= d.minY + 0.5, .bottom where p.y >= d.maxY - 1:
                return Float((p.x - d.minX) / max(d.width, 1))
            default: continue
            }
        }
        return nil
    }

    /// A point just inside the outer `edge`, at `position` along the (tallest/widest) display on that edge.
    static func entryPoint(_ edge: ShareEdge, position: Float) -> CGPoint {
        let ds = edgeDisplay(edge)
        guard let d = ds.max(by: { $0.width * $0.height < $1.width * $1.height }) else { return .zero }
        let t = CGFloat(min(max(position, 0), 1))
        switch edge {
        case .left: return CGPoint(x: d.minX + 2, y: d.minY + t * (d.height - 1))
        case .right: return CGPoint(x: d.maxX - 3, y: d.minY + t * (d.height - 1))
        case .top: return CGPoint(x: d.minX + t * (d.width - 1), y: d.minY + 2)
        case .bottom: return CGPoint(x: d.minX + t * (d.width - 1), y: d.maxY - 3)
        }
    }

    /// Clamp a point onto the desktop (handles displays of different sizes / gaps between them).
    static func clamp(_ p: CGPoint) -> CGPoint {
        let ds = displays()
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

// MARK: - Capture

protocol ShareCaptureDelegate: AnyObject {
    /// The local pointer reached the sharing edge; return true to start capturing (forwarding) input.
    func captureEdgeHit(position: Float) -> Bool
    func captured(_ msg: ShareMsg)
    /// ⌃⌥⌘ + key pressed while capturing — handled locally, never forwarded (1/2/3/S, Esc = release).
    func captureLocalHotkey(_ keyCode: UInt16)
}

/// One session-level event tap. Idle: watches for the pointer reaching the edge. Capturing: swallows all
/// keyboard/mouse input and hands it to the delegate. Requires the Accessibility permission.
final class ShareCapture {
    weak var delegate: ShareCaptureDelegate?
    var edge: ShareEdge = .right
    var swapCmdCtrl = true
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
        end(at: nil)
        if let t = tap { CGEvent.tapEnable(tap: t, enable: false); CFMachPortInvalidate(t) }
        if let s = source { CFRunLoopRemoveSource(CFRunLoopGetMain(), s, .commonModes) }
        tap = nil; source = nil
    }

    func begin() {
        guard !capturing else { return }
        capturing = true
        lastFlags = CGEventSource.flagsState(.combinedSessionState).rawValue
        scrollRemainder = (0, 0)
        CGAssociateMouseAndMouseCursorPosition(0)      // pointer stays put; deltas keep coming
        ShareCursor.hide()
    }

    /// Stop capturing; if `position` is given, put the local pointer just inside the sharing edge there.
    func end(at position: Float?) {
        guard capturing else { return }
        capturing = false
        CGAssociateMouseAndMouseCursorPosition(1)
        if let position {
            let p = ShareScreens.entryPoint(edge, position: position)
            CGWarpMouseCursorPosition(CGPoint(x: edge == .right ? p.x - 4 : edge == .left ? p.x + 4 : p.x, y: p.y))
        }
        ShareCursor.show()
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let t = tap { CGEvent.tapEnable(tap: t, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == kShareInjectedTag { return Unmanaged.passUnretained(event) }

        guard capturing else {
            if type == .mouseMoved || type == .leftMouseDragged || type == .rightMouseDragged || type == .otherMouseDragged,
               let pos = ShareScreens.hit(event.location, edge: edge),
               movingTowardEdge(event), delegate?.captureEdgeHit(position: pos) == true {
                begin()
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
            let f = event.flags
            if type == .keyDown, f.contains(.maskControl), f.contains(.maskAlternate), f.contains(.maskCommand) {
                delegate?.captureLocalHotkey(code)          // ⌃⌥⌘ shortcuts stay on this Mac
                return nil
            }
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return nil }   // the other OS repeats itself
            if let u = ShareKeyMap.toHID[code] {
                delegate?.captured(.key(usage: swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(u) : u, down: type == .keyDown))
            }
        case .flagsChanged:
            let now = event.flags.rawValue, changed = now ^ lastFlags
            lastFlags = now
            for (usage, bits) in ShareKeyMap.modifierBits {
                let device = bits & 0xFFFF
                if changed & device != 0 {
                    let u = swapCmdCtrl ? ShareKeyMap.swapCmdCtrl(usage) : usage
                    delegate?.captured(.key(usage: u, down: now & device != 0))
                }
            }
        default: break
        }
        return nil   // capturing: nothing reaches this Mac
    }

    private func movingTowardEdge(_ e: CGEvent) -> Bool {
        let dx = e.getIntegerValueField(.mouseEventDeltaX), dy = e.getIntegerValueField(.mouseEventDeltaY)
        switch edge { case .right: return dx > 0; case .left: return dx < 0; case .bottom: return dy > 0; case .top: return dy < 0 }
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
    var edge: ShareEdge = .right              // the edge facing the peer
    var swapCmdCtrl = true
    /// The controlled pointer was pushed back out through `edge` — return control to the peer.
    var onLeave: ((Float) -> Void)?

    private let source = CGEventSource(stateID: .privateState)
    private var pressedKeys = Set<UInt16>()      // mac key codes
    private var pressedButtons = Set<UInt8>()
    private var flags: UInt64 = 0
    private var lastClick: (button: UInt8, time: TimeInterval, count: Int64, at: CGPoint) = (0, 0, 0, .zero)
    private var scrollRemainder: (x: Int, y: Int) = (0, 0)
    private(set) var active = false

    private func post(_ e: CGEvent?) {
        guard let e else { return }
        e.setIntegerValueField(.eventSourceUserData, value: kShareInjectedTag)
        e.post(tap: .cghidEventTap)
    }
    private var cursor: CGPoint { CGEvent(source: nil)?.location ?? .zero }

    func enter(position: Float) {
        active = true
        let p = ShareScreens.entryPoint(edge, position: position)
        CGWarpMouseCursorPosition(p)
        post(CGEvent(mouseEventSource: source, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left))
    }

    func leave() { releaseAll(); active = false }

    func move(dx: Int16, dy: Int16) {
        guard active else { return }
        let p = ShareScreens.clamp(CGPoint(x: cursor.x + CGFloat(dx), y: cursor.y + CGFloat(dy)))
        let type: CGEventType = pressedButtons.contains(1) ? .leftMouseDragged
            : pressedButtons.contains(2) ? .rightMouseDragged
            : pressedButtons.isEmpty ? .mouseMoved : .otherMouseDragged
        let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: p, mouseButton: .left)
        e?.setIntegerValueField(.mouseEventDeltaX, value: Int64(dx))
        e?.setIntegerValueField(.mouseEventDeltaY, value: Int64(dy))
        post(e)
        let pushingOut = (edge == .right && dx > 0) || (edge == .left && dx < 0) || (edge == .bottom && dy > 0) || (edge == .top && dy < 0)
        if pushingOut, let pos = ShareScreens.hit(p, edge: edge) {
            leave()
            onLeave?(pos)
        }
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
        // Whole notches → line events (like a wheel); anything finer → pixel events (like a trackpad).
        if dy % 120 == 0 && dx % 120 == 0 {
            post(CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 2, wheel1: Int32(dy / 120), wheel2: Int32(dx / 120), wheel3: 0))
        } else {
            post(CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2, wheel1: Int32(dy) / 3, wheel2: Int32(dx) / 3, wheel3: 0))
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
