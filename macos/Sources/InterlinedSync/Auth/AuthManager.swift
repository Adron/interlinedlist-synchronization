import Foundation

protocol TokenStorage: Sendable {
    func save(_ token: String, for account: String) throws
    func load(for account: String) throws -> String
    func delete(for account: String) throws
}

enum AuthError: Error, Equatable {
    case invalidCredentials
    case networkError(Error)
    case tokenStorageFailure
    case malformedResponse

    static func == (lhs: AuthError, rhs: AuthError) -> Bool {
        switch (lhs, rhs) {
        case (.invalidCredentials, .invalidCredentials),
             (.tokenStorageFailure, .tokenStorageFailure),
             (.malformedResponse, .malformedResponse):
            return true
        case (.networkError, .networkError):
            return true
        default:
            return false
        }
    }
}

actor AuthManager {
    private struct LoginRequest: Encodable {
        let email: String
        let password: String
    }

    private struct LoginResponse: Decodable {
        let token: String

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            if let value = try container.decodeIfPresent(String.self, forKey: .token) {
                token = value
            } else if let value = try container.decodeIfPresent(String.self, forKey: .syncToken) {
                token = value
            } else if let value = try container.decodeIfPresent(String.self, forKey: .accessToken) {
                token = value
            } else {
                throw AuthError.malformedResponse
            }
        }

        private enum CodingKeys: String, CodingKey {
            case token
            case syncToken
            case accessToken
        }
    }

    private static let loginPath = "/api/auth/sync-token"
    private static let keychainAccount = "session-token"

    private let baseURL: URL
    private let session: URLSession
    private let tokenStorage: TokenStorage

    init(baseURL: URL, session: URLSession, tokenStorage: TokenStorage) {
        self.baseURL = baseURL
        self.session = session
        self.tokenStorage = tokenStorage
    }

    var isAuthenticated: Bool {
        get async {
            do {
                let token = try tokenStorage.load(for: Self.keychainAccount)
                return !token.isEmpty
            } catch {
                return false
            }
        }
    }

    @discardableResult
    func login(email: String, password: String) async throws -> String {
        var request = URLRequest(url: Self.endpoint(baseURL, Self.loginPath))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        do {
            request.httpBody = try JSONEncoder().encode(LoginRequest(email: email, password: password))
        } catch {
            throw AuthError.malformedResponse
        }

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw AuthError.networkError(error)
        }

        guard let http = response as? HTTPURLResponse else {
            throw AuthError.malformedResponse
        }

        switch http.statusCode {
        case 200...299:
            break
        case 401, 403:
            throw AuthError.invalidCredentials
        default:
            throw AuthError.malformedResponse
        }

        let token: String
        do {
            token = try JSONDecoder().decode(LoginResponse.self, from: data).token
        } catch {
            throw AuthError.malformedResponse
        }

        do {
            try tokenStorage.save(token, for: Self.keychainAccount)
        } catch {
            throw AuthError.tokenStorageFailure
        }

        return token
    }

    func logout() async {
        try? tokenStorage.delete(for: Self.keychainAccount)
    }

    private static func endpoint(_ base: URL, _ path: String) -> URL {
        var trimmed = base.absoluteString
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        let suffix = path.hasPrefix("/") ? path : "/" + path
        return URL(string: trimmed + suffix) ?? base.appendingPathComponent(path)
    }
}
