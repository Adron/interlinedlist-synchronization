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
    }

    private static let loginPath = "/api/auth/login"
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
        var request = URLRequest(url: baseURL.appendingPathComponent(Self.loginPath))
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
}
