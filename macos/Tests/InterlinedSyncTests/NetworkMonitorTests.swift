import XCTest
@testable import InterlinedSync

final class NetworkMonitorTests: XCTestCase {
    func testStub_reportsInitialSatisfiedState() async {
        let monitor = StubNetworkMonitor(satisfied: true)
        let satisfied = await monitor.isSatisfied
        XCTAssertTrue(satisfied)
    }

    func testStub_reportsInitialUnsatisfiedState() async {
        let monitor = StubNetworkMonitor(satisfied: false)
        let satisfied = await monitor.isSatisfied
        XCTAssertFalse(satisfied)
    }

    func testStub_transitionDeliversChangeToHandler() async {
        let monitor = StubNetworkMonitor(satisfied: true)
        let recorder = TransitionRecorder()

        await monitor.start { satisfied in recorder.record(satisfied) }
        await monitor.simulate(satisfied: false)
        await monitor.simulate(satisfied: true)

        XCTAssertEqual(recorder.values, [false, true])
    }

    func testStub_duplicateStateDoesNotNotify() async {
        let monitor = StubNetworkMonitor(satisfied: true)
        let recorder = TransitionRecorder()

        await monitor.start { satisfied in recorder.record(satisfied) }
        await monitor.simulate(satisfied: true)

        XCTAssertTrue(recorder.values.isEmpty)
    }

    func testStub_stopDetachesHandler() async {
        let monitor = StubNetworkMonitor(satisfied: true)
        let recorder = TransitionRecorder()

        await monitor.start { satisfied in recorder.record(satisfied) }
        await monitor.stop()
        await monitor.simulate(satisfied: false)

        XCTAssertTrue(recorder.values.isEmpty)
    }
}

/// Records handler invocations synchronously so call order is preserved for assertions.
private final class TransitionRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [Bool] = []

    func record(_ value: Bool) {
        lock.lock(); defer { lock.unlock() }
        storage.append(value)
    }

    var values: [Bool] {
        lock.lock(); defer { lock.unlock() }
        return storage
    }
}
