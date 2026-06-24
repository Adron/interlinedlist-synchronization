import XCTest
@testable import InterlinedSync

final class ChangeSetTests: XCTestCase {
    private let t0 = Date(timeIntervalSince1970: 1_000)
    private let t1 = Date(timeIntervalSince1970: 2_000)
    private let t2 = Date(timeIntervalSince1970: 3_000)

    // MARK: - First sync (empty ledger)

    func testRemoteOnlyDocument_firstSync_isRemoteCreated() {
        let remote = makeRemote(id: "a", updatedAt: t1)
        let set = ChangeSet.compute(
            trackedLocal: [], untrackedLocal: [], remote: [remote], ledger: [:]
        )
        XCTAssertEqual(set.remoteChanges, [.created(remote)])
        XCTAssertTrue(set.localChanges.isEmpty)
        XCTAssertTrue(set.conflicts.isEmpty)
    }

    func testUntrackedLocalFile_isLocalCreated() {
        let url = URL(fileURLWithPath: "/tmp/New Note.md")
        let set = ChangeSet.compute(
            trackedLocal: [],
            untrackedLocal: [(url: url, title: "New Note", body: "hi")],
            remote: [],
            ledger: [:]
        )
        XCTAssertEqual(set.localChanges, [.created(url: url, title: "New Note", body: "hi")])
        XCTAssertTrue(set.remoteChanges.isEmpty)
    }

    // MARK: - Subsequent syncs (with ledger)

    func testRemoteUpdatedSinceLastSync_isRemoteUpdated() {
        let local = makeLocal(id: "a", modifiedAt: t1)
        let remote = makeRemote(id: "a", updatedAt: t2)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [local], untrackedLocal: [], remote: [remote], ledger: ledger
        )
        XCTAssertEqual(set.remoteChanges, [.updated(remote)])
        XCTAssertTrue(set.localChanges.isEmpty)
        XCTAssertTrue(set.conflicts.isEmpty)
    }

    func testLocalModifiedSinceLastSync_isLocalUpdated() {
        let local = makeLocal(id: "a", modifiedAt: t2)
        let remote = makeRemote(id: "a", updatedAt: t1)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [local], untrackedLocal: [], remote: [remote], ledger: ledger
        )
        XCTAssertEqual(set.localChanges, [.updated(local)])
        XCTAssertTrue(set.remoteChanges.isEmpty)
    }

    func testBothSidesChanged_isConflict() {
        let local = makeLocal(id: "a", modifiedAt: t2)
        let remote = makeRemote(id: "a", updatedAt: t2)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [local], untrackedLocal: [], remote: [remote], ledger: ledger
        )
        XCTAssertEqual(set.conflicts, [ChangeSet.Conflict(local: local, remote: remote)])
        XCTAssertTrue(set.localChanges.isEmpty)
        XCTAssertTrue(set.remoteChanges.isEmpty)
    }

    func testNothingChanged_isEmpty() {
        let local = makeLocal(id: "a", modifiedAt: t1)
        let remote = makeRemote(id: "a", updatedAt: t1)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [local], untrackedLocal: [], remote: [remote], ledger: ledger
        )
        XCTAssertTrue(set.isEmpty)
    }

    func testRemoteDeleted_isRemoteDeleted() {
        let local = makeLocal(id: "a", modifiedAt: t1)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [local], untrackedLocal: [], remote: [], ledger: ledger
        )
        XCTAssertEqual(set.remoteChanges, [.deleted(id: "a")])
    }

    func testLocalDeleted_isLocalDeleted() {
        let remote = makeRemote(id: "a", updatedAt: t1)
        let ledger = ["a": SyncRecord(remoteUpdatedAt: t1, localModifiedAt: t1)]
        let set = ChangeSet.compute(
            trackedLocal: [], untrackedLocal: [], remote: [remote], ledger: ledger
        )
        XCTAssertEqual(set.localChanges, [.deleted(id: "a")])
    }

    // MARK: - Helpers

    private func makeLocal(id: String, modifiedAt: Date) -> LocalDocument {
        LocalDocument(
            id: id,
            url: URL(fileURLWithPath: "/tmp/\(id).md"),
            title: id,
            body: "body-\(id)",
            modifiedAt: modifiedAt
        )
    }

    private func makeRemote(id: String, updatedAt: Date) -> DocumentDTO {
        DocumentDTO(id: id, title: id, content: "body-\(id)", folderId: nil, updatedAt: updatedAt)
    }
}
