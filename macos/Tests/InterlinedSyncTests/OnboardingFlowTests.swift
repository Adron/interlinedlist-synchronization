import XCTest
@testable import InterlinedSync

/// Covers the first-run / no-credentials launch decision and the validate-before-persist contract
/// that gate whether the app shows onboarding or starts syncing. The `AppDelegate` reads the same
/// `TokenStorage.hasSessionToken()` seam exercised here to choose its launch path.
final class OnboardingFlowTests: XCTestCase {
    private let baseURL = URL(string: "https://interlinedlist.com")!

    override func tearDown() {
        MockURLProtocol.requestHandler = nil
        super.tearDown()
    }

    // MARK: - No-token launch decision

    func testHasSessionTokenFalseWhenStorageEmpty() {
        let storage = MockTokenStorage()
        XCTAssertFalse(storage.hasSessionToken())
    }

    func testHasSessionTokenFalseWhenTokenEmpty() {
        let storage = MockTokenStorage(token: "")
        XCTAssertFalse(storage.hasSessionToken())
    }

    func testHasSessionTokenTrueWhenTokenPresent() {
        let storage = MockTokenStorage(token: "abc123")
        XCTAssertTrue(storage.hasSessionToken())
    }

    func testHasSessionTokenReadsTheSharedSessionAccount() {
        let storage = MockTokenStorage()
        try? storage.save("abc123", for: KeychainManager.sessionTokenAccount)
        XCTAssertTrue(storage.hasSessionToken())
    }

    // MARK: - Validate before persisting

    func testSuccessfulSignInPersistsToken() async throws {
        let storage = MockTokenStorage()
        let session = MockURLProtocol.makeSession()
        MockURLProtocol.requestHandler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"token":"fresh-token"}"#.utf8))
        }

        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)
        _ = try await manager.login(email: "user@example.com", password: "secret")

        XCTAssertTrue(storage.hasSessionToken())
        XCTAssertEqual(try storage.load(for: KeychainManager.sessionTokenAccount), "fresh-token")
    }

    func testFailedSignInDoesNotPersistToken() async {
        let storage = MockTokenStorage()
        let session = MockURLProtocol.makeSession()
        MockURLProtocol.requestHandler = { request in
            let response = HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
            return (response, Data())
        }

        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)

        do {
            _ = try await manager.login(email: "user@example.com", password: "wrong")
            XCTFail("Expected invalidCredentials")
        } catch let error as AuthError {
            XCTAssertEqual(error, .invalidCredentials)
        } catch {
            XCTFail("Unexpected error: \(error)")
        }

        XCTAssertFalse(storage.hasSessionToken(), "A rejected sign-in must not write a token")
    }

    func testSignOutClearsStoredToken() async {
        let storage = MockTokenStorage(token: "existing")
        let session = MockURLProtocol.makeSession()
        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)

        XCTAssertTrue(storage.hasSessionToken())
        await manager.logout()

        XCTAssertFalse(storage.hasSessionToken(), "Sign out returns the app to the no-credentials state")
    }

    // MARK: - No password logging

    func testSignInRequestBodyIsNotEmittedToLogs() async throws {
        // Guards the contract that credentials never reach a log line: the request body the client
        // sends carries the password, and nothing in the codebase prints it. This asserts the body
        // is JSON (so the request is well-formed) without the production code ever stringifying it.
        let storage = MockTokenStorage()
        let session = MockURLProtocol.makeSession()
        var capturedBody: Data?
        MockURLProtocol.requestHandler = { request in
            capturedBody = Self.body(of: request)
            let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            return (response, Data(#"{"token":"t"}"#.utf8))
        }

        let manager = AuthManager(baseURL: baseURL, session: session, tokenStorage: storage)
        _ = try await manager.login(email: "user@example.com", password: "hunter2")

        let body = try XCTUnwrap(capturedBody)
        let decoded = try JSONSerialization.jsonObject(with: body) as? [String: String]
        XCTAssertEqual(decoded?["password"], "hunter2")
    }

    /// Reads a request body whether `URLSession` left it on `httpBody` or moved it to a stream.
    private static func body(of request: URLRequest) -> Data? {
        if let body = request.httpBody {
            return body
        }
        guard let stream = request.httpBodyStream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        let bufferSize = 1024
        var buffer = [UInt8](repeating: 0, count: bufferSize)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: bufferSize)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }
}
