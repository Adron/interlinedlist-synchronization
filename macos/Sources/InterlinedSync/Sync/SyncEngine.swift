import Combine
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
    private let networkMonitor: NetworkMonitoring?
    private let gate = DocumentGate()
    private var pollInterval: TimeInterval

    private var ledger: [String: SyncRecord] = [:]
    private var watcher: FSEventsWatcher?
    private var pollTask: Task<Void, Never>?
    private var watchTask: Task<Void, Never>?
    private var reachabilityTask: Task<Void, Never>?
    private var intervalCancellable: AnyCancellable?
    private var isSyncing = false
    private var isOffline = false
    private var rateLimitBackoff: TimeInterval = 0
    private var consecutiveRateLimits = 0

    private static let maxRateLimitBackoff: TimeInterval = 300

    init(
        client: DocumentFetching & DocumentMutating,
        mapper: DocumentMapper,
        resolver: ConflictResolving = RemoteWinsConflictResolver(),
        state: SyncState,
        notifications: NotificationManager? = nil,
        networkMonitor: NetworkMonitoring? = nil,
        pollInterval: TimeInterval = 30
    ) {
        self.client = client
        self.mapper = mapper
        self.resolver = resolver
        self.state = state
        self.notifications = notifications
        self.networkMonitor = networkMonitor
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

        startNetworkMonitoring()
        startPollTimer()
    }

    private func startPollTimer() {
        pollTask = Task { [weak self] in
            guard let self else { return }
            await self.syncNow()
            while !Task.isCancelled {
                let interval = await self.currentPollInterval
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                if Task.isCancelled { return }
                await self.syncNow()
            }
        }
    }

    private func startNetworkMonitoring() {
        guard let networkMonitor else { return }
        let (stream, continuation) = AsyncStream<Bool>.makeStream()
        reachabilityTask = Task { [weak self] in
            for await satisfied in stream {
                await self?.handleReachabilityChange(satisfied: satisfied)
            }
        }
        Task {
            await networkMonitor.start { satisfied in
                continuation.yield(satisfied)
            }
            if await networkMonitor.isSatisfied == false {
                continuation.yield(false)
            }
        }
    }

    private func handleReachabilityChange(satisfied: Bool) async {
        if satisfied {
            guard isOffline else { return }
            isOffline = false
            await state.cameOnline()
            await syncNow()
        } else {
            isOffline = true
            await state.wentOffline()
        }
    }

    /// Test seam: drives the reachability path directly so offline/online transitions can be
    /// asserted deterministically without the poll timer and file watcher racing the state machine.
    func applyReachabilityForTesting(satisfied: Bool) async {
        await handleReachabilityChange(satisfied: satisfied)
    }

    func stop() {
        pollTask?.cancel()
        pollTask = nil
        watchTask?.cancel()
        watchTask = nil
        reachabilityTask?.cancel()
        reachabilityTask = nil
        watcher?.stop()
        watcher = nil
        if let networkMonitor {
            Task { await networkMonitor.stop() }
        }
    }

    /// Clears the in-memory ledger so the next cycle rebuilds from a full server fetch. Auth and
    /// on-disk documents are left untouched.
    func resetLedger() async {
        ledger.removeAll()
        await state.resetSyncMarker()
    }

    /// Subscribes to live changes of the user's poll-interval preference. The publisher is
    /// `@MainActor`-isolated, so updates are bridged back into the actor via a `Task`.
    func bindPollInterval(to publisher: AnyPublisher<TimeInterval, Never>) {
        intervalCancellable = publisher
            .removeDuplicates()
            .sink { [weak self] interval in
                Task { await self?.updatePollInterval(interval) }
            }
    }

    func updatePollInterval(_ interval: TimeInterval) {
        guard interval > 0, interval != pollInterval else { return }
        pollInterval = interval
        guard pollTask != nil else { return }
        reschedulePollTimer()
    }

    var currentPollInterval: TimeInterval {
        pollInterval
    }

    private func reschedulePollTimer() {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                let interval = await self.currentPollInterval
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                if Task.isCancelled { return }
                await self.syncNow()
            }
        }
    }

    func resetRateLimitBackoff() {
        rateLimitBackoff = 0
        consecutiveRateLimits = 0
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
        guard !isOffline else { return }
        isSyncing = true
        defer { isSyncing = false }

        await state.beginSync()
        do {
            let outcome = try await runCycle()
            resetRateLimitBackoff()
            guard !isOffline else { return }
            await state.finishSync(at: outcome.syncedAt ?? Date())
            await reportSuccess(outcome)
        } catch let error as SyncError {
            await handle(error)
        } catch {
            await state.fail(error.localizedDescription)
            await notifications?.notifySyncFailed(message: error.localizedDescription)
        }
    }

    private func handle(_ error: SyncError) async {
        switch error {
        case .authExpired:
            await state.authExpired()
            await notifications?.notifyAuthExpired()
        case .offline:
            isOffline = true
            await state.wentOffline()
        case let .rateLimited(retryAfter):
            await applyRateLimitBackoff(retryAfter: retryAfter)
        default:
            let message = Self.describe(error)
            await state.fail(message)
            await notifications?.notifySyncFailed(message: message)
        }
    }

    private func applyRateLimitBackoff(retryAfter: TimeInterval?) async {
        consecutiveRateLimits += 1
        let delay: TimeInterval
        if let retryAfter {
            delay = retryAfter
        } else {
            let exponential = pow(2.0, Double(consecutiveRateLimits)) * baseRetryDelay
            let jitter = Double.random(in: 0...baseRetryDelay)
            delay = min(exponential + jitter, Self.maxRateLimitBackoff)
        }
        rateLimitBackoff = min(delay, Self.maxRateLimitBackoff)
        await state.fail(Self.describe(.rateLimited(retryAfter: rateLimitBackoff)))

        guard pollTask != nil else { return }
        pollTask?.cancel()
        let backoff = rateLimitBackoff
        pollTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(backoff * 1_000_000_000))
            if Task.isCancelled { return }
            await self?.resumePollAfterBackoff()
        }
    }

    private func resumePollAfterBackoff() async {
        startPollTimer()
    }

    /// The base unit for exponential backoff and jitter when no `Retry-After` header is present.
    private let baseRetryDelay: TimeInterval = 1

    @discardableResult
    func runCycle() async throws -> SyncOutcome {
        if let since = await state.lastSyncedAt {
            return try await runDeltaCycle(since: since)
        }
        return try await runFullCycle()
    }

    private func runFullCycle() async throws -> SyncOutcome {
        let remote = try await client.fetchDocuments()
        let (tracked, untracked) = try loadLocal()

        let changeSet = ChangeSet.compute(
            trackedLocal: tracked,
            untrackedLocal: untracked,
            remote: remote,
            ledger: ledger
        )

        try await applyConflicts(changeSet.conflicts)
        try await applyRemoteChanges(changeSet.remoteChanges)
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

    private func runDeltaCycle(since: Date) async throws -> SyncOutcome {
        let delta = try await client.fetchDelta(since: since)
        let tombstones = delta.documents.filter { $0.isDeleted }
        let updates = delta.documents.filter { !$0.isDeleted }.map(Self.makeDocument)
        let updatedIDs = Set(updates.map { $0.id })

        for tombstone in tombstones {
            try mapper.removeLocalDocument(id: tombstone.id)
            ledger[tombstone.id] = nil
        }

        let (tracked, untracked) = try loadLocal()
        let pull = ChangeSet.compute(
            trackedLocal: tracked.filter { updatedIDs.contains($0.id) },
            untrackedLocal: [],
            remote: updates,
            ledger: ledger.filter { updatedIDs.contains($0.key) }
        )
        try await applyConflicts(pull.conflicts)
        try await applyRemoteChanges(pull.remoteChanges)
        try await applyLocalChanges(pull.localChanges)

        let pushed = try await pushLocalChanges(tracked: tracked, untracked: untracked, skipping: updatedIDs)

        rebuildLedgerForDelta(updates: updates)

        let documentsChanged = tombstones.count
            + pull.localChanges.count
            + pull.remoteChanges.count
            + pull.conflicts.count
            + pushed
        return SyncOutcome(
            documentsChanged: documentsChanged,
            conflictCopiesCreated: pull.conflicts.count,
            syncedAt: delta.syncedAt
        )
    }

    private func pushLocalChanges(
        tracked: [LocalDocument],
        untracked: [(url: URL, title: String, body: String)],
        skipping handled: Set<String>
    ) async throws -> Int {
        var changes: [ChangeSet.LocalChange] = []
        for file in untracked {
            changes.append(.created(url: file.url, title: file.title, body: file.body))
        }
        for local in tracked where !handled.contains(local.id) {
            guard let record = ledger[local.id] else { continue }
            if local.modifiedAt > record.localModifiedAt {
                changes.append(.updated(local))
            }
        }
        let trackedIDs = Set(tracked.map { $0.id })
        for id in ledger.keys where !handled.contains(id) && !trackedIDs.contains(id) {
            changes.append(.deleted(id: id))
        }
        try await applyLocalChanges(changes)
        return changes.count
    }

    private func rebuildLedgerForDelta(updates: [DocumentDTO]) {
        let remoteByID = Dictionary(updates.map { ($0.id, $0) }, uniquingKeysWith: { first, _ in first })
        let tracked = (try? mapper.localDocuments()) ?? []
        let trackedIDs = Set(tracked.map { $0.id })

        for file in tracked {
            let modifiedAt = mapper.modificationDate(of: file.url)
            let remoteUpdatedAt = remoteByID[file.id]?.updatedAt ?? ledger[file.id]?.remoteUpdatedAt ?? modifiedAt
            ledger[file.id] = SyncRecord(remoteUpdatedAt: remoteUpdatedAt, localModifiedAt: modifiedAt)
        }
        for id in ledger.keys where !trackedIDs.contains(id) {
            ledger[id] = nil
        }
    }

    private static func makeDocument(from delta: DocumentDelta) -> DocumentDTO {
        DocumentDTO(
            id: delta.id,
            title: delta.title,
            content: delta.content ?? "",
            folderId: delta.folderId,
            updatedAt: delta.updatedAt
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

    /// Runs per-document work concurrently across documents while the gate keeps any single
    /// document's operations serialized. Operations are dispatched as child tasks; the first thrown
    /// error fails the cycle.
    private func runPerDocument(
        _ ids: [String],
        _ work: @escaping @Sendable (String) async throws -> Void
    ) async throws {
        let gate = self.gate
        try await withThrowingTaskGroup(of: Void.self) { group in
            for id in ids {
                group.addTask {
                    try await gate.run(id) { try await work(id) }
                }
            }
            try await group.waitForAll()
        }
    }

    private func applyConflicts(_ conflicts: [ChangeSet.Conflict]) async throws {
        let byID = Dictionary(conflicts.map { ($0.local.id, $0) }, uniquingKeysWith: { first, _ in first })
        let mapper = self.mapper
        let resolver = self.resolver
        try await runPerDocument(Array(byID.keys)) { id in
            guard let conflict = byID[id] else { return }
            _ = try resolver.resolve(local: conflict.local, remote: conflict.remote, using: mapper)
        }
    }

    private func applyRemoteChanges(_ changes: [ChangeSet.RemoteChange]) async throws {
        let byID = Dictionary(changes.map { (Self.id(of: $0), $0) }, uniquingKeysWith: { first, _ in first })
        let mapper = self.mapper
        try await runPerDocument(Array(byID.keys)) { id in
            guard let change = byID[id] else { return }
            switch change {
            case let .created(document), let .updated(document):
                try mapper.write(document: document)
            case let .deleted(id):
                try mapper.removeLocalDocument(id: id)
            }
        }
    }

    private static func id(of change: ChangeSet.RemoteChange) -> String {
        switch change {
        case let .created(document), let .updated(document): return document.id
        case let .deleted(id): return id
        }
    }

    private func applyLocalChanges(_ changes: [ChangeSet.LocalChange]) async throws {
        let creations = changes.compactMap { change -> ChangeSet.LocalChange? in
            if case .created = change { return change }
            return nil
        }
        for change in creations {
            try await applyLocalCreation(change)
        }

        let keyed = changes.compactMap { change -> (String, ChangeSet.LocalChange)? in
            switch change {
            case let .updated(local): return (local.id, change)
            case let .deleted(id): return (id, change)
            case .created: return nil
            }
        }
        let byID = Dictionary(keyed, uniquingKeysWith: { first, _ in first })
        try await runPerDocument(Array(byID.keys)) { [weak self] id in
            guard let self, let change = byID[id] else { return }
            try await self.applyTrackedLocalChange(change)
        }
    }

    private func applyLocalCreation(_ change: ChangeSet.LocalChange) async throws {
        guard case let .created(url, title, body) = change else { return }
        let created = try await client.createDocument(
            DocumentUpdateRequest(title: title, content: body)
        )
        // Remove the untracked source first so re-materializing it through the mapper
        // (which stamps the new document ID as an xattr) doesn't leave a "-2" duplicate.
        if FileManager.default.fileExists(atPath: url.path) {
            try? FileManager.default.removeItem(at: url)
        }
        try mapper.write(document: created)
    }

    private func applyTrackedLocalChange(_ change: ChangeSet.LocalChange) async throws {
        switch change {
        case let .updated(local):
            if let remote = try await remoteIfNewerThanLedger(for: local.id) {
                _ = try resolver.resolve(local: local, remote: remote, using: mapper)
                ledger[local.id] = SyncRecord(
                    remoteUpdatedAt: remote.updatedAt,
                    localModifiedAt: mapper.modificationDate(of: local.url)
                )
                await notifications?.notifyConflictCopyCreated(count: 1)
            } else {
                let updated = try await client.updateDocument(
                    id: local.id,
                    update: DocumentUpdateRequest(title: local.title, content: local.body)
                )
                ledger[local.id] = SyncRecord(
                    remoteUpdatedAt: updated.updatedAt,
                    localModifiedAt: mapper.modificationDate(of: local.url)
                )
            }
        case let .deleted(id):
            try await client.deleteDocument(id: id)
        case .created:
            break
        }
    }

    /// Returns the remote document when the server's copy is newer than what the ledger recorded at
    /// the last sync — meaning a push would silently clobber a remote edit and must instead route
    /// through the conflict resolver. Returns nil to proceed with a normal `PATCH`.
    private func remoteIfNewerThanLedger(for id: String) async throws -> DocumentDTO? {
        guard let known = ledger[id]?.remoteUpdatedAt else { return nil }
        guard let remote = try await fetchRemoteDocument(id: id) else { return nil }
        return remote.updatedAt > known ? remote : nil
    }

    private func fetchRemoteDocument(id: String) async throws -> DocumentDTO? {
        let remote = try await client.fetchDocuments()
        return remote.first { $0.id == id }
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
        case .authExpired:
            return "Your session expired. Sign in again to keep syncing."
        case .offline:
            return "Offline — sync paused until the network returns."
        case let .rateLimited(retryAfter):
            if let retryAfter {
                return "Rate limited — retrying in \(Int(retryAfter.rounded()))s."
            }
            return "Rate limited — retrying shortly."
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
