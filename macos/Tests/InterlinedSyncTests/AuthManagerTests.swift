import XCTest
@testable import InterlinedSync

final class AuthManagerTests: XCTestCase {
    private final class TestTokenStorage: TokenStorage, @unchecked Sendable {
        private let lock = NSLock()
        private var store: [String: String] = [:]

        func save(_ token: String, for account: String) throws {
            lock.lock(); defer { lock.unlock() }
            store[account] = token
        }

        func load(for account: String) throws -> String {
            lock.lock(); defer { lock.unlock() }
            guard let value = store[account] else { throw KeychainError.itemNotFound }
            return value
        }

        func delete(for account: String) throws {
            lock.lock(); defer { lock.unlock() }
            store[account] = nil
        }
    }

    private let baseURL = URL(string: "https://interlinedlist.com")!

    override func tearDown() {
        MockURLProtocol.requestHandler = nil
        super.tearDown()
    }

    func testLoginSuccessStoresAndReturnsToken() async throws {
        let storage = TestTokenStorage()
        let session = MockURLProtocol.makeSession()
        MockURLProtocol.requestHandler = { request in
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: nil
            )!
            let data = Data(#"{"token":"abc123"}"#.utf8)
            return (response, data)
        }

        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)
        let token = try await manager.login(email: "user@example.com", password: "secret")

        XCTAssertEqual(token, "abc123")
        XCTAssertEqual(try storage.load(for: "session-token"), "abc123")
    }

    func testLoginUnauthorizedThrowsInvalidCredentials() async {
        let storage = TestTokenStorage()
        let session = MockURLProtocol.makeSession()
        MockURLProtocol.requestHandler = { request in
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 401,
                httpVersion: nil,
                headerFields: nil
            )!
            return (response, Data())
        }

        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)

        do {
            _ = try await manager.login(email: "user@example.com", password: "wrong")
            XCTFail("Expected invalidCredentials error")
        } catch let error as AuthError {
            XCTAssertEqual(error, .invalidCredentials)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }
    }

    func testIsAuthenticatedFalseWhenStorageEmpty() async {
        let storage = TestTokenStorage()
        let session = MockURLProtocol.makeSession()
        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)

        let authenticated = await manager.isAuthenticated
        XCTAssertFalse(authenticated)
    }
}
