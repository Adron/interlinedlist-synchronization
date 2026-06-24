import XCTest
@testable import InterlinedSync

final class NotificationManagerTests: XCTestCase {
    private func makeManager(
        center: MockNotificationCenter,
        enabled: Bool = true
    ) -> NotificationManager {
        NotificationManager(center: center, isEnabled: { enabled })
    }

    func testSyncCompleted_postsWhenDocumentsChanged() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 3)

        XCTAssertEqual(center.added.count, 1)
        XCTAssertEqual(center.added.first?.content.title, "Sync complete")
        XCTAssertEqual(center.added.first?.content.body, "3 documents synced.")
    }

    func testSyncCompleted_doesNotPostWhenNoDocumentsChanged() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 0)

        XCTAssertTrue(center.added.isEmpty)
    }

    func testSyncCompleted_singularCopy() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 1)

        XCTAssertEqual(center.added.first?.content.body, "1 document synced.")
    }

    func testSyncFailed_postsErrorNotification() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifySyncFailed(message: "Network error: offline")

        XCTAssertEqual(center.added.count, 1)
        XCTAssertEqual(center.added.first?.content.title, "Sync failed")
        XCTAssertEqual(center.added.first?.content.body, "Network error: offline")
    }

    func testConflictCopy_postsWhenCreated() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifyConflictCopyCreated(count: 2)

        XCTAssertEqual(center.added.count, 1)
        XCTAssertEqual(center.added.first?.content.title, "Sync conflict")
    }

    func testConflictCopy_doesNotPostWhenZero() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifyConflictCopyCreated(count: 0)

        XCTAssertTrue(center.added.isEmpty)
    }

    func testDisabledPreference_suppressesNotifications() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center, enabled: false)

        await manager.notifySyncCompleted(documentsChanged: 5)
        await manager.notifySyncFailed(message: "boom")

        XCTAssertTrue(center.added.isEmpty)
        XCTAssertEqual(center.requestCount, 0)
    }

    func testAuthorization_requestedJustInTimeAndOnlyOnce() async {
        let center = MockNotificationCenter(status: .notDetermined, authorizationGranted: true)
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 1)
        await manager.notifySyncCompleted(documentsChanged: 1)

        XCTAssertEqual(center.requestCount, 1)
        XCTAssertEqual(center.added.count, 2)
    }

    func testDeniedAuthorization_doesNotPost() async {
        let center = MockNotificationCenter(status: .denied)
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 1)

        XCTAssertTrue(center.added.isEmpty)
        XCTAssertEqual(center.requestCount, 0)
    }

    func testNotDeterminedThenDenied_doesNotPost() async {
        let center = MockNotificationCenter(status: .notDetermined, authorizationGranted: false)
        let manager = makeManager(center: center)

        await manager.notifySyncCompleted(documentsChanged: 1)

        XCTAssertEqual(center.requestCount, 1)
        XCTAssertTrue(center.added.isEmpty)
    }

    func testDisabledCategory_suppressesOnlyThatCategory() async {
        let center = MockNotificationCenter()
        let manager = NotificationManager(
            center: center,
            isEnabled: { true },
            isCategoryEnabled: { $0 != .completion }
        )

        await manager.notifySyncCompleted(documentsChanged: 2)
        await manager.notifySyncFailed(message: "boom")

        XCTAssertEqual(center.addedTitles, ["Sync failed"])
    }

    func testDisabledConflictCategory_suppressesConflictNotification() async {
        let center = MockNotificationCenter()
        let manager = NotificationManager(
            center: center,
            isEnabled: { true },
            isCategoryEnabled: { $0 != .conflict }
        )

        await manager.notifyConflictCopyCreated(count: 1)

        XCTAssertTrue(center.added.isEmpty)
    }

    func testAuthExpired_postsSignInNotification() async {
        let center = MockNotificationCenter()
        let manager = makeManager(center: center)

        await manager.notifyAuthExpired()

        XCTAssertEqual(center.added.count, 1)
        XCTAssertEqual(center.added.first?.content.title, "Sign in again")
    }

    func testDisabledAuthCategory_suppressesAuthNotification() async {
        let center = MockNotificationCenter()
        let manager = NotificationManager(
            center: center,
            isEnabled: { true },
            isCategoryEnabled: { $0 != .auth }
        )

        await manager.notifyAuthExpired()

        XCTAssertTrue(center.added.isEmpty)
    }
}
