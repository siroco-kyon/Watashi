using System.Reflection;

namespace Watashi.Client.Services;

public static class AppVersion
{
    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    public static string Display => CleanVersion(
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static Version Current => TryParse(Display)
        ?? Assembly.GetName().Version
        ?? new Version(0, 0, 0, 0);

    public static Version? TryParse(string? value)
    {
        var clean = CleanVersion(value);
        return Version.TryParse(clean, out var version) ? version : null;
    }

    private static string? CleanVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var clean = value.Trim();
        var metadataIndex = clean.IndexOfAny(['+', '-']);
        if (metadataIndex >= 0)
            clean = clean[..metadataIndex];

        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
