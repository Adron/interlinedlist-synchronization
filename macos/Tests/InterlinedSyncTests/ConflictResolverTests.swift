import XCTest
@testable import InterlinedSync

final class ConflictResolverTests: XCTestCase {
    private var tempDir: URL!
    private var mapper: DocumentMapper!

    override func setUp() async throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        mapper = DocumentMapper(rootURL: tempDir)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: tempDir)
    }

    // MARK: - decide() decision table

    func testDecide_neitherSidePresent_isNoOp() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: false, localExists: false, remoteExists: false
        )
        XCTAssertEqual(decision, .noOp)
    }

    func testDecide_bothPresentBothChanged_isConflictCopy() {
        let decision = ConflictResolver.decide(
            localChanged: true, remoteChanged: true, localExists: true, remoteExists: true
        )
        XCTAssertEqual(decision, .conflictCopy)
    }

    func testDecide_bothPresentOnlyLocalChanged_isPush() {
        let decision = ConflictResolver.decide(
            localChanged: true, remoteChanged: false, localExists: true, remoteExists: true
        )
        XCTAssertEqual(decision, .push)
    }

    func testDecide_bothPresentOnlyRemoteChanged_isPull() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: true, localExists: true, remoteExists: true
        )
        XCTAssertEqual(decision, .pull)
    }

    func testDecide_bothPresentNeitherChanged_isNoOp() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: false, localExists: true, remoteExists: true
        )
        XCTAssertEqual(decision, .noOp)
    }

    func testDecide_localOnlyUnchanged_pullsRemoteDeletion() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: false, localExists: true, remoteExists: false
        )
        XCTAssertEqual(decision, .pull)
    }

    func testDecide_localOnlyChanged_pushesRecreation() {
        let decision = ConflictResolver.decide(
            localChanged: true, remoteChanged: false, localExists: true, remoteExists: false
        )
        XCTAssertEqual(decision, .push)
    }

    func testDecide_remoteOnlyUnchanged_pushesLocalDeletion() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: false, localExists: false, remoteExists: true
        )
        XCTAssertEqual(decision, .push)
    }

    func testDecide_remoteOnlyChanged_pullsRecreation() {
        let decision = ConflictResolver.decide(
            localChanged: false, remoteChanged: true, localExists: false, remoteExists: true
        )
        XCTAssertEqual(decision, .pull)
    }

    // MARK: - RemoteWinsConflictResolver

    func testRemoteWins_keepsRemoteAndWritesConflictCopy() throws {
        // Materialize through the mapper so the file carries the document-ID xattr that
        // RemoteWinsConflictResolver relies on to overwrite the canonical file in place.
        let seed = DocumentDTO(
            id: "doc1", title: "Shared", content: "local edit", folderId: nil,
            updatedAt: Date(timeIntervalSince1970: 1_700_000_000)
        )
        let fileURL = try mapper.write(document: seed)
        let modifiedAt = Date(timeIntervalSince1970: 1_700_000_000)
        try FileManager.default.setAttributes([.modificationDate: modifiedAt], ofItemAtPath: fileURL.path)

        let local = LocalDocument(
            id: "doc1", url: fileURL, title: "Shared", body: "local edit", modifiedAt: modifiedAt
        )
        let remote = DocumentDTO(
            id: "doc1", title: "Shared", content: "remote edit", folderId: nil,
            updatedAt: Date(timeIntervalSince1970: 1_700_000_100)
        )

        let resolution = try RemoteWinsConflictResolver().resolve(local: local, remote: remote, using: mapper)

        guard case let .keptRemote(conflictCopy) = resolution else {
            return XCTFail("Expected keptRemote")
        }
        XCTAssertEqual(try String(contentsOf: fileURL, encoding: .utf8), "remote edit")
        XCTAssertEqual(try String(contentsOf: conflictCopy, encoding: .utf8), "local edit")
        XCTAssertTrue(conflictCopy.lastPathComponent.contains(".conflict-"))
        XCTAssertTrue(conflictCopy.pathExtension == "md")
    }

    func testRemoteWins_conflictCopyNameMatchesConvention() throws {
        let fileURL = tempDir.appendingPathComponent("Notes.md")
        let modifiedAt = Date(timeIntervalSince1970: 1_700_000_000)
        try "x".write(to: fileURL, atomically: true, encoding: .utf8)

        let copy = try mapper.writeConflictCopy(of: fileURL, body: "x", at: modifiedAt)

        let name = copy.lastPathComponent
        XCTAssertTrue(name.hasPrefix("Notes.conflict-"))
        XCTAssertTrue(name.hasSuffix(".md"))
    }
}
