import Foundation
import ServiceManagement

// Lives in Contents/MacOS alongside DashyNMS, so mainApp is the containing
// application. macOS owns its registration and the Login Items approval UI.
if #available(macOS 13.0, *) {
    let service = SMAppService.mainApp
    do {
        switch CommandLine.arguments.dropFirst().first ?? "status" {
        case "enable":
            if service.status != .enabled { try service.register() }
            if service.status == .requiresApproval {
                fputs("Allow DashyNMS in System Settings > General > Login Items, then try again.\n", stderr)
                exit(2)
            }
            guard service.status == .enabled else {
                fputs("macOS did not enable the DashyNMS login item.\n", stderr)
                exit(1)
            }
        case "disable":
            if service.status != .notRegistered { try service.unregister() }
        case "status":
            print(service.status == .enabled ? "enabled" : "disabled")
        default:
            fputs("Unknown login item command.\n", stderr)
            exit(1)
        }
    } catch {
        fputs("The login item couldn't be changed: \(error.localizedDescription)\n", stderr)
        exit(1)
    }
} else {
    fputs("Login items require macOS 13 or later.\n", stderr)
    exit(1)
}
