using System.IO;

namespace TabBrowser.Services;

/// <summary>共有診断ログの行整形、容量確認、排他的な追記を所有する。</summary>
internal sealed class DiagnosticLogWriter(Func<string> getDirectory, Func<string> getLogPath)
{
    internal const long MaximumLogBytes = 256 * 1024;
    private readonly object _gate = new();

    public static string BuildLine(IEnumerable<string> fields, DateTimeOffset? timestamp = null)
    {
        var normalizedFields = new List<string>
        {
            (timestamp ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
        };
        normalizedFields.AddRange(fields.Where(static field => !string.IsNullOrWhiteSpace(field)));
        return string.Join(" | ", normalizedFields);
    }

    public static string SummarizeDiagnosticUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var suffix = url.IndexOfAny(['?', '#']);
        var address = suffix < 0 ? url : url[..suffix];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is "tabbrowser" or "about")
        {
            return address;
        }

        var path = uri.AbsolutePath;
        return string.IsNullOrWhiteSpace(path) || path == "/"
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}{path}";
    }

    public void AppendLines(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                var directory = getDirectory();
                Directory.CreateDirectory(directory);
                var logPath = getLogPath();
                if (File.Exists(logPath) && new FileInfo(logPath).Length > MaximumLogBytes)
                {
                    File.Delete(logPath);
                }

                File.AppendAllText(logPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            }
        }
        catch
        {
            // 診断ログの失敗で通常操作は止めない。
        }
    }
}
