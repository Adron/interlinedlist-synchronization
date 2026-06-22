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

    // MARK: - Pull

    func testPull_writesNewRemoteDocumentsToDisk() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Remote One", body: "alpha", updatedAt: date(1)))
        let engine = makeEngine()

        try await engine.runCycle()

        let fileURL = tempDir.appendingPathComponent("Remote One.md")
        XCTAssertEqual(try String(contentsOf: fileURL, encoding: .utf8), "alpha")
        XCTAssertEqual(mapper.documentID(for: fileURL), "r1")
    }

    func testPull_setsLastSyncedAt() async throws {
        server.seed(DocumentDTO(id: "r1", title: "X", body: "y", updatedAt: date(1)))
        let engine = makeEngine()

        await engine.syncNow()

        let synced = await state.lastSyncedAt
        let status = await state.status
        XCTAssertNotNil(synced)
        XCTAssertEqual(status, .idle)
    }

    func testPull_remoteDeletion_removesLocalFile() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Doomed", body: "z", updatedAt: date(1)))
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
        XCTAssertEqual(created?.body, "fresh content")
        XCTAssertEqual(server.createCount, 1)
    }

    func testPush_localEdit_putsToServer() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Editable", body: "old", updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()

        let fileURL = tempDir.appendingPathComponent("Editable.md")
        try editInPlace(fileURL, body: "edited locally", modifiedAt: date(100))

        try await engine.runCycle()

        XCTAssertEqual(server.documents["r1"]?.body, "edited locally")
        XCTAssertGreaterThanOrEqual(server.updateCount, 1)
    }

    func testPush_localDeletion_deletesOnServer() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Removable", body: "x", updatedAt: date(1)))
        let engine = makeEngine()
        try await engine.runCycle()

        try FileManager.default.removeItem(at: tempDir.appendingPathComponent("Removable.md"))
        try await engine.runCycle()

        XCTAssertNil(server.documents["r1"])
        XCTAssertEqual(server.deleteCount, 1)
    }

    // MARK: - Conflict

    func testConflict_writesConflictCopyAndAcceptsRemote() async throws {
        server.seed(DocumentDTO(id: "r1", title: "Shared", body: "original", updatedAt: date(1)))
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
