import Foundation
import XCTest

enum IntegrationTestEnv {
    private static let emailKey = "INTERLINEDLIST_EMAIL"
    private static let passwordKey = "INTERLINEDLIST_PASSWORD"
    private static let baseURLKey = "INTERLINEDLIST_API_BASE_URL"
    private static let dotEnvOptInKey = "INTERLINEDLIST_LOAD_DOTENV"

    static var email: String? { value(for: emailKey) }
    static var password: String? { value(for: passwordKey) }

    static var baseURL: URL? {
        guard let raw = value(for: baseURLKey) else { return nil }
        return URL(string: raw)
    }

    static var isConfigured: Bool {
        email != nil && password != nil && baseURL != nil
    }

    static func skipIfUnconfigured(in test: XCTestCase) throws {
        try XCTSkipUnless(
            isConfigured,
            "Integration test creds not set; skipping live-API test."
        )
    }

    private static func value(for key: String) -> String? {
        if let fromEnv = nonEmpty(ProcessInfo.processInfo.environment[key]) {
            return fromEnv
        }
        return nonEmpty(dotEnvValues[key])
    }

    private static func nonEmpty(_ raw: String?) -> String? {
        guard let trimmed = raw?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty else { return nil }
        return trimmed
    }

    private static let dotEnvValues: [String: String] = loadDotEnv()

    private static func loadDotEnv() -> [String: String] {
        guard ProcessInfo.processInfo.environment[dotEnvOptInKey] != nil else { return [:] }
        let candidates = dotEnvCandidatePaths()
        for path in candidates {
            guard let contents = try? String(contentsOfFile: path, encoding: .utf8) else { continue }
            return parse(contents)
        }
        return [:]
    }

    private static func dotEnvCandidatePaths() -> [String] {
        let fileManager = FileManager.default
        let cwd = fileManager.currentDirectoryPath
        var seen = Set<String>()
        var paths: [String] = []
        var dir = URL(fileURLWithPath: cwd)
        for _ in 0..<5 {
            let candidate = dir.appendingPathComponent(".env").path
            if seen.insert(candidate).inserted {
                paths.append(candidate)
            }
            dir.deleteLastPathComponent()
        }
        return paths
    }

    private static func parse(_ contents: String) -> [String: String] {
        var result: [String: String] = [:]
        for line in contents.split(whereSeparator: \.isNewline) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard !trimmed.isEmpty, !trimmed.hasPrefix("#") else { continue }
            guard let separator = trimmed.firstIndex(of: "=") else { continue }
            let key = String(trimmed[..<separator]).trimmingCharacters(in: .whitespaces)
            var rawValue = String(trimmed[trimmed.index(after: separator)...])
                .trimmingCharacters(in: .whitespaces)
            rawValue = stripQuotes(rawValue)
            result[key] = rawValue
        }
        return result
    }

    private static func stripQuotes(_ value: String) -> String {
        guard value.count >= 2 else { return value }
        let isDoubleQuoted = value.hasPrefix("\"") && value.hasSuffix("\"")
        let isSingleQuoted = value.hasPrefix("'") && value.hasSuffix("'")
        guard isDoubleQuoted || isSingleQuoted else { return value }
        return String(value.dropFirst().dropLast())
    }
}
