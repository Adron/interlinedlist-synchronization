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
    @Published var errorMessage: String?

    func beginSync() {
        status = .syncing
        errorMessage = nil
    }

    func finishSync(at date: Date) {
        status = .idle
        lastSyncedAt = date
        errorMessage = nil
    }

    func fail(_ message: String) {
        status = .error(message)
        errorMessage = message
    }

    func paused() {
        status = .paused
    }
}
