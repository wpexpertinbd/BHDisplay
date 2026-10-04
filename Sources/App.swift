import AppKit
import SwiftUI
import Carbon.HIToolbox
import ServiceManagement

enum Brand {
    static let website = "https://www.biswashost.com/"
    static let websiteLabel = "www.biswashost.com"
    static let repo = "https://github.com/wpexpertinbd/BHDisplay"
    static let repoLabel = "github.com/wpexpertinbd/BHDisplay"
    /// `-DocsScreenshot` on the command line hides the real serial number for public screenshots.
    static let docsMode = CommandLine.arguments.contains("-DocsScreenshot")
    static var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev" }
}

@main
enum Entry {
    static func main() {
        // Take a queue ticket before anything slow, so --input runs keep the order they were launched in.
        if CommandLine.arguments.contains("--input") { InputQueue.reserve() }
        if CommandLine.arguments.count > 1, CommandLine.arguments[1].hasPrefix("--") { CLI.run(CommandLine.arguments) }
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}

// BHDisplay --get | --input dp|hdmi1|hdmi2|mac|other | --login on|off|status
// Raw VCP access is for diagnostics only and must be asked for explicitly:
//   --read CODE           CODE is hex (e.g. E2 or 0xE2)
//   --unsafe --set CODE VALUE   VALUE is decimal, or hex with a 0x prefix
// Commands that can wipe or power off the monitor additionally need --really.
/// Serialises `--input` runs across processes so the LAST request always wins.
/// `reserve()` runs first thing in main(): under a lock it takes the next ticket from a counter file, so
/// tickets follow the order the processes were launched (no wall clock — a clock change can't stall it).
/// `takeTurn()` re-locks and proceeds only if no newer ticket was issued meanwhile; that lock is held until
/// the process exits, so writes to the monitor never overlap. If the lock can't be used, switching still
/// works (unordered) and a warning is printed — a broken cache folder must not break the monitor switch.
enum InputQueue {
    nonisolated(unsafe) private static var lockFD: Int32 = -1
    nonisolated(unsafe) private static var ticket: UInt64 = 0
    private static var dir: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("com.biswashost.bhdisplay", isDirectory: true)
    }
    private static var counterURL: URL { dir.appendingPathComponent("input.ticket") }
    private static func counter() -> UInt64 {
        (try? String(contentsOf: counterURL, encoding: .utf8)).flatMap { UInt64($0.trimmingCharacters(in: .whitespacesAndNewlines)) } ?? 0
    }

    static func reserve() {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        lockFD = open(dir.appendingPathComponent("input.lock").path, O_CREAT | O_RDWR | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard lockFD >= 0, flock(lockFD, LOCK_EX) == 0 else {
            fputs("warning: switch queue unavailable (\(String(cString: strerror(errno)))); switching unordered\n", stderr)
            lockFD = -1; return
        }
        ticket = counter() &+ 1
        if ticket == 0 { ticket = 1 }                    // 0 means "unordered"; never hand it out
        do { try String(ticket).write(to: counterURL, atomically: true, encoding: .utf8) }
        catch { fputs("warning: switch queue not writable; switching unordered\n", stderr); ticket = 0 }
        flock(lockFD, LOCK_UN)
    }

    static func takeTurn() -> Bool {
        guard lockFD >= 0, ticket != 0 else { return true }
        guard flock(lockFD, LOCK_EX) == 0 else {         // held until exit
            fputs("warning: switch queue lock failed; switching unordered\n", stderr); return true
        }
        return counter() == ticket
    }
}

enum CLI {
    /// Factory/geometry/colour restore and power mode — a typo here costs the user their settings.
    static let destructive: Set<UInt8> = [0x04, 0x05, 0x06, 0x08, 0x0A, 0xD6]

    static func learned(_ key: String) -> UInt16? {
        guard let i = UserDefaults.standard.object(forKey: key) as? Int,
              let v = UInt16(exactly: i), MonitorInput.isValid(v) else { return nil }
        return v
    }

    static func hexCode(_ s: String) -> UInt8? {
        let t = s.lowercased().hasPrefix("0x") ? String(s.dropFirst(2)) : s
        return UInt8(t, radix: 16)
    }
    static func value(_ s: String) -> UInt16? {
        s.lowercased().hasPrefix("0x") ? UInt16(s.dropFirst(2), radix: 16) : UInt16(s, radix: 10)
    }

