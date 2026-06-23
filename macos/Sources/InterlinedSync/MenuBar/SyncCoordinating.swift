import Foundation

/// The control surface the menu bar needs to drive the sync engine without depending on the
/// `SyncEngine` actor type directly. Keeps `StatusItemController` testable with a lightweight stub.
protocol SyncCoordinating: Sendable {
    func syncNow() async
    func pause() async
    func resume() async
}

extension SyncEngine: SyncCoordinating {}
