import SwiftUI

@MainActor
struct OnboardingView: View {
    let authManager: AuthManager
    @ObservedObject var preferences: PreferencesManager
    let onComplete: () -> Void

    @State private var email = ""
    @State private var password = ""
    @State private var errorMessage: String?
    @State private var isSigningIn = false
    @State private var isSignedIn = false

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Sign in to InterlinedList")
                .font(.title2.bold())

            if isSignedIn {
                folderStep
            } else {
                credentialsStep
            }

            if let errorMessage {
                Text(errorMessage)
                    .font(.callout)
                    .foregroundColor(.red)
            }

            Spacer()
        }
        .padding(24)
        .frame(width: 420, height: 360)
    }

    private var credentialsStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            TextField("Email", text: $email)
                .textFieldStyle(.roundedBorder)
                .disableAutocorrection(true)

            SecureField("Password", text: $password)
                .textFieldStyle(.roundedBorder)

            Button(action: signIn) {
                if isSigningIn {
                    ProgressView().controlSize(.small)
                } else {
                    Text("Sign In")
                }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(email.isEmpty || password.isEmpty || isSigningIn)
        }
    }

    private var folderStep: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Choose a folder to sync your documents into.")
                .font(.callout)

            Button("Choose Sync Folder…", action: chooseFolder)
        }
    }

    private func signIn() {
        errorMessage = nil
        isSigningIn = true
        Task {
            defer { isSigningIn = false }
            do {
                _ = try await authManager.login(email: email, password: password)
                preferences.accountEmail = email
                isSignedIn = true
            } catch AuthError.invalidCredentials {
                errorMessage = "Incorrect email or password."
            } catch {
                errorMessage = "Sign in failed. Please try again."
            }
        }
    }

    private func chooseFolder() {
        Task {
            if let _ = await preferences.selectSyncFolder() {
                preferences.hasCompletedOnboarding = true
                onComplete()
            } else {
                errorMessage = "Please choose a folder to continue."
            }
        }
    }
}
