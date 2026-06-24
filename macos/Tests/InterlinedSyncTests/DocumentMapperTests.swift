import XCTest
@testable import InterlinedSync

final class DocumentMapperTests: XCTestCase {
    private var tempDir: URL!
    private var mapper: DocumentMapper!

    override func setUpWithError() throws {
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        mapper = DocumentMapper(rootURL: tempDir)
    }

    override func tearDownWithError() throws {
        try FileManager.default.removeItem(at: tempDir)
    }

    // MARK: - write

    func testWrite_createsFileWithDocumentBody() throws {
        let doc = makeDocument(id: "1", title: "Hello", body: "world content")
        let url = try mapper.write(document: doc)
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), "world content")
    }

    func testWrite_storesDocumentIDAsXattr() throws {
        let doc = makeDocument(id: "doc-42", title: "Note")
        let url = try mapper.write(document: doc)
        XCTAssertEqual(mapper.documentID(for: url), "doc-42")
    }

    func testWrite_filenameUsedSanitizedTitle() throws {
        let doc = makeDocument(id: "1", title: "My Note")
        let url = try mapper.write(document: doc)
        XCTAssertEqual(url.lastPathComponent, "My Note.md")
    }

    func testWrite_sanitizesSlashInTitle() throws {
        let doc = makeDocument(id: "1", title: "A/B")
        let url = try mapper.write(document: doc)
        XCTAssertFalse(url.lastPathComponent.contains("/"))
    }

    func testWrite_sanitizesColonInTitle() throws {
        let doc = makeDocument(id: "1", title: "Time: Now")
        let url = try mapper.write(document: doc)
        XCTAssertFalse(url.lastPathComponent.contains(":"))
    }

    func testWrite_emptyTitle_usesUntitled() throws {
        let doc = makeDocument(id: "1", title: "")
        let url = try mapper.write(document: doc)
        XCTAssertEqual(url.lastPathComponent, "untitled.md")
    }

    func testWrite_titleOver200Chars_truncates() throws {
        let long = String(repeating: "a", count: 250)
        let doc = makeDocument(id: "1", title: long)
        let url = try mapper.write(document: doc)
        XCTAssertLessThanOrEqual(url.deletingPathExtension().lastPathComponent.count, 200)
    }

    // MARK: - collision handling

    func testWrite_collidingTitles_appendsCounter() throws {
        let doc1 = makeDocument(id: "id-1", title: "Note")
        let doc2 = makeDocument(id: "id-2", title: "Note")
        let url1 = try mapper.write(document: doc1)
        let url2 = try mapper.write(document: doc2)
        XCTAssertNotEqual(url1, url2)
        XCTAssertEqual(url2.lastPathComponent, "Note-2.md")
    }

    func testWrite_rewriteSameDocument_doesNotCollideWithItself() throws {
        let doc = makeDocument(id: "id-1", title: "Note")
        let url1 = try mapper.write(document: doc)
        let url2 = try mapper.write(document: doc)
        XCTAssertEqual(url1, url2)
    }

    // MARK: - title rename

    func testWrite_titleChangedOnServer_renamesLocalFile() throws {
        let original = makeDocument(id: "id-1", title: "Original")
        let renamed = makeDocument(id: "id-1", title: "Renamed")
        _ = try mapper.write(document: original)
        let renamedURL = try mapper.write(document: renamed)
        XCTAssertTrue(renamedURL.lastPathComponent.hasPrefix("Renamed"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: tempDir.appendingPathComponent("Original.md").path))
    }

    func testWrite_titleRenamePreservesDocumentID() throws {
        let original = makeDocument(id: "id-1", title: "Before")
        let renamed = makeDocument(id: "id-1", title: "After")
        _ = try mapper.write(document: original)
        let url = try mapper.write(document: renamed)
        XCTAssertEqual(mapper.documentID(for: url), "id-1")
    }

    // MARK: - localDocuments

    func testLocalDocuments_returnsAllTrackedFiles() throws {
        _ = try mapper.write(document: makeDocument(id: "a", title: "A"))
        _ = try mapper.write(document: makeDocument(id: "b", title: "B"))
        let all = try mapper.localDocuments()
        XCTAssertEqual(Set(all.map(\.id)), ["a", "b"])
    }

    func testLocalDocuments_ignoresFilesWithoutXattr() throws {
        _ = try mapper.write(document: makeDocument(id: "tracked", title: "Tracked"))
        FileManager.default.createFile(atPath: tempDir.appendingPathComponent("untracked.md").path, contents: Data())
        let all = try mapper.localDocuments()
        XCTAssertEqual(all.map(\.id), ["tracked"])
    }

    // MARK: - documentID

    func testDocumentID_missingXattr_returnsNil() {
        let url = tempDir.appendingPathComponent("no-xattr.md")
        FileManager.default.createFile(atPath: url.path, contents: Data())
        XCTAssertNil(mapper.documentID(for: url))
    }

    // MARK: - Helpers

    private func makeDocument(id: String, title: String, body: String = "") -> DocumentDTO {
        DocumentDTO(id: id, title: title, content: body, folderId: nil, updatedAt: Date(timeIntervalSince1970: 0))
    }
}
