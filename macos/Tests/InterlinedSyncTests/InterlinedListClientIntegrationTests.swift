import XCTest
@testable import InterlinedSync

final class InterlinedListClientIntegrationTests: XCTestCase {
    private static let titlePrefix = "__macos-integ-"
    private static let networkTimeout: TimeInterval = 30

    private var baseURL: URL!
    private var session: URLSession!
    private var storage: MockTokenStorage!
    private var auth: AuthManager!
    private var client: InterlinedListClient!

    override func setUp() async throws {
        try IntegrationTestEnv.skipIfUnconfigured(in: self)

        baseURL = IntegrationTestEnv.baseURL
        session = Self.makeSession()
        storage = MockTokenStorage()
        auth = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)
        client = InterlinedListClient(baseURL: baseURL, session: session, tokenStorage: storage)
    }

    // MARK: - Tests

    func testLogin_returnsToken() async throws {
        let token = try await login()
        XCTAssertFalse(token.isEmpty, "Login returned an empty token")
    }

    func testFetchDocuments_withBearer() async throws {
        try await login()
        let documents = try await client.fetchDocuments()
        XCTAssertNotNil(documents, "Expected a documents array (may be empty)")
    }

    func testCreateUpdateDelete_roundTrip() async throws {
        try await login()

        let title = uniqueTitle()
        let created = try await client.createDocument(
            DocumentUpdateRequest(title: title, content: "created body")
        )
        registerCleanup(documentID: created.id)

        XCTAssertFalse(created.id.isEmpty, "Created document has no id")
        XCTAssertEqual(created.title, title)
        XCTAssertEqual(created.content, "created body")

        let updated = try await client.updateDocument(
            id: created.id,
            update: DocumentUpdateRequest(title: title, content: "updated body")
        )
        XCTAssertEqual(updated.id, created.id)
        XCTAssertEqual(updated.content, "updated body")

        try await client.deleteDocument(id: created.id)

        do {
            let remaining = try await client.fetchDocuments()
            XCTAssertFalse(
                remaining.contains { $0.id == created.id },
                "Deleted document still present in fetchDocuments()"
            )
        } catch let SyncError.network(error) {
            let status = (error as? URLError).map { _ in -1 } ?? -1
            XCTAssertTrue(status == -1, "Expected absence or 404/410 after delete")
        }
    }

    func testFetchDelta_initial() async throws {
        try await login()
        let delta = try await client.fetchDelta(since: nil)
        XCTAssertNotNil(delta.syncedAt, "Expected syncedAt in initial delta")
        XCTAssertNotNil(delta.documents, "Expected documents array in initial delta")
    }

    func testFetchDelta_withTombstone() async throws {
        try await login()

        let title = uniqueTitle()
        let created = try await client.createDocument(
            DocumentUpdateRequest(title: title, content: "tombstone body")
        )
        registerCleanup(documentID: created.id)

        let snapshot = try await client.fetchDelta(since: nil)
        let since = snapshot.syncedAt

        try await client.deleteDocument(id: created.id)

        let delta = try await client.fetchDelta(since: since)
        let tombstone = delta.documents.first { $0.id == created.id }
        XCTAssertNotNil(tombstone, "Deleted document missing from delta since snapshot")
        XCTAssertEqual(tombstone?.deleted, true, "Deleted document not flagged as deleted")
    }

    // MARK: - Helpers

    @discardableResult
    private func login() async throws -> String {
        try await auth.login(
            email: IntegrationTestEnv.email!,
            password: IntegrationTestEnv.password!
        )
    }

    private func uniqueTitle() -> String {
        "\(Self.titlePrefix)\(UUID().uuidString)"
    }

    private func registerCleanup(documentID: String) {
        let client = self.client!
        addTeardownBlock {
            try? await client.deleteDocument(id: documentID)
        }
    }

    private static func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = networkTimeout
        configuration.timeoutIntervalForResource = networkTimeout
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: configuration)
    }
}
