import CoreServices
import Foundation

/// Recursively watches a folder for `.md` file changes using FSEvents and surfaces batched
/// changed URLs as an `AsyncStream`. Start the stream by iterating `changes`; tear it down with
/// `stop()`, which releases the underlying `FSEventStream`.
final class FSEventsWatcher: @unchecked Sendable {
    let changes: AsyncStream<[URL]>

    private let url: URL
    private let latency: TimeInterval
    private let queue = DispatchQueue(label: "com.interlinedlist.sync.fsevents")
    private let lock = NSLock()
    private let continuation: AsyncStream<[URL]>.Continuation

    private var stream: FSEventStreamRef?
    private var isFinished = false

    init(url: URL, latency: TimeInterval = 0.3) {
        self.url = url
        self.latency = latency
        var capturedContinuation: AsyncStream<[URL]>.Continuation!
        self.changes = AsyncStream(bufferingPolicy: .unbounded) { capturedContinuation = $0 }
        self.continuation = capturedContinuation
    }

    func start() {
        lock.lock()
        defer { lock.unlock() }
        guard stream == nil, !isFinished else { return }

        var context = FSEventStreamContext(
            version: 0,
            info: Unmanaged.passUnretained(self).toOpaque(),
            retain: nil,
            release: nil,
            copyDescription: nil
        )

        // kFSEventStreamCreateFlagUseCFTypes makes `eventPaths` a CFArray of CFString, which
        // bridges safely to [String]. Without it the callback receives a raw C string array.
        let callback: FSEventStreamCallback = { _, info, count, paths, _, _ in
            guard let info else { return }
            let watcher = Unmanaged<FSEventsWatcher>.fromOpaque(info).takeUnretainedValue()
            let cfArray = Unmanaged<CFArray>.fromOpaque(paths).takeUnretainedValue()
            guard let pathStrings = cfArray as? [String] else { return }
            watcher.emit(Array(pathStrings.prefix(count)))
        }

        let flags = UInt32(
            kFSEventStreamCreateFlagFileEvents
                | kFSEventStreamCreateFlagNoDefer
                | kFSEventStreamCreateFlagUseCFTypes
        )

        guard let created = FSEventStreamCreate(
            kCFAllocatorDefault,
            callback,
            &context,
            [url.path] as CFArray,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow),
            latency,
            flags
        ) else {
            return
        }

        FSEventStreamSetDispatchQueue(created, queue)
        FSEventStreamStart(created)
        stream = created
    }

    func stop() {
        lock.lock()
        let toRelease = stream
        stream = nil
        let alreadyFinished = isFinished
        isFinished = true
        lock.unlock()

        if let toRelease {
            FSEventStreamStop(toRelease)
            FSEventStreamInvalidate(toRelease)
            FSEventStreamRelease(toRelease)
        }
        if !alreadyFinished {
            continuation.finish()
        }
    }

    deinit {
        if let stream {
            FSEventStreamStop(stream)
            FSEventStreamInvalidate(stream)
            FSEventStreamRelease(stream)
        }
    }

    private func emit(_ paths: [String]) {
        let urls = paths
            .map { URL(fileURLWithPath: $0) }
            .filter { $0.pathExtension == "md" }
        guard !urls.isEmpty else { return }

        lock.lock()
        let finished = isFinished
        lock.unlock()
        guard !finished else { return }
        continuation.yield(urls)
    }
}