    static func run(_ argv: [String]) -> Never {
        let allowRaw = argv.contains("--unsafe"), really = argv.contains("--really")
        let args = argv.filter { $0 != "--unsafe" && $0 != "--really" }
        guard args.count > 1 else { usage() }
        if args[1] == "--login" {   // --login on|off|status  (same SMAppService as the menu item)
            let svc = SMAppService.mainApp
            do {
                switch args.count > 2 ? args[2] : "status" {
                case "on": try svc.register()
                case "off": try svc.unregister()
                default: break
                }
            } catch { fputs("login item: \(error.localizedDescription)\n", stderr); exit(1) }
            let names: [SMAppService.Status: String] = [.enabled: "enabled", .notRegistered: "not registered",
                                                        .requiresApproval: "requires approval in System Settings → Login Items",
                                                        .notFound: "not found"]
            print("launch at login:", names[svc.status] ?? "unknown")
            exit(0)
        }
        guard let ddc = DDC.firstExternal() else { fputs("\(DDCError.noExternalDisplay)\n", stderr); exit(1) }
        do {
            switch args[1] {
            case "--get":
                let v = try ddc.read(VCP.input)
                print(MonitorInput.name(for: v.current), String(format: "(0x%02X)", v.current & 0xFF))
            case "--input" where args.count > 2:
                let byName = Dictionary(uniqueKeysWithValues: MonitorInput.all.map {
                    ($0.name.lowercased().replacingOccurrences(of: " ", with: ""), $0.id) })
                let key = args[2].lowercased()
                let code: UInt16
                if key == "mac" || key == "other" {
                    // Ports the app learned (same preferences domain) — handy for scripts and Shortcuts.
                    guard let mac = learned("macInput") else {
                        fputs("the Mac's port isn't known yet — open BHDisplay once while the monitor shows the Mac\n", stderr); exit(2)
                    }
                    code = key == "mac" ? mac : (learned("otherInput").flatMap { $0 != mac ? $0 : nil } ?? (mac == 0x0F ? 0x12 : 0x0F))
                } else {
                    guard let c = byName[key] ?? (key == "dp" ? 0x0F : nil)
                            ?? hexCode(key).map({ UInt16($0) }).flatMap({ MonitorInput.isValid($0) ? $0 : nil })
                    else { fputs("unknown input \(args[2]) — use dp, hdmi1, hdmi2, mac or other\n", stderr); exit(2) }
                    code = c
                }
                // Scripts/automation can fire several of these within a second. Run them one
                // at a time and drop any request a newer one has replaced, so the LAST move always wins.
                guard InputQueue.takeTurn() else { print("superseded by a newer switch"); exit(0) }
                // Re-selecting the input that is already showing can blank some monitors for a moment.
                if let cur = try? ddc.read(VCP.input), cur.current & 0xFF == code {
                    print("already on \(MonitorInput.name(for: code))"); exit(0)
                }
                try ddc.write(VCP.input, code, repeats: 2)
                print("switched to \(MonitorInput.name(for: code))")
            case "--read" where args.count > 2:
                guard let c = hexCode(args[2]) else { fputs("CODE must be hex, e.g. E2\n", stderr); exit(2) }
                let v = try ddc.read(c)
                print(String(format: "VCP 0x%02X = %d (max %d)", c, v.current, v.max))
            case "--set" where args.count > 3:
                guard allowRaw else {
                    fputs("--set writes raw monitor commands; repeat with --unsafe if you mean it\n", stderr); exit(3)
                }
                guard let c = hexCode(args[2]), let v = value(args[3]) else {
                    fputs("CODE must be hex (E2), VALUE decimal (40) or 0x-hex (0x28)\n", stderr); exit(2)
                }
                if c == VCP.input && !MonitorInput.isValid(v) {
                    fputs("not an input of this monitor — use --input dp|hdmi1|hdmi2\n", stderr); exit(2)
                }
                if destructive.contains(c) && !really {
                    fputs(String(format: "VCP 0x%02X can reset or power off the monitor; add --really to send it\n", c), stderr); exit(3)
                }
                try ddc.write(c, v)
                print(String(format: "VCP 0x%02X <- %d", c, v))
            default:
                usage()
            }
            exit(0)
        } catch { fputs("\(error)\n", stderr); exit(1) }
    }

