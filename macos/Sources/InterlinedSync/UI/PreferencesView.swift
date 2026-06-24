import AppKit
import SwiftUI

@MainActor
struct PreferencesView: View {
    @ObservedObject var model: PreferencesViewModel

    var body: some View {
        TabView {
            GeneralPreferencesTab(model: model)
                .tabItem { Label("General", systemImage: "gearshape") }

            AccountPreferencesTab(model: model)
                .tabItem { Label("Account", systemImage: "person.crop.circle") }

            NotificationsPreferencesTab(model: model)
                .tabItem { Label("Notifications", systemImage: "bell") }

            AdvancedPreferencesTab(model: model)
                .tabItem { Label("Advanced", systemImage: "wrench.and.screwdriver") }
        }
        .frame(width: 480, height: 360)
    }
}

@MainActor
private struct GeneralPreferencesTab: View {
    @ObservedObject var model: PreferencesViewModel

    private var launchAtLogin: Binding<Bool> {
        Binding(
            get: { model.preferences.launchAtLogin },
            set: { model.setLaunchAtLogin($0) }
        )
    }

    private var interval: Binding<Double> {
        Binding(
            get: { model.preferences.pollIntervalSeconds },
            set: { model.preferences.pollIntervalSeconds = $0.rounded() }
        )
    }

    var body: some View {
        Form {
            Section("Sync Folder") {
                LabeledContent("Folder") {
                    HStack {
                        Text(model.syncFolderDisplayPath)
                            .truncationMode(.middle)
                            .lineLimit(1)
                        Spacer()
                        Button("Choose…") {
                            Task { await model.chooseSyncFolder() }
                        }
                    }
                }
            }

            Section("Startup") {
                Toggle("Launch at Login", isOn: launchAtLogin)
                if model.launchAtLoginFailed {
                    Text("Could not update the login item. Approve it in System Settings › General › Login Items.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            Section("Sync Interval") {
                Slider(
                    value: interval,
                    in: PreferencesManager.minimumPollIntervalSeconds...PreferencesManager.maximumPollIntervalSeconds,
                    step: 1
                ) {
                    Text("Interval")
                } minimumValueLabel: {
                    Text("5s")
                } maximumValueLabel: {
                    Text("300s")
                }
                LabeledContent("Sync every") {
                    Text("\(Int(model.preferences.pollIntervalSeconds)) seconds")
                }
            }
        }
        .formStyle(.grouped)
        .padding()
    }
}

@MainActor
private struct AccountPreferencesTab: View {
    @ObservedObject var model: PreferencesViewModel

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "person.crop.circle")
                .font(.system(size: 48))
                .foregroundColor(.secondary)

            Text("Signed in as \(model.accountEmail)")
                .font(.headline)

            Button(role: .destructive) {
                Task { await model.signOut() }
            } label: {
                Text("Sign Out")
            }

            Spacer()
        }
        .frame(maxWidth: .infinity)
        .padding(32)
    }
}

@MainActor
private struct NotificationsPreferencesTab: View {
    @ObservedObject var model: PreferencesViewModel

    private var enabled: Binding<Bool> {
        Binding(
            get: { model.preferences.notificationsEnabled },
            set: { model.preferences.notificationsEnabled = $0 }
        )
    }
    private var completion: Binding<Bool> {
        Binding(
            get: { model.preferences.notifyOnSyncCompletion },
            set: { model.preferences.notifyOnSyncCompletion = $0 }
        )
    }
    private var errors: Binding<Bool> {
        Binding(
            get: { model.preferences.notifyOnErrors },
            set: { model.preferences.notifyOnErrors = $0 }
        )
    }
    private var conflicts: Binding<Bool> {
        Binding(
            get: { model.preferences.notifyOnConflictCopies },
            set: { model.preferences.notifyOnConflictCopies = $0 }
        )
    }

    var body: some View {
        Form {
            Section {
                Toggle("Enable notifications", isOn: enabled)
            }

            Section("Notify me about") {
                Toggle("Sync completion", isOn: completion)
                Toggle("Errors", isOn: errors)
                Toggle("Conflict copies", isOn: conflicts)
            }
            .disabled(!model.preferences.notificationsEnabled)
        }
        .formStyle(.grouped)
        .padding()
    }
}

@MainActor
private struct AdvancedPreferencesTab: View {
    @ObservedObject var model: PreferencesViewModel
    @State private var showingResetAlert = false

    var body: some View {
        Form {
            Section("Logs") {
                LabeledContent("Log file") {
                    HStack {
                        Text(model.logFileURL.path)
                            .truncationMode(.middle)
                            .lineLimit(1)
                        Spacer()
                        Button("Reveal in Finder") {
                            NSWorkspace.shared.activateFileViewerSelecting([model.logFileURL])
                        }
                    }
                }
            }

            Section("Maintenance") {
                Button("Reset State") {
                    showingResetAlert = true
                }
            }

            Section {
                LabeledContent("About", value: model.versionString)
            }
        }
        .formStyle(.grouped)
        .padding()
        .alert("Reset sync state?", isPresented: $showingResetAlert) {
            Button("Cancel", role: .cancel) {}
            Button("Reset", role: .destructive) {
                Task { await model.resetState() }
            }
        } message: {
            Text("This clears the local sync ledger and last-synced time. Your documents and sign-in are kept; the next sync rebuilds from scratch.")
        }
    }
}
