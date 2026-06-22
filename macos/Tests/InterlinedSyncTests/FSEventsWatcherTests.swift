import XCTest
@testable import InterlinedSync

final class FSEventsWatcherTests: XCTestCase {
    private var tempDir: URL!

    override func setUpWithError() throws {
        // FSEvents reports canonical paths; resolve the /var -> /private/var symlink up front so
        // emitted URLs are comparable to the ones we create.
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
            .resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: tempDir)
    }

    func testEmitsWhenMarkdownFileIsCreated() async throws {
        let watcher = FSEventsWatcher(url: tempDir, latency: 0.05)
        watcher.start()
        defer { watcher.stop() }

        let fileURL = tempDir.appendingPathComponent("note.md")
        let collected = await collectFirstEvent(from: watcher) {
            try? "hello".write(to: fileURL, atomically: true, encoding: .utf8)
        }

        let paths = try XCTUnwrap(collected).map { $0.resolvingSymlinksInPath().lastPathComponent }
        XCTAssertTrue(paths.contains("note.md"), "Expected note.md in \(paths)")
    }

    func testEmitsWhenMarkdownFileIsModified() async throws {
        let fileURL = tempDir.appendingPathComponent("edit.md")
        try "first".write(to: fileURL, atomically: true, encoding: .utf8)

        let watcher = FSEventsWatcher(url: tempDir, latency: 0.05)
        watcher.start()
        defer { watcher.stop() }

        let collected = await collectFirstEvent(from: watcher) {
            try? "second".write(to: fileURL, atomically: true, encoding: .utf8)
        }
        XCTAssertNotNil(collected, "Expected an event after modifying edit.md")
    }

    func testEmitsWhenMarkdownFileIsDeleted() async throws {
        let fileURL = tempDir.appendingPathComponent("gone.md")
        try "bye".write(to: fileURL, atomically: true, encoding: .utf8)

        let watcher = FSEventsWatcher(url: tempDir, latency: 0.05)
        watcher.start()
        defer { watcher.stop() }

        let collected = await collectFirstEvent(from: watcher) {
            try? FileManager.default.removeItem(at: fileURL)
        }
        XCTAssertNotNil(collected, "Expected an event after deleting gone.md")
    }

    func testIgnoresNonMarkdownFiles() async throws {
        let watcher = FSEventsWatcher(url: tempDir, latency: 0.05)
        watcher.start()
        defer { watcher.stop() }

        let txtURL = tempDir.appendingPathComponent("ignore.txt")
        let mdURL = tempDir.appendingPathComponent("keep.md")
        let collected = await collectFirstEvent(from: watcher) {
            try? "x".write(to: txtURL, atomically: true, encoding: .utf8)
            try? "y".write(to: mdURL, atomically: true, encoding: .utf8)
        }

        let paths = try XCTUnwrap(collected).map { $0.lastPathComponent }
        XCTAssertFalse(paths.contains("ignore.txt"))
        XCTAssertTrue(paths.contains("keep.md"))
    }

    // MARK: - Helpers

    /// Runs `mutate`, then waits for the watcher to emit at least one batch, returning the first
    /// non-empty batch or nil on timeout.
    private func collectFirstEvent(
        from watcher: FSEventsWatcher,
        timeout: TimeInterval = 5,
        mutate: @escaping () -> Void
    ) async -> [URL]? {
        await withTaskGroup(of: [URL]?.self) { group in
            group.addTask {
                for await batch in watcher.changes where !batch.isEmpty {
                    return batch
                }
                return nil
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                return nil
            }

            // Give the stream a beat to schedule before mutating the directory.
            try? await Task.sleep(nanoseconds: 100_000_000)
            mutate()

            let result = await group.next() ?? nil
            group.cancelAll()
            return result
        }
    }
}
