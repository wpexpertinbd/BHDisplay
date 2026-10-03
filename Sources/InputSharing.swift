import AppKit

/// Keyboard & mouse sharing between this Mac and another computer, provided by **Lan Mouse**
/// (github.com/feschber/lan-mouse, GPL-3.0 — a separate app; BHDisplay does not bundle it).
///
/// BHDisplay only switches Lan Mouse's background service on and off. The service runs as a per-user
/// launchd job, NOT as a child of BHDisplay: macOS checks the Accessibility permission of the process
/// that is responsible for input capture, and a child would be charged to BHDisplay instead of Lan Mouse.
/// launchd also starts it at login and restarts it if it crashes. Lan Mouse's own window attaches to
/// a running service, so its settings stay available; it never changes the monitor input.
enum InputSharing {
    static let lanMouseID = "de.feschber.LanMouse"
    static let label = "com.biswashost.bhdisplay.lanmouse"
    static let releasesURL = URL(string: "https://github.com/feschber/lan-mouse/releases/latest")!

    enum State: Equatable {
        case notInstalled        // Lan Mouse app not found
        case off                 // our launchd job is not loaded
        case running(pid: Int32) // job loaded and the service is up
        case starting            // job loaded but no service process yet (or it exited and launchd will retry)
    }

    private static var uid: uid_t { getuid() }
    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var logURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/BHDisplay/lan-mouse.log")
    }

    /// The installed Lan Mouse app, verified by its bundle identifier (not just by name or path).
    static func lanMouseApp() -> URL? {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: lanMouseID),
              Bundle(url: url)?.bundleIdentifier == lanMouseID else { return nil }
        return url
    }

    static func state() -> State {
        guard lanMouseApp() != nil else { return .notInstalled }
        guard let out = launchctl(["print", "gui/\(uid)/\(label)"]), out.status == 0 else { return .off }
        if let line = out.text.split(separator: "\n").first(where: { $0.trimmingCharacters(in: .whitespaces).hasPrefix("pid = ") }),
           let pid = Int32(line.split(separator: "=").last!.trimmingCharacters(in: .whitespaces)) {
            return .running(pid: pid)
        }
        return .starting
    }

    enum SharingError: LocalizedError {
        case notInstalled, launchctl(String)
        var errorDescription: String? {
            switch self {
            case .notInstalled: return "Lan Mouse is not installed."
            case .launchctl(let m): return "Couldn't start Lan Mouse: \(m)"
            }
        }
    }

    static func enable() throws {
        guard let app = lanMouseApp(), let exe = Bundle(url: app)?.executableURL else { throw SharingError.notInstalled }
        // Lan Mouse's own window runs its own service, which would make ours exit as "already running"
        // and stop when the window closes. Quit it; reopened later, the window attaches to our service.
        for running in NSRunningApplication.runningApplications(withBundleIdentifier: lanMouseID) { running.terminate() }
        waitForOtherServicesToExit(exe: exe)

        try? FileManager.default.createDirectory(at: logURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        trimLog()
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [exe.path, "daemon"],
            "RunAtLoad": true,
            "KeepAlive": ["SuccessfulExit": false],  // restart after a crash, not after a clean "already running" exit
            "ThrottleInterval": 10,
            "ProcessType": "Interactive",             // input latency matters
            "StandardOutPath": logURL.path,
            "StandardErrorPath": logURL.path,
        ]
        try? FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: plistURL, options: .atomic)

        _ = launchctl(["bootout", "gui/\(uid)/\(label)"])           // reload cleanly if it was already there
        let r = launchctl(["bootstrap", "gui/\(uid)", plistURL.path])
        guard let r, r.status == 0 else {
            throw SharingError.launchctl(r?.text.trimmingCharacters(in: .whitespacesAndNewlines) ?? "launchctl could not run")
        }
    }

    static func disable() {
        _ = launchctl(["bootout", "gui/\(uid)/\(label)"])
        try? FileManager.default.removeItem(at: plistURL)
    }

    /// Opens Lan Mouse's window (to change devices or settings); it attaches to the running service.
    static func openSettings() {
        guard let app = lanMouseApp() else { NSWorkspace.shared.open(releasesURL); return }
        NSWorkspace.shared.openApplication(at: app, configuration: NSWorkspace.OpenConfiguration())
    }

    /// Keeps the log from growing without bound: start fresh once it passes 1 MB.
    static func trimLog() {
        if let size = (try? FileManager.default.attributesOfItem(atPath: logURL.path))?[.size] as? Int, size > 1_000_000 {
            try? FileManager.default.removeItem(at: logURL)
        }
    }

    private static func waitForOtherServicesToExit(exe: URL) {
        for _ in 0..<20 {
            guard NSRunningApplication.runningApplications(withBundleIdentifier: lanMouseID).isEmpty,
                  !serviceRunning(exe: exe) else { usleep(250_000); continue }
            return
        }
    }

    /// A `lan-mouse daemon` from this exact executable (e.g. one the Lan Mouse window started).
    private static func serviceRunning(exe: URL) -> Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        p.arguments = ["-f", "^" + NSRegularExpression.escapedPattern(for: exe.path) + " daemon$"]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return false }
        p.waitUntilExit()
        return p.terminationStatus == 0
    }

    private static func launchctl(_ args: [String]) -> (status: Int32, text: String)? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return (p.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}

/// UI state for the sharing switch. launchctl and the hand-over wait run off the main thread.
@MainActor
final class SharingModel: ObservableObject {
    static let shared = SharingModel()
    @Published private(set) var state: InputSharing.State = .off
    @Published private(set) var busy = false
    @Published var error: String?
    private let q = DispatchQueue(label: "com.biswashost.bhdisplay.sharing")

    var isOn: Bool { if case .running = state { return true }; return state == .starting }
    var statusText: String {
        switch state {
        case .notInstalled: return "Needs the free Lan Mouse app"
        case .off: return "Off"
        case .starting: return "Starting…"
        case .running: return "On — keyboard & mouse cross to the other computer at the screen edge"
        }
    }

    func refresh() {
        q.async { let s = InputSharing.state(); DispatchQueue.main.async { self.state = s } }
    }

    func set(_ on: Bool) {
        guard !busy else { return }
        busy = true; error = nil
        q.async {
            var err: String?
            if on { do { try InputSharing.enable() } catch { err = error.localizedDescription } }
            else { InputSharing.disable() }
            usleep(800_000)                                   // give launchd a moment before reporting
            let s = InputSharing.state()
            DispatchQueue.main.async { self.busy = false; self.error = err; self.state = s }
        }
    }
}
