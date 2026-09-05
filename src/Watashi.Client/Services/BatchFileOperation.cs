namespace Watashi.Client.Services;

public sealed record FileOperationOutcome(string Name, bool Succeeded, string? Error);

public static class BatchFileOperation
{
    public static async Task<IReadOnlyList<FileOperationOutcome>> RunAsync<T>(
        IEnumerable<T> items, Func<T, string> name, Func<T, Task> operation)
    {
        var results = new List<FileOperationOutcome>();
        foreach (var item in items.ToArray())
        {
            try
            {
                await operation(item);
                results.Add(new(name(item), true, null));
            }
            catch (Exception ex) { results.Add(new(name(item), false, ex.Message)); }
        }
        return results;
    }
}
