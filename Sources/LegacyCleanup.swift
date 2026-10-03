import Foundation

/// One-time clean-up of the keyboard & mouse sharing setup an earlier development build created
/// (a per-user launchd job that ran a third-party tool). BHDisplay's sharing is its own code; this only
/// removes the old job so it can't fight BHDisplay for the mouse.
enum LegacyCleanup {
    private static let label = "com.biswashost.bhdisplay.lanmouse"

    static func removeOldSharingJob() {
        let plist = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
        guard FileManager.default.fileExists(atPath: plist.path) else { return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        p.arguments = ["bootout", "gui/\(getuid())/\(label)"]
        p.standardOutput = FileHandle.nullDevice; p.standardError = FileHandle.nullDevice
        try? p.run(); p.waitUntilExit()
        try? FileManager.default.removeItem(at: plist)
        try? FileManager.default.removeItem(at: FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Logs/BHDisplay/lan-mouse.log"))
    }
}
