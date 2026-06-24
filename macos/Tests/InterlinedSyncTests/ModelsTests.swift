import XCTest
@testable import InterlinedSync

final class ModelsTests: XCTestCase {
    private let decoder = JSONDecoder.interlinedList()

    func testDocumentDTO_decodesFolderIdWhenPresent() throws {
        let json = Data("""
        {"id":"d1","title":"With Folder","content":"body","folderId":"f1","updatedAt":"1970-01-01T00:00:00Z"}
        """.utf8)
        let dto = try decoder.decode(DocumentDTO.self, from: json)
        XCTAssertEqual(dto.folderId, "f1")
        XCTAssertEqual(dto.content, "body")
    }

    func testDocumentDTO_decodesFolderIdWhenNull() throws {
        let json = Data("""
        {"id":"d2","title":"Root Doc","content":"body","folderId":null,"updatedAt":"1970-01-01T00:00:00Z"}
        """.utf8)
        let dto = try decoder.decode(DocumentDTO.self, from: json)
        XCTAssertNil(dto.folderId)
    }

    func testDocumentDTO_decodesFolderIdWhenAbsent() throws {
        let json = Data("""
        {"id":"d3","title":"No Key","content":"body","updatedAt":"1970-01-01T00:00:00Z"}
        """.utf8)
        let dto = try decoder.decode(DocumentDTO.self, from: json)
        XCTAssertNil(dto.folderId)
    }

    func testDocumentDelta_defaultsDeletedToFalseWhenAbsent() throws {
        let json = Data("""
        {"id":"d4","title":"Live","content":"body","folderId":null,"updatedAt":"1970-01-01T00:00:00Z"}
        """.utf8)
        let delta = try decoder.decode(DocumentDelta.self, from: json)
        XCTAssertFalse(delta.isDeleted)
        XCTAssertEqual(delta.content, "body")
    }

    func testDocumentDelta_tombstoneCarriesDeletedAt() throws {
        let json = Data("""
        {"id":"d5","title":"Gone","folderId":null,"updatedAt":"1970-01-01T00:00:00Z","deletedAt":"1970-01-01T00:00:05Z"}
        """.utf8)
        let delta = try decoder.decode(DocumentDelta.self, from: json)
        XCTAssertTrue(delta.isDeleted)
        XCTAssertNil(delta.content)
    }

    func testDocumentDTO_decodesContentHashWhenPresent() throws {
        let json = Data("""
        {"id":"d6","title":"Hashed","content":"body","folderId":null,"updatedAt":"1970-01-01T00:00:00Z","contentHash":"abc123"}
        """.utf8)
        let dto = try decoder.decode(DocumentDTO.self, from: json)
        XCTAssertEqual(dto.contentHash, "abc123")
    }

    func testDeltaResponse_decodesLastSyncAtKey() throws {
        let json = Data("""
        {"lastSyncAt":"1970-01-01T00:00:10Z","documents":[]}
        """.utf8)
        let delta = try decoder.decode(DeltaResponse.self, from: json)
        XCTAssertEqual(delta.syncedAt, Date(timeIntervalSince1970: 10))
        XCTAssertTrue(delta.documents.isEmpty)
    }
}
