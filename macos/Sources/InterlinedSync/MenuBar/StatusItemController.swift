import AppKit
import SwiftUI

@MainActor
final class StatusItemController: NSObject {
    private let statusItem: NSStatusItem
    private let preferences: PreferencesManager
    private var preferencesWindow: NSWindow?

    private let statusMenuItem = NSMenuItem(title: "Status: Idle", action: nil, keyEquivalent: "")
    private let lastSyncedMenuItem = NSMenuItem(title: "Last synced: Never", action: nil, keyEquivalent: "")
    private let pauseResumeMenuItem = NSMenuItem(title: "Pause Sync", action: nil, keyEquivalent: "")

    init(preferences: PreferencesManager) {
        self.preferences = preferences
        self.statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        configureButton()
        configureMenu()
    }

    private func configureButton() {
        if let button = statusItem.button {
            let image = NSImage(
                systemSymbolName: "arrow.triangle.2.circlepath",
                accessibilityDescription: "InterlinedList Sync"
            )
            image?.isTemplate = true
            button.image = image
        }
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

        pauseResumeMenuItem.target = self
        pauseResumeMenuItem.action = #selector(togglePause)
        pauseResumeMenuItem.title = preferences.syncEnabled ? "Pause Sync" : "Resume Sync"
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

    @objc private func togglePause() {
        preferences.syncEnabled.toggle()
        pauseResumeMenuItem.title = preferences.syncEnabled ? "Pause Sync" : "Resume Sync"
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
