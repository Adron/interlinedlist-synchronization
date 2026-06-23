import Foundation
import UserNotifications

/// Posts system notifications for sync events. Authorization is requested just-in-time on the
/// first event rather than at launch, per Apple's HIG. All posting is gated behind the user's
/// `notificationsEnabled` preference. The underlying notification center is injected so the
/// posting path is exercised in tests without scheduling real system notifications.
actor NotificationManager {
    private let center: UserNotificationScheduling
    private let isEnabled: @Sendable () -> Bool

    private var didRequestAuthorization = false

    init(
        center: UserNotificationScheduling,
        isEnabled: @escaping @Sendable () -> Bool
    ) {
        self.center = center
        self.isEnabled = isEnabled
    }

    func notifySyncCompleted(documentsChanged count: Int) async {
        guard count > 0 else { return }
        let body = count == 1
            ? "1 document synced."
            : "\(count) documents synced."
        await post(title: "Sync complete", body: body, identifier: "sync.completed")
    }

    func notifySyncFailed(message: String) async {
        await post(title: "Sync failed", body: message, identifier: "sync.failed")
    }

    func notifyConflictCopyCreated(count: Int) async {
        guard count > 0 else { return }
        let body = count == 1
            ? "A conflicting local edit was saved as a copy."
            : "\(count) conflicting local edits were saved as copies."
        await post(title: "Sync conflict", body: body, identifier: "sync.conflict")
    }

    private func post(title: String, body: String, identifier: String) async {
        guard isEnabled() else { return }
        guard await ensureAuthorized() else { return }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body

        let request = UNNotificationRequest(
            identifier: "\(identifier).\(UUID().uuidString)",
            content: content,
            trigger: nil
        )

        try? await center.add(request)
    }

    private func ensureAuthorized() async -> Bool {
        let status = await center.currentAuthorizationStatus()
        switch status {
        case .authorized, .provisional:
            return true
        case .denied:
            return false
        case .notDetermined:
            guard !didRequestAuthorization else { return false }
            didRequestAuthorization = true
            return (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
        @unknown default:
            return false
        }
    }
}
