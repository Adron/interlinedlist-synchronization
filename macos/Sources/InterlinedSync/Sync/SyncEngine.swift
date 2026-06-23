import Foundation

/// Drives the bidirectional sync cycle: pull remote changes to disk, push local changes to the
/// server, and route documents that changed on both sides through the conflict resolver. Owns the
/// in-memory ledger that records each document's state at the last successful sync so subsequent
/// cycles can tell "changed" from "unchanged".
actor SyncEngine {
    private let client: DocumentFetching & DocumentMutating
    private let mapper: DocumentMapper
    private let resolver: ConflictResolving
    private let state: SyncState
    private let notifications: NotificationManager?
    private let pollInterval: TimeInterval

    private var ledger: [String: SyncRecord] = [:]
    private var watcher: FSEventsWatcher?
    private var pollTask: Task<Void, Never>?
    private var watchTask: Task<Void, Never>?
    private var isSyncing = false

    init(
        client: DocumentFetching & DocumentMutating,
        mapper: DocumentMapper,
        resolver: ConflictResolving = RemoteWinsConflictResolver(),
        state: SyncState,
        notifications: NotificationManager? = nil,
        pollInterval: TimeInterval = 30
    ) {
        self.client = client
        self.mapper = mapper
        self.resolver = resolver
        self.state = state
        self.notifications = notifications
        self.pollInterval = pollInterval
    }

    // MARK: - Lifecycle

    func start() {
        guard pollTask == nil else { return }

        let watcher = FSEventsWatcher(url: mapper.rootURL)
        self.watcher = watcher
        watcher.start()

        watchTask = Task { [weak self] in
            for await _ in watcher.changes {
                guard let self else { return }
                await self.syncNow()
            }
        }

        pollTask = Task { [weak self] in
            guard let self else { return }
            await self.syncNow()
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(self.pollInterval * 1_000_000_000))
                if Task.isCancelled { return }
                await self.syncNow()
            }
        }
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
        watchTask?.cancel()
        watchTask = nil
        watcher?.stop()
        watcher = nil
    }

    func pause() async {
        stop()
        await state.paused()
    }

    func resume() async {
        await state.resumed()
        start()
    }

    // MARK: - One cycle

    func syncNow() async {
        guard !isSyncing else { return }
        isSyncing = true
        defer { isSyncing = false }

        await state.beginSync()
        do {
            let outcome = try await runCycle()
            await state.finishSync(at: Date())
            await reportSuccess(outcome)
        } catch let error as SyncError {
            let message = Self.describe(error)
            await state.fail(message)
            await notifications?.notifySyncFailed(message: message)
        } catch {
            let message = error.localizedDescription
            await state.fail(message)
            await notifications?.notifySyncFailed(message: message)
        }
    }

    @discardableResult
    func runCycle() async throws -> SyncOutcome {
        let remote = try await client.fetchDocuments()
        let (tracked, untracked) = try loadLocal()

        let changeSet = ChangeSet.compute(
            trackedLocal: tracked,
            untrackedLocal: untracked,
            remote: remote,
            ledger: ledger
        )

        try await applyConflicts(changeSet.conflicts)
        try applyRemoteChanges(changeSet.remoteChanges)
        try await applyLocalChanges(changeSet.localChanges)

        rebuildLedger(remote: remote)

        let documentsChanged = changeSet.localChanges.count
            + changeSet.remoteChanges.count
            + changeSet.conflicts.count
        return SyncOutcome(
            documentsChanged: documentsChanged,
            conflictCopiesCreated: changeSet.conflicts.count
        )
    }

    private func reportSuccess(_ outcome: SyncOutcome) async {
        guard let notifications, outcome.hasChanges else { return }
        await notifications.notifyConflictCopyCreated(count: outcome.conflictCopiesCreated)
        await notifications.notifySyncCompleted(documentsChanged: outcome.documentsChanged)
    }

    // MARK: - Local enumeration

    private func loadLocal() throws -> (tracked: [LocalDocument], untracked: [(url: URL, title: String, body: String)]) {
        let trackedFiles = try mapper.localDocuments()
        var tracked: [LocalDocument] = []
        for file in trackedFiles {
            let read = try mapper.read(at: file.url)
            tracked.append(
                LocalDocument(
                    id: file.id,
                    url: file.url,
                    title: mapper.title(for: file.url),
                    body: read.body,
                    modifiedAt: read.modifiedAt
                )
            )
        }

        let trackedPaths = Set(trackedFiles.map { $0.url.resolvingSymlinksInPath().path })
        var untracked: [(url: URL, title: String, body: String)] = []
        let contents = try FileManager.default.contentsOfDirectory(
            at: mapper.rootURL, includingPropertiesForKeys: nil
        )
        for url in contents where url.pathExtension == "md" {
            let resolved = url.resolvingSymlinksInPath().path
            guard !trackedPaths.contains(resolved) else { continue }
            guard !Self.isConflictCopy(url) else { continue }
            let read = try mapper.read(at: url)
            untracked.append((url: url, title: mapper.title(for: url), body: read.body))
        }

        return (tracked, untracked)
    }

    // MARK: - Apply

    private func applyConflicts(_ conflicts: [ChangeSet.Conflict]) async throws {
        for conflict in conflicts {
            _ = try resolver.resolve(local: conflict.local, remote: conflict.remote, using: mapper)
        }
    }

    private func applyRemoteChanges(_ changes: [ChangeSet.RemoteChange]) throws {
        for change in changes {
            switch change {
            case let .created(document), let .updated(document):
                try mapper.write(document: document)
            case let .deleted(id):
                try mapper.deleteLocalFile(id: id)
            }
        }
    }

    private func applyLocalChanges(_ changes: [ChangeSet.LocalChange]) async throws {
        for change in changes {
            switch change {
            case let .created(url, title, body):
                let created = try await client.createDocument(
                    DocumentUpdateRequest(title: title, body: body)
                )
                // Remove the untracked source first so re-materializing it through the mapper
                // (which stamps the new document ID as an xattr) doesn't leave a "-2" duplicate.
                if FileManager.default.fileExists(atPath: url.path) {
                    try? FileManager.default.removeItem(at: url)
                }
                try mapper.write(document: created)
            case let .updated(local):
                _ = try await client.updateDocument(
                    id: local.id,
                    update: DocumentUpdateRequest(title: local.title, body: local.body)
                )
            case let .deleted(id):
                try await client.deleteDocument(id: id)
            }
        }
    }

    private func rebuildLedger(remote: [DocumentDTO]) {
        let remoteByID = Dictionary(remote.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let tracked = (try? mapper.localDocuments()) ?? []

        var rebuilt: [String: SyncRecord] = [:]
        for file in tracked {
            guard let document = remoteByID[file.id] ?? freshlyKnownRemote(file.id, remote) else { continue }
            let modifiedAt = mapper.modificationDate(of: file.url)
            rebuilt[file.id] = SyncRecord(remoteUpdatedAt: document.updatedAt, localModifiedAt: modifiedAt)
        }
        ledger = rebuilt
    }

    private func freshlyKnownRemote(_ id: String, _ remote: [DocumentDTO]) -> DocumentDTO? {
        remote.first { $0.id == id }
    }

    // MARK: - Helpers

    private static func isConflictCopy(_ url: URL) -> Bool {
        url.deletingPathExtension().lastPathComponent.contains(".conflict-")
    }

    private static func describe(_ error: SyncError) -> String {
        switch error {
        case .notImplemented:
            return "Sync is not available."
        case .notAuthenticated:
            return "Sign in to continue syncing."
        case let .network(underlying):
            return "Network error: \(underlying.localizedDescription)"
        case let .fileSystem(underlying):
            return "File error: \(underlying.localizedDescription)"
        case let .conflict(documentID):
            return "Unresolved conflict for document \(documentID)."
        case .mapping:
            return "Could not read the server response."
        }
    }
}
