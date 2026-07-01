using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Xml.Linq;

namespace Watashi.Client.Services;

public enum StartupUpdateCheckOutcome
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    CheckFailed
}

public sealed record StartupUpdateCheckResult(
    StartupUpdateCheckOutcome Outcome,
    string CurrentVersion,
    string? LatestVersion = null,
    Uri? ManifestUri = null,
    string? Message = null);

public static class StartupUpdateChecker
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    public static async Task<StartupUpdateCheckResult> CheckAsync(string? updateManifestUrl, CancellationToken ct)
    {
        var currentText = AppVersion.Display;
        var current = AppVersion.Current;

        if (string.IsNullOrWhiteSpace(updateManifestUrl))
            return new StartupUpdateCheckResult(StartupUpdateCheckOutcome.NotConfigured, currentText);

        if (!Uri.TryCreate(updateManifestUrl.Trim(), UriKind.Absolute, out var manifestUri))
        {
            return new StartupUpdateCheckResult(
                StartupUpdateCheckOutcome.CheckFailed,
                currentText,
                Message: $"Invalid update manifest URL: {updateManifestUrl}");
        }

        try
        {
            var latestText = await ReadPublishedVersionAsync(manifestUri, ct);
            var latest = AppVersion.TryParse(latestText);
            if (latest is null)
            {
                return new StartupUpdateCheckResult(
                    StartupUpdateCheckOutcome.CheckFailed,
                    currentText,
                    ManifestUri: manifestUri,
                    Message: "Update manifest did not contain a valid version.");
            }

            AppLog.Info($"Startup update check: current={currentText}, latest={latestText}, manifest={manifestUri}");

            return latest.CompareTo(current) > 0
                ? new StartupUpdateCheckResult(StartupUpdateCheckOutcome.UpdateAvailable, currentText, latestText, manifestUri)
                : new StartupUpdateCheckResult(StartupUpdateCheckOutcome.UpToDate, currentText, latestText, manifestUri);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Startup update check failed: manifest={manifestUri}", ex);
            return new StartupUpdateCheckResult(
                StartupUpdateCheckOutcome.CheckFailed,
                currentText,
                ManifestUri: manifestUri,
                Message: ex.Message);
        }
    }

    public static bool LaunchUpdate(Uri manifestUri)
    {
        var process = Process.Start(new ProcessStartInfo(manifestUri.ToString())
        {
            UseShellExecute = true
        });

        return process is not null;
    }

    private static async Task<string?> ReadPublishedVersionAsync(Uri manifestUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MaxAge = TimeSpan.Zero
        };
        request.Headers.Pragma.ParseAdd("no-cache");

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct);
        return ExtractPublishedVersion(xml);
    }

    internal static string? ExtractPublishedVersion(string manifestXml)
    {
        var doc = XDocument.Parse(manifestXml);
        var identities = doc.Descendants()
            .Where(e => e.Name.LocalName == "assemblyIdentity")
            .ToList();

        var applicationIdentity = identities.FirstOrDefault(e =>
            (e.Attribute("name")?.Value ?? string.Empty)
                .EndsWith(".application", StringComparison.OrdinalIgnoreCase));

        return (applicationIdentity ?? identities.FirstOrDefault())?.Attribute("version")?.Value;
    }
}
