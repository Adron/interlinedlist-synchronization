import Foundation

/// Wraps the security-scoped resource calls a sandboxed app must make to reach a folder it only
/// holds a bookmark for. Extracted behind a protocol so `PreferencesManager`'s start/stop
/// bookkeeping can be unit-tested without a real sandbox extension — XCTest runs unsandboxed, where
/// `startAccessingSecurityScopedResource()` always returns `false`.
protocol SecurityScopedAccessing {
    /// Begins access to `url`, returning whether the sandbox granted it.
    func startAccessing(_ url: URL) -> Bool
    /// Ends access previously begun with `startAccessing(_:)`.
    func stopAccessing(_ url: URL)
}

/// Production accessor that forwards to `URL`'s security-scoped resource methods.
struct SecurityScopedAccessor: SecurityScopedAccessing {
    func startAccessing(_ url: URL) -> Bool {
        url.startAccessingSecurityScopedResource()
    }

    func stopAccessing(_ url: URL) {
        url.stopAccessingSecurityScopedResource()
    }
}
