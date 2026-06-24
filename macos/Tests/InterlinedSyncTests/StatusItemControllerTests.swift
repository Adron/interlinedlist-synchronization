import AppKit
import XCTest
@testable import InterlinedSync

@MainActor
final class StatusItemControllerTests: XCTestCase {
    private var preferences: PreferencesManager!
    private var state: SyncState!
    private var coordinator: StubSyncCoordinator!
    private var presenter: MockStatusItemPresenter!
    private var controller: StatusItemController!

    override func setUp() async throws {
        UserDefaults.standard.removePersistentDomain(forName: "StatusItemControllerTests")
        preferences = PreferencesManager()
        preferences.syncEnabled = true
        state = SyncState()
        coordinator = StubSyncCoordinator()
        presenter = MockStatusItemPresenter()
        controller = StatusItemController(
            presenter: presenter,
            preferences: preferences,
            state: state,
            coordinator: coordinator
        )
    }

    func testInitialLabels() {
        XCTAssertEqual(controller.statusMenuItem.title, "Status: Idle")
        XCTAssertEqual(controller.lastSyncedMenuItem.title, "Last synced: Never")
        XCTAssertEqual(controller.pauseResumeMenuItem.title, "Pause Sync")
    }

    func testMenuIsAttachedToPresenter() {
        XCTAssertNotNil(presenter.attachedMenu)
    }

    func testRenderSyncingStatus() {
        controller.render(status: .syncing, lastSyncedAt: nil)

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Syncing…")
        XCTAssertFalse(controller.syncNowMenuItem.isEnabled)
    }

    func testRenderPausedStatus() {
        controller.render(status: .paused, lastSyncedAt: nil)

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Paused")
        XCTAssertEqual(controller.pauseResumeMenuItem.title, "Resume Sync")
        XCTAssertFalse(controller.syncNowMenuItem.isEnabled)
    }

    func testRenderErrorStatus() {
        controller.render(status: .error("offline"), lastSyncedAt: nil)

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Error — offline")
    }

    func testRenderOfflineStatus() {
        controller.render(status: .offline, lastSyncedAt: nil)

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Offline")
        XCTAssertFalse(controller.syncNowMenuItem.isEnabled, "Sync Now is pointless while offline")
        XCTAssertEqual(presenter.lastIcon?.symbolName, "wifi.slash")
    }

    func testRenderAuthExpiredStatus() {
        controller.render(status: .authExpired, lastSyncedAt: nil)

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Sign in required")
        XCTAssertEqual(presenter.lastIcon?.symbolName, "exclamationmark.triangle.fill")
    }

    func testRenderLastSyncedAtUsesRelativeFormatting() {
        let fiveMinutesAgo = Date().addingTimeInterval(-300)
        controller.render(status: .idle, lastSyncedAt: fiveMinutesAgo)

        XCTAssertNotEqual(controller.lastSyncedMenuItem.title, "Last synced: Never")
        XCTAssertTrue(controller.lastSyncedMenuItem.title.hasPrefix("Last synced: "))
    }

    func testRenderForwardsIconToPresenter() {
        controller.render(status: .paused, lastSyncedAt: nil)

        XCTAssertEqual(presenter.lastIcon?.symbolName, "pause.circle")
    }

    func testStateChangeUpdatesMenuViaObservation() async {
        state.status = .syncing
        await pumpMainRunLoop()

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Syncing…")
    }

    func testFinishSyncUpdatesLastSyncedLabel() async {
        state.finishSync(at: Date())
        await pumpMainRunLoop()

        XCTAssertEqual(controller.statusMenuItem.title, "Status: Idle")
        XCTAssertNotEqual(controller.lastSyncedMenuItem.title, "Last synced: Never")
    }

    func testIconDescriptorPerStatus() {
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .idle).symbolName, "arrow.triangle.2.circlepath")
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .syncing).symbolName, "arrow.triangle.2.circlepath")
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .paused).symbolName, "pause.circle")
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .offline).symbolName, "wifi.slash")
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .authExpired).symbolName, "exclamationmark.triangle.fill")
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .error("x")).symbolName, "exclamationmark.triangle")
    }

    private func pumpMainRunLoop() async {
        for _ in 0..<3 {
            await Task.yield()
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
    }
}

@MainActor
private final class MockStatusItemPresenter: StatusItemPresenting {
    private(set) var attachedMenu: NSMenu?
    private(set) var lastIcon: (symbolName: String, accessibilityDescription: String)?

    func attach(menu: NSMenu) {
        attachedMenu = menu
    }

    func setIcon(symbolName: String, accessibilityDescription: String) {
        lastIcon = (symbolName, accessibilityDescription)
    }
}

private actor StubSyncCoordinator: SyncCoordinating {
    private(set) var syncNowCount = 0
    private(set) var pauseCount = 0
    private(set) var resumeCount = 0

    func syncNow() async {
        syncNowCount += 1
    }

    func pause() async {
        pauseCount += 1
    }

    func resume() async {
        resumeCount += 1
    }
}
