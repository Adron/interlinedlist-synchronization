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
        XCTAssertFalse(delta.deleted)
        XCTAssertEqual(delta.content, "body")
    }

    func testDocumentDelta_tombstoneOmitsContent() throws {
        let json = Data("""
        {"id":"d5","title":"Gone","folderId":null,"updatedAt":"1970-01-01T00:00:00Z","deleted":true}
        """.utf8)
        let delta = try decoder.decode(DocumentDelta.self, from: json)
        XCTAssertTrue(delta.deleted)
        XCTAssertNil(delta.content)
    }
}
