using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace Watashi.Client.Services;

/// <summary>
/// WPF 標準 ThemeMode と Watashi 固有のカラーパレットを同期する。
/// ThemeMode は .NET 10 でも experimental のため、このクラス以外へ依存を広げない。
/// </summary>
public sealed class ThemeService : ObservableObject, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private readonly AppSettings _settings;
    private string _selectedMode;
    private bool _disposed;

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        _selectedMode = AppThemeModes.Normalize(settings.ThemeMode);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplyCore();
    }

    public IReadOnlyList<ThemeOption> Options { get; } =
    [
        new(AppThemeModes.System, "Windows に合わせる"),
        new(AppThemeModes.Light, "ライト"),
        new(AppThemeModes.Dark, "ダーク"),
    ];

    public string SelectedMode
    {
        get => _selectedMode;
        set
        {
            var normalized = AppThemeModes.Normalize(value);
            if (!SetProperty(ref _selectedMode, normalized)) return;

            ApplyCore();
            _settings.ThemeMode = normalized;
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                AppLog.Error("テーマ設定の保存に失敗しました。", ex);
            }
        }
    }

    public bool IsDarkEffective =>
        SelectedMode == AppThemeModes.Dark ||
        (SelectedMode == AppThemeModes.System && IsSystemDarkMode());

    private void ApplyCore()
    {
        var application = Application.Current;
        if (application is null) return;

#pragma warning disable WPF0001
        application.ThemeMode = SelectedMode switch
        {
            AppThemeModes.Dark => ThemeMode.Dark,
            AppThemeModes.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001

        ReplacePaletteDictionaries(application.Resources, IsDarkEffective ? "Dark" : "Light");
        OnPropertyChanged(nameof(IsDarkEffective));
    }

    private static void ReplacePaletteDictionaries(ResourceDictionary dictionary, string palette)
    {
        for (var i = 0; i < dictionary.MergedDictionaries.Count; i++)
        {
            var child = dictionary.MergedDictionaries[i];
            if (IsPaletteDictionary(child.Source))
            {
                dictionary.MergedDictionaries[i] = new ResourceDictionary
                {
                    Source = BuildPaletteUri(palette),
                };
                continue;
            }

            ReplacePaletteDictionaries(child, palette);
        }
    }

    private static Uri BuildPaletteUri(string palette)
    {
        // Branding guide の手順で AssemblyName を変更してもテーマ切替を壊さない。
        var assemblyName = typeof(ThemeService).Assembly.GetName().Name
            ?? throw new InvalidOperationException("クライアントのアセンブリ名を取得できませんでした。");
        return new Uri(
            $"pack://application:,,,/{Uri.EscapeDataString(assemblyName)};component/Themes/Colors.{palette}.xaml",
            UriKind.Absolute);
    }

    private static bool IsPaletteDictionary(Uri? source)
    {
        if (source is null) return false;
        var value = source.OriginalString.Replace('\\', '/');
        return value.EndsWith("/Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith("/Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightTheme) is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (SelectedMode != AppThemeModes.System) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke(ApplyCore);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}

public sealed record ThemeOption(string Value, string Label);
