using System.IO;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;
using TabBrowser.Views;

namespace TabBrowser.Services.QuickLook;

internal sealed class QuickLookPdfRenderer : IQuickLookRenderer
{
    private QuickLookPdfView? _view;
    private Task _releaseTask = Task.CompletedTask;

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && request.PrimaryItem?.FilePath is { } path
            && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = request.PrimaryItem!;
        if (!File.Exists(item.FilePath))
        {
            return Task.FromResult(QuickLookInformationRenderer.CreateMissingResult(item));
        }

        _view = new QuickLookPdfView(item.FilePath!, request.IsPrivate);
        _view.SetReleaseOwner(request.PdfReleaseOwner);
        return Task.FromResult(new QuickLookRenderResult(
            QuickLookRenderKind.Interactive,
            item.DisplayName,
            item.FilePath!,
            string.Empty,
            InteractiveContent: _view));
    }

    public ValueTask UnloadAsync()
    {
        if (_view is { } view)
        {
            view.Dispose();
            _releaseTask = view.ReleaseCompletion;
            _view = null;
        }
        return new ValueTask(_releaseTask);
    }
}

internal sealed class QuickLookMediaRenderer(QuickLookSettings settings) : IQuickLookRenderer
{
    private QuickLookMediaView? _view;

    public bool CanRender(QuickLookRequest request)
    {
        return request.Items.Count == 1
            && request.PrimaryItem?.FilePath is { } path
            && QuickLookMediaFormats.IsSupported(path);
    }

    public Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = request.PrimaryItem!;
        if (!File.Exists(item.FilePath))
        {
            return Task.FromResult(QuickLookInformationRenderer.CreateMissingResult(item));
        }

        _view = new QuickLookMediaView(item.FilePath!, settings.AutoPlayMedia);
        return Task.FromResult(new QuickLookRenderResult(
            QuickLookRenderKind.Interactive,
            item.DisplayName,
            item.FilePath!,
            string.Empty,
            InteractiveContent: _view));
    }

    public ValueTask UnloadAsync()
    {
        _view?.Dispose();
        _view = null;
        return ValueTask.CompletedTask;
    }
}

internal static class QuickLookMediaFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".m4v", ".mpeg", ".mpg", ".wmv", ".ts",
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma"
    };

    public static bool IsSupported(string path)
    {
        return Extensions.Contains(Path.GetExtension(path));
    }
}
