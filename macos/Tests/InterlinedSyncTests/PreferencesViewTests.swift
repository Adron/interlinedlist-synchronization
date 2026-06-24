import XCTest
@testable import InterlinedSync

@MainActor
final class PreferencesViewTests: XCTestCase {
    private var preferences: MockPreferencesManager!
    private var loginItems: MockLoginItemManager!

    override func setUp() async throws {
        preferences = MockPreferencesManager()
        loginItems = MockLoginItemManager()
    }

    private func makeModel(
        onSignOut: @escaping () async -> Void = {},
        onResetState: @escaping () async -> Void = {}
    ) -> PreferencesViewModel {
        PreferencesViewModel(
            preferences: preferences,
            loginItems: loginItems,
            onSignOut: onSignOut,
            onResetState: onResetState
        )
    }

    func testInitSyncsLaunchAtLoginFromLoginItemStatus() {
        loginItems = MockLoginItemManager(initiallyEnabled: true)
        let model = makeModel()

        XCTAssertTrue(model.preferences.launchAtLogin)
    }

    func testSetLaunchAtLoginEnablesLoginItem() {
        let model = makeModel()

        model.setLaunchAtLogin(true)

        XCTAssertEqual(loginItems.setEnabledCalls, [true])
        XCTAssertTrue(preferences.launchAtLogin)
        XCTAssertFalse(model.launchAtLoginFailed)
    }

    func testSetLaunchAtLoginFailureRevertsPreference() {
        loginItems.errorToThrow = MockLoginItemError.registrationFailed
        let model = makeModel()

        model.setLaunchAtLogin(true)

        XCTAssertTrue(model.launchAtLoginFailed)
        XCTAssertFalse(preferences.launchAtLogin, "Preference should mirror the manager's real state")
    }

    func testChooseSyncFolderInvokesPreferences() async {
        preferences.folderToReturn = URL(fileURLWithPath: "/tmp/sync")
        let model = makeModel()

        await model.chooseSyncFolder()

        XCTAssertEqual(preferences.selectSyncFolderCallCount, 1)
        XCTAssertEqual(model.syncFolderDisplayPath, "/tmp/sync")
    }

    func testSyncFolderDisplayPathFallsBackToNotSet() {
        let model = makeModel()

        XCTAssertEqual(model.syncFolderDisplayPath, "Not set")
    }

    func testAccountEmailFallsBackToUnknown() {
        let model = makeModel()

        XCTAssertEqual(model.accountEmail, "Unknown")
    }

    func testAccountEmailReflectsStoredEmail() {
        preferences.accountEmail = "user@example.com"
        let model = makeModel()

        XCTAssertEqual(model.accountEmail, "user@example.com")
    }

    func testSignOutInvokesCallback() async {
        var didSignOut = false
        let model = makeModel(onSignOut: { didSignOut = true })

        await model.signOut()

        XCTAssertTrue(didSignOut)
    }

    func testResetStateInvokesCallback() async {
        var didReset = false
        let model = makeModel(onResetState: { didReset = true })

        await model.resetState()

        XCTAssertTrue(didReset)
    }

    func testVersionStringFormat() {
        let model = makeModel()

        XCTAssertTrue(model.versionString.hasPrefix("Version "))
    }
}
