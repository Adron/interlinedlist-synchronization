using System.IO;
using System.IO.Abstractions;
using InterlinedSync.API;
using InterlinedSync.Auth;
using InterlinedSync.Configuration;
using InterlinedSync.FileSystem;
using InterlinedSync.Storage;
using InterlinedSync.Sync;
using InterlinedSync.SystemTray;
using InterlinedSync.UI.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using FileSystemImpl = System.IO.Abstractions.FileSystem;

namespace InterlinedSync;

/// <summary>
/// Builds the shared <see cref="IHost"/>. The WPF entry point lives in
/// <c>App.xaml.cs</c> and calls into this class so the same DI graph is exercised
/// regardless of whether we boot the GUI or a future headless test harness.
/// </summary>
public static class Program
{
    public static IHost BuildHost(string[]? args = null)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.AppDataFolderName);
        Directory.CreateDirectory(appDataDir);

        var localAppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.AppDataFolderName);
        Directory.CreateDirectory(localAppDataDir);

        var settingsPath = Path.Combine(appDataDir, AppConstants.AppSettingsFileName);

        var builder = Host.CreateApplicationBuilder(args ?? Array.Empty<string>());

        builder.Configuration
            .SetBasePath(appDataDir)
            .AddJsonFile(AppConstants.AppSettingsFileName, optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "INTERLINEDSYNC_");

        ConfigureSerilog(builder, localAppDataDir);

        ConfigureOptions(builder);
        ConfigureServices(builder, localAppDataDir);

        return builder.Build();
    }

    private static void ConfigureSerilog(HostApplicationBuilder builder, string localAppDataDir)
    {
        var logDir = Path.Combine(localAppDataDir, AppConstants.LogsFolderName);
        Directory.CreateDirectory(logDir);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDir, AppConstants.LogFileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: AppConstants.LogRetainedFileCount,
                shared: true)
            .CreateLogger();

        Log.Logger = logger;

        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(logger, dispose: true);
    }

    private static void ConfigureOptions(HostApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<SyncPreferences>()
            .Bind(builder.Configuration.GetSection(SyncPreferences.SectionName))
            .PostConfigure(o =>
            {
                if (string.IsNullOrEmpty(o.SyncFolder))
                {
                    o.SyncFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        AppConstants.DefaultSyncFolderName);
                }
                if (o.PollIntervalSeconds < AppConstants.MinimumPollIntervalSeconds)
                {
                    o.PollIntervalSeconds = AppConstants.DefaultPollIntervalSeconds;
                }
            });

        builder.Services
            .AddOptions<ApiOptions>()
            .Bind(builder.Configuration.GetSection(ApiOptions.SectionName))
            .PostConfigure(o =>
            {
                if (string.IsNullOrEmpty(o.BaseUrl))
                {
                    o.BaseUrl = AppConstants.DefaultApiBaseUrl;
                }
                if (string.IsNullOrEmpty(o.LoginEndpoint))
                {
                    o.LoginEndpoint = AppConstants.LoginEndpoint;
                }
            });
    }

    private static void ConfigureServices(HostApplicationBuilder builder, string localAppDataDir)
    {
        var services = builder.Services;

        services.AddSingleton<IFileSystem, FileSystemImpl>();
        services.AddSingleton<IPreferencesStore, PreferencesManager>();

#if WINDOWS_BUILD
        services.AddSingleton<ICredentialStore, Storage.CredentialManager>();
#else
        services.AddSingleton<ICredentialStore, InMemoryCredentialStore>();
#endif

        services.AddSingleton<IAuthProvider, AuthManager>();
        services.AddSingleton<ISyncStateNotifier, SyncStateNotifier>();

        var dbPath = Path.Combine(localAppDataDir, "state.db");
        services.AddSingleton<ISyncStateRepository>(sp =>
            new SyncStateRepository(dbPath, sp.GetRequiredService<ILogger<SyncStateRepository>>()));

        services.AddSingleton<IFileMapper, FileMapper>();

        services.AddTransient<BearerTokenHandler>();
        services.AddHttpClient<IInterlinedListClient, InterlinedListClient>()
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddSingleton<SyncEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<SyncEngine>());

#if WINDOWS_BUILD
        services.AddSingleton<TrayMenuBuilder>();
#endif

        services.AddTransient<OnboardingViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SyncStatusViewModel>();
    }
}
