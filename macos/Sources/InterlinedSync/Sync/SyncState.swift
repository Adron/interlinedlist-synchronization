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

/// The result of one completed sync cycle, used to drive just-in-time notifications.
struct SyncOutcome: Sendable, Equatable {
    var documentsChanged: Int
    var conflictCopiesCreated: Int

    static let unchanged = SyncOutcome(documentsChanged: 0, conflictCopiesCreated: 0)

    var hasChanges: Bool {
        documentsChanged > 0 || conflictCopiesCreated > 0
    }
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

    func resumed() {
        if case .paused = status {
            status = .idle
        }
    }
}
