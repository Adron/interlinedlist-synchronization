import Foundation

actor SyncEngine {
    private let client: DocumentFetching & DocumentMutating
    private let resolver: ConflictResolver

    init(client: DocumentFetching & DocumentMutating, resolver: ConflictResolver) {
        self.client = client
        self.resolver = resolver
    }

    func runCycle() async throws {
        // TODO: Phase 2 — poll remote, diff against disk, resolve conflicts, push/pull.
        throw SyncError.notImplemented
    }
}
