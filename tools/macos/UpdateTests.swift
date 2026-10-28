import Foundation

@main
struct UpdateTests {
    static func main() throws {
        func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
            try require(try condition(), message)
        }
        // Exercise real same-volume atomic exchange, including interruption at
        // the exchange boundary: a complete installed app always remains.
        let directory = files.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent(UUID().uuidString)
        try files.createDirectory(at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? files.removeItem(at: directory) }
        let app = directory.appendingPathComponent("Installed.app"), previous = directory.appendingPathComponent("Previous.app")
        for path in [app, previous] { try files.createDirectory(at: path, withIntermediateDirectories: false) }
        try Data("old".utf8).write(to: app.appendingPathComponent("version"))
        try Data("new".utf8).write(to: previous.appendingPathComponent("version"))
        try swap(app, previous)
        try expect(try String(contentsOf: app.appendingPathComponent("version"), encoding: .utf8) == "new", "Exchange did not install the complete candidate")
        try expect(try String(contentsOf: previous.appendingPathComponent("version"), encoding: .utf8) == "old", "Exchange lost the old bundle")
        try swap(app, previous)
        // Launch failures, failed health confirmation, a stuck process and a
        // failed replacement must each preserve the recoverable prior version.
        for failure in ["none", "exchange", "start", "confirm", "stop", "journal"] {
            var exchanges = 0, starts = 0, restores = 0
            var states: [String] = []
            var failed = false
            do {
                try replaceAndConfirm(exchange: {
                    if failure == "exchange" { throw UpdateFailure(message: "injected exchange failure") }
                    exchanges += 1
                }, start: { () throws -> Int in
                    starts += 1
                    if failure == "start" { throw UpdateFailure(message: "injected restart failure") }
                    return 1
                }, confirm: { _ in
                    if failure == "confirm" || failure == "stop" { throw UpdateFailure(message: "injected health failure") }
                }, stop: { _ in failure != "stop" }, state: { state in
                    if failure == "journal" && state == "awaiting-startup" { throw UpdateFailure(message: "injected journal failure") }
                    states.append(state)
                }, restartPrevious: { restores += 1 })
            } catch { failed = true }
            try expect(failed == (failure != "none"), "Unexpected transaction outcome: " + failure)
            switch failure {
            case "none":
                try expect(exchanges == 1 && starts == 1 && restores == 0 && states.last == "confirmed", "Successful update did not launch exactly once")
            case "exchange":
                try expect(exchanges == 0 && starts == 0 && restores == 0, "Failed replacement tried to restart")
            case "stop":
                try expect(exchanges == 1 && restores == 0, "Recovery replaced a still-running app")
            default:
                try expect(exchanges == 2 && restores == 1 && states.last == "rolled-back", "Failed update was not rolled back")
            }
        }
        // A link must not be accepted as a private staging directory.
        let link = directory.appendingPathComponent("link")
        try files.createSymbolicLink(at: link, withDestinationURL: directory)
        var rejectedLink = false
        do { try safeDirectory(link) } catch { rejectedLink = true }
        try expect(rejectedLink, "A symlink staging directory was accepted")
        // Check native architecture parsing without requiring Xcode tools on
        // users' Macs, including truncated and universal headers.
        let binary = directory.appendingPathComponent("binary")
        for (header, architecture, expected) in [
            ([UInt8]([0xcf,0xfa,0xed,0xfe,0x0c,0,0,1]), "arm64", true),
            ([UInt8]([0xcf,0xfa,0xed,0xfe,7,0,0,1]), "arm64", false),
            ([UInt8]([0xcf,0xfa,0xed,0xfe,7,0,0,1]), "x86_64", true),
            ([UInt8]([0xcf,0xfa]), "arm64", false),
            ([UInt8]([0xca,0xfe,0xba,0xbe,0,0,0,1,1,0,0,0x0c]), "arm64", true),
            ([UInt8]([0xca,0xfe,0xba,0xbe,0xff,0xff,0xff,0xff]), "arm64", false)
        ] {
            try Data(header).write(to: binary)
            try expect(try supportsArchitecture(binary, architecture) == expected, "Incorrect Mach-O architecture result")
        }
        var rejectedSignature = false
        do { _ = try signature(binary, team: "ABCDEFGHIJ") } catch { rejectedSignature = true }
        try expect(rejectedSignature, "Unsigned update code was accepted")
        print("macOS atomic replacement, recovery, signatures and architecture tests passed.")
    }
}
