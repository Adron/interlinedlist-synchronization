import Foundation
import ServiceManagement

@MainActor
struct LaunchAgentManager {
    var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    func enable() throws {
        // TODO: Phase 3 — register the app as a login item.
        try SMAppService.mainApp.register()
    }

    func disable() throws {
        // TODO: Phase 3 — unregister the login item.
        try SMAppService.mainApp.unregister()
    }
}
