import Foundation
import UserNotifications

/// Posts system notifications for sync events. Authorization is requested just-in-time on the
/// first event rather than at launch, per Apple's HIG. All posting is gated behind the user's
/// `notificationsEnabled` preference. The underlying notification center is injected so the
/// posting path is exercised in tests without scheduling real system notifications.
actor NotificationManager {
    enum Category {
        case completion
        case error
        case conflict
        case auth
    }

    private let center: UserNotificationScheduling
    private let isEnabled: @Sendable () -> Bool
    private let isCategoryEnabled: @Sendable (Category) -> Bool

    private var didRequestAuthorization = false

    init(
        center: UserNotificationScheduling,
        isEnabled: @escaping @Sendable () -> Bool,
        isCategoryEnabled: @escaping @Sendable (Category) -> Bool = { _ in true }
    ) {
        self.center = center
        self.isEnabled = isEnabled
        self.isCategoryEnabled = isCategoryEnabled
    }

    func notifySyncCompleted(documentsChanged count: Int) async {
        guard count > 0, isCategoryEnabled(.completion) else { return }
        let body = count == 1
            ? "1 document synced."
            : "\(count) documents synced."
        await post(title: "Sync complete", body: body, identifier: "sync.completed")
    }

    func notifySyncFailed(message: String) async {
        guard isCategoryEnabled(.error) else { return }
        await post(title: "Sync failed", body: message, identifier: "sync.failed")
    }

    func notifyConflictCopyCreated(count: Int) async {
        guard count > 0, isCategoryEnabled(.conflict) else { return }
        let body = count == 1
            ? "A conflicting local edit was saved as a copy."
            : "\(count) conflicting local edits were saved as copies."
        await post(title: "Sync conflict", body: body, identifier: "sync.conflict")
    }

    func notifyAuthExpired() async {
        guard isCategoryEnabled(.auth) else { return }
        await post(
            title: "Sign in again",
            body: "Your session expired. Open Preferences to sign back in and resume syncing.",
            identifier: "sync.auth-expired"
        )
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
