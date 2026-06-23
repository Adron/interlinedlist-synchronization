import UserNotifications

/// The slice of `UNUserNotificationCenter` the app relies on, expressed as a protocol so the
/// notification posting path is testable without touching the system notification center.
protocol UserNotificationScheduling: Sendable {
    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool
    func currentAuthorizationStatus() async -> UNAuthorizationStatus
    func add(_ request: UNNotificationRequest) async throws
}

extension UNUserNotificationCenter: UserNotificationScheduling {
    func currentAuthorizationStatus() async -> UNAuthorizationStatus {
        await notificationSettings().authorizationStatus
    }
}
