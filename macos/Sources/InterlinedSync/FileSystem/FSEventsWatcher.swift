import CoreServices
import Foundation

// TODO: Phase 2 — wrap FSEventStreamCreate to watch the sync folder recursively.

final class FSEventsWatcher {
    private let url: URL
    private let onChange: @Sendable ([String]) -> Void

    init(url: URL, onChange: @escaping @Sendable ([String]) -> Void) {
        self.url = url
        self.onChange = onChange
    }

    func start() {
        // TODO: Phase 2 — create and schedule the FSEventStream.
    }

    func stop() {
        // TODO: Phase 2 — invalidate and release the FSEventStream.
    }
}
