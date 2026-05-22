using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;
using Watashi.Client.Views;

namespace Watashi.Client;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private AppSettings _settings = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _settings = AppSettings.Load();
        Services = BuildServices(_settings);

        if (!_settings.IsConfigured)
        {
            var sw = Services.GetRequiredService<ConnectionSettingsWindow>();
            if (sw.ShowDialog() != true) { Shutdown(); return; }
            Services.GetRequiredService<ApiClient>().ConfigureBaseAddress();
        }

        await StartLoginFlowAsync();
    }

    public async void RestartLoginFlow() => await StartLoginFlowAsync();

    private async Task StartLoginFlowAsync()
    {
        var session = Services.GetRequiredService<SessionManager>();
        var api = Services.GetRequiredService<ApiClient>();
        var cred = Services.GetRequiredService<CredentialStore>();

        if (_settings.IsHttps)
        {
            var saved = cred.LoadDeviceToken();
            if (saved is not null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    var res = await api.AutoLoginAsync(saved.Value.machineName, saved.Value.windowsUser, saved.Value.token, cts.Token);
                    session.SetFromLogin(res);
                    if (res.MustChangePassword && !ShowChangePassword()) { Shutdown(); return; }
                    ShowMain();
                    return;
                }
                catch { cred.ClearDeviceToken(); }
            }
        }

        if (!ShowLogin()) { Shutdown(); return; }
        if (session.MustChangePassword && !ShowChangePassword()) { Shutdown(); return; }
        ShowMain();
    }

    private bool ShowLogin()
    {
        var w = Services.GetRequiredService<LoginWindow>();
        return w.ShowDialog() == true;
    }

    private bool ShowChangePassword()
    {
        var w = Services.GetRequiredService<ChangePasswordWindow>();
        return w.ShowDialog() == true;
    }

    private void ShowMain()
    {
        var w = Services.GetRequiredService<MainWindow>();
        MainWindow = w;
        w.Show();
    }

    private static IServiceProvider BuildServices(AppSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<LocalFileService>();
        services.AddHttpClient<ApiClient>(c =>
        {
            if (settings.IsConfigured)
                c.BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddHttpClient("settings-test", c => c.Timeout = TimeSpan.FromSeconds(5));

        services.AddTransient<ConnectionSettingsViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<ChangePasswordViewModel>();
        services.AddTransient<LocalPaneViewModel>();
        services.AddTransient<RemotePaneViewModel>();
        services.AddTransient<MainViewModel>();

        services.AddTransient<ConnectionSettingsWindow>();
        services.AddTransient<LoginWindow>();
        services.AddTransient<ChangePasswordWindow>();
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.Admin.AdminWindow>();

        services.AddTransient<ViewModels.Admin.AdminShellViewModel>();
        services.AddTransient<ViewModels.Admin.UserManagementViewModel>();
        services.AddTransient<ViewModels.Admin.HostManagementViewModel>();
        services.AddTransient<ViewModels.Admin.ShareManagementViewModel>();
        services.AddTransient<ViewModels.Admin.PermissionTemplateViewModel>();
        services.AddTransient<ViewModels.Admin.UserPermissionViewModel>();
        services.AddTransient<ViewModels.Admin.DeviceManagementViewModel>();
        services.AddTransient<ViewModels.Admin.NodeManagementViewModel>();
        services.AddTransient<ViewModels.Admin.AuditLogViewModel>();
        services.AddTransient<ViewModels.Admin.SystemSettingsViewModel>();

        var sp = services.BuildServiceProvider();
        var session = sp.GetRequiredService<SessionManager>();
        session.RefreshDelegate = async (rid, rt, ct) =>
        {
            var api = sp.GetRequiredService<ApiClient>();
            return await api.RefreshAsync(rid, rt, ct);
        };
        return sp;
    }
}
