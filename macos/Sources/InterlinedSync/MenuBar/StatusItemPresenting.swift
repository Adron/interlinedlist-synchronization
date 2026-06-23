import AppKit

/// The slice of `NSStatusItem` the menu bar controller relies on, expressed as a protocol so the
/// rendering path is testable without touching `NSStatusBar`/`NSStatusItem` (which abort on a
/// headless runner with no window server).
@MainActor
protocol StatusItemPresenting: AnyObject {
    func attach(menu: NSMenu)
    func setIcon(symbolName: String, accessibilityDescription: String)
}

@MainActor
final class AppKitStatusItemPresenter: StatusItemPresenting {
    private let statusItem: NSStatusItem

    init() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    }

    func attach(menu: NSMenu) {
        statusItem.menu = menu
    }

    func setIcon(symbolName: String, accessibilityDescription: String) {
        guard let button = statusItem.button else { return }

        if let image = NSImage(
            systemSymbolName: symbolName,
            accessibilityDescription: accessibilityDescription
        ) {
            image.isTemplate = true
            button.image = image
            button.imageScaling = .scaleProportionallyDown
        }
    }
}
