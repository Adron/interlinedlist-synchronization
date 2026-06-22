import Foundation

/// The decision a conflict strategy reaches for a single document that changed on both sides.
enum ConflictResolution: Sendable, Equatable {
    /// The remote copy is canonical; the local edit was preserved as a side copy.
    case keptRemote(conflictCopy: URL)
    /// The local copy is canonical and should be pushed to the server.
    case keptLocal
}

/// A strategy for reconciling a document that changed locally and remotely since the last sync.
/// Phase 3 ships only `RemoteWinsConflictResolver`; this seam leaves room for user-selectable
/// strategies in a later phase.
protocol ConflictResolving: Sendable {
    func resolve(
        local: LocalDocument,
        remote: DocumentDTO,
        using mapper: DocumentMapper
    ) throws -> ConflictResolution
}

/// A local `.md` file the engine has correlated to a document ID.
struct LocalDocument: Sendable, Equatable {
    let id: String
    let url: URL
    let title: String
    let body: String
    let modifiedAt: Date
}

/// Phase 3 default: keep the local edit as a `<name>.conflict-<timestamp>.md` copy, then accept
/// the remote document as canonical by overwriting the tracked file.
struct RemoteWinsConflictResolver: ConflictResolving {
    func resolve(
        local: LocalDocument,
        remote: DocumentDTO,
        using mapper: DocumentMapper
    ) throws -> ConflictResolution {
        let conflictCopy = try mapper.writeConflictCopy(
            of: local.url,
            body: local.body,
            at: local.modifiedAt
        )
        try mapper.write(document: remote)
        return .keptRemote(conflictCopy: conflictCopy)
    }
}
