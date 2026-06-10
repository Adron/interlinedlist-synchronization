import Foundation

// TODO: Phase 2 — populate from diffing local files against remote documents.

struct ChangeSet: Sendable, Equatable {
    enum Change: Sendable, Equatable {
        case created(DocumentDTO)
        case updated(DocumentDTO)
        case deleted(id: String)
    }

    var localChanges: [Change]
    var remoteChanges: [Change]

    static let empty = ChangeSet(localChanges: [], remoteChanges: [])

    var isEmpty: Bool {
        localChanges.isEmpty && remoteChanges.isEmpty
    }
}
