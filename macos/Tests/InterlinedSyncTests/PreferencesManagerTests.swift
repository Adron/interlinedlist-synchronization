import Combine
import XCTest
@testable import InterlinedSync

@MainActor
final class PreferencesManagerTests: XCTestCase {
    override func setUp() async throws {
        UserDefaults.standard.removeObject(forKey: "pollIntervalSeconds")
    }

    override func tearDown() async throws {
        UserDefaults.standard.removeObject(forKey: "pollIntervalSeconds")
    }

    func testDefaultIntervalWhenUnset() {
        let preferences = PreferencesManager()

        XCTAssertEqual(preferences.pollIntervalSeconds, 30)
    }

    func testIntervalClampsBelowMinimum() {
        let preferences = PreferencesManager()

        preferences.pollIntervalSeconds = 1

        XCTAssertEqual(preferences.pollIntervalSeconds, PreferencesManager.minimumPollIntervalSeconds)
    }

    func testIntervalClampsAboveMaximum() {
        let preferences = PreferencesManager()

        preferences.pollIntervalSeconds = 5_000

        XCTAssertEqual(preferences.pollIntervalSeconds, PreferencesManager.maximumPollIntervalSeconds)
    }

    func testIntervalPersistsAcrossInstances() {
        let preferences = PreferencesManager()
        preferences.pollIntervalSeconds = 120

        let reloaded = PreferencesManager()

        XCTAssertEqual(reloaded.pollIntervalSeconds, 120)
    }

    func testNotificationsEnabledDefaultsTrue() {
        UserDefaults.standard.removeObject(forKey: "notificationsEnabled")
        let preferences = PreferencesManager()

        XCTAssertTrue(preferences.notificationsEnabled)
    }

    func testIntervalPublisherEmitsOnChange() {
        let preferences = PreferencesManager()
        var received: [TimeInterval] = []
        let cancellable = preferences.pollIntervalPublisher.sink { received.append($0) }
        defer { cancellable.cancel() }

        preferences.pollIntervalSeconds = 60

        XCTAssertTrue(received.contains(60))
    }
}
