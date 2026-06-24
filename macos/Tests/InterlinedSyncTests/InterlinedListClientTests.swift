import XCTest
@testable import InterlinedSync

final class InterlinedListClientTests: XCTestCase {
    private var client: InterlinedListClient!
    private var storage: MockTokenStorage!
    private let baseURL = URL(string: "https://interlinedlist.com")!
    private let encoder = JSONEncoder.interlinedList()
    private let fixtureDate = Date(timeIntervalSince1970: 0)

    override func setUp() async throws {
        storage = MockTokenStorage(token: "test-token")
        client = InterlinedListClient(
            baseURL: baseURL,
            session: MockURLProtocol.makeSession(),
            tokenStorage: storage
        )
    }

    override func tearDown() {
        MockURLProtocol.requestHandler = nil
    }

    // MARK: - fetchDocuments

    func testFetchDocuments_success_returnsDecodedDocuments() async throws {
        let expected = DocumentDTO(id: "1", title: "Note", content: "Hello", folderId: nil, updatedAt: fixtureDate)
        MockURLProtocol.requestHandler = { [encoder] request in
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Authorization"), "Bearer test-token")
            let data = try encoder.encode(DocumentListResponse(documents: [expected]))
            return (self.response(for: request, status: 200), data)
        }
        let docs = try await client.fetchDocuments()
        XCTAssertEqual(docs, [expected])
    }

    func testFetchDocuments_emptyList_returnsEmpty() async throws {
        MockURLProtocol.requestHandler = { [encoder] request in
            let data = try encoder.encode(DocumentListResponse(documents: []))
            return (self.response(for: request, status: 200), data)
        }
        let docs = try await client.fetchDocuments()
        XCTAssertTrue(docs.isEmpty)
    }

    func testFetchDocuments_noToken_throwsNotAuthenticated() async {
        let emptyStorage = MockTokenStorage()
        let clientWithoutToken = InterlinedListClient(
            baseURL: baseURL,
            session: MockURLProtocol.makeSession(),
            tokenStorage: emptyStorage
        )
        await XCTAssertThrowsSyncError(.notAuthenticated) {
            _ = try await clientWithoutToken.fetchDocuments()
        }
    }

    func testFetchDocuments_401_throwsNotAuthenticated() async {
        MockURLProtocol.requestHandler = { request in
            (self.response(for: request, status: 401), Data())
        }
        await XCTAssertThrowsSyncError(.notAuthenticated) {
            _ = try await self.client.fetchDocuments()
        }
    }

    func testFetchDocuments_serverError_throwsNetwork() async {
        MockURLProtocol.requestHandler = { request in
            (self.response(for: request, status: 500), Data())
        }
        await XCTAssertThrowsNetwork {
            _ = try await self.client.fetchDocuments()
        }
    }

    // MARK: - createDocument

    func testCreateDocument_success_returnsDTO() async throws {
        let expected = DocumentDTO(id: "new", title: "Draft", content: "...", folderId: nil, updatedAt: fixtureDate)
        MockURLProtocol.requestHandler = { [encoder] request in
            XCTAssertEqual(request.httpMethod, "POST")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Content-Type"), "application/json")
            let data = try encoder.encode(DocumentEnvelope(message: "Created", document: expected))
            return (self.response(for: request, status: 201), data)
        }
        let doc = try await client.createDocument(DocumentUpdateRequest(title: "Draft", content: "..."))
        XCTAssertEqual(doc, expected)
    }

    func testCreateDocument_sendsCorrectBody() async throws {
        let update = DocumentUpdateRequest(title: "T", content: "B")
        MockURLProtocol.requestHandler = { [encoder] request in
            // URLSession converts httpBody to httpBodyStream inside URLProtocol; read from
            // whichever is available.
            let bodyData = request.httpBody ?? Self.drain(request.httpBodyStream)
            XCTAssertNotNil(bodyData, "Expected a request body")
            if let bodyData {
                let decoded = try JSONDecoder().decode(DocumentUpdateRequest.self, from: bodyData)
                XCTAssertEqual(decoded.title, "T")
                XCTAssertEqual(decoded.content, "B")
            }
            let stub = DocumentDTO(id: "x", title: "T", content: "B", folderId: nil, updatedAt: .distantPast)
            return (self.response(for: request, status: 201), try encoder.encode(DocumentEnvelope(message: nil, document: stub)))
        }
        _ = try await client.createDocument(update)
    }

    private static func drain(_ stream: InputStream?) -> Data? {
        guard let stream else { return nil }
        var result = Data()
        let buf = UnsafeMutablePointer<UInt8>.allocate(capacity: 4096)
        defer { buf.deallocate() }
        stream.open()
        while stream.hasBytesAvailable {
            let n = stream.read(buf, maxLength: 4096)
            if n > 0 { result.append(buf, count: n) }
        }
        stream.close()
        return result.isEmpty ? nil : result
    }

    // MARK: - updateDocument

