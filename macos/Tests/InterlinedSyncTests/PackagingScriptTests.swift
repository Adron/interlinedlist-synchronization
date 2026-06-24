import XCTest

/// Smoke-tests the packaging scripts in `macos/scripts/`. Exercises only the
/// unsigned code path: the scripts must produce a valid bundle / installer
/// without any Apple Developer credentials configured.
///
/// Gated behind `RUN_PACKAGING_SMOKE_TESTS=1` so plain `swift test` runs stay
/// fast — full SwiftPM Release builds take ~30s on CI hardware. The CI release
/// workflow is the primary forcing function for the script paths; this test
/// exists so contributors can locally verify changes to the scripts without
/// cutting a tag.
final class PackagingScriptTests: XCTestCase {

    private var macosDir: URL {
        // This file lives at macos/Tests/InterlinedSyncTests/PackagingScriptTests.swift.
        // Walk up three components to reach macos/.
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    private var shouldRun: Bool {
        ProcessInfo.processInfo.environment["RUN_PACKAGING_SMOKE_TESTS"] == "1"
    }

    func testBuildAppScriptProducesUnsignedBundle() throws {
        try XCTSkipUnless(shouldRun, "Set RUN_PACKAGING_SMOKE_TESTS=1 to run.")

        let scriptURL = macosDir.appendingPathComponent("scripts/build-app.sh")
        XCTAssertTrue(FileManager.default.fileExists(atPath: scriptURL.path),
                      "build-app.sh missing at \(scriptURL.path)")

        let result = try runBash(scriptPath: scriptURL.path, env: [:])
        XCTAssertEqual(result.exitCode, 0,
                       "build-app.sh failed with exit \(result.exitCode):\n\(result.output)")

        let bundleURL = macosDir.appendingPathComponent("build/InterlinedSync.app")
        XCTAssertTrue(FileManager.default.fileExists(atPath: bundleURL.path),
                      ".app bundle not found at \(bundleURL.path)")

        let executableURL = bundleURL.appendingPathComponent("Contents/MacOS/InterlinedSync")
        XCTAssertTrue(FileManager.default.fileExists(atPath: executableURL.path),
                      "executable missing inside bundle")

        let infoPlistURL = bundleURL.appendingPathComponent("Contents/Info.plist")
        XCTAssertTrue(FileManager.default.fileExists(atPath: infoPlistURL.path),
                      "Info.plist missing inside bundle")
    }

    func testBuildPkgScriptProducesUnsignedPkg() throws {
        try XCTSkipUnless(shouldRun, "Set RUN_PACKAGING_SMOKE_TESTS=1 to run.")

        let bundleURL = macosDir.appendingPathComponent("build/InterlinedSync.app")
        try XCTSkipUnless(FileManager.default.fileExists(atPath: bundleURL.path),
                          "Run testBuildAppScriptProducesUnsignedBundle first.")

        let scriptURL = macosDir.appendingPathComponent("scripts/build-pkg.sh")
        let result = try runBash(scriptPath: scriptURL.path, env: [:])
        XCTAssertEqual(result.exitCode, 0,
                       "build-pkg.sh failed with exit \(result.exitCode):\n\(result.output)")

        let buildDir = macosDir.appendingPathComponent("build")
        let contents = try FileManager.default.contentsOfDirectory(atPath: buildDir.path)
        let pkgs = contents.filter { $0.hasPrefix("InterlinedSync-") && $0.hasSuffix(".pkg") }
        XCTAssertFalse(pkgs.isEmpty, "no .pkg produced in \(buildDir.path)")
    }

    // MARK: - Helpers

    private struct ScriptResult {
        let exitCode: Int32
        let output: String
    }

    private func runBash(scriptPath: String, env: [String: String]) throws -> ScriptResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/bash")
        process.arguments = [scriptPath]

        var combined = ProcessInfo.processInfo.environment
        for (key, value) in env { combined[key] = value }
        process.environment = combined

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe

        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()

        return ScriptResult(
            exitCode: process.terminationStatus,
            output: String(data: data, encoding: .utf8) ?? ""
        )
    }
}

