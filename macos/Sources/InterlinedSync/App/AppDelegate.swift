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
    private lazy var authManager = AuthManager(
        baseURL: Self.apiBaseURL,
        session: .shared,
        tokenStorage: keychain
    )
    private lazy var notificationManager = NotificationManager(
        center: UNUserNotificationCenter.current(),
        isEnabled: {
            UserDefaults.standard.object(forKey: "notificationsEnabled") as? Bool ?? true
        }
    )

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        if !preferences.hasCompletedOnboarding {
            presentOnboarding()
        } else {
            startSync()
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
            pollInterval: preferences.pollIntervalSeconds
        )
        syncEngine = engine

        statusItemController = StatusItemController(
            preferences: preferences,
            state: syncState,
            coordinator: engine
        )

        if preferences.syncEnabled {
            Task { await engine.start() }
        } else {
            syncState.paused()
        }
    }

    private func presentOnboarding() {
        statusItemController = StatusItemController(preferences: preferences, state: syncState)

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
