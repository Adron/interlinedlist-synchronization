import XCTest
@testable import InterlinedSync

@MainActor
final class LaunchAgentManagerTests: XCTestCase {
    func testSetEnabledTrueRecordsCall() throws {
        let manager = MockLoginItemManager(initiallyEnabled: false)

        try manager.setEnabled(true)

        XCTAssertEqual(manager.setEnabledCalls, [true])
        XCTAssertTrue(manager.isEnabled)
    }

    func testSetEnabledFalseRecordsCall() throws {
        let manager = MockLoginItemManager(initiallyEnabled: true)

        try manager.setEnabled(false)

        XCTAssertEqual(manager.setEnabledCalls, [false])
        XCTAssertFalse(manager.isEnabled)
    }

    func testSetEnabledPropagatesError() {
        let manager = MockLoginItemManager()
        manager.errorToThrow = MockLoginItemError.registrationFailed

        XCTAssertThrowsError(try manager.setEnabled(true))
        XCTAssertEqual(manager.setEnabledCalls, [true])
        XCTAssertFalse(manager.isEnabled, "State should not flip when registration fails")
    }
}
