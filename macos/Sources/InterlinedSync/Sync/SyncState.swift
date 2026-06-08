import Foundation

enum SyncError: Error {
    case notImplemented
    case notAuthenticated
    case network(Error)
    case fileSystem(Error)
    case conflict(documentID: String)
    case mapping
}

enum SyncStatus: Equatable, Sendable {
    case idle
    case syncing
    case paused
    case error(String)
}

@MainActor
final class SyncState: ObservableObject {
    @Published var status: SyncStatus = .idle
    @Published var lastSyncedAt: Date?

    // TODO: Phase 2 — drive these from SyncEngine cycle results.
}
