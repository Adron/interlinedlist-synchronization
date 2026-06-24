import Foundation
import Network

/// Reachability the sync engine consults to pause polling while offline. Modeled as a protocol so
/// tests can drive transitions deterministically without a real network interface.
protocol NetworkMonitoring: Sendable {
    var isSatisfied: Bool { get async }
    func start(onChange: @escaping @Sendable (Bool) -> Void) async
    func stop() async
}

/// Production reachability backed by `NWPathMonitor`. Available on macOS 13+.
actor NetworkMonitor: NetworkMonitoring {
    private let monitor: NWPathMonitor
    private let queue = DispatchQueue(label: "com.interlinedlist.sync.network-monitor")
    private var currentlySatisfied = true
    private var handler: (@Sendable (Bool) -> Void)?

    init() {
        monitor = NWPathMonitor()
    }

    var isSatisfied: Bool {
        currentlySatisfied
    }

    func start(onChange: @escaping @Sendable (Bool) -> Void) async {
        handler = onChange
        monitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            Task { await self?.report(satisfied) }
        }
        monitor.start(queue: queue)
    }

    func stop() async {
        monitor.pathUpdateHandler = nil
        monitor.cancel()
        handler = nil
    }

    private func report(_ satisfied: Bool) {
        guard satisfied != currentlySatisfied else { return }
        currentlySatisfied = satisfied
        handler?(satisfied)
    }
}

/// Deterministic monitor for tests: starts in a fixed reachability state and lets the test push
/// transitions to exercise pause/resume without a live interface.
actor StubNetworkMonitor: NetworkMonitoring {
    private var currentlySatisfied: Bool
    private var handler: (@Sendable (Bool) -> Void)?

    init(satisfied: Bool = true) {
        currentlySatisfied = satisfied
    }

    var isSatisfied: Bool {
        currentlySatisfied
    }

    var isStarted: Bool {
        handler != nil
    }

    func start(onChange: @escaping @Sendable (Bool) -> Void) async {
        handler = onChange
    }

    func stop() async {
        handler = nil
    }

    func simulate(satisfied: Bool) {
        guard satisfied != currentlySatisfied else { return }
        currentlySatisfied = satisfied
        handler?(satisfied)
    }
}
