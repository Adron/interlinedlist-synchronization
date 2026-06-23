import AppKit
import Combine
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private let statusItem: NSStatusItem
    private let preferences: PreferencesManager
    private let state: SyncState
    private let coordinator: SyncCoordinating?
    private var preferencesWindow: NSWindow?
    private var cancellables: Set<AnyCancellable> = []

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
        preferences: PreferencesManager,
        state: SyncState,
        coordinator: SyncCoordinating? = nil
    ) {
        self.preferences = preferences
        self.state = state
        self.coordinator = coordinator
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
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

        let titleItem = NSMenuItem(title: "InterlinedList Sync", action: nil, keyEquivalent: "")
        titleItem.isEnabled = false
        menu.addItem(titleItem)

        menu.addItem(.separator())

        statusMenuItem.isEnabled = false
        menu.addItem(statusMenuItem)

        lastSyncedMenuItem.isEnabled = false
        menu.addItem(lastSyncedMenuItem)

        menu.addItem(.separator())

        syncNowMenuItem.target = self
        syncNowMenuItem.action = #selector(syncNow)
        menu.addItem(syncNowMenuItem)

        pauseResumeMenuItem.target = self
        pauseResumeMenuItem.action = #selector(togglePause)
        menu.addItem(pauseResumeMenuItem)

        let prefsItem = NSMenuItem(title: "Preferences…", action: #selector(openPreferences), keyEquivalent: ",")
        prefsItem.target = self
        menu.addItem(prefsItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(quitItem)

        statusItem.menu = menu
    }

    func render(status: SyncStatus, lastSyncedAt: Date?) {
        statusMenuItem.title = "Status: \(Self.label(for: status))"
        lastSyncedMenuItem.title = "Last synced: \(lastSyncedDescription(lastSyncedAt))"

        let isPaused = status == .paused
        pauseResumeMenuItem.title = isPaused ? "Resume Sync" : "Pause Sync"
        syncNowMenuItem.isEnabled = !isPaused && status != .syncing

        applyIcon(for: status)
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
        case let .error(message): return "Error — \(message)"
        }
    }

    private func applyIcon(for status: SyncStatus) {
        guard let button = statusItem.button else { return }
        let descriptor = Self.iconDescriptor(for: status)

        if let image = NSImage(
            systemSymbolName: descriptor.symbolName,
            accessibilityDescription: descriptor.accessibility
        ) {
            image.isTemplate = true
            button.image = image
            button.imageScaling = .scaleProportionallyDown
        }
    }

    static func iconDescriptor(for status: SyncStatus) -> (symbolName: String, accessibility: String) {
        switch status {
        case .idle:
            return ("arrow.triangle.2.circlepath", "InterlinedList Sync — idle")
        case .syncing:
            return ("arrow.triangle.2.circlepath", "InterlinedList Sync — syncing")
        case .paused:
            return ("pause.circle", "InterlinedList Sync — paused")
        case .error:
            return ("exclamationmark.triangle", "InterlinedList Sync — error")
        }
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
        if preferencesWindow == nil {
            let window = NSWindow(
                contentRect: NSRect(x: 0, y: 0, width: 480, height: 320),
                styleMask: [.titled, .closable],
                backing: .buffered,
                defer: false
            )
            window.title = "Preferences"
            window.contentView = NSHostingView(rootView: PreferencesView(preferences: preferences))
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
