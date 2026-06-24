import Foundation

/// The decision a conflict strategy reaches for a single document that changed on both sides.
enum ConflictResolution: Sendable, Equatable {
    /// The remote copy is canonical; the local edit was preserved as a side copy.
    case keptRemote(conflictCopy: URL)
    /// The local copy is canonical and should be pushed to the server.
    case keptLocal
}

/// The action a single document needs given which sides changed since the last sync. Pure and
/// state-free so the decision table can be exercised directly without touching disk or the network.
enum ConflictDecision: Sendable, Equatable {
    case noOp
    case push
    case pull
    case conflictCopy
}

/// Namespace for the pure conflict decision table, kept separate from the side-effecting
/// `ConflictResolving` strategies so the routing logic is unit-testable in isolation.
enum ConflictResolver {
    static func decide(
        localChanged: Bool,
        remoteChanged: Bool,
        localExists: Bool,
        remoteExists: Bool
    ) -> ConflictDecision {
        switch (localExists, remoteExists) {
        case (false, false):
            return .noOp
        case (true, false):
            // Gone from the server. A local change means the user re-created it; push. Otherwise
            // the server deleted it and the local copy should follow on pull.
            return localChanged ? .push : .pull
        case (false, true):
            // Gone from disk. A remote change means the server re-created it; pull. Otherwise the
            // user deleted it locally and that deletion should push.
            return remoteChanged ? .pull : .push
        case (true, true):
            switch (localChanged, remoteChanged) {
            case (true, true): return .conflictCopy
            case (true, false): return .push
            case (false, true): return .pull
            case (false, false): return .noOp
            }
        }
    }
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
