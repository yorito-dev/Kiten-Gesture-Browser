using System.Diagnostics;
using System.IO;
using TabBrowser.Models;
using TabBrowser.Models.QuickLook;

namespace TabBrowser.Services.QuickLook;

public readonly record struct QuickLookServiceDiagnosticSnapshot(
    bool IsDisposed,
    bool HasActiveLoad,
    bool HasCurrentRenderer);

public sealed class QuickLookService : IDisposable, IAsyncDisposable
{
    private readonly IReadOnlyList<IQuickLookRenderer> _renderers;
    private CancellationTokenSource? _loadCancellation;
    private IQuickLookRenderer? _currentRenderer;
    private int _generation;
    private bool _isDisposed;

    public QuickLookService(QuickLookSettings settings)
    {
        var normalized = QuickLookSettingsProfile.Normalize(settings);
        _renderers =
        [
            new QuickLookMultipleSelectionRenderer(),
            new QuickLookImageRenderer(normalized),
            new QuickLookZipRenderer(),
            new QuickLookTextRenderer(normalized),
            new QuickLookPdfRenderer(),
            new QuickLookMediaRenderer(normalized),
            new QuickLookWebSummaryRenderer(),
            new QuickLookInformationRenderer()
        ];
    }

    public QuickLookServiceDiagnosticSnapshot GetDiagnosticSnapshot()
    {
        return new QuickLookServiceDiagnosticSnapshot(
            _isDisposed,
            _loadCancellation is not null,
            _currentRenderer is not null);
    }

    public Task<QuickLookRenderResult> LoadAsync(
        QuickLookRequest request,
        CancellationToken cancellationToken = default)
    {
        return LoadCoreAsync(request, immediateResult: null, cancellationToken);
    }

    internal Task<QuickLookRenderResult> LoadWithImmediateResultAsync(
        QuickLookRequest request,
        QuickLookRenderResult immediateResult,
        CancellationToken cancellationToken = default)
    {
        return LoadCoreAsync(request, immediateResult, cancellationToken);
    }

    private async Task<QuickLookRenderResult> LoadCoreAsync(
        QuickLookRequest request,
        QuickLookRenderResult? immediateResult,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        CancelCurrentLoad();
        var generation = Interlocked.Increment(ref _generation);
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var currentCancellation = _loadCancellation;
        try
        {
            if (_currentRenderer is not null)
            {
                await _currentRenderer.UnloadAsync();
            }

            var renderer = _renderers.First(renderer => renderer.CanRender(request));
            _currentRenderer = renderer;
            var result = immediateResult is not null
                && renderer is QuickLookWebSummaryRenderer
                && !QuickLookWebSummaryRenderer.NeedsAsyncRefresh(request, immediateResult)
                    ? immediateResult
                    : await renderer.RenderAsync(request, currentCancellation.Token);
            currentCancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation))
            {
                throw new OperationCanceledException(currentCancellation.Token);
            }

            return result;
        }
        catch (OperationCanceledException) when (currentCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            if (immediateResult is not null)
            {
                return immediateResult;
            }

            return new QuickLookRenderResult(
                QuickLookRenderKind.Error,
                request.PrimaryItem?.DisplayName ?? "プレビューエラー",
                request.PrimaryItem?.FilePath ?? request.SourceId,
                "内容をプレビューできませんでした: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, currentCancellation))
            {
                _loadCancellation = null;
            }

            currentCancellation.Dispose();
        }
    }

    public async ValueTask CloseAsync()
    {
        CancelCurrentLoad();
        Interlocked.Increment(ref _generation);
        if (_currentRenderer is not null)
        {
            await _currentRenderer.UnloadAsync();
            _currentRenderer = null;
        }
    }

    public void OpenWithDefaultApplication(QuickLookItem item)
    {
        if (item.FilePath is { } path && (File.Exists(path) || Directory.Exists(path)))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        if (item.Uri is { } uri
            && System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            Process.Start(new ProcessStartInfo(parsed.ToString()) { UseShellExecute = true });
            return;
        }

        throw new FileNotFoundException("開く対象が見つかりません。", item.FilePath);
    }

    public void ShowInFolder(QuickLookItem item)
    {
        if (item.FilePath is not { } path)
        {
            throw new InvalidOperationException("場所を表示できるファイルパスがありません。");
        }

        var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        if (Directory.Exists(path))
        {
            startInfo.ArgumentList.Add(path);
        }
        else if (File.Exists(path))
        {
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(path);
        }
        else
        {
            throw new FileNotFoundException("表示する場所が見つかりません。", path);
        }

        Process.Start(startInfo);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelCurrentLoad();
        Interlocked.Increment(ref _generation);
        var renderer = _currentRenderer;
        _currentRenderer = null;
        if (renderer is not null)
        {
            try
            {
                renderer.UnloadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (!IsFatalException(ex))
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await CloseAsync();
        _isDisposed = true;
    }

    private void CancelCurrentLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private static bool IsFatalException(Exception exception)
    {
        return exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
    }
}
