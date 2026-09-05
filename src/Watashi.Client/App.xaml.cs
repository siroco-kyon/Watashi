using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Watashi.Client.Services;
using Watashi.Client.ViewModels;
using Watashi.Client.Views;

namespace Watashi.Client;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;
    private AppSettings _settings = null!;
    private ThemeService? _themeService;
    private SplashWindow? _splash;
    private SessionManager? _session;
    public MaintenanceMonitorService Maintenance { get; private set; } = null!;
    private bool _updateInProgress;

    /// <summary>
    /// スプラッシュを閉じる。ログイン画面などの対話 UI を出す直前と、起動を中断する各経路で呼ぶ。
    /// 再ログインフロー (ログアウト後の StartLoginFlowAsync) では既に null なので何もしない。
    /// </summary>
    private void CloseSplash()
    {
        _splash?.Close();
        _splash = null;
    }

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
            // 最初のウィンドウを作る前に保存済みテーマを反映し、ライト画面の瞬間表示を防ぐ。
            _settings = AppSettings.Load();
            _themeService = new ThemeService(_settings);

            // 起動処理 (更新確認・自動ログイン等) はウィンドウ表示前にネットワークへ出るため、
            // 環境によっては十数秒かかる。無反応に見えないよう最初にスプラッシュを表示し、
            // 進捗 (%) と現在の工程を出す。
            _splash = new SplashWindow();
            _splash.Show();
            _splash.SetProgress(5, "設定を読み込んでいます...");

            // 接続先サーバと機能設定はアプリ同梱の deployment.json で固定する (管理者が配布時に設定)。
            // クライアントからは変更できない。settings.json の ServerUrl より優先される。
            if (!DeploymentConfig.Apply(_settings))
            {
                // deployment.json が無い / serverUrl 未設定 = 配布パッケージの不備。
                // 利用者は接続先を変更できないため、設定画面ではなく明確なエラーを出して終了する。
                CloseSplash();
                MessageBox.Show(
                    "接続先サーバまたは更新マニフェスト URL が配布設定 (deployment.json) に指定されていません。\n" +
                    "配布パッケージが正しくないため起動できません。管理者に連絡してください。",
                    "Watashi - 配布設定エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            Maintenance = new MaintenanceMonitorService(_settings, verifyVersion: async ct =>
                (await StartupUpdateChecker.CheckAsync(_settings.UpdateManifestUrl, ct)).Outcome == StartupUpdateCheckOutcome.UpToDate);
            await Maintenance.RefreshAsync();
            Maintenance.Start();
            if (Maintenance.IsBlocked)
            {
                CloseSplash();
                if (new MaintenanceWindow(Maintenance).ShowDialog() != true) { Shutdown(); return; }
            }
            _splash?.SetProgress(25, "更新を確認しています...");
            if (await StopForPublishedUpdateAsync())
            {
                CloseSplash();
                return;
            }

            _splash?.SetProgress(60, "アプリケーションを初期化しています...");
            Services = BuildServices(_settings, _themeService, Maintenance);
            _session = Services.GetRequiredService<SessionManager>();
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            Services.GetRequiredService<ApiClient>().ConfigureBaseAddress();

            await StartLoginFlowAsync();
        }
        catch (Exception ex)
        {
            CloseSplash();
            ShowFatal("起動失敗", ex);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Maintenance?.Dispose();
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        _session = null;
        base.OnExit(e);
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        // API通信や転送キューの進行では延長せず、全トップレベル/モーダル画面での
        // 実際の利用者入力だけをセッション活動として扱う。
        if (e.StagingItem.Input is KeyEventArgs or MouseEventArgs or TouchEventArgs or StylusEventArgs)
            _session?.ResetIdleTimer();
    }

    // 旧 API 互換（直接呼ぶ箇所がもう無くなったらこのメソッドごと削除可）。
    public void RestartLoginFlow() => RequestLogout();

    private async Task<bool> StopForPublishedUpdateAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var result = await StartupUpdateChecker.CheckAsync(_settings.UpdateManifestUrl, cts.Token);

        if (result.Outcome == StartupUpdateCheckOutcome.CheckFailed)
        {
            MessageBox.Show(
                "必須の更新確認を完了できませんでした。ネットワーク接続を確認して起動し直してください。\n\n" +
                (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : $"詳細: {result.Message}"),
                "Watashi - 更新確認エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            if (MainWindow is not Watashi.Client.MainWindow) Shutdown();
            return true;
        }

        if (result.Outcome != StartupUpdateCheckOutcome.UpdateAvailable || result.ManifestUri is null)
            return false;

        MessageBox.Show(
            $"新しい Watashi が公開されています。\n\n現在のバージョン: {result.CurrentVersion}\n最新のバージョン: {result.LatestVersion}\n\n更新を開始するため、このアプリを終了します。",
            "Watashi - 更新",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        if (MainWindow is MainWindow main)
        {
            _updateInProgress = true;
            main.Close();
            await main.CleanupCompleted;
        }
        if (!StartupUpdateChecker.LaunchUpdate(result.ManifestUri))
        {
            MessageBox.Show(
                "更新プログラムを起動できませんでした。インストールページから Watashi を起動し直してください。",
                "Watashi - 更新エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Shutdown();
        return true;
    }

    private enum LoginFlowOutcome
    {
        MainShown,
        Retry,
        Shutdown,
    }

    private async Task StartLoginFlowAsync(bool allowAutoLogin = true)
    {
        while (true)
        {
            var outcome = await TryStartLoginFlowAsync(allowAutoLogin);
            if (outcome == LoginFlowOutcome.MainShown) return;
            if (outcome == LoginFlowOutcome.Shutdown)
            {
                Shutdown();
                return;
            }

            // 失効した記憶済み端末で同じ自動ログインを繰り返さない。
            allowAutoLogin = false;
        }
    }

    private async Task<LoginFlowOutcome> TryStartLoginFlowAsync(bool allowAutoLogin)
    {
        var session = Services.GetRequiredService<SessionManager>();
        var api = Services.GetRequiredService<ApiClient>();
        var cred = Services.GetRequiredService<CredentialStore>();

        // HTTP/HTTPS どちらでも自動ログイン試行。サーバー側 Auth:AllowHttpForAutoLogin で最終判定される。
        var saved = allowAutoLogin ? cred.LoadDeviceToken() : null;
        if (saved is not null)
        {
            try
            {
                _splash?.SetProgress(80, "自動ログインしています...");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var res = await api.AutoLoginAsync(saved.Value.machineName, saved.Value.windowsUser, saved.Value.token, cts.Token);
                session.SetFromLogin(res);
                _splash?.SetProgress(100, "起動しています...");
                CloseSplash();
                if (res.MustChangePassword && !ShowChangePassword())
                    return session.IsAuthenticated ? LoginFlowOutcome.Shutdown : LoginFlowOutcome.Retry;
                if (!session.IsAuthenticated) return LoginFlowOutcome.Retry;
                ShowPasswordExpiryWarning(session);
                if (!session.IsAuthenticated) return LoginFlowOutcome.Retry;
                ShowMain();
                return LoginFlowOutcome.MainShown;
            }
            catch (ApiException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                cred.ClearDeviceToken();
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden && ex.Message == "account_disabled")
            {
                // 管理者が無効化した端末 token はサーバー側でも失効済み。次回起動時に
                // 同じ無効な token で自動ログインを繰り返さないようローカル側も破棄する。
                cred.ClearDeviceToken();
            }
            catch
            {
                // Network errors, server-side HTTP policy, and locked accounts should not erase a valid remembered device.
            }
        }

        // ここから先はログイン画面 (対話 UI)。スプラッシュは役目を終えたので閉じる。
        CloseSplash();
        if (!ShowLogin(out var rememberDevice)) return LoginFlowOutcome.Shutdown;
        if (session.MustChangePassword && !ShowChangePassword())
            return session.IsAuthenticated ? LoginFlowOutcome.Shutdown : LoginFlowOutcome.Retry;
        if (!session.IsAuthenticated) return LoginFlowOutcome.Retry;
        if (rememberDevice) await TrySaveTrustedDeviceAsync(api, cred);
        if (!session.IsAuthenticated) return LoginFlowOutcome.Retry;
        ShowPasswordExpiryWarning(session);
        if (!session.IsAuthenticated) return LoginFlowOutcome.Retry;
        ShowMain();
        return LoginFlowOutcome.MainShown;
    }

    private void ShowPasswordExpiryWarning(SessionManager session)
    {
        if (session.PasswordExpiresInDays is not int remaining ||
            remaining <= 0 || remaining > session.PasswordWarningDays) return;

        var choice = MessageBox.Show(
            $"パスワードの有効期限まで残り {remaining} 日です。\n今すぐパスワードを変更しますか？",
            "Watashi - パスワード期限",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        // 「キャンセル」を選んでもメイン画面には進む。変更したい場合は OK で任意変更フローへ。
        if (choice == MessageBoxResult.OK)
            ShowChangePassword(mandatory: false);
    }

    /// <summary>サーバが返す refresh 失効理由コードを利用者向けメッセージに変換する。</summary>
    private static string DescribeSessionExpiry(string? reason) => reason switch
    {
        "password_changed" => "パスワードが変更されたため、セッションが無効になりました。",
        "token_reuse_detected" => "セキュリティ保護のため、全てのセッションを無効化しました。",
        "account_locked" => "アカウントがロックされています。管理者に連絡してください。",
        "account_disabled" => "アカウントが無効化されています。管理者に連絡してください。",
        "device_revoked" => "この端末の登録が無効化されています。",
        _ => "セッションの有効期限が切れました。",
    };

    private static void ShowFatal(string title, Exception ex)
    {
        AppLog.Error(title, ex);
        var msg = $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}";
        if (ex.InnerException is not null)
            msg += $"\n\n--- Inner ---\n{ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";
        MessageBox.Show(msg, $"Watashi - {title}", MessageBoxButton.OK, MessageBoxImage.Error);
        Console.Error.WriteLine($"[{title}] {ex}");
    }

    private Window? _activeAuthenticationDialog;

    private bool ShowLogin(out bool rememberDevice)
    {
        var w = Services.GetRequiredService<LoginWindow>();
        _activeAuthenticationDialog = w;
        try
        {
            var ok = w.ShowDialog() == true;
            rememberDevice = ok && w.RememberDeviceRequested;
            return ok;
        }
        finally
        {
            if (ReferenceEquals(_activeAuthenticationDialog, w))
                _activeAuthenticationDialog = null;
        }
    }

    private static async Task TrySaveTrustedDeviceAsync(ApiClient api, CredentialStore cred)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var trusted = await api.TrustDeviceAsync(Environment.MachineName, Environment.UserName, cts.Token);
            cred.SaveDeviceToken(Environment.MachineName, Environment.UserName, trusted.DeviceToken);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // SessionExpired が利用者への通知と再ログイン遷移を担当する。
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
    private bool _suppressAutoLoginOnce;

    /// <summary>ログアウト要求。MainWindow を閉じて再ログインフローを開始する。</summary>
    public void RequestLogout()
    {
        _logoutInProgress = true;
        _suppressAutoLoginOnce = true;
        MainWindow?.Close();
    }

    private void ShowMain()
    {
        var w = Services.GetRequiredService<MainWindow>();
        w.AttachMaintenance(Maintenance);
        MainWindow = w;
        w.Closed += async (_, _) =>
        {
            // MainWindow側のasync cleanup（転送worker停止・queue lease解放）を待ってから
            // 同じ利用者の次のMainWindowを生成する。
            await w.CleanupCompleted;
            if (ReferenceEquals(MainWindow, w)) MainWindow = null;
            if (_updateInProgress) return;
            if (_logoutInProgress)
            {
                _logoutInProgress = false;
                var allowAutoLogin = !_suppressAutoLoginOnce;
                _suppressAutoLoginOnce = false;
                try { await StartLoginFlowAsync(allowAutoLogin); }
                catch (Exception ex) { ShowFatal("再ログイン失敗", ex); Shutdown(); }
            }
            else
            {
                Shutdown();
            }
        };
        w.Show();
    }

    public bool ShowChangePassword(bool mandatory = true)
        => ShowChangePassword(mandatory, out _);

    public bool ShowChangePassword(bool mandatory, out bool endedMandatory)
    {
        var w = Services.GetRequiredService<ChangePasswordWindow>();
        w.ViewModel.IsMandatory = mandatory;
        endedMandatory = mandatory;
        if (MainWindow is { IsVisible: true } main)
        {
            // 管理画面など別の modal が前面にいる場合、その window を owner にして
            // 強制変更画面が背面へ回らないようにする。
            w.Owner = Windows.OfType<Window>()
                .FirstOrDefault(candidate => candidate.IsActive && !ReferenceEquals(candidate, w))
                ?? main;
        }
        _activeAuthenticationDialog = w;
        try
        {
            var completed = w.ShowDialog() == true;
            endedMandatory = w.ViewModel.IsMandatory;
            return completed;
        }
        finally
        {
            if (ReferenceEquals(_activeAuthenticationDialog, w))
                _activeAuthenticationDialog = null;
        }
    }

    /// <summary>
    /// メイン画面の表示前後を問わず、現在見えている認証 UI を閉じてログインへ戻す。
    /// StartLoginFlowAsync は dialog 終了後に session を再確認し、未認証なら Retry する。
    /// </summary>
    private void ReturnToLogin(string message, string title)
    {
        var main = MainWindow is { IsVisible: true } ? MainWindow : null;
        var authDialog = _activeAuthenticationDialog is { IsVisible: true }
            ? _activeAuthenticationDialog
            : null;
        var owner = Windows.OfType<Window>().FirstOrDefault(window => window.IsActive && window.IsVisible)
            ?? authDialog
            ?? main;

        if (owner is not null)
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        if (main is not null)
        {
            RequestLogout();
            return;
        }

        if (authDialog is not null)
        {
            try { authDialog.DialogResult = false; }
            catch (InvalidOperationException) { authDialog.Close(); }
        }
    }

    public async Task CheckMaintenanceUpdateAsync()
    {
        if (!await StopForPublishedUpdateAsync()) await Maintenance.RefreshAsync();
    }

    private static IServiceProvider BuildServices(AppSettings settings, ThemeService themeService,
        MaintenanceMonitorService maintenance)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(themeService);
        services.AddSingleton(maintenance);
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<LocalFileService>();
        services.AddHttpClient<ApiClient>(c =>
        {
            if (settings.IsConfigured)
                c.BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromMinutes(10);
            // 監査ログにどの端末からの操作かを残すため、全リクエストにマシン名を付与する。
            c.DefaultRequestHeaders.Add("X-Client-Hostname", Environment.MachineName);
        });
        services.AddHttpClient("file-transfer", c =>
        {
            if (settings.IsConfigured)
                c.BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromMinutes(settings.FileTransferTimeoutMinutes);
            c.DefaultRequestHeaders.Add("X-Client-Hostname", Environment.MachineName);
        });
        services.AddHttpClient("settings-test", c => c.Timeout = TimeSpan.FromSeconds(5));
        // 初回パスワード設定の本人確認だけに使う。ログオン中の Windows 資格情報で Negotiate する。
        // Negotiate は接続単位の認証で HTTP/2 では成立しないため、DefaultRequestVersion は
        // 既定の 1.1 のままにしておくこと。
        services.AddHttpClient(ApiClient.WindowsAuthClientName, c =>
        {
            if (settings.IsConfigured)
                c.BaseAddress = new Uri(settings.ServerUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.Add("X-Client-Hostname", Environment.MachineName);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseDefaultCredentials = true,
        });

        // 転送キューはWatashiユーザー単位で分離する。別アカウントへログインし直した際に、
        // 前のユーザーのジョブを新しい権限で誤実行しないため、MainWindowごとに生成・破棄する。
        services.AddTransient<TransferQueueStore>(sp =>
        {
            var userId = sp.GetRequiredService<SessionManager>().UserId
                ?? throw new InvalidOperationException("転送キューはログイン後にだけ作成できます。");
            return new TransferQueueStore(userId, settings.ServerUrl);
        });
        services.AddTransient<ITransferProtocol>(sp => sp.GetRequiredService<ApiClient>());
        services.AddTransient<TransferQueueService>();
        services.AddTransient<TransferQueueViewModel>();
        services.AddTransient<TrustedDevicesViewModel>();

        services.AddTransient<ConnectionSettingsViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<ChangePasswordViewModel>();
        services.AddTransient<InitialPasswordViewModel>();
        services.AddTransient<LocalPaneViewModel>();
        services.AddTransient<RemotePaneViewModel>();
        services.AddTransient<MainViewModel>();

        services.AddTransient<ConnectionSettingsWindow>();
        services.AddTransient<LoginWindow>();
        services.AddTransient<ChangePasswordWindow>();
        services.AddTransient<InitialPasswordWindow>();
        services.AddTransient<MainWindow>();
        services.AddTransient<Views.TrustedDevicesWindow>();
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
        services.AddTransient<ViewModels.Admin.OperationsViewModel>();

        var sp = services.BuildServiceProvider();
        var session = sp.GetRequiredService<SessionManager>();
        session.RefreshDelegate = async (rid, rt, ct) =>
        {
            var api = sp.GetRequiredService<ApiClient>();
            return await api.RefreshAsync(rid, rt, ct);
        };
        // アイドルタイムアウト発火時はメインウィンドウを閉じてログインに戻す。
        // SessionManager 側はタイマーを管理するだけ。UI に戻すのは Dispatcher 経由で行う。
        session.IdleTimedOut += timeout =>
        {
            if (Current is App app)
            {
                app.Dispatcher.BeginInvoke(async () =>
                {
                    if (session.IsIdleTimeoutCurrent(timeout))
                    {
                        // 手動ログアウトと同様、サーバ側でも refresh token を失効させる。
                        // ローカルを消すだけだとサーバ側では最大 30 日間有効なまま残る。
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            // 認証ヘッダの取得で access token の refresh (= refresh token の
                            // ローテーション) が起こり得るため、先にトークンを確定させてから
                            // 最新の rid/rt を読む。先に rid/rt を掴むと、ローテーションで
                            // 発行された新しい refresh token が失効されずに生き残る。
                            var refresh = await session.GetRefreshTokenForLogoutAsync(cts.Token);
                            if (refresh is not null)
                                await sp.GetRequiredService<ApiClient>().LogoutAsync(
                                    refresh.Value.RefreshTokenId,
                                    refresh.Value.RefreshToken,
                                    cts.Token);
                        }
                        catch { /* オフライン等で失効できなくてもログアウト自体は続行する */ }
                        // refresh が 401 で拒否された場合は SessionExpired 側が Clear と
                        // 再ログイン誘導を済ませているので、二重にダイアログを出さない。
                        if (!session.IsAuthenticated) return;
                        session.Clear();
                        app.ReturnToLogin(
                            "無操作のためログアウトしました。再ログインしてください。",
                            "アイドルタイムアウト");
                    }
                });
            }
        };
        // refresh token がサーバに拒否された (期限切れ/パスワード変更/盗難検知など)。
        // SessionManager 側で既に Clear 済みなので、通知して再ログインへ戻すだけ。
        session.SessionExpired += reason =>
        {
            if (Current is App app)
            {
                void NotifyAndReturn()
                {
                    // 通知が Dispatcher 待ちの間に再ログイン済みなら、古い通知で
                    // 新しい認証画面を閉じない。
                    if (!session.IsSessionExpiryPending) return;
                    app.ReturnToLogin(
                        DescribeSessionExpiry(reason) + "\n再ログインしてください。",
                        "セッション期限切れ");
                }

                // メイン画面表示前は同期処理し、Retry で次の login dialog を開いた後に
                // 古い通知が割り込んで閉じてしまう競合を防ぐ。メイン画面表示中は API の
                // 例外処理を先に完了させ、閉じた ViewModel を再入可能にしない。
                if (app.Dispatcher.CheckAccess() && app.MainWindow is not { IsVisible: true })
                    NotifyAndReturn();
                else
                {
                    app.Dispatcher.BeginInvoke(NotifyAndReturn);
                }
            }
        };
        // アプリを開いたままパスワード期限を迎えた場合、refresh で mcp token が
        // 発行される。放置すると全 API が 403 を返し続けるため、強制変更へ遷移する。
        session.RefreshNeedsPasswordChange += () =>
        {
            if (Current is not App app) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                if (!session.IsAuthenticated || app.MainWindow is not { IsVisible: true }) return;

                if (app._activeAuthenticationDialog is ChangePasswordWindow activeChangePassword &&
                    activeChangePassword.IsVisible)
                {
                    activeChangePassword.ViewModel.IsMandatory = true;
                    return;
                }

                if (!app.ShowChangePassword())
                {
                    if (session.IsAuthenticated)
                    {
                        session.Clear();
                        app.RequestLogout();
                    }
                }
            });
        };
        return sp;
    }
}