    func testUpdateDocument_success_returnsDTO() async throws {
        let expected = DocumentDTO(id: "abc", title: "Updated", content: "new", folderId: nil, updatedAt: fixtureDate)
        MockURLProtocol.requestHandler = { [encoder] request in
            XCTAssertEqual(request.httpMethod, "PATCH")
            XCTAssertTrue(request.url?.path.hasSuffix("/abc") ?? false)
            let data = try encoder.encode(DocumentEnvelope(message: "Updated", document: expected))
            return (self.response(for: request, status: 200), data)
        }
        let doc = try await client.updateDocument(
            id: "abc", update: DocumentUpdateRequest(title: "Updated", content: "new")
        )
        XCTAssertEqual(doc, expected)
    }

    // MARK: - fetchDelta

    func testFetchDelta_parsesResponse() async throws {
        let syncedAt = Date(timeIntervalSince1970: 1000)
        let expected = DeltaResponse(
            syncedAt: syncedAt,
            documents: [
                DocumentDelta(
                    id: "d1", title: "Delta One", content: "alpha",
                    folderId: "f1", updatedAt: fixtureDate, deletedAt: nil
                )
            ]
        )
        MockURLProtocol.requestHandler = { [encoder] request in
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.url?.path, "/api/documents/sync")
            return (self.response(for: request, status: 200), try encoder.encode(expected))
        }
        let delta = try await client.fetchDelta(since: Date(timeIntervalSince1970: 0))
        XCTAssertEqual(delta, expected)
    }

    func testFetchDelta_emptySince() async throws {
        MockURLProtocol.requestHandler = { [encoder] request in
            let components = URLComponents(url: request.url!, resolvingAgainstBaseURL: false)
            let hasQuery = components?.queryItems?.contains { $0.name == "lastSyncAt" } ?? false
            XCTAssertFalse(hasQuery, "Expected no lastSyncAt query when since is nil")
            let payload = DeltaResponse(syncedAt: .distantPast, documents: [])
            return (self.response(for: request, status: 200), try encoder.encode(payload))
        }
        let delta = try await client.fetchDelta(since: nil)
        XCTAssertTrue(delta.documents.isEmpty)
    }

    func testFetchDelta_withTombstones() async throws {
        let expected = DeltaResponse(
            syncedAt: Date(timeIntervalSince1970: 2000),
            documents: [
                DocumentDelta(
                    id: "live", title: "Live", content: "body",
                    folderId: nil, updatedAt: fixtureDate, deletedAt: nil
                ),
                DocumentDelta(
                    id: "gone", title: "Gone", content: nil,
                    folderId: nil, updatedAt: fixtureDate, deletedAt: Date(timeIntervalSince1970: 2000)
                )
            ]
        )
        MockURLProtocol.requestHandler = { [encoder] request in
            return (self.response(for: request, status: 200), try encoder.encode(expected))
        }
        let delta = try await client.fetchDelta(since: fixtureDate)
        XCTAssertEqual(delta.documents.count, 2)
        let tombstone = delta.documents.first { $0.isDeleted }
        XCTAssertEqual(tombstone?.id, "gone")
        XCTAssertNil(tombstone?.content)
    }

    // MARK: - deleteDocument

    func testDeleteDocument_204_doesNotThrow() async throws {
        MockURLProtocol.requestHandler = { request in
            XCTAssertEqual(request.httpMethod, "DELETE")
            XCTAssertTrue(request.url?.path.hasSuffix("/xyz") ?? false)
            return (self.response(for: request, status: 204), Data())
        }
        try await client.deleteDocument(id: "xyz")
    }

    func testDeleteDocument_401_throwsNotAuthenticated() async {
        MockURLProtocol.requestHandler = { request in
            (self.response(for: request, status: 401), Data())
        }
        await XCTAssertThrowsSyncError(.notAuthenticated) {
            try await self.client.deleteDocument(id: "xyz")
        }
    }

    // MARK: - Helpers

    private func response(for request: URLRequest, status: Int) -> HTTPURLResponse {
        HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
    }
}

// MARK: - Assertion helpers

private func XCTAssertThrowsSyncError(
    _ expected: SyncError,
    _ block: () async throws -> Void,
    file: StaticString = #filePath, line: UInt = #line
) async {
    do {
        try await block()
        XCTFail("Expected \(expected) but no error was thrown", file: file, line: line)
    } catch let error as SyncError where "\(error)" == "\(expected)" {
        // expected — SyncError cases with associated values don't conform to Equatable
    } catch {
        XCTFail("Expected \(expected), got \(error)", file: file, line: line)
    }
}

private func XCTAssertThrowsNetwork(
    _ block: () async throws -> Void,
    file: StaticString = #filePath, line: UInt = #line
) async {
    do {
        try await block()
        XCTFail("Expected SyncError.network but no error was thrown", file: file, line: line)
    } catch SyncError.network {
        // expected
    } catch {
        XCTFail("Expected SyncError.network, got \(error)", file: file, line: line)
    }
}
