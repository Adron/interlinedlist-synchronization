import Combine
import Foundation
@testable import InterlinedSync

@MainActor
final class MockPreferencesManager: PreferenceStoring {
    var syncFolderPath = ""
    @Published var pollIntervalSecondsStorage: TimeInterval = 30
    var launchAtLogin = false
    var syncEnabled = true
    var notificationsEnabled = true
    var notifyOnSyncCompletion = true
    var notifyOnErrors = true
    var notifyOnConflictCopies = true
    var accountEmail = ""
    var hasCompletedOnboarding = false

    private(set) var selectSyncFolderCallCount = 0
    var folderToReturn: URL?

    var pollIntervalSeconds: TimeInterval {
        get { pollIntervalSecondsStorage }
        set { pollIntervalSecondsStorage = newValue }
    }

    var pollIntervalPublisher: AnyPublisher<TimeInterval, Never> {
        $pollIntervalSecondsStorage.eraseToAnyPublisher()
    }

    func selectSyncFolder() async -> URL? {
        selectSyncFolderCallCount += 1
        if let folderToReturn {
            syncFolderPath = folderToReturn.path
        }
        return folderToReturn
    }

    func resolveSyncFolder() -> URL? {
        folderToReturn
    }
}
