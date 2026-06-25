using Microsoft.Data.Sqlite;

namespace Watashi.Server.Services;

public static class DatabaseBackupCommand
{
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, "--backup", StringComparison.OrdinalIgnoreCase));

    public static int Run(IReadOnlyList<string> args)
    {
        string? outputPath = null;
        try
        {
            var databasePath = Path.GetFullPath(RequiredOption(args, "--database"));
            var requestedOutputPath = Path.GetFullPath(RequiredOption(args, "--output"));
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("バックアップ元 DB が見つかりません。", databasePath);
            if (PathsEqual(databasePath, requestedOutputPath) ||
                PathsEqual(ResolvePathLinks(databasePath), ResolvePathLinks(requestedOutputPath)))
                throw new InvalidOperationException("バックアップ元 DB と出力先には別のファイルを指定してください。");

            outputPath = requestedOutputPath;
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            if (File.Exists(outputPath)) File.Delete(outputPath);

            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
            };
            var destinationBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = outputPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            };

            using var source = new SqliteConnection(sourceBuilder.ConnectionString);
            using var destination = new SqliteConnection(destinationBuilder.ConnectionString);
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
            Console.WriteLine($"SQLite backup completed: {outputPath}");
            return 0;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(outputPath))
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
            Console.Error.WriteLine($"SQLite backup failed: {ex.Message}");
            return 1;
        }
    }

    private static string RequiredOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
                return args[i + 1];
        }
        throw new ArgumentException($"必須オプション {name} が指定されていません。");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePathForComparison(left), NormalizePathForComparison(right), GetPathComparison());

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, GetPathComparison())
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    internal static string ResolvePathLinks(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return fullPath;

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".") return fullPath;

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }
            .Distinct()
            .ToArray();
        var current = root;
        foreach (var part in relativePath.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, part);
            current = TryResolveExistingLink(candidate) ?? candidate;
        }

        return Path.GetFullPath(current);
    }

    private static string? TryResolveExistingLink(string path)
    {
        try
        {
            FileSystemInfo info;
            if (Directory.Exists(path))
            {
                info = new DirectoryInfo(path);
            }
            else if (File.Exists(path))
            {
                info = new FileInfo(path);
            }
            else
            {
                return null;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) == 0 &&
                string.IsNullOrEmpty(info.LinkTarget))
                return null;

            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
