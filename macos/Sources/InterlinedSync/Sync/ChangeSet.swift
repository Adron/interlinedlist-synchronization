import Foundation

/// What the engine recorded about a document the last time it synced successfully. Used to
/// distinguish "changed since last sync" from "unchanged" on both sides.
struct SyncRecord: Sendable, Equatable {
    let remoteUpdatedAt: Date
    let localModifiedAt: Date
}

/// A pure diff between the local sync folder and the remote document list. Correlation is by
/// document ID (xattr); locally-created files that carry no ID are treated as brand-new uploads.
struct ChangeSet: Sendable, Equatable {
    /// A document changed on both sides since the last sync and needs conflict resolution.
    struct Conflict: Sendable, Equatable {
        let local: LocalDocument
        let remote: DocumentDTO
    }

    enum LocalChange: Sendable, Equatable {
        /// A new `.md` file with no document ID yet — POST to the server.
        case created(url: URL, title: String, body: String)
        /// A tracked file whose contents changed locally — PATCH to the server.
        case updated(LocalDocument)
        /// A tracked file removed locally — DELETE on the server.
        case deleted(id: String)
    }

    enum RemoteChange: Sendable, Equatable {
        /// A document present on the server but not on disk — write it locally.
        case created(DocumentDTO)
        /// A document whose server copy is newer than the last sync — overwrite local copy.
        case updated(DocumentDTO)
        /// A document removed on the server — delete the local file.
        case deleted(id: String)
    }

    var localChanges: [LocalChange]
    var remoteChanges: [RemoteChange]
    var conflicts: [Conflict]

    static let empty = ChangeSet(localChanges: [], remoteChanges: [], conflicts: [])

    var isEmpty: Bool {
        localChanges.isEmpty && remoteChanges.isEmpty && conflicts.isEmpty
    }

    /// Diffs disk against server.
    ///
    /// - Parameters:
    ///   - trackedLocal: local `.md` files that carry a document ID.
    ///   - untrackedLocal: local `.md` files with no document ID (new local documents).
    ///   - remote: documents returned by the server.
    ///   - ledger: per-ID snapshot of the previous successful sync; empty on first sync.
    static func compute(
        trackedLocal: [LocalDocument],
        untrackedLocal: [(url: URL, title: String, body: String)],
        remote: [DocumentDTO],
        ledger: [String: SyncRecord]
    ) -> ChangeSet {
        let localByID = Dictionary(trackedLocal.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let remoteByID = Dictionary(remote.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })

        var localChanges: [LocalChange] = []
        var remoteChanges: [RemoteChange] = []
        var conflicts: [Conflict] = []

        for file in untrackedLocal {
            localChanges.append(.created(url: file.url, title: file.title, body: file.body))
        }

        let allIDs = Set(localByID.keys).union(remoteByID.keys).union(ledger.keys)

        for id in allIDs.sorted() {
            let local = localByID[id]
            let remote = remoteByID[id]
            let record = ledger[id]

            switch (local, remote, record) {
            case let (local?, remote?, record?):
                let localChanged = local.modifiedAt > record.localModifiedAt
                let remoteChanged = remote.updatedAt > record.remoteUpdatedAt
                switch (localChanged, remoteChanged) {
                case (true, true):
                    conflicts.append(Conflict(local: local, remote: remote))
                case (true, false):
                    localChanges.append(.updated(local))
                case (false, true):
                    remoteChanges.append(.updated(remote))
                case (false, false):
                    break
                }

            case let (local?, remote?, nil):
                if local.modifiedAt > remote.updatedAt {
                    localChanges.append(.updated(local))
                } else if remote.updatedAt > local.modifiedAt {
                    remoteChanges.append(.updated(remote))
                }

            case (_?, nil, _?):
                // Present locally and in the ledger but gone from the server: the server deleted
                // it, so remove the local file.
                remoteChanges.append(.deleted(id: id))

            case let (local?, nil, nil):
                localChanges.append(.updated(local))

            case (nil, _?, _?):
                // In the ledger and still on the server but gone from disk: the user deleted the
                // local file, so delete it on the server.
                localChanges.append(.deleted(id: id))

            case let (nil, remote?, nil):
                remoteChanges.append(.created(remote))

            case (nil, nil, _):
                break
            }
        }

        return ChangeSet(localChanges: localChanges, remoteChanges: remoteChanges, conflicts: conflicts)
    }
}
