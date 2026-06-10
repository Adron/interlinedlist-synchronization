import Foundation
@testable import InterlinedSync

final class MockTokenStorage: TokenStorage, @unchecked Sendable {
    private let lock = NSLock()
    private var store: [String: String] = [:]

    init(token: String? = nil, account: String = "session-token") {
        if let token { store[account] = token }
    }

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
