import Foundation
import AppKit
import Security
import CryptoKit
import Darwin

// This tool never elevates. Its transaction is a private sibling of the installed
// bundle, so RENAME_SWAP keeps a complete app at its original path at all times.
struct UpdateFailure: Error { let message: String }
func require(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    if try !condition() { throw UpdateFailure(message: message) }
}
let files = FileManager.default
let bundleID = "net.dashynms.desktop"

@discardableResult
func run(_ executable: String, _ arguments: [String]) throws -> Data {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = arguments
    let output = Pipe()
    process.standardOutput = output
    process.standardError = FileHandle.nullDevice
    try process.run()
    // These system commands produce bounded output. Bound their lifetime too,
    // so a disk-image or Gatekeeper problem cannot hang update preparation.
    if !waitUntil(120, { !process.isRunning }) {
        process.terminate()
        if !waitUntil(5, { !process.isRunning }) { kill(process.processIdentifier, SIGKILL) }
        throw UpdateFailure(message: "macOS timed out while preparing the update. The installed app has been kept.")
    }
    let data = output.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    try require(process.terminationStatus == 0, "macOS could not verify or prepare the update. The installed app has been kept.")
    return data
}
func info(_ app: URL) throws -> [String: Any] {
    let data = try Data(contentsOf: app.appendingPathComponent("Contents/Info.plist"))
    guard let result = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else {
        throw UpdateFailure(message: "The app bundle metadata is invalid.")
    }
    return result
}
func publisherRequirement(_ team: String) throws -> SecRequirement {
    try require(team.range(of: "^[A-Z0-9]{10}$", options: .regularExpression) != nil, "The update publisher is not configured.")
    let rule = "anchor apple generic and certificate leaf[subject.OU] = \"\(team)\" and certificate leaf[field.1.2.840.113635.100.6.1.13] exists and notarized and identifier \"\(bundleID)\""
    var requirement: SecRequirement?
    try require(SecRequirementCreateWithString(rule as CFString, [], &requirement) == errSecSuccess, "The update publisher requirement is invalid.")
    return requirement!
}
func signature(_ app: URL, team: String? = nil) throws -> [String: Any] {
    var code: SecStaticCode?
    try require(SecStaticCodeCreateWithPath(app as CFURL, [], &code) == errSecSuccess, "The app has no valid code signature.")
    let requirement = try team.map { try publisherRequirement($0) }
    let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
    try require(SecStaticCodeCheckValidity(code!, flags, requirement) == errSecSuccess, "The update signature does not match the trusted DashyNMS publisher, or bundle contents have changed.")
    var dictionary: CFDictionary?
    try require(SecCodeCopySigningInformation(code!, SecCSFlags(rawValue: kSecCSSigningInformation), &dictionary) == errSecSuccess, "The app signature cannot be read.")
    return dictionary as? [String: Any] ?? [:]
}
func fingerprint(_ app: URL) throws -> String {
    let signing = try signature(app)
    guard let hash = signing[kSecCodeInfoUnique as String] as? Data else { throw UpdateFailure(message: "The app identity cannot be read.") }
    return hash.base64EncodedString()
}
func trustedTeam(_ app: URL) throws -> String {
    let signing = try signature(app)
    if let team = signing[kSecCodeInfoTeamIdentifier as String] as? String { return team }
    // Ad-hoc development builds can bootstrap only with a publisher explicitly
    // pinned by the packager, never a publisher learned from the download.
    let metadata = try info(app)
    guard let team = metadata["DashyNMSUpdateTeamIdentifier"] as? String,
          team.range(of: "^[A-Z0-9]{10}$", options: .regularExpression) != nil else {
        throw UpdateFailure(message: "This development build has no trusted update publisher. Build it with MACOS_UPDATE_TEAM_ID set to the public Developer ID team identifier, or install the preview manually.")
    }
    return team
}
func safeDirectory(_ path: URL) throws {
    var attributes = stat()
    try require(lstat(path.path, &attributes) == 0 && (attributes.st_mode & S_IFMT) == S_IFDIR && attributes.st_uid == getuid(), "The update folder is not a user-owned directory.")
    try require((attributes.st_mode & 0o077) == 0, "The update folder is not private.")
}
func installedLocation(_ app: URL) throws {
    let parent = app.deletingLastPathComponent()
    let userApps = files.homeDirectoryForCurrentUser.appendingPathComponent("Applications").standardizedFileURL
    try require(app.path == app.resolvingSymlinksInPath().path && app.pathExtension == "app"
        && (parent.path == "/Applications" || parent.path == userApps.path),
        "Install DashyNMS in your user Applications folder (~/Applications), or a writable /Applications folder, before updating. Apps running from a disk image or another location cannot update in place.")
    try require(geteuid() != 0 && files.isWritableFile(atPath: parent.path) && files.isWritableFile(atPath: app.path),
        "DashyNMS cannot replace this installation without administrator access. Move it to ~/Applications or ask your administrator to install the update. The current app has been kept.")
    try require(try info(app)["CFBundleIdentifier"] as? String == bundleID, "The installed app identity is invalid.")
}
func swap(_ first: URL, _ second: URL) throws {
    // RENAME_NOFOLLOW_ANY is not supported with atomic exchange on all
    // supported macOS filesystems. Refuse links explicitly and use the same
    // renameatx_np exchange primitive as Foundation's safe replacement path.
    try require(first.path == first.resolvingSymlinksInPath().path && second.path == second.resolvingSymlinksInPath().path,
        "The update destination contains a symbolic link. The installed app has been kept.")
    let result = renameatx_np(AT_FDCWD, first.path, AT_FDCWD, second.path, UInt32(RENAME_SWAP))
    let failure = errno
    try require(result == 0,
        "This destination does not support safe bundle replacement or is no longer writable (macOS error \(failure)). The previous bundle has been retained.")
}
struct Transaction: Codable {
    let app: String
    let version: String
    let architecture: String
    let team: String
    let previousFingerprint: String
    let candidateFingerprint: String
    let token: String
}
func save<T: Encodable>(_ value: T, at path: URL) throws {
    try JSONEncoder().encode(value).write(to: path, options: .atomic)
}
func load(_ directory: URL) throws -> Transaction {
    try safeDirectory(directory)
    let value = try JSONDecoder().decode(Transaction.self, from: Data(contentsOf: directory.appendingPathComponent("transaction.json")))
    let app = URL(fileURLWithPath: value.app)
    try installedLocation(app)
    try require(directory.deletingLastPathComponent().path == app.deletingLastPathComponent().path
        && directory.lastPathComponent.hasPrefix(".DashyNMS-update-"), "The update transaction location is invalid.")
    try require(try trustedTeam(app) == value.team, "The installed update publisher has changed.")
    return value
}
// Read Mach-O headers directly: /usr/bin/lipo and xcrun are developer-tool
// shims and cannot be required on an ordinary user's Mac.
func supportsArchitecture(_ path: URL, _ architecture: String) throws -> Bool {
    let handle = try FileHandle(forReadingFrom: path)
    defer { try? handle.close() }
    let data = try handle.read(upToCount: 4096) ?? Data()
    func word(_ offset: Int, little: Bool) -> UInt32? {
        guard offset >= 0 && offset + 4 <= data.count else { return nil }
        let bytes = Array(data[offset..<(offset + 4)])
        return little ? UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24
            : UInt32(bytes[3]) | UInt32(bytes[2]) << 8 | UInt32(bytes[1]) << 16 | UInt32(bytes[0]) << 24
    }
    let expected: UInt32 = architecture == "arm64" ? 0x0100000c : 0x01000007
    switch word(0, little: false) {
    case 0xcffaedfe: return word(4, little: true) == expected
    case 0xfeedfacf: return word(4, little: false) == expected
    case 0xcafebabe, 0xcafebabf, 0xbebafeca, 0xbfbafeca:
        let magic = word(0, little: false)!
        let little = magic == 0xbebafeca || magic == 0xbfbafeca
        let stride = magic == 0xcafebabf || magic == 0xbfbafeca ? 32 : 20
        guard let count = word(4, little: little), count <= 64 else { return false }
        return (0..<Int(count)).contains { word(8 + $0 * stride, little: little) == expected }
    default: return false
    }
}
func validateCandidate(_ app: URL, version: String, architecture: String, team: String) throws {
    let metadata = try info(app)
    try require(metadata["CFBundleIdentifier"] as? String == bundleID
        && metadata["DashyNMSReleaseVersion"] as? String == version, "The update does not contain the requested DashyNMS release.")
    _ = try signature(app, team: team)
    try run("/usr/sbin/spctl", ["--assess", "--type", "execute", app.path])
    for executable in ["DashyNMS", "dashynms-login", "dashynms-update", "mcp/dashynms-mcp"] {
        let path = app.appendingPathComponent("Contents/MacOS/" + executable)
        try require(files.isExecutableFile(atPath: path.path), "The update is missing a required executable.")
        try require(try supportsArchitecture(path, architecture), "The update contains code for a different processor.")
    }
    if let entries = files.enumerator(at: app, includingPropertiesForKeys: nil) {
        for case let library as URL in entries where library.pathExtension == "dylib" {
            try require(try supportsArchitecture(library, architecture), "The update contains a native library for a different processor.")
        }
    }
    // This resource is sealed by the bundle signature. Verify it after signing,
    // which changes the native executable bytes during distribution packaging.
    let metadataURL = app.appendingPathComponent("Contents/Resources/mcp/version.json")
    guard let mcp = try JSONSerialization.jsonObject(with: Data(contentsOf: metadataURL)) as? [String: Any],
          let expected = mcp["Sha256"] as? String else { throw UpdateFailure(message: "The update network tools metadata is invalid.") }
    let binary = try Data(contentsOf: app.appendingPathComponent("Contents/MacOS/mcp/dashynms-mcp"), options: .mappedIfSafe)
    let hash = SHA256.hash(data: binary).map { String(format: "%02x", $0) }.joined()
    try require(hash.caseInsensitiveCompare(expected) == .orderedSame, "The update network tools do not match their bundled metadata.")
}
func transactionDirectories(_ app: URL) -> [URL] {
    ((try? files.contentsOfDirectory(at: app.deletingLastPathComponent(), includingPropertiesForKeys: nil)) ?? [])
        .filter { $0.lastPathComponent.hasPrefix(".DashyNMS-update-") && UUID(uuidString: String($0.lastPathComponent.dropFirst(".DashyNMS-update-".count))) != nil }
}
func cleanupConfirmedTransactions(except current: URL, app: URL) {
    // Retain one confirmed prior bundle, and every incomplete transaction. Never
    // remove a recovery copy until a newer update has itself confirmed startup.
    for directory in transactionDirectories(app) where directory != current {
        guard let transaction = try? load(directory), transaction.app == app.path,
              let data = try? Data(contentsOf: directory.appendingPathComponent("state.json")),
              let state = try? JSONSerialization.jsonObject(with: data) as? [String: String], state["state"] == "confirmed" else { continue }
        try? files.removeItem(at: directory)
    }
}
func prepare(app: URL, package: URL, version: String, architecture: String) throws {
    try installedLocation(app)
    try require(["arm64", "x86_64"].contains(architecture), "The update architecture is unsupported.")
    let team = try trustedTeam(app)
    for directory in transactionDirectories(app) {
        guard !files.fileExists(atPath: directory.appendingPathComponent("state.json").path),
              let transaction = try? load(directory), transaction.app == app.path,
              transaction.version == version, transaction.architecture == architecture,
              transaction.previousFingerprint == (try? fingerprint(app)) else { continue }
        try validateCandidate(directory.appendingPathComponent("Previous.app"), version: version, architecture: architecture, team: team)
        print(directory.path)
        return
    }
    let directory = app.deletingLastPathComponent().appendingPathComponent(".DashyNMS-update-" + UUID().uuidString)
    try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
    var completed = false
    defer { if !completed { try? files.removeItem(at: directory) } }
    // Check the filesystem capability before copying an entire bundle.
    let probeA = directory.appendingPathComponent("probe-a"), probeB = directory.appendingPathComponent("probe-b")
    try files.createDirectory(at: probeA, withIntermediateDirectories: false)
    try files.createDirectory(at: probeB, withIntermediateDirectories: false)
    try swap(probeA, probeB)
    try files.removeItem(at: probeA); try files.removeItem(at: probeB)
    let mount = directory.appendingPathComponent("mount")
    try files.createDirectory(at: mount, withIntermediateDirectories: false)
    var mounted = true
    defer { if mounted { _ = try? run("/usr/bin/hdiutil", ["detach", mount.path]) } }
    try run("/usr/bin/hdiutil", ["attach", package.path, "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount.path])
    let candidate = directory.appendingPathComponent("Previous.app")
    try run("/usr/bin/ditto", [mount.appendingPathComponent("DashyNMS.app").path, candidate.path])
    try run("/usr/bin/hdiutil", ["detach", mount.path]); mounted = false
    try files.removeItem(at: mount)
    try validateCandidate(candidate, version: version, architecture: architecture, team: team)
    let value = Transaction(app: app.path, version: version, architecture: architecture, team: team,
        previousFingerprint: try fingerprint(app), candidateFingerprint: try fingerprint(candidate), token: UUID().uuidString)
    try save(value, at: directory.appendingPathComponent("transaction.json"))
    // Execute the currently installed, trusted helper outside the bundle being
    // exchanged. Never run an updater supplied by the candidate before validation.
    let helper = app.appendingPathComponent("Contents/MacOS/dashynms-update")
    try files.copyItem(at: helper, to: directory.appendingPathComponent("apply"))
    completed = true
    print(directory.path)
}
func waitUntil(_ seconds: TimeInterval, _ condition: () -> Bool) -> Bool {
    let deadline = ProcessInfo.processInfo.systemUptime + seconds
    while !condition() {
        if ProcessInfo.processInfo.systemUptime >= deadline { return false }
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.1))
    }
    return true
}
func launch(_ app: URL, environment: [String: String] = [:]) throws -> Process {
    // Launch the exact executable once. Unlike an asynchronous LaunchServices
    // request, Process.run cannot leave a delayed launch pending after a timeout
    // and race a rollback. The normal app startup honours tray/login settings.
    let process = Process()
    process.executableURL = app.appendingPathComponent("Contents/MacOS/DashyNMS")
    process.currentDirectoryURL = files.homeDirectoryForCurrentUser
    process.standardInput = FileHandle.nullDevice
    process.standardOutput = FileHandle.nullDevice
    process.standardError = FileHandle.nullDevice
    var variables = ProcessInfo.processInfo.environment
    variables.removeValue(forKey: "DASHYNMS_UPDATE_DIRECTORY")
    variables.removeValue(forKey: "DASHYNMS_UPDATE_TOKEN")
    process.environment = variables.merging(environment) { _, new in new }
    try process.run()
    return process
}

