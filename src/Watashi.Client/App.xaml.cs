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

        // クラッシュ原因を黙って消さずに表示する。
        DispatcherUnhandledException += (_, ev) =>
        {
            ShowFatal("UI スレッド例外", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) ShowFatal("未処理例外", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            ShowFatal("非同期例外", ev.Exception);
            ev.SetObserved();
        };

        try
        {
            _settings = AppSettings.Load();
            Services = BuildServices(_settings);

            // BootstrapUrl が設定されていれば config.json から ServerUrl を取得 (管理者一元管理)
            var boot = Services.GetRequiredService<BootstrapService>();
            await boot.TryBootstrapAsync(_settings);
            // ServerUrl が更新された可能性があるので ApiClient を再構成
            Services.GetRequiredService<ApiClient>().ConfigureBaseAddress();

            if (!_settings.IsConfigured)
            {
                var sw = Services.GetRequiredService<ConnectionSettingsWindow>();
                if (sw.ShowDialog() != true) { Shutdown(); return; }
                Services.GetRequiredService<ApiClient>().ConfigureBaseAddress();
            }

            await StartLoginFlowAsync();
        }
        catch (Exception ex)
        {
            ShowFatal("起動失敗", ex);
            Shutdown();
        }
    }

    // 旧 API 互換（直接呼ぶ箇所がもう無くなったらこのメソッドごと削除可）。
    public void RestartLoginFlow() => RequestLogout();

    private async Task StartLoginFlowAsync()
    {
        var session = Services.GetRequiredService<SessionManager>();
        var api = Services.GetRequiredService<ApiClient>();
        var cred = Services.GetRequiredService<CredentialStore>();

        // HTTP/HTTPS どちらでも自動ログイン試行。サーバー側 Auth:AllowHttpForAutoLogin で最終判定される。
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

        if (!ShowLogin()) { Shutdown(); return; }
        if (session.MustChangePassword && !ShowChangePassword()) { Shutdown(); return; }
        ShowMain();
    }

    private static void ShowFatal(string title, Exception ex)
    {
        var msg = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
        if (ex.InnerException is not null)
            msg += $"\n\n--- Inner ---\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        MessageBox.Show(msg, $"Watashi - {title}", MessageBoxButton.OK, MessageBoxImage.Error);
        Console.Error.WriteLine($"[{title}] {ex}");
    }

    private bool ShowLogin()
    {
        var w = Services.GetRequiredService<LoginWindow>();
        return w.ShowDialog() == true;
    }

    private bool _logoutInProgress;

    /// <summary>ログアウト要求。MainWindow を閉じて再ログインフローを開始する。</summary>
    public void RequestLogout()
    {
        _logoutInProgress = true;
        MainWindow?.Close();
    }

    private void ShowMain()
    {
        var w = Services.GetRequiredService<MainWindow>();
        MainWindow = w;
        w.Closed += async (_, _) =>
        {
            if (_logoutInProgress)
            {
                _logoutInProgress = false;
                try { await StartLoginFlowAsync(); }
                catch (Exception ex) { ShowFatal("再ログイン失敗", ex); Shutdown(); }
            }
            else
            {
                Shutdown();
            }
        };
        w.Show();
    }

    private bool ShowChangePassword()
    {
        var w = Services.GetRequiredService<ChangePasswordWindow>();
        return w.ShowDialog() == true;
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
        services.AddHttpClient("bootstrap", c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddSingleton<BootstrapService>();

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
