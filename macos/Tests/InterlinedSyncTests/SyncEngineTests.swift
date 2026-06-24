import XCTest
@testable import InterlinedSync

final class SyncEngineTests: XCTestCase {
    private var tempDir: URL!
    private var mapper: DocumentMapper!
    private var client: InterlinedListClient!
    private var state: SyncState!
    private var server: FakeServer!

    private let baseURL = URL(string: "https://interlinedlist.com")!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)

        mapper = DocumentMapper(rootURL: tempDir)
        server = FakeServer()
        MockURLProtocol.requestHandler = server.handler()

        client = InterlinedListClient(
            baseURL: baseURL,
            session: MockURLProtocol.makeSession(),
            tokenStorage: MockTokenStorage(token: "test-token")
        )
        state = await SyncState()
    }

    override func tearDown() async throws {
        MockURLProtocol.requestHandler = nil
        try? FileManager.default.removeItem(at: tempDir)
    }

    private func makeEngine() -> SyncEngine {
        SyncEngine(client: client, mapper: mapper, state: state, pollInterval: 999)
    }

    private func makeEngine(notifications: NotificationManager) -> SyncEngine {
        SyncEngine(
            client: client,
            mapper: mapper,
            state: state,
            notifications: notifications,
            pollInterval: 999
        )
    }

    // MARK: - Pull

    func testPull_writesNewRemoteDocumentsToDisk() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Remote One", content: "alpha", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()

        try await engine.runCycle()

        let fileURL = tempDir.appendingPathComponent("Remote One.md")
        XCTAssertEqual(try String(contentsOf: fileURL, encoding: .utf8), "alpha")
        XCTAssertEqual(mapper.documentID(for: fileURL), "r1")
    }

    func testPull_setsLastSyncedAt() async throws {
        server.seed(DocumentDTO(id: "r1", title: "X", content: "y", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()

        await engine.syncNow()

        let synced = await state.lastSyncedAt
        let status = await state.status
        XCTAssertNotNil(synced)
        XCTAssertEqual(status, .idle)
    }

    func testPull_remoteDeletion_removesLocalFile() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Doomed", content: "z", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()
        XCTAssertTrue(FileManager.default.fileExists(atPath: tempDir.appendingPathComponent("Doomed.md").path))

        server.remove(id: "r1")
        try await engine.runCycle()

        XCTAssertFalse(FileManager.default.fileExists(atPath: tempDir.appendingPathComponent("Doomed.md").path))
    }

    // MARK: - Push

    func testPush_newLocalFile_isCreatedOnServer() async throws {
        let fileURL = tempDir.appendingPathComponent("Brand New.md")
        try "fresh content".write(to: fileURL, atomically: true, encoding: .utf8)

        let engine = makeEngine()
        try await engine.runCycle()

        let created = server.documents.values.first { $0.title == "Brand New" }
        XCTAssertNotNil(created, "Expected the new local file to be POSTed")
        XCTAssertEqual(created?.content, "fresh content")
        XCTAssertEqual(server.createCount, 1)
    }

    func testPush_localEdit_putsToServer() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Editable", content: "old", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()

        let fileURL = tempDir.appendingPathComponent("Editable.md")
        try editInPlace(fileURL, body: "edited locally", modifiedAt: date(100))

        try await engine.runCycle()

        XCTAssertEqual(server.documents["r1"]?.content, "edited locally")
        XCTAssertGreaterThanOrEqual(server.updateCount, 1)
    }

    func testPush_localDeletion_deletesOnServer() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Removable", content: "x", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()

        try FileManager.default.removeItem(at: tempDir.appendingPathComponent("Removable.md"))
        try await engine.runCycle()

        XCTAssertNil(server.documents["r1"])
        XCTAssertEqual(server.deleteCount, 1)
    }

    // MARK: - Conflict

    func testConflict_writesConflictCopyAndAcceptsRemote() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Shared", content: "original", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()

        let fileURL = tempDir.appendingPathComponent("Shared.md")
        try editInPlace(fileURL, body: "local edit", modifiedAt: date(200))
        server.update(id: "r1", body: "remote edit", updatedAt: date(300))

        try await engine.runCycle()

        let canonical = try String(contentsOf: fileURL, encoding: .utf8)
        XCTAssertEqual(canonical, "remote edit", "Remote should win as canonical")

        let conflictCopies = try FileManager.default
            .contentsOfDirectory(at: tempDir, includingPropertiesForKeys: nil)
            .filter { $0.lastPathComponent.contains(".conflict-") }
        XCTAssertEqual(conflictCopies.count, 1, "Expected exactly one conflict copy")
        let copyBody = try String(contentsOf: conflictCopies[0], encoding: .utf8)
        XCTAssertEqual(copyBody, "local edit", "Conflict copy should preserve the local edit")
    }

    // MARK: - Delta

    func testSync_usesFullListOnFirstSync() async throws {
        server.seed(DocumentDTO(id: "r1", title: "First", content: "x", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()

        try await engine.runCycle()

        XCTAssertEqual(server.deltaFetchCount, 0, "First sync should use the full list, not the delta endpoint")
        XCTAssertTrue(FileManager.default.fileExists(atPath: tempDir.appendingPathComponent("First.md").path))
    }

    func testSync_persistsLastSyncedAtFromDelta() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Seed", content: "x", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        await engine.syncNow()

        let deltaSyncedAt = Date(timeIntervalSince1970: 5_000_000)
        server.stageDelta(DeltaResponse(syncedAt: deltaSyncedAt, documents: []))

        await engine.syncNow()

        XCTAssertEqual(server.deltaFetchCount, 1, "Second sync should hit the delta endpoint")
        let synced = await state.lastSyncedAt
        XCTAssertEqual(synced, deltaSyncedAt)
    }

    func testSync_appliesDeltaTombstones() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Keep", content: "x", folderId: nil, updatedAt: date(1)))
        server.seed(DocumentDTO(id: "r2", title: "Drop", content: "y", folderId: nil, updatedAt: date(1)))
        let engine = makeEngine()
        await engine.syncNow()

        let keepURL = tempDir.appendingPathComponent("Keep.md")
        let dropURL = tempDir.appendingPathComponent("Drop.md")
        XCTAssertTrue(FileManager.default.fileExists(atPath: keepURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: dropURL.path))

        server.stageDelta(
            DeltaResponse(
                syncedAt: Date(timeIntervalSince1970: 6_000_000),
                documents: [
                    DocumentDelta(
                        id: "r2", title: "Drop", content: nil,
                        folderId: nil, updatedAt: date(2), deleted: true
                    )
                ]
            )
        )

        await engine.syncNow()

        XCTAssertTrue(FileManager.default.fileExists(atPath: keepURL.path), "Untouched doc should remain")
        XCTAssertFalse(FileManager.default.fileExists(atPath: dropURL.path), "Tombstoned doc should be deleted locally")
    }

    // MARK: - Error surfacing

    func testFetchFailure_setsErrorState() async throws {
        server.failNextFetch = true
        let engine = makeEngine()

        await engine.syncNow()

        let status = await state.status
        let message = await state.errorMessage
        if case .error = status {} else { XCTFail("Expected .error, got \(status)") }
        XCTAssertNotNil(message)
    }

    // MARK: - Notifications

    func testSyncNow_notifiesCompletionWhenDocumentsChanged() async throws {
        server.seed(DocumentDTO(id: "r1", title: "New", content: "hello", folderId: nil, updatedAt: date(1)))
        let center = MockNotificationCenter()
        let notifications = NotificationManager(center: center, isEnabled: { true })
        let engine = makeEngine(notifications: notifications)

        await engine.syncNow()

        XCTAssertTrue(center.addedTitles.contains("Sync complete"))
    }

    func testSyncNow_doesNotNotifyWhenNothingChanged() async throws {
        let center = MockNotificationCenter()
        let notifications = NotificationManager(center: center, isEnabled: { true })
        let engine = makeEngine(notifications: notifications)

        await engine.syncNow()

        XCTAssertTrue(center.added.isEmpty)
    }

    func testSyncNow_notifiesOnFailure() async throws {
        server.failNextFetch = true
        let center = MockNotificationCenter()
        let notifications = NotificationManager(center: center, isEnabled: { true })
        let engine = makeEngine(notifications: notifications)

        await engine.syncNow()

        XCTAssertTrue(center.addedTitles.contains("Sync failed"))
    }

    // MARK: - Helpers

    // Anchored near "now" so test-controlled mtimes can be ordered relative to the real
    // wall-clock mtimes that DocumentMapper.write stamps during the first cycle.
    private let clock = Date()

    private func date(_ offset: TimeInterval) -> Date {
        clock.addingTimeInterval(offset)
    }

    /// Rewrites a tracked file's contents in place (non-atomically) so the document-ID xattr and
    /// inode survive — mirroring an editor that saves without replacing the file — then stamps a
    /// deterministic modification date.
    private func editInPlace(_ url: URL, body: String, modifiedAt: Date) throws {
        try body.write(to: url, atomically: false, encoding: .utf8)
        try FileManager.default.setAttributes([.modificationDate: modifiedAt], ofItemAtPath: url.path)
    }
}