// The replacement policy is independent of launch/trust mechanisms so failures
// at every boundary can be tested without signing credentials or a real profile.
func replaceAndConfirm<Running>(exchange: () throws -> Void, start: () throws -> Running,
    confirm: (Running) throws -> Void, stop: (Running) -> Bool,
    state: (String) throws -> Void, restartPrevious: () -> Void) throws {
    try state("replacing")
    try exchange()
    var running: Running?
    do {
        try state("awaiting-startup")
        let application = try start()
        running = application
        try confirm(application)
        try state("confirmed")
    } catch {
        if let application = running {
            try require(stop(application), "The updated app could not close for recovery. The previous bundle is retained in the update folder.")
        }
        try exchange()
        try state("rolled-back")
        restartPrevious()
        throw error
    }
}
func acquireLock(_ app: URL) throws -> Int32 {
    let lockPath = app.deletingLastPathComponent().appendingPathComponent(".DashyNMS-update-lock").path
    let descriptor = open(lockPath, O_CREAT | O_RDWR | O_NOFOLLOW, 0o600)
    try require(descriptor >= 0, "The update destination is locked or unavailable.")
    if flock(descriptor, LOCK_EX | LOCK_NB) != 0 {
        close(descriptor)
        throw UpdateFailure(message: "Another DashyNMS update is already running.")
    }
    return descriptor
}
func recover(_ directory: URL) throws {
    let value = try load(directory)
    let app = URL(fileURLWithPath: value.app)
    let lock = try acquireLock(app)
    defer { close(lock) }
    try require(!NSWorkspace.shared.runningApplications.contains { $0.bundleURL?.standardizedFileURL.path == app.standardizedFileURL.path }, "Quit DashyNMS before recovering its previous version.")
    let previous = directory.appendingPathComponent("Previous.app")
    try require(try fingerprint(app) == value.candidateFingerprint && fingerprint(previous) == value.previousFingerprint,
        "These bundles no longer match the recoverable update. No files have been changed.")
    try swap(app, previous)
    try save(["state": "rolled-back"], at: directory.appendingPathComponent("state.json"))
    _ = try launch(app, environment: ["DASHYNMS_UPDATE_DIRECTORY": directory.path, "DASHYNMS_UPDATE_TOKEN": value.token])
}
func apply(directory: URL, parent: Int32) throws {
    let value = try load(directory)
    let app = URL(fileURLWithPath: value.app)
    let previous = directory.appendingPathComponent("Previous.app")
    // Only one helper may replace this app. O_NOFOLLOW refuses an attacker-
    // supplied link, and the lock is kept until confirmation or recovery ends.
    let lock = try acquireLock(app)
    defer { close(lock) }
    try require(try fingerprint(app) == value.previousFingerprint, "The installed app has changed since this update was prepared. Check for updates again.")
    try validateCandidate(previous, version: value.version, architecture: value.architecture, team: value.team)
    let host = NSRunningApplication(processIdentifier: parent)
    try require(host?.bundleURL?.standardizedFileURL.path == app.standardizedFileURL.path && parent == getppid(), "The updater was not started by the installed app.")
    for name in ["handoff", "startup.json"] {
        let path = directory.appendingPathComponent(name)
        if files.fileExists(atPath: path.path) { try files.removeItem(at: path) }
    }
    try save(["state": "waiting"], at: directory.appendingPathComponent("state.json"))
    try Data().write(to: directory.appendingPathComponent("handoff"), options: .atomic)
    try require(waitUntil(60) { host?.isTerminated != false }, "DashyNMS did not finish closing. The current installation has been kept.")
    // Refuse replacement if a login item or a second launch raced shutdown.
    try require(!NSWorkspace.shared.runningApplications.contains { $0.bundleURL?.standardizedFileURL.path == app.standardizedFileURL.path }, "DashyNMS is still running. The current installation has been kept.")
    try require(try fingerprint(app) == value.previousFingerprint, "The installed app changed while closing.")
    try replaceAndConfirm(exchange: { try swap(app, previous) }, start: {
        try launch(app, environment: ["DASHYNMS_UPDATE_DIRECTORY": directory.path, "DASHYNMS_UPDATE_TOKEN": value.token])
    }, confirm: { process in
        let confirmed = waitUntil(120) {
            guard process.isRunning,
                  let data = try? Data(contentsOf: directory.appendingPathComponent("startup.json")),
                  let receipt = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return false }
            return receipt["token"] as? String == value.token && receipt["version"] as? String == value.version
                && (receipt["pid"] as? Int32) == process.processIdentifier
        }
        try require(confirmed, "The updated app did not confirm startup.")
    }, stop: { process in
        if process.isRunning {
            if let application = NSRunningApplication(processIdentifier: process.processIdentifier) { _ = application.terminate() }
            else { process.terminate() }
        }
        return waitUntil(20) { !process.isRunning }
    }, state: { state in
        try save(["state": state], at: directory.appendingPathComponent("state.json"))
    }, restartPrevious: {
        try? save(["state": "failed", "message": "The updated app did not start successfully. DashyNMS restored the previous version."], at: directory.appendingPathComponent("failure.json"))
        _ = try? launch(app, environment: ["DASHYNMS_UPDATE_DIRECTORY": directory.path, "DASHYNMS_UPDATE_TOKEN": value.token])
    })
    cleanupConfirmedTransactions(except: directory, app: app)

}

