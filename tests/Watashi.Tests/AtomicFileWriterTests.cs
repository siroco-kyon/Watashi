using FluentAssertions;
using Watashi.Client.Services;

namespace Watashi.Tests;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task Failed_write_preserves_existing_file_and_removes_temporary_file()
    {
        var directory = NewTestDirectory();
        try
        {
            var destination = Path.Combine(directory, "users.csv");
            await File.WriteAllTextAsync(destination, "existing");

            var action = () => AtomicFileWriter.WriteAsync(destination, async stream =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync("partial");
                await writer.FlushAsync();
                throw new IOException("download failed");
            });

            await action.Should().ThrowAsync<IOException>();
            (await File.ReadAllTextAsync(destination)).Should().Be("existing");
            Directory.GetFiles(directory).Should().Equal(destination);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Successful_write_replaces_existing_file()
    {
        var directory = NewTestDirectory();
        try
        {
            var destination = Path.Combine(directory, "audit.csv");
            await File.WriteAllTextAsync(destination, "old");

            await AtomicFileWriter.WriteAsync(destination, async stream =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync("complete");
            });

            (await File.ReadAllTextAsync(destination)).Should().Be("complete");
            Directory.GetFiles(directory).Should().Equal(destination);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "watashi-atomic-writer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
