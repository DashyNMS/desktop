import Foundation
import UserNotifications

// Loaded into the Avalonia process. Keeping the delegate in the application
// process lets macOS deliver actions even when the main window is hidden.
private typealias Callback = @convention(c) (UnsafePointer<CChar>?) -> Void
private var callback: Callback?

private func emit(_ value: [String: String]) {
    guard let data = try? JSONSerialization.data(withJSONObject: value),
          let json = String(data: data, encoding: .utf8) else { return }
    json.withCString { callback?($0) }
}

private final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate {
    func userNotificationCenter(_ center: UNUserNotificationCenter,
        willPresent notification: UNNotification, withCompletionHandler completion: @escaping (UNNotificationPresentationOptions) -> Void) {
        completion([.banner, .list, .sound])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse, withCompletionHandler completion: @escaping () -> Void) {
        let action = response.actionIdentifier == "acknowledge" ? "acknowledge" : response.notification.request.content.userInfo["defaultAction"] as? String ?? "show"
        if response.actionIdentifier != UNNotificationDismissActionIdentifier {
            emit(["action": action, "alert": response.notification.request.content.userInfo["alert"] as? String ?? ""])
        }
        completion()
    }
}

private let notificationDelegate = NotificationDelegate()

@_cdecl("dashynms_notifications_initialize")
public func initializeNotifications(_ handler: @escaping @convention(c) (UnsafePointer<CChar>?) -> Void) {
    callback = handler
    DispatchQueue.main.async {
        guard Bundle.main.bundleIdentifier != nil, Bundle.main.bundlePath.hasSuffix(".app") else {
            emit(["error": "Native notifications require the installed macOS app bundle."])
            return
        }
        let center = UNUserNotificationCenter.current()
        center.delegate = notificationDelegate
        let acknowledge = UNNotificationAction(identifier: "acknowledge", title: "Acknowledge", options: [])
        center.setNotificationCategories([
            UNNotificationCategory(identifier: "dashynms-alert", actions: [acknowledge], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: "dashynms-info", actions: [], intentIdentifiers: [], options: [])
        ])
    }
}

// Authorisation is requested on the first attempted delivery, never merely
// because the user opened the app or signed in to LibreNMS.
private var commands: [[String: Any]] = []
private var delivering = false

private func deliverNext() {
    guard !delivering, !commands.isEmpty else { return }
    guard Bundle.main.bundleIdentifier != nil, Bundle.main.bundlePath.hasSuffix(".app") else {
        commands.removeAll()
        emit(["error": "Native notifications require the installed macOS app bundle."])
        return
    }
    delivering = true
    let value = commands.removeFirst()
    let center = UNUserNotificationCenter.current()
    let complete = {
        DispatchQueue.main.async {
            delivering = false
            deliverNext()
        }
    }
    if let closing = value["close"] as? String {
        center.removePendingNotificationRequests(withIdentifiers: [closing])
        center.removeDeliveredNotifications(withIdentifiers: [closing])
        complete()
        return
    }
    center.requestAuthorization(options: [.alert, .sound, .badge]) { granted, error in
        guard granted else {
            emit(["error": error?.localizedDescription ?? "Allow notifications for DashyNMS in System Settings > Notifications."])
            complete()
            return
        }
        let content = UNMutableNotificationContent()
        content.title = value["Title"] as? String ?? "DashyNMS"
        content.body = value["Body"] as? String ?? ""
        let id = value["Id"] as? String ?? "preview"
        content.threadIdentifier = id
        content.categoryIdentifier = value["CanAcknowledge"] as? Bool == true ? "dashynms-alert" : "dashynms-info"
        content.userInfo = ["alert": value["Alert"] as? String ?? "", "defaultAction": value["DefaultAction"] as? String ?? "show"]
        if value["PlaySound"] as? Bool == true && value["IsUpdate"] as? Bool != true { content.sound = .default }
        center.add(UNNotificationRequest(identifier: id, content: content, trigger: nil)) { error in
            if let error = error { emit(["error": error.localizedDescription]) }
            complete()
        }
    }
}

@_cdecl("dashynms_notification_send")
public func sendNotification(_ json: UnsafePointer<CChar>?) {
    guard let json = json, let data = String(cString: json).data(using: .utf8),
          let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
    DispatchQueue.main.async { commands.append(value); deliverNext() }
}

@_cdecl("dashynms_notification_close")
public func closeNotification(_ id: UnsafePointer<CChar>?) {
    guard let id = id else { return }
    let value = String(cString: id)
    DispatchQueue.main.async { commands.append(["close": value]); deliverNext() }
}
