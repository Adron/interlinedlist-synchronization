import XCTest
@testable import InterlinedSync

@MainActor
final class StatusItemControllerTests: XCTestCase {
    private var preferences: PreferencesManager!
    private var state: SyncState!
    private var coordinator: StubSyncCoordinator!
    private var controller: StatusItemController!

    override func setUp() async throws {
        UserDefaults.standard.removePersistentDomain(forName: "StatusItemControllerTests")
        preferences = PreferencesManager()
        preferences.syncEnabled = true
        state = SyncState()
        coordinator = StubSyncCoordinator()
        controller = StatusItemController(
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

    func testRenderLastSyncedAtUsesRelativeFormatting() {
        let fiveMinutesAgo = Date().addingTimeInterval(-300)
        controller.render(status: .idle, lastSyncedAt: fiveMinutesAgo)

        XCTAssertNotEqual(controller.lastSyncedMenuItem.title, "Last synced: Never")
        XCTAssertTrue(controller.lastSyncedMenuItem.title.hasPrefix("Last synced: "))
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
        XCTAssertEqual(StatusItemController.iconDescriptor(for: .error("x")).symbolName, "exclamationmark.triangle")
    }

    private func pumpMainRunLoop() async {
        await Task.yield()
        RunLoop.main.run(until: Date().addingTimeInterval(0.05))
    }
}

private final class StubSyncCoordinator: SyncCoordinating, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var syncNowCount = 0
    private(set) var pauseCount = 0
    private(set) var resumeCount = 0

    func syncNow() async {
        lock.lock(); defer { lock.unlock() }
        syncNowCount += 1
    }

    func pause() async {
        lock.lock(); defer { lock.unlock() }
        pauseCount += 1
    }

    func resume() async {
        lock.lock(); defer { lock.unlock() }
        resumeCount += 1
    }
}
