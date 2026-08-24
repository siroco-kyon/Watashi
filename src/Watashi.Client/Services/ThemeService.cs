using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace Watashi.Client.Services;

/// <summary>
/// WPF 標準 ThemeMode と Watashi 固有のカラーパレットを同期する。
/// ThemeMode は .NET 10 でも experimental のため、このクラス以外へ依存を広げない。
/// </summary>
public sealed class ThemeService : ObservableObject
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private readonly AppSettings _settings;
    private string _selectedMode;

    public ThemeService(AppSettings settings)
    {
        _settings = settings;
        var storedMode = AppThemeModes.Normalize(settings.ThemeMode);
        _selectedMode = storedMode == AppThemeModes.System
            ? AppThemeModes.ResolveInitialMode(storedMode, IsSystemDarkMode())
            : storedMode;
        ApplyCore();
        if (!string.Equals(settings.ThemeMode, _selectedMode, StringComparison.Ordinal))
        {
            _settings.ThemeMode = _selectedMode;
            TrySave();
        }
    }

    public bool IsDarkEffective => _selectedMode == AppThemeModes.Dark;

    /// <summary>現在表示しているテーマとは反対側の、ボタンを押した後の状態を説明する。</summary>
    public string ToggleLabel => IsDarkEffective
        ? "ライトモードに切り替え"
        : "ダークモードに切り替え";

    public void Toggle()
    {
        var nextMode = IsDarkEffective ? AppThemeModes.Light : AppThemeModes.Dark;
        SetMode(nextMode);
    }

    public void SetMode(string? mode)
    {
        var nextMode = AppThemeModes.Normalize(mode) == AppThemeModes.Dark
            ? AppThemeModes.Dark
            : AppThemeModes.Light;
        if (!SetProperty(ref _selectedMode, nextMode)) return;

        ApplyCore();
        _settings.ThemeMode = nextMode;
        TrySave();
    }

    private void ApplyCore()
    {
        var application = Application.Current;
        if (application is null) return;

#pragma warning disable WPF0001
        application.ThemeMode = IsDarkEffective ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        ReplacePaletteDictionaries(application.Resources, IsDarkEffective ? "Dark" : "Light");
        OnPropertyChanged(nameof(IsDarkEffective));
        OnPropertyChanged(nameof(ToggleLabel));
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

    private void TrySave()
    {
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