#if !UPDATE_TESTS
@main
struct UpdateMain {
    static func main() {
        let arguments = Array(CommandLine.arguments.dropFirst())
        do {
            switch arguments.first {
            case "prepare" where arguments.count == 5:
                try prepare(app: URL(fileURLWithPath: arguments[1]), package: URL(fileURLWithPath: arguments[2]), version: arguments[3], architecture: arguments[4])
            case "recover" where arguments.count == 2:
                try recover(URL(fileURLWithPath: arguments[1]))
            case "apply" where arguments.count == 3:
                guard let parent = Int32(arguments[2]) else { throw UpdateFailure(message: "Invalid updater parent process.") }
                try apply(directory: URL(fileURLWithPath: arguments[1]), parent: parent)
            default: throw UpdateFailure(message: "Invalid updater command.")
            }
        } catch {
            let message = (error as? UpdateFailure)?.message ?? "The macOS update could not complete. The installed or previous app bundle has been retained."
            if arguments.first == "apply", arguments.count == 3 {
                let directory = URL(fileURLWithPath: arguments[1])
                if (try? safeDirectory(directory)) != nil { try? save(["state": "failed", "message": message], at: directory.appendingPathComponent("failure.json")) }
            }
            fputs(message + "\n", stderr)
            exit(1)
        }

    }
}
#endif
