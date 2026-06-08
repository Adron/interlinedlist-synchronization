import XCTest
@testable import InterlinedSync

final class KeychainManagerTests: XCTestCase {
    private let service = "com.interlinedlist.sync.tests"
    private let account = "unit-test-account"
    private var keychain: KeychainManager!

    override func setUp() {
        super.setUp()
        keychain = KeychainManager(service: service)
        try? keychain.delete(for: account)
    }

    override func tearDown() {
        try? keychain.delete(for: account)
        super.tearDown()
    }

    func testSaveLoadDeleteRoundTrip() throws {
        try keychain.save("token-value", for: account)
        XCTAssertEqual(try keychain.load(for: account), "token-value")

        try keychain.delete(for: account)

        XCTAssertThrowsError(try keychain.load(for: account)) { error in
            XCTAssertEqual(error as? KeychainError, .itemNotFound)
        }
    }

    func testSaveOverwritesExistingValue() throws {
        try keychain.save("first", for: account)
        try keychain.save("second", for: account)
        XCTAssertEqual(try keychain.load(for: account), "second")
    }
}
