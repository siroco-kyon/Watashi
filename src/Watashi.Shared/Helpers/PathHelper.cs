namespace Watashi.Shared.Helpers;

public static class PathHelper
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var p = path.Replace('\\', '/').TrimEnd('/');
        if (!p.StartsWith('/')) p = '/' + p;
        var segments = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var seg in segments)
        {
            if (seg == "..")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else if (seg != ".")
            {
                stack.Push(seg);
            }
        }
        return "/" + string.Join("/", stack.Reverse());
    }

    public static bool IsPathWithin(string allowedPath, string requestedPath)
    {
        var a = NormalizePath(allowedPath);
        var r = NormalizePath(requestedPath);
        if (a == "/") return true;
        return string.Equals(a, r, StringComparison.OrdinalIgnoreCase)
            || r.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetParent(string path)
    {
        var p = NormalizePath(path);
        var idx = p.LastIndexOf('/');
        return idx <= 0 ? "/" : p[..idx];
    }
}
