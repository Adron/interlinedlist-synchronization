import SwiftUI

@MainActor
struct PreferencesView: View {
    @ObservedObject var preferences: PreferencesManager

    var body: some View {
        Form {
            Section("Sync") {
                LabeledContent("Folder") {
                    Text(preferences.syncFolderPath.isEmpty ? "Not set" : preferences.syncFolderPath)
                        .truncationMode(.middle)
                        .lineLimit(1)
                }

                // TODO: Phase 2 — wire interval + enabled controls to SyncEngine.
                Stepper(
                    "Sync every \(preferences.syncIntervalMinutes) min",
                    value: $preferences.syncIntervalMinutes,
                    in: 1...60
                )

                Toggle("Sync Enabled", isOn: $preferences.syncEnabled)
            }
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 320)
        .padding()
    }
}
