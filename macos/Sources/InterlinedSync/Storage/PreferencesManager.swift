import AppKit
import SwiftUI

@MainActor
final class PreferencesManager: ObservableObject {
    @AppStorage("syncFolderPath") var syncFolderPath: String = ""
    @AppStorage("syncIntervalMinutes") var syncIntervalMinutes: Int = 5
    @AppStorage("syncEnabled") var syncEnabled: Bool = true
    @AppStorage("notificationsEnabled") var notificationsEnabled: Bool = true
    @AppStorage("hasCompletedOnboarding") var hasCompletedOnboarding: Bool = false

    private static let bookmarkKey = "syncFolderBookmark"

    /// Poll cadence the SyncEngine uses, in seconds. Derived from the user-facing minutes
    /// preference with a 30-second floor so an aggressive setting can't hammer the server.
    var pollIntervalSeconds: TimeInterval {
        max(30, TimeInterval(syncIntervalMinutes) * 60)
    }

    func selectSyncFolder() async -> URL? {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = true
        panel.prompt = "Choose Sync Folder"

        let response = await panel.begin()
        guard response == .OK, let url = panel.url else {
            return nil
        }

        storeBookmark(for: url)
        syncFolderPath = url.path
        return url
    }

    func resolveSyncFolder() -> URL? {
        guard let data = UserDefaults.standard.data(forKey: Self.bookmarkKey) else {
            return nil
        }

        var isStale = false
        guard let url = try? URL(
            resolvingBookmarkData: data,
            options: .withSecurityScope,
            relativeTo: nil,
            bookmarkDataIsStale: &isStale
        ) else {
            return nil
        }

        if isStale {
            storeBookmark(for: url)
        }
        return url
    }

    private func storeBookmark(for url: URL) {
        guard let data = try? url.bookmarkData(
            options: .withSecurityScope,
            includingResourceValuesForKeys: nil,
            relativeTo: nil
        ) else {
            return
        }
        UserDefaults.standard.set(data, forKey: Self.bookmarkKey)
    }
}
