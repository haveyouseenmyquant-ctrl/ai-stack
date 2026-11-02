import Foundation
import UserNotifications

enum Notifier {
    static let center = UNUserNotificationCenter.current()

    enum Category {
        static let detected = "MEETING_DETECTED"
        static let recording = "RECORDING"
        static let ready = "NOTES_READY"
        static let doctorPlan = "DOCTOR_PLAN"
    }

    enum Action {
        static let start = "START"
        static let stop = "STOP"
        static let open = "OPEN"
    }

    static func setup(delegate: UNUserNotificationCenterDelegate) {
        center.delegate = delegate
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Category.detected, actions: [
                UNNotificationAction(identifier: Action.start, title: "Take notes", options: []),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.recording, actions: [
                UNNotificationAction(identifier: Action.stop, title: "Stop", options: []),
                UNNotificationAction(identifier: Action.open, title: "Open notes", options: [.foreground]),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.ready, actions: [
                UNNotificationAction(identifier: Action.open, title: "Open", options: [.foreground]),
            ], intentIdentifiers: []),
            UNNotificationCategory(identifier: Category.doctorPlan, actions: [
                UNNotificationAction(identifier: Action.open, title: "Review", options: [.foreground]),
            ], intentIdentifiers: []),
        ])
    }

    static func requestPermission() async -> Bool {
        (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
    }

    static func authorizationStatus() async -> UNAuthorizationStatus {
        await center.notificationSettings().authorizationStatus
    }

    static func meetingDetected(app: String, title: String?) {
        post(id: "detected", title: "Call detected — \(app)", body: title.map { "\($0) · Click to take notes" } ?? "Click to take notes for this call",
             category: Category.detected, userInfo: [:])
    }

    static func recordingStarted(_ meeting: Meeting) {
        post(id: "recording-\(meeting.id)", title: "Taking notes", body: meeting.title,
             category: Category.recording, userInfo: ["meeting": meeting.id.uuidString], sound: false)
    }

    static func notesReady(_ meeting: Meeting) {
        center.removeDeliveredNotifications(withIdentifiers: ["recording-\(meeting.id)"])
        post(id: "ready-\(meeting.id)", title: "Meeting notes ready", body: meeting.title,
             category: Category.ready, userInfo: ["meeting": meeting.id.uuidString])
    }

    static func notesFailed(_ meeting: Meeting, error: String) {
        post(id: "failed-\(meeting.id)", title: "Couldn't write meeting notes", body: error,
             category: Category.ready, userInfo: ["meeting": meeting.id.uuidString])
    }

    static func doctorPlanReady(_ r: DoctorCheckup.Result) {
        let size = ByteCountFormatter.string(fromByteCount: r.planBytes, countStyle: .file)
        var body = "\(r.planItems) suggestions (\(size)) wait for your OK. Nothing is removed until you approve."
        if r.tier1.freedBytes > 0 {
            body = "Freed \(ByteCountFormatter.string(fromByteCount: r.tier1.freedBytes, countStyle: .file)) without deleting anything. " + body
        }
        post(id: "doctor-plan", title: "Laptop Doctor checkup", body: body, category: Category.doctorPlan,
             userInfo: ["folder": r.folder])
    }

    private static func post(id: String, title: String, body: String, category: String, userInfo: [String: String], sound: Bool = true) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.categoryIdentifier = category
        content.userInfo = userInfo
        if sound { content.sound = .default }
        center.add(UNNotificationRequest(identifier: id, content: content, trigger: nil))
    }
}