    static func usage() -> Never {
        print("""
        usage: BHDisplay --get
               BHDisplay --input dp|hdmi1|hdmi2|mac|other
               BHDisplay --login on|off|status
               BHDisplay --read CODE                      (diagnostics, CODE in hex)
               BHDisplay --unsafe --set CODE VALUE        (diagnostics, raw write)
        """)
        exit(2)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, NSWindowDelegate {
    private var status: NSStatusItem!
    private let statusMenu = NSMenu()

    @objc private func statusClicked() {
        let e = NSApp.currentEvent
        if e?.type == .rightMouseUp || e?.modifierFlags.contains(.control) == true { showStatusMenu() } else { showWindow() }
    }

    /// Shows the menu under the icon (the menu is attached only while it is open, so a left-click stays a click).
    private func showStatusMenu() {
        status.menu = statusMenu
        status.button?.performClick(nil)
        status.menu = nil
    }
    private var window: NSWindow?
    private var hotKeys: [EventHotKeyRef?] = []
    private let m = MonitorModel.shared

    func applicationWillTerminate(_ n: Notification) {
        ShareController.shared.stop()             // release capture and any keys/buttons we pressed for the peer
        DisplayPower.turnOn()                     // never leave the shared monitor without the Mac's signal
    }

    func applicationDidFinishLaunching(_ n: Notification) {
        DisplayPower.restoreLeftover()          // our display-off never outlives a quit or crash
        if DisplayPower.reenableRemembered() { ShareLog.write("turned the monitor back on (it was replugged while off)") }
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.image = NSImage(systemSymbolName: "display.2", accessibilityDescription: "BHDisplay")
        // Left-click opens the window, right-click (or Control-click) shows the menu — like the Windows tray icon.
        statusMenu.delegate = self
        status.button?.target = self
        status.button?.action = #selector(statusClicked)
        status.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        registerHotKeys()
        LegacyCleanup.removeOldSharingJob()
        ShareLog.prune(force: true)             // old entries go at every start too
        ShareController.shared.bootstrap()
        m.refresh()
        // Re-read when the monitor is plugged/unplugged or wakes.
        CGDisplayRegisterReconfigurationCallback({ _, flags, _ in
            if flags.contains(.beginConfigurationFlag) { return }
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { MonitorModel.shared.refresh() }
        }, nil)
        if CommandLine.arguments.contains("-DocsOpenMenu") {   // docs only: pop the menu for a screenshot
            DispatchQueue.main.asyncAfter(deadline: .now() + 10) { self.showStatusMenu() }   // after sharing reconnects
            return
        }
        if !launchedAsLoginItem() { showWindow() }
    }

    func applicationShouldHandleReopen(_ s: NSApplication, hasVisibleWindows: Bool) -> Bool { showWindow(); return false }

    private func launchedAsLoginItem() -> Bool {
        guard let ev = NSAppleEventManager.shared().currentAppleEvent else { return false }
        return ev.eventID == kAEOpenApplication &&
            ev.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }

    @objc func showWindow() {
        if window == nil {
            let w = NSWindow(contentRect: .zero, styleMask: [.titled, .closable, .miniaturizable, .resizable],
                             backing: .buffered, defer: false)
            w.title = "BHDisplay"
            w.contentView = NSHostingView(rootView: ContentView())
            w.contentMinSize = NSSize(width: 470, height: 360)
            w.isReleasedWhenClosed = false
            w.delegate = self
            // Natural size, but never bigger than the screen it opens on; a size the user chose is remembered.
            if !w.setFrameUsingName("BHDisplayMain") {
                let natural = NSHostingView(rootView: ContentView(scrolls: false)).fittingSize
                let screen = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame.size ?? natural
                let frame = w.frameRect(forContentRect: NSRect(origin: .zero, size: natural))
                let chrome = frame.height - natural.height
                w.setContentSize(NSSize(width: min(natural.width, screen.width - 40),
                                        height: min(natural.height, screen.height - chrome - 20)))
                w.center()
            }
            w.setFrameAutosaveName("BHDisplayMain")
            window = w
        }
        // Dock icon only while the settings window is open; closing it returns to menu-bar only.
        NSApp.setActivationPolicy(.regular)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate()
        m.refresh()
    }


    func windowWillClose(_ n: Notification) { NSApp.setActivationPolicy(.accessory) }

    // Rebuilt on every open so the tick reflects the monitor's answer, not a cached guess.
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let head = NSMenuItem(title: m.error ?? "\(m.info.name) — \(m.input.map(m.label) ?? "reading…")", action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        menu.addItem(.separator())
        if let n = m.notice {
            let w = NSMenuItem(title: "⚠︎ " + n, action: nil, keyEquivalent: ""); w.isEnabled = false
            menu.addItem(w); menu.addItem(.separator())
        }
        if m.macInput != nil {
            let t = NSMenuItem(title: m.input == m.macInput ? "Switch to Other Computer" : "Switch to This Mac",
                               action: #selector(toggle), keyEquivalent: "s")
            t.keyEquivalentModifierMask = [.control, .option, .command]; t.target = self
            menu.addItem(t)
            menu.addItem(.separator())
        }
        for (i, inp) in MonitorInput.all.enumerated() {
            let it = NSMenuItem(title: m.label(inp.id), action: #selector(pick(_:)), keyEquivalent: "\(i + 1)")
            it.keyEquivalentModifierMask = [.control, .option, .command]
            it.tag = Int(inp.id); it.target = self
            it.state = m.input == inp.id ? .on : .off
            menu.addItem(it)
        }
        let ad = NSMenuItem(title: "Auto Detect Input", action: #selector(toggleAutoDetect), keyEquivalent: ""); ad.target = self
        ad.state = m.autoDetect == true ? .on : .off
        ad.isEnabled = m.autoDetect != nil
        menu.addItem(ad)
        menu.addItem(.separator())
        let keep = NSMenuItem(title: "Keep a Log", action: #selector(toggleLog), keyEquivalent: ""); keep.target = self
        keep.state = ShareLog.enabled ? .on : .off
        menu.addItem(keep)
        let check = NSMenuItem(title: "Check Log…", action: ShareLog.enabled ? #selector(checkLog) : nil, keyEquivalent: ""); check.target = self
        check.isEnabled = ShareLog.enabled
        menu.addItem(check)
        let age = NSMenuItem(title: "Delete Log Entries Older Than", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for d in [3, 7] {
            let i = NSMenuItem(title: "\(d) Days", action: #selector(setLogDays(_:)), keyEquivalent: ""); i.target = self
            i.tag = d; i.state = ShareLog.keepDays == d ? .on : .off
            sub.addItem(i)
        }
        age.submenu = sub; age.isEnabled = ShareLog.enabled
        menu.addItem(age)
        menu.addItem(.separator())
        let ab = NSMenuItem(title: "About BHDisplay", action: #selector(showAbout), keyEquivalent: ""); ab.target = self
        menu.addItem(ab)
        let o = NSMenuItem(title: "Open BHDisplay…", action: #selector(showWindow), keyEquivalent: ","); o.target = self
        menu.addItem(o)
        // Keyboard & mouse sharing — never changes the monitor input.
        let share = ShareController.shared
        let k = NSMenuItem(title: "Keyboard & Mouse Sharing", action: #selector(toggleSharing), keyEquivalent: ""); k.target = self
        k.state = share.enabled ? .on : .off
        menu.addItem(k)
        if share.enabled {
            let st = NSMenuItem(title: "    " + share.status, action: nil, keyEquivalent: ""); st.isEnabled = false
            menu.addItem(st)
            let arm = NSMenuItem(title: "    Pair a new computer…", action: #selector(armPairing), keyEquivalent: ""); arm.target = self
            menu.addItem(arm)
            for d in share.discovered {
                let p = NSMenuItem(title: "    Pair with \(d.name)…", action: #selector(pairDevice(_:)), keyEquivalent: ""); p.target = self
                p.representedObject = d.id
                menu.addItem(p)
            }
        }
        menu.addItem(.separator())
        let l = NSMenuItem(title: "Launch at Login", action: #selector(toggleLogin), keyEquivalent: ""); l.target = self
        switch SMAppService.mainApp.status {
        case .enabled: l.state = .on
        case .requiresApproval: l.state = .mixed; l.title = "Launch at Login — approve in System Settings…"
        default: l.state = .off
        }
        menu.addItem(l)
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit BHDisplay", action: #selector(NSApp.terminate(_:)), keyEquivalent: "q"))
        m.refresh(full: true)
    }

    @objc private func toggle() { m.toggleMacOther() }
    @objc private func toggleSharing() {
        let share = ShareController.shared
        share.enabled.toggle()
        if share.enabled && share.needsAccessibility { showWindow() }
    }
    @objc private func armPairing() { ShareController.shared.armPairing() }
    @objc private func toggleLog() {
        if ShareLog.enabled { ShareLog.write("log turned off"); ShareLog.enabled = false }
        else { ShareLog.enabled = true; ShareLog.write("log turned on") }
    }
    @objc private func checkLog() { ShareLog.open() }
    @objc private func setLogDays(_ item: NSMenuItem) { ShareLog.keepDays = item.tag }
    @objc private func pairDevice(_ item: NSMenuItem) {
        let share = ShareController.shared
        if let id = item.representedObject as? String, let d = share.discovered.first(where: { $0.id == id }) { share.pair(with: d) }
    }

    @objc private func showAbout() {
        let credits = NSMutableAttributedString()
        let body: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.labelColor]
        func line(_ s: String) { credits.append(NSAttributedString(string: s, attributes: body)) }
        func link(_ title: String, _ url: String) {
            var a = body; a[.link] = URL(string: url)!
            credits.append(NSAttributedString(string: title, attributes: a))
        }
        line("Built by BiswasHost\n"); link(Brand.websiteLabel, Brand.website)
        line("\n\nFree & open-source:\n"); link(Brand.repoLabel, Brand.repo)
        line("\n\nMonitor control over DDC/CI for ViewSonic displays.\nNot affiliated with or endorsed by ViewSonic.")
        let p = NSMutableParagraphStyle(); p.alignment = .center
        credits.addAttribute(.paragraphStyle, value: p, range: NSRange(location: 0, length: credits.length))
        NSApp.activate()
        NSApp.orderFrontStandardAboutPanel(options: [.credits: credits])
    }
    @objc private func toggleAutoDetect() { if let a = m.autoDetect { m.setAutoDetect(!a) } }
    @objc private func pick(_ s: NSMenuItem) { m.switchTo(UInt16(s.tag)) }

    @objc private func toggleLogin() {
        let svc = SMAppService.mainApp
        do {
            switch svc.status {
            case .enabled: try svc.unregister()
            case .requiresApproval: SMAppService.openSystemSettingsLoginItems()   // the user must approve it there
            default:
                try svc.register()
                if svc.status == .requiresApproval { SMAppService.openSystemSettingsLoginItems() }
            }
        } catch { m.error = "Login item: \(error.localizedDescription)" }
    }

    // Global ⌃⌥⌘S / 1 / 2 / 3 — Carbon hot keys need no Accessibility permission.
    private func registerHotKeys() {
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, ev, _ in
            var hk = EventHotKeyID()
            GetEventParameter(ev, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            let id = Int(hk.id)
            DispatchQueue.main.async {
                let m = MonitorModel.shared
                if id == 10 { ShareController.shared.takeBack() }
                else if id == 9 { m.toggleMacOther() }
                else if MonitorInput.all.indices.contains(id - 1) { m.switchTo(MonitorInput.all[id - 1].id) }
            }
            return noErr
        }, 1, &spec, nil, nil)
        let keys: [(Int, UInt32)] = [(kVK_ANSI_1, 1), (kVK_ANSI_2, 2), (kVK_ANSI_3, 3), (kVK_ANSI_S, 9), (kVK_Escape, 10)]
        for (k, id) in keys {
            var ref: EventHotKeyRef?
            RegisterEventHotKey(UInt32(k), UInt32(controlKey | optionKey | cmdKey),
                                EventHotKeyID(signature: OSType(0x42484450), id: id), GetApplicationEventTarget(), 0, &ref)
            hotKeys.append(ref)
        }
    }
}
