import XCTest
@testable import InterlinedSync

final class DocumentGateTests: XCTestCase {
    func testSameDocument_operationsRunSerially() async throws {
        let gate = DocumentGate()
        let tracker = OverlapTracker()

        await withThrowingTaskGroup(of: Void.self) { group in
            for _ in 0..<8 {
                group.addTask {
                    try await gate.run("doc") {
                        await tracker.enter()
                        try await Task.sleep(nanoseconds: 2_000_000)
                        await tracker.exit()
                    }
                }
            }
            try? await group.waitForAll()
        }

        let maxConcurrent = await tracker.maxConcurrent
        XCTAssertEqual(maxConcurrent, 1, "Operations on the same document must never overlap")
    }

    func testDifferentDocuments_operationsRunConcurrently() async throws {
        let gate = DocumentGate()
        let tracker = OverlapTracker()

        await withThrowingTaskGroup(of: Void.self) { group in
            for index in 0..<6 {
                group.addTask {
                    try await gate.run("doc-\(index)") {
                        await tracker.enter()
                        try await Task.sleep(nanoseconds: 20_000_000)
                        await tracker.exit()
                    }
                }
            }
            try? await group.waitForAll()
        }

        let maxConcurrent = await tracker.maxConcurrent
        XCTAssertGreaterThan(maxConcurrent, 1, "Different documents should run in parallel")
    }

    func testRun_returnsOperationValue() async throws {
        let gate = DocumentGate()
        let value = try await gate.run("doc") { 42 }
        XCTAssertEqual(value, 42)
    }

    func testRun_propagatesThrownError() async {
        let gate = DocumentGate()
        do {
            _ = try await gate.run("doc") { throw SyncError.mapping }
            XCTFail("Expected the operation error to propagate")
        } catch SyncError.mapping {
            // expected
        } catch {
            XCTFail("Expected SyncError.mapping, got \(error)")
        }
    }
}

private actor OverlapTracker {
    private(set) var current = 0
    private(set) var maxConcurrent = 0

    func enter() {
        current += 1
        maxConcurrent = max(maxConcurrent, current)
    }

    func exit() {
        current -= 1
    }
}
