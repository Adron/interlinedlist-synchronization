import Foundation
@testable import InterlinedSync

@MainActor
final class MockLoginItemManager: LoginItemManaging {
    private(set) var setEnabledCalls: [Bool] = []
    var errorToThrow: Error?

    private var enabledState: Bool

    init(initiallyEnabled: Bool = false) {
        enabledState = initiallyEnabled
    }

    var isEnabled: Bool { enabledState }

    func setEnabled(_ enabled: Bool) throws {
        setEnabledCalls.append(enabled)
        if let errorToThrow {
            throw errorToThrow
        }
        enabledState = enabled
    }
}

enum MockLoginItemError: Error {
    case registrationFailed
}
