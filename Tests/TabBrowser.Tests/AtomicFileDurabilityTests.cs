using System.Text;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class AtomicFileDurabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FlushFailurePreservesPrimaryAndBackup(bool async)
    {
        var root = Path.Combine(Path.GetTempPath(), "kiten-atomic-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var backup = Path.Combine(root, "state.backup.json");
        try
        {
            await File.WriteAllTextAsync(path, "current-fixture");
            await File.WriteAllTextAsync(backup, "backup-fixture");
            if (async)
                await Assert.ThrowsAsync<ObjectDisposedException>(() => AtomicFileService.WriteAsync(path, backup,
                    stream => { stream.Write(Encoding.UTF8.GetBytes("new-fixture")); stream.Dispose(); return Task.CompletedTask; }));
            else
                Assert.Throws<ObjectDisposedException>(() => AtomicFileService.Write(path, backup,
                    stream => { stream.Write(Encoding.UTF8.GetBytes("new-fixture")); stream.Dispose(); }));
            Assert.Equal("current-fixture", await File.ReadAllTextAsync(path));
            Assert.Equal("backup-fixture", await File.ReadAllTextAsync(backup));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicSuccessMaintainsReplacementAndBackup(bool async)
    {
        var root = Path.Combine(Path.GetTempPath(), "kiten-atomic-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        var backup = Path.Combine(root, "state.backup.json");
        try
        {
            await File.WriteAllTextAsync(path, "current-fixture");
            if (async)
                await AtomicFileService.WriteAsync(path, backup,
                    stream => stream.WriteAsync(Encoding.UTF8.GetBytes("new-fixture")).AsTask());
            else AtomicFileService.Write(path, backup, stream => stream.Write(Encoding.UTF8.GetBytes("new-fixture")));
            Assert.Equal("new-fixture", await File.ReadAllTextAsync(path));
            Assert.Equal("current-fixture", await File.ReadAllTextAsync(backup));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriterDisposalLeavesOwnedStreamAvailableForDurableCommit(bool async)
    {
        var root = Path.Combine(Path.GetTempPath(), "kiten-atomic-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        try
        {
            if (async)
                await AtomicFileService.WriteAsync(path, null, async stream =>
                {
                    await using var writer = new StreamWriter(stream, leaveOpen: true);
                    await writer.WriteAsync("fixture-日本語");
                });
            else
                AtomicFileService.Write(path, null, stream =>
                {
                    using var writer = new StreamWriter(stream, leaveOpen: true);
                    writer.Write("fixture-日本語");
                });
            Assert.Equal("fixture-日本語", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
