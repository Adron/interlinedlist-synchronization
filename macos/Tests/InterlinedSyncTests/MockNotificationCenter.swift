import UserNotifications
@testable import InterlinedSync

final class MockNotificationCenter: UserNotificationScheduling, @unchecked Sendable {
    private let lock = NSLock()

    private var _status: UNAuthorizationStatus
    private var _authorizationGranted: Bool
    private var _requestCount = 0
    private var _added: [UNNotificationRequest] = []

    init(status: UNAuthorizationStatus = .authorized, authorizationGranted: Bool = true) {
        self._status = status
        self._authorizationGranted = authorizationGranted
    }

    var requestCount: Int {
        lock.lock(); defer { lock.unlock() }
        return _requestCount
    }

    var added: [UNNotificationRequest] {
        lock.lock(); defer { lock.unlock() }
        return _added
    }

    var addedTitles: [String] {
        added.map { $0.content.title }
    }

    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        _requestCount += 1
        if _authorizationGranted {
            _status = .authorized
        } else {
            _status = .denied
        }
        return _authorizationGranted
    }

    func currentAuthorizationStatus() async -> UNAuthorizationStatus {
        lock.lock(); defer { lock.unlock() }
        return _status
    }

    func add(_ request: UNNotificationRequest) async throws {
        lock.lock(); defer { lock.unlock() }
        _added.append(request)
    }
}
