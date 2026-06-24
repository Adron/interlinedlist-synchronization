import Foundation

/// Serializes work per document ID while letting work on different documents proceed in parallel.
/// Each ID owns a tail `Task`; new work for that ID chains onto the tail, so same-document operations
/// run in submission order and never overlap. The chain self-prunes when a document goes idle.
actor DocumentGate {
    private var tails: [String: Task<Void, Never>] = [:]

    func run<T: Sendable>(_ id: String, operation: @escaping @Sendable () async throws -> T) async throws -> T {
        let previous = tails[id]
        let box = ResultBox<T>()

        let task = Task {
            await previous?.value
            do {
                let value = try await operation()
                await box.set(.success(value))
            } catch {
                await box.set(.failure(error))
            }
        }
        tails[id] = task

        await task.value
        prune(id, completed: task)
        return try await box.take()
    }

    private func prune(_ id: String, completed: Task<Void, Never>) {
        if tails[id] == completed {
            tails[id] = nil
        }
    }
}

private actor ResultBox<T: Sendable> {
    private var result: Result<T, Error>?

    func set(_ value: Result<T, Error>) {
        result = value
    }

    func take() throws -> T {
        guard let result else { throw SyncError.notImplemented }
        return try result.get()
    }
}
