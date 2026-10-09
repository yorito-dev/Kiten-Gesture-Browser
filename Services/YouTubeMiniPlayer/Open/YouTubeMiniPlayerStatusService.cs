namespace TabBrowser.Services;

public static class YouTubeMiniPlayerStatusService
{
    public const int SoftWarningThreshold = 4;

    public static string CreateOpenedStatus(int openPlayerCount)
    {
        return openPlayerCount > SoftWarningThreshold
            ? $"YouTubeミニプレイヤーを開きました。{openPlayerCount}個開いているため、再生数が多い場合はCPU/GPU負荷に注意してください"
            : "YouTubeミニプレイヤーを開きました";
    }
}
