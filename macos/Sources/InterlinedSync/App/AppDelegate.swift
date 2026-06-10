import AppKit
import SwiftUI

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private static let apiBaseURL = URL(string: "https://interlinedlist.com")!

    private var statusItemController: StatusItemController?
    private var onboardingWindow: NSWindow?

    private let preferences = PreferencesManager()
    private let keychain = KeychainManager()
    private lazy var authManager = AuthManager(
        baseURL: Self.apiBaseURL,
        session: .shared,
        tokenStorage: keychain
    )

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)

        statusItemController = StatusItemController(preferences: preferences)

        if !preferences.hasCompletedOnboarding {
            presentOnboarding()
        }
    }

    private func presentOnboarding() {
        let view = OnboardingView(authManager: authManager, preferences: preferences) { [weak self] in
            self?.onboardingWindow?.close()
            self?.onboardingWindow = nil
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
