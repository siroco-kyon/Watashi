using System.IO;
using System.Net;
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

            // 接続先サーバと機能設定はアプリ同梱の deployment.json で固定する (管理者が配布時に設定)。
            // クライアントからは変更できない。settings.json の ServerUrl より優先される。
            if (!DeploymentConfig.Apply(_settings))
            {
                // deployment.json が無い / serverUrl 未設定 = 配布パッケージの不備。
                // 利用者は接続先を変更できないため、設定画面ではなく明確なエラーを出して終了する。
                MessageBox.Show(
                    "接続先サーバが配布設定 (deployment.json) に指定されていません。\n" +
                    "配布パッケージが正しくないため起動できません。管理者に連絡してください。",
                    "Watashi - 配布設定エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            Services = BuildServices(_settings);
            Services.GetRequiredService<ApiClient>().ConfigureBaseAddress();

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
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                cred.ClearDeviceToken();
            }
            catch
            {
                // Network errors, server-side HTTP policy, and locked accounts should not erase a valid remembered device.
            }
        }

        if (!ShowLogin(out var rememberDevice)) { Shutdown(); return; }
        if (session.MustChangePassword && !ShowChangePassword()) { Shutdown(); return; }
        if (rememberDevice) await TrySaveTrustedDeviceAsync(api, cred);
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

    private bool ShowLogin(out bool rememberDevice)
    {
        var w = Services.GetRequiredService<LoginWindow>();
        var ok = w.ShowDialog() == true;
        rememberDevice = ok && w.RememberDeviceRequested;
        return ok;
    }

    private static async Task TrySaveTrustedDeviceAsync(ApiClient api, CredentialStore cred)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var trusted = await api.TrustDeviceAsync(Environment.MachineName, Environment.UserName, cts.Token);
            cred.SaveDeviceToken(Environment.MachineName, Environment.UserName, trusted.DeviceToken);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "この PC の記憶に失敗しました。次回起動時は通常ログインが必要です。\n\n" + ex.Message,
                "Watashi - PC 記憶",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
        services.AddTransient<ViewModels.Admin.PermissionBundleViewModel>();
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
        // アイドルタイムアウト発火時はメインウィンドウを閉じてログインに戻す。
        // SessionManager 側はタイマーを管理するだけ。UI に戻すのは Dispatcher 経由で行う。
        session.IdleTimedOut += () =>
        {
            if (Current is App app)
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (app.MainWindow is not null)
                    {
                        MessageBox.Show(app.MainWindow,
                            "無操作のためログアウトしました。再ログインしてください。",
                            "アイドルタイムアウト", MessageBoxButton.OK, MessageBoxImage.Information);
                        app.RequestLogout();
                    }
                });
            }
        };
        return sp;
    }
}
