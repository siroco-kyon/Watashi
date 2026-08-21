using System.Windows;
using Microsoft.Win32;

namespace Watashi.Client.Services;

/// <summary>
/// 配色の適用と切り替え。Application.Resources の先頭にある配色辞書
/// (Themes/Colors.Light.xaml か Themes/Colors.Dark.xaml) を差し替える。
///
/// Themes/Colors.xaml のブラシは色を DynamicResource で引いているため、辞書を
/// 入れ替えるだけでブラシのインスタンスはそのままに色だけが更新される。
/// 各画面の {StaticResource XxxBrush} は同じインスタンスを掴み続けるので、
/// 開いたままのウィンドウも再起動なしで追従する。
/// </summary>
public sealed class ThemeManager
{
    private const string LightSource = "Themes/Colors.Light.xaml";
    private const string DarkSource = "Themes/Colors.Dark.xaml";

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private readonly AppSettings _settings;
    private readonly Application _application;

    public ThemeManager(AppSettings settings, Application application)
    {
        _settings = settings;
        _application = application;
    }

    /// <summary>利用者が選んだ設定。System の場合は OS 追従。</summary>
    public ThemeMode Mode => _settings.ThemeMode;

    /// <summary>実際に適用されている配色がダークか。</summary>
    public bool IsDarkApplied { get; private set; }

    /// <summary>配色が変わったときに発生する。UI のチェック状態更新に使う。</summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// 起動時に一度だけ呼ぶ。保存された設定を適用し、OS 側の変更を監視し始める。
    /// ウィンドウが作られるより前に呼ぶこと (最初の描画からダークにするため)。
    /// </summary>
    public void Initialize()
    {
        // タイトルバー (非クライアント領域) は WPF のリソースが効かないため、
        // ウィンドウが HWND を持った時点で個別に DWM へ設定する。
        EventManager.RegisterClassHandler(
            typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAnyWindowLoaded));

        // アプリ終了まで購読したままにする。ThemeManager は App と同じ寿命。
        SystemEvents.UserPreferenceChanged += (_, e) => OnUserPreferenceChanged(e);
        ApplyCurrent();
    }

    /// <summary>利用者の選択を保存して即座に適用する。</summary>
    public void Select(ThemeMode mode)
    {
        if (_settings.ThemeMode == mode) return;
        _settings.ThemeMode = mode;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            // 保存に失敗しても今のセッションの見た目は切り替える。次回起動時に元へ戻るだけ。
            AppLog.Error("配色設定の保存に失敗", ex);
        }
        ApplyCurrent();
    }

    /// <summary>現在の設定と OS の状態から、適用すべき配色を決めて反映する。</summary>
    public void ApplyCurrent()
    {
        var dark = ShouldUseDark();
        SwapPalette(dark);
        IsDarkApplied = dark;
        foreach (var window in _application.Windows.OfType<Window>())
            WindowTitleBarTheme.Apply(window, dark);
        ThemeChanged?.Invoke();
    }

    private bool ShouldUseDark()
    {
        // ハイコントラストが優先。Controls.xaml の各スタイルが OS のシステム色へ
        // 切り替わる仕組みになっており、ダーク配色を重ねると壊れる。
        if (SystemParameters.HighContrast) return false;

        return Mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemUsingDarkTheme(),
        };
    }

    /// <summary>Windows の「アプリのモード」設定を読む。読めない環境ではライト扱い。</summary>
    public static bool IsSystemUsingDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // 値が無い = ダークモードを持たない古い Windows。
            return key?.GetValue(AppsUseLightThemeValue) is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private void SwapPalette(bool dark)
    {
        var dictionaries = _application.Resources.MergedDictionaries;
        var wantedFile = dark ? "Colors.Dark.xaml" : "Colors.Light.xaml";
        var index = IndexOfPalette(dictionaries);

        if (index < 0)
        {
            // App.xaml が配色辞書を先頭に持つ前提が崩れている。差し替え先が無いので追加する。
            dictionaries.Insert(0, LoadPalette(dark));
            return;
        }

        if (IsPaletteFile(dictionaries[index], wantedFile)) return;
        // 同じ位置へ差し替える。検索順が変わると他の辞書のキーを上書きしてしまう。
        dictionaries[index] = LoadPalette(dark);
    }

    private static ResourceDictionary LoadPalette(bool dark) =>
        new() { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Relative) };

    private static int IndexOfPalette(IList<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            if (IsPaletteFile(dictionaries[i], "Colors.Light.xaml") ||
                IsPaletteFile(dictionaries[i], "Colors.Dark.xaml"))
                return i;
        }
        return -1;
    }

    // Source は pack URI へ正規化されることがあるため、末尾のファイル名で判定する。
    private static bool IsPaletteFile(ResourceDictionary dictionary, string fileName) =>
        dictionary.Source?.OriginalString.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true;

    private void OnAnyWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) WindowTitleBarTheme.Apply(window, IsDarkApplied);
    }

    private void OnUserPreferenceChanged(UserPreferenceChangedEventArgs e)
    {
        // OS 側のテーマ変更もハイコントラストの切替も General/Color/Accessibility で通知される。
        if (e.Category is not (UserPreferenceCategory.General or
                               UserPreferenceCategory.Color or
                               UserPreferenceCategory.Accessibility))
            return;

        // SystemEvents は専用スレッドから発火する。リソース操作は UI スレッドで行う。
        _application.Dispatcher.BeginInvoke(new Action(ApplyCurrent));
    }
}
