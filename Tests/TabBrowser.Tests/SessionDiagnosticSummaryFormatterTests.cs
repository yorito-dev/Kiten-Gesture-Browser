using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SessionDiagnosticSummaryFormatterTests
{
    [Fact]
    public void EmptyInputUsesUnreadableFallback()
    {
        Assert.Equal(
            "直近のセッション診断を読み取れませんでした。",
            SessionDiagnosticSummaryFormatter.Format([]));
    }

    [Fact]
    public void FormatBuildsTimestampDetailAndExceptionLinesFromPersistedShape()
    {
        var summary = SessionDiagnosticSummaryFormatter.Format(
        [
            "2026-09-10 10:00:00.000 +09:00 | runner=session-service | operation=load-failed | detail=source=backup;target=C:\\session.json;file=backup.json;tab=abc;window=2;windowTabs=4;automatic=True;extra=value;note | type=IOException | message=read failed"
        ]);

        Assert.Equal(
            "直近のセッション診断:\n\n" +
            "・2026-09-10 10:00:00.000 +09:00\n" +
            "  セッションファイルの読み込みに失敗しました\n" +
            "  詳細: 復旧元=backup / 対象=C:\\session.json / ファイル=backup.json / タブ=abc / ウィンドウ=2 / ウィンドウ内タブ数=4 / 自動復旧=True / extra=value / note\n" +
            "  例外: IOException / read failed",
            summary);
    }

    [Fact]
    public void FormatKeepsEntryOrderAndSeparatesEntriesWithBlankLine()
    {
        var summary = SessionDiagnosticSummaryFormatter.Format(
        [
            "first | operation=load-empty",
            "second | operation=custom-operation | malformed | MESSAGE=only message"
        ]);

        Assert.Equal(
            "直近のセッション診断:\n\n" +
            "・first\n" +
            "  復元できるセッションがなく、空の状態で開始しました\n\n" +
            "・second\n" +
            "  custom-operation\n" +
            "  例外: only message",
            summary);
    }

    [Fact]
    public void MissingOperationUsesGenericDescription()
    {
        Assert.Equal(
            "直近のセッション診断:\n\n・timestamp\n  診断イベントを記録しました",
            SessionDiagnosticSummaryFormatter.Format(["timestamp | runner=session-service"]));
    }

    [Theory]
    [InlineData("load-failed", "セッションファイルの読み込みに失敗しました")]
    [InlineData("load-recovered", "バックアップまたはスナップショットから復旧しました")]
    [InlineData("load-empty", "復元できるセッションがなく、空の状態で開始しました")]
    [InlineData("repair-failed", "復旧後のセッションファイル修復に失敗しました")]
    [InlineData("save-failed", "セッション保存に失敗しました")]
    [InlineData("youtube-state-capture-failed", "YouTube 再生状態の保存に失敗しました")]
    [InlineData("window-state-capture-failed", "ウィンドウ状態の保存に失敗しました")]
    [InlineData("tab-state-capture-failed", "タブ状態の保存に失敗しました")]
    [InlineData("startup-session-restore-failed", "起動時のセッション復元に失敗しました")]
    [InlineData("startup-tab-activate-failed", "復元したタブの読み込みに失敗しました")]
    [InlineData("protected-session-restore-failed", "保護復元点の手動復元に失敗しました")]
    [InlineData("closed-tab-history-restore-failed", "閉じたタブ履歴の復元に失敗しました")]
    [InlineData("tab-restore-failed", "一部タブの復元に失敗しました")]
    [InlineData("window-placement-restore-failed", "ウィンドウ位置の復元に失敗しました")]
    [InlineData("window-restore-failed", "一部ウィンドウの復元に失敗しました")]
    [InlineData("webview-recover-failed", "WebView2 の再作成に失敗しました")]
    public void KnownOperationsUseExistingJapaneseDescriptions(string operation, string description)
    {
        var summary = SessionDiagnosticSummaryFormatter.Format([$"timestamp | operation={operation}"]);

        Assert.Contains("  " + description, summary);
    }

    [Fact]
    public void MaximumEntryCountKeepsExistingUiLimit()
    {
        Assert.Equal(8, SessionDiagnosticSummaryFormatter.MaximumEntryCount);
    }
}
