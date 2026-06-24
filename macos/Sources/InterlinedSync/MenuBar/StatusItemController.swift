import AppKit
import Combine
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private let presenter: StatusItemPresenting
    private let preferences: PreferencesManager
    private let state: SyncState
    private let coordinator: SyncCoordinating?
    private let preferencesViewModel: PreferencesViewModel?
    private let onSignIn: (() -> Void)?
    private let isSignedIn: Bool
    private var preferencesWindow: NSWindow?
    private var cancellables: Set<AnyCancellable> = []

    let signInMenuItem = NSMenuItem(title: "Sign In…", action: nil, keyEquivalent: "")
    let statusMenuItem = NSMenuItem(title: "Status: Idle", action: nil, keyEquivalent: "")
    let lastSyncedMenuItem = NSMenuItem(title: "Last synced: Never", action: nil, keyEquivalent: "")
    let syncNowMenuItem = NSMenuItem(title: "Sync Now", action: nil, keyEquivalent: "r")
    let pauseResumeMenuItem = NSMenuItem(title: "Pause Sync", action: nil, keyEquivalent: "")

    private let relativeFormatter: RelativeDateTimeFormatter = {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter
    }()

    init(
        presenter: StatusItemPresenting,
        preferences: PreferencesManager,
        state: SyncState,
        coordinator: SyncCoordinating? = nil,
        preferencesViewModel: PreferencesViewModel? = nil,
        isSignedIn: Bool = true,
        onSignIn: (() -> Void)? = nil
    ) {
        self.presenter = presenter
        self.preferences = preferences
        self.state = state
        self.coordinator = coordinator
        self.preferencesViewModel = preferencesViewModel
        self.isSignedIn = isSignedIn
        self.onSignIn = onSignIn
        super.init()

        configureMenu()
        observeState()
        render(status: state.status, lastSyncedAt: state.lastSyncedAt)
    }

    private func observeState() {
        state.$status
            .receive(on: RunLoop.main)
            .sink { [weak self] status in
                guard let self else { return }
                self.render(status: status, lastSyncedAt: self.state.lastSyncedAt)
            }
            .store(in: &cancellables)

        state.$lastSyncedAt
            .receive(on: RunLoop.main)
            .sink { [weak self] date in
                guard let self else { return }
                self.render(status: self.state.status, lastSyncedAt: date)
            }
            .store(in: &cancellables)
    }

    private func configureMenu() {
        let menu = NSMenu()
        menu.autoenablesItems = false

        let titleItem = NSMenuItem(title: "InterlinedList Sync", action: nil, keyEquivalent: "")
        titleItem.isEnabled = false
        menu.addItem(titleItem)

        menu.addItem(.separator())

        signInMenuItem.target = self
        signInMenuItem.action = #selector(presentSignIn)
        signInMenuItem.isEnabled = !isSignedIn
        menu.addItem(signInMenuItem)

        menu.addItem(.separator())

        statusMenuItem.isEnabled = false
        menu.addItem(statusMenuItem)

        lastSyncedMenuItem.isEnabled = false
        menu.addItem(lastSyncedMenuItem)

        menu.addItem(.separator())

        syncNowMenuItem.target = self
        syncNowMenuItem.action = #selector(syncNow)
        syncNowMenuItem.isEnabled = isSignedIn
        menu.addItem(syncNowMenuItem)

        pauseResumeMenuItem.target = self
        pauseResumeMenuItem.action = #selector(togglePause)
        pauseResumeMenuItem.isEnabled = isSignedIn
        menu.addItem(pauseResumeMenuItem)

        let prefsItem = NSMenuItem(title: "Preferences…", action: #selector(openPreferences), keyEquivalent: ",")
        prefsItem.target = self
        prefsItem.isEnabled = isSignedIn
        menu.addItem(prefsItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        quitItem.isEnabled = true
        menu.addItem(quitItem)

        presenter.attach(menu: menu)
    }

    func render(status: SyncStatus, lastSyncedAt: Date?) {
        guard isSignedIn else {
            statusMenuItem.title = "Status: Sign in needed"
            lastSyncedMenuItem.title = "Last synced: \(lastSyncedDescription(lastSyncedAt))"
            applyIcon(for: nil)
            presenter.setToolTip("Sign in needed")
            return
        }

        statusMenuItem.title = "Status: \(Self.label(for: status))"
        lastSyncedMenuItem.title = "Last synced: \(lastSyncedDescription(lastSyncedAt))"

        let isPaused = status == .paused
        pauseResumeMenuItem.title = isPaused ? "Resume Sync" : "Pause Sync"
        syncNowMenuItem.isEnabled = !isPaused && status != .syncing && status != .offline

        applyIcon(for: status)
        presenter.setToolTip(nil)
    }

    private func lastSyncedDescription(_ date: Date?) -> String {
        guard let date else { return "Never" }
        return relativeFormatter.localizedString(for: date, relativeTo: Date())
    }

    private static func label(for status: SyncStatus) -> String {
        switch status {
        case .idle: return "Idle"
        case .syncing: return "Syncing…"
        case .paused: return "Paused"
        case .offline: return "Offline"
        case .authExpired: return "Sign in required"
        case let .error(message): return "Error — \(message)"
        }
    }

    /// Renders the icon for the given status, or the signed-out warning icon when `status` is nil.
    private func applyIcon(for status: SyncStatus?) {
        let descriptor = status.map(Self.iconDescriptor(for:)) ?? Self.signedOutIconDescriptor
        presenter.setIcon(
            symbolName: descriptor.symbolName,
            accessibilityDescription: descriptor.accessibility
        )
    }

    static let signedOutIconDescriptor: (symbolName: String, accessibility: String) =
        ("exclamationmark.triangle", "InterlinedList Sync — sign in needed")

    static func iconDescriptor(for status: SyncStatus) -> (symbolName: String, accessibility: String) {
        switch status {
        case .idle:
            return ("arrow.triangle.2.circlepath", "InterlinedList Sync — idle")
        case .syncing:
            return ("arrow.triangle.2.circlepath", "InterlinedList Sync — syncing")
        case .paused:
            return ("pause.circle", "InterlinedList Sync — paused")
        case .offline:
            return ("wifi.slash", "InterlinedList Sync — offline")
        case .authExpired:
            return ("exclamationmark.triangle.fill", "InterlinedList Sync — sign in required")
        case .error:
            return ("exclamationmark.triangle", "InterlinedList Sync — error")
        }
    }

    @objc private func presentSignIn() {
        onSignIn?()
    }

    @objc private func syncNow() {
        guard let coordinator else { return }
        Task { await coordinator.syncNow() }
    }

    @objc private func togglePause() {
        let shouldPause = state.status != .paused
        preferences.syncEnabled = !shouldPause

        guard let coordinator else {
            if shouldPause { state.paused() } else { state.resumed() }
            return
        }

        Task {
            if shouldPause {
                await coordinator.pause()
            } else {
                await coordinator.resume()
            }
        }
    }

    @objc private func openPreferences() {
        guard let preferencesViewModel else { return }

        if preferencesWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 480, height: 360),
                styleMask: [.titled, .closable],
                backing: .buffered,
                defer: false
            )
            window.title = "Preferences"
            window.contentView = NSHostingView(rootView: PreferencesView(model: preferencesViewModel))
            window.center()
            window.isReleasedWhenClosed = false
            preferencesWindow = window
        }

        NSApp.activate(ignoringOtherApps: true)
        preferencesWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}
