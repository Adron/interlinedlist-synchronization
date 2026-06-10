import Foundation

enum ConflictResolution: Sendable, Equatable {
    case useLocal
    case useRemote
}

protocol ConflictResolver: Sendable {
    func resolve(local: DocumentDTO, remote: DocumentDTO) -> ConflictResolution
}

struct LastWriteWinsResolver: ConflictResolver {
    func resolve(local: DocumentDTO, remote: DocumentDTO) -> ConflictResolution {
        // TODO: Phase 2 — refine tie-breaking and surface conflicts to the user.
        local.updatedAt >= remote.updatedAt ? .useLocal : .useRemote
    }
}
