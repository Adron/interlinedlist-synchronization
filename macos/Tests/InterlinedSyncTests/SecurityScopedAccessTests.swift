import XCTest
@testable import InterlinedSync

@MainActor
final class SecurityScopedAccessTests: XCTestCase {
    /// Records start/stop calls so the reference-count balancing can be asserted without a real
    /// sandbox extension.
    private final class SpyAccessor: SecurityScopedAccessing {
        private(set) var started: [URL] = []
        private(set) var stopped: [URL] = []
        var grant = true

        func startAccessing(_ url: URL) -> Bool {
            started.append(url)
            return grant
        }

        func stopAccessing(_ url: URL) {
            stopped.append(url)
        }
    }

    private let folderA = URL(fileURLWithPath: "/tmp/interlined-folder-a")
    private let folderB = URL(fileURLWithPath: "/tmp/interlined-folder-b")

    func testBeginStartsAccess() {
        let spy = SpyAccessor()
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.beginSecurityScopedAccess(to: folderA)

        XCTAssertEqual(spy.started, [folderA])
    }

    func testBeginIsIdempotentForSameFolder() {
        let spy = SpyAccessor()
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.beginSecurityScopedAccess(to: folderA)
        preferences.beginSecurityScopedAccess(to: folderA)

        XCTAssertEqual(spy.started, [folderA], "Re-resolving the same folder must not open a second extension")
        XCTAssertTrue(spy.stopped.isEmpty)
    }

    func testBeginSwapsWhenFolderChanges() {
        let spy = SpyAccessor()
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.beginSecurityScopedAccess(to: folderA)
        preferences.beginSecurityScopedAccess(to: folderB)

        XCTAssertEqual(spy.started, [folderA, folderB])
        XCTAssertEqual(spy.stopped, [folderA], "Changing folders must release the previous extension")
    }

    func testStopReleasesHeldFolder() {
        let spy = SpyAccessor()
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.beginSecurityScopedAccess(to: folderA)
        preferences.stopSecurityScopedAccess()

        XCTAssertEqual(spy.stopped, [folderA])
    }

    func testStopIsNoOpWhenNothingHeld() {
        let spy = SpyAccessor()
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.stopSecurityScopedAccess()

        XCTAssertTrue(spy.stopped.isEmpty)
    }

    func testFolderDeniedBySandboxIsNotReleased() {
        let spy = SpyAccessor()
        spy.grant = false
        let preferences = PreferencesManager(scopedAccessor: spy)

        preferences.beginSecurityScopedAccess(to: folderA)
        preferences.stopSecurityScopedAccess()

        XCTAssertEqual(spy.started, [folderA])
        XCTAssertTrue(spy.stopped.isEmpty, "A folder the sandbox refused must never be stopped")
    }
}
