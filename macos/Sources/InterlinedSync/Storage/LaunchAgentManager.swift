import Foundation
import ServiceManagement

/// Abstraction over the system "Launch at Login" registration so the preferences UI and tests
/// don't touch `SMAppService` directly — registering a login item on a dev machine has a real
/// side effect (and can surface a System Settings approval prompt).
@MainActor
protocol LoginItemManaging: AnyObject {
    var isEnabled: Bool { get }
    func setEnabled(_ enabled: Bool) throws
}

@MainActor
final class SMAppServiceLoginItemManager: LoginItemManaging {
    var isEnabled: Bool {
        SMAppService.mainApp.status == .enabled
    }

    func setEnabled(_ enabled: Bool) throws {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    }
}
