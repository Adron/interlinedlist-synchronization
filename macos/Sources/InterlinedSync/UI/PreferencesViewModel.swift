import Combine
import Foundation

/// Drives the preferences window: bridges the preference store and login-item manager to the SwiftUI
/// panels, and owns the side-effecting account actions (sign out, reset state) as injected closures
/// so the view stays decoupled from `AppDelegate`'s wiring.
@MainActor
final class PreferencesViewModel: ObservableObject {
    let preferences: PreferenceStoring
    private let loginItems: LoginItemManaging
    private let onSignOut: () async -> Void
    private let onResetState: () async -> Void

    @Published var launchAtLoginFailed = false

    init(
        preferences: PreferenceStoring,
        loginItems: LoginItemManaging,
        onSignOut: @escaping () async -> Void = {},
        onResetState: @escaping () async -> Void = {}
    ) {
        self.preferences = preferences
        self.loginItems = loginItems
        self.onSignOut = onSignOut
        self.onResetState = onResetState
        preferences.launchAtLogin = loginItems.isEnabled
    }

    var syncFolderDisplayPath: String {
        preferences.syncFolderPath.isEmpty ? "Not set" : preferences.syncFolderPath
    }

    var accountEmail: String {
        preferences.accountEmail.isEmpty ? "Unknown" : preferences.accountEmail
    }

    func chooseSyncFolder() async {
        _ = await preferences.selectSyncFolder()
    }

    func setLaunchAtLogin(_ enabled: Bool) {
        do {
            try loginItems.setEnabled(enabled)
            preferences.launchAtLogin = enabled
            launchAtLoginFailed = false
        } catch {
            preferences.launchAtLogin = loginItems.isEnabled
            launchAtLoginFailed = true
        }
    }

    func signOut() async {
        await onSignOut()
    }

    func resetState() async {
        await onResetState()
    }

    var logFileURL: URL {
        let base = FileManager.default
            .urls(for: .libraryDirectory, in: .userDomainMask)
            .first?
            .appendingPathComponent("Logs/InterlinedListSync", isDirectory: true)
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("InterlinedListSync.log")
    }

    var versionString: String {
        let info = Bundle.main.infoDictionary
        let version = info?["CFBundleShortVersionString"] as? String ?? "0.0.0"
        let build = info?["CFBundleVersion"] as? String ?? "0"
        return "Version \(version) (\(build))"
    }
}
