using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class WebMessageExceptionDiagnosticGateTests
{
    [Fact]
    public void KnownTypeRecordsOnlyTypeAndExceptionClass()
    {
        var gate = new WebMessageExceptionDiagnosticGate(() => DateTimeOffset.UnixEpoch);

        Assert.True(gate.TryGetFields(
            "gesture",
            new InvalidOperationException("https://example.test/?token=secret"),
            out var fields));
        Assert.Equal(["web_message_error", "type=gesture", "exception=InvalidOperationException"], fields);
    }

    [Fact]
    public void UnknownTypeCannotEnterDiagnosticLog()
    {
        var gate = new WebMessageExceptionDiagnosticGate(() => DateTimeOffset.UnixEpoch);

        Assert.True(gate.TryGetFields("selectedText=secret", new FormatException("query=private"), out var fields));
        Assert.Equal(["web_message_error", "type=unknown", "exception=FormatException"], fields);
    }

    [Fact]
    public void RepeatedPairIsRecordedAtMostOncePerSixtySeconds()
    {
        var now = DateTimeOffset.UnixEpoch;
        var gate = new WebMessageExceptionDiagnosticGate(() => now);

        Assert.True(gate.TryGetFields("gesture", new FormatException(), out _));
        now = now.AddSeconds(59);
        Assert.False(gate.TryGetFields("gesture", new FormatException(), out _));
        Assert.True(gate.TryGetFields("gesture", new InvalidOperationException(), out _));
        Assert.True(gate.TryGetFields("newTabSearch", new FormatException(), out _));
        now = now.AddSeconds(1);
        Assert.True(gate.TryGetFields("gesture", new FormatException(), out _));
    }

    [Fact]
    public void MainWindowRecordsCaughtWebMessageErrorsThroughGate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TabBrowser.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var code = File.ReadAllText(Path.Combine(directory.FullName, "MainWindow.xaml.cs"));
        var start = code.IndexOf("private void HandleWebMessage(", StringComparison.Ordinal);
        var end = code.IndexOf("private Task ExecuteBrowserCommandAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var handler = code[start..end];

        Assert.Contains("s_webMessageExceptionDiagnosticGate.TryGetFields", handler);
        Assert.Contains("AppendDiagnosticLogLine(fields);", handler);
    }
}
