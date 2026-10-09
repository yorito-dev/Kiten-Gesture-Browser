using System.Windows;
using System.Windows.Media;
using TabBrowser.Models.QuickLook;

namespace TabBrowser.Services.QuickLook;

public enum QuickLookRenderKind
{
    Information,
    WebSummary,
    Image,
    Text,
    Interactive,
    Error
}

public sealed record QuickLookRenderResult(
    QuickLookRenderKind Kind,
    string Title,
    string Subtitle,
    string Body,
    ImageSource? PreviewImage = null,
    ImageSource? Icon = null,
    FrameworkElement? InteractiveContent = null,
    bool IsTruncated = false,
    string? Detail = null,
    string? TypeLabel = null);

public interface IQuickLookRenderer
{
    bool CanRender(QuickLookRequest request);

    Task<QuickLookRenderResult> RenderAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken);

    ValueTask UnloadAsync();
}

public interface IQuickLookHost : IDisposable
{
    bool IsOpen { get; }

    Task ShowAsync(QuickLookRequest request, CancellationToken cancellationToken = default);

    void Close();
}
