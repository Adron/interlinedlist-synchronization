import AppKit
import SwiftUI
import UserNotifications

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let apiBaseURL = URL(string: "https://interlinedlist.com")!

    private var statusItemController: StatusItemController?
    private var onboardingWindow: NSWindow?
    private var syncEngine: SyncEngine?

    private let preferences = PreferencesManager()
    private let keychain = KeychainManager()
    private let syncState = SyncState()
    private let loginItems: LoginItemManaging = SMAppServiceLoginItemManager()
    private lazy var authManager = AuthManager(
        baseURL: Self.apiBaseURL,
        session: .shared,
        tokenStorage: keychain
    )
    private lazy var notificationManager = NotificationManager(
        center: UNUserNotificationCenter.current(),
        isEnabled: {
            UserDefaults.standard.object(forKey: "notificationsEnabled") as? Bool ?? true
        },
        isCategoryEnabled: { category in
            switch category {
            case .auth:
                return true
            case .completion, .error, .conflict:
                let key: String
                switch category {
                case .completion: key = "notifyOnSyncCompletion"
                case .error: key = "notifyOnErrors"
                case .conflict: key = "notifyOnConflictCopies"
                case .auth: key = ""
                }
                return UserDefaults.standard.object(forKey: key) as? Bool ?? true
            }
        }
    )

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        if keychain.hasSessionToken() && preferences.hasCompletedOnboarding {
            startSync()
        } else {
            presentOnboarding()
        }
    }

    private func startSync() {
        guard let folder = preferences.resolveSyncFolder() else { return }

        let client = InterlinedListClient(
            baseURL: Self.apiBaseURL,
            session: .shared,
            tokenStorage: keychain
        )
        let mapper = DocumentMapper(rootURL: folder)
        let engine = SyncEngine(
            client: client,
            mapper: mapper,
            state: syncState,
            notifications: notificationManager,
            networkMonitor: NetworkMonitor(),
            pollInterval: preferences.pollIntervalSeconds
        )
        syncEngine = engine

        let publisher = preferences.pollIntervalPublisher
        Task { await engine.bindPollInterval(to: publisher) }

        let preferencesViewModel = PreferencesViewModel(
            preferences: preferences,
            loginItems: loginItems,
            onSignOut: { [weak self] in await self?.signOut() },
            onResetState: { await engine.resetLedger() }
        )

        statusItemController = StatusItemController(
            presenter: AppKitStatusItemPresenter(),
            preferences: preferences,
            state: syncState,
            coordinator: engine,
            preferencesViewModel: preferencesViewModel,
            isSignedIn: true
        )

        if preferences.syncEnabled {
            Task { await engine.start() }
        } else {
            syncState.paused()
        }
    }

    private func signOut() async {
        await syncEngine?.stop()
        await authManager.logout()
        await syncEngine?.resetLedger()
        syncEngine = nil

        preferences.hasCompletedOnboarding = false
        preferences.accountEmail = ""
        syncState.resetSyncMarker()

        statusItemController = nil
        presentOnboarding()
    }

    private func presentOnboarding() {
        statusItemController = StatusItemController(
            presenter: AppKitStatusItemPresenter(),
            preferences: preferences,
            state: syncState,
            isSignedIn: false,
            onSignIn: { [weak self] in self?.showOnboardingWindow() }
        )

        showOnboardingWindow()
    }

    private func showOnboardingWindow() {
        if let onboardingWindow {
            NSApp.activate(ignoringOtherApps: true)
            onboardingWindow.makeKeyAndOrderFront(nil)
            return
        }

        let view = OnboardingView(authManager: authManager, preferences: preferences) { [weak self] in
            self?.onboardingWindow?.close()
            self?.onboardingWindow = nil
            self?.statusItemController = nil
            self?.startSync()
        }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 420, height: 360),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "Welcome to InterlinedList Sync"
        window.contentView = NSHostingView(rootView: view)
        window.center()
        window.isReleasedWhenClosed = false

        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        onboardingWindow = window
    }
}
