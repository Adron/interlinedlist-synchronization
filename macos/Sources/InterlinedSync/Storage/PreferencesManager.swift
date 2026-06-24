import AppKit
import Combine
import SwiftUI

/// The user-facing settings the preferences UI and sync engine read and write. Expressed as a
/// protocol so the view model is testable with a lightweight in-memory double, and so the engine
/// can depend on the interval publisher without importing AppKit.
@MainActor
protocol PreferenceStoring: AnyObject {
    var syncFolderPath: String { get set }
    var pollIntervalSeconds: TimeInterval { get set }
    var pollIntervalPublisher: AnyPublisher<TimeInterval, Never> { get }
    var launchAtLogin: Bool { get set }
    var syncEnabled: Bool { get set }
    var notificationsEnabled: Bool { get set }
    var notifyOnSyncCompletion: Bool { get set }
    var notifyOnErrors: Bool { get set }
    var notifyOnConflictCopies: Bool { get set }
    var accountEmail: String { get set }
    var hasCompletedOnboarding: Bool { get set }

    func selectSyncFolder() async -> URL?
    func resolveSyncFolder() -> URL?
}

@MainActor
final class PreferencesManager: ObservableObject, PreferenceStoring {
    static let minimumPollIntervalSeconds: TimeInterval = 5
    static let maximumPollIntervalSeconds: TimeInterval = 300

    @AppStorage("syncFolderPath") var syncFolderPath: String = ""
    @AppStorage("syncEnabled") var syncEnabled: Bool = true
    @AppStorage("notificationsEnabled") var notificationsEnabled: Bool = true
    @AppStorage("notifyOnSyncCompletion") var notifyOnSyncCompletion: Bool = true
    @AppStorage("notifyOnErrors") var notifyOnErrors: Bool = true
    @AppStorage("notifyOnConflictCopies") var notifyOnConflictCopies: Bool = true
    @AppStorage("launchAtLogin") var launchAtLogin: Bool = false
    @AppStorage("accountEmail") var accountEmail: String = ""
    @AppStorage("hasCompletedOnboarding") var hasCompletedOnboarding: Bool = false

    /// Poll cadence the SyncEngine uses, in seconds. Stored directly (clamped to the supported
    /// range) and published so the running engine can pick up changes without a restart.
    @Published var pollIntervalSeconds: TimeInterval {
        didSet {
            let clamped = Self.clampInterval(pollIntervalSeconds)
            if clamped != pollIntervalSeconds {
                pollIntervalSeconds = clamped
                return
            }
            UserDefaults.standard.set(clamped, forKey: Self.intervalKey)
        }
    }

    var pollIntervalPublisher: AnyPublisher<TimeInterval, Never> {
        $pollIntervalSeconds.eraseToAnyPublisher()
    }

    private static let intervalKey = "pollIntervalSeconds"
    private static let bookmarkKey = "syncFolderBookmark"

    init() {
        let stored = UserDefaults.standard.object(forKey: Self.intervalKey) as? TimeInterval
        pollIntervalSeconds = Self.clampInterval(stored ?? 30)
    }

    private static func clampInterval(_ value: TimeInterval) -> TimeInterval {
        min(maximumPollIntervalSeconds, max(minimumPollIntervalSeconds, value.rounded()))
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
