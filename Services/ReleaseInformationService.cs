using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace TabBrowser.Services;

public sealed record ReleaseBuildInformation(
    string ProductName,
    string PublicVersion,
    string SourceCommit,
    string ReleaseClass,
    string BuildVerification,
    Uri? DistributionPageUri,
    Uri? IssueReportUri);

public sealed record ReleaseInformationField(string Name, string Value);

public sealed record ReleaseInformationSnapshot(
    ReleaseBuildInformation Build,
    string Channel,
    string ProcessArchitecture,
    string WindowsVersion,
    string DotNetRuntimeVersion,
    string WebView2RuntimeVersion)
{
    public IReadOnlyList<ReleaseInformationField> Fields =>
    [
        new("製品名", Build.ProductName),
        new("公開版番号", Build.PublicVersion),
        new("ソースSHA", Build.SourceCommit),
        new("公開区分", Build.ReleaseClass),
        new("実行チャンネル", Channel),
        new("ビルド状態", Build.BuildVerification),
        new("実行アーキテクチャ", ProcessArchitecture),
        new("Windows", WindowsVersion),
        new(".NET Runtime", DotNetRuntimeVersion),
        new("WebView2 Runtime", WebView2RuntimeVersion)
    ];

    public string FormatForClipboard()
    {
        return string.Join(
            Environment.NewLine,
            new[] { "Kiten 診断用基本情報" }
                .Concat(Fields.Select(static field => $"{field.Name}: {field.Value}")));
    }
}

public sealed record ReleaseInformationActionResult(bool Succeeded, string Message);

public sealed class ReleaseInformationService
{
    internal const string UnknownValue = "未取得";
    internal const string UnverifiedBuildMessage = "公開検証未実施（未コミット変更を含む可能性）";
    internal const string DirtyBuildMessage = "未コミット変更を含む開発ビルド";
    internal const string VerifiedCleanBuildMessage = "clean検証済み候補（公開承認前）";

    private const string PublicVersionMetadataName = "KitenPublicVersion";
    private const string ReleaseClassMetadataName = "KitenReleaseClass";
    private const string BuildVerificationMetadataName = "KitenBuildVerification";
    private const string SourceRevisionMetadataName = "KitenSourceRevisionId";
    private const string DistributionPageUrlMetadataName = "KitenDistributionPageUrl";
    private const string IssueReportUrlMetadataName = "KitenIssueReportUrl";

    private static readonly Regex PublicVersionPattern = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SourceCommitPattern = new(
        @"(?:\+|\.)(?<sha>[0-9a-fA-F]{40})(?:$|[.+-])",
        RegexOptions.CultureInvariant);

    private readonly Assembly _assembly;

    public ReleaseInformationService(Assembly assembly)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }

    public ReleaseInformationSnapshot Capture(
        ApplicationChannel channel,
        IEnumerable<string?>? existingWebView2RuntimeVersions)
    {
        var build = ReadBuildInformation(_assembly);
        return CreateSnapshot(
            build,
            channel,
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            existingWebView2RuntimeVersions);
    }

    internal static ReleaseBuildInformation ReadBuildInformation(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(static item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Last().Value,
                StringComparer.Ordinal);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var productName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        return ParseBuildInformation(productName, informationalVersion, metadata);
    }

    internal static ReleaseBuildInformation ParseBuildInformation(
        string? productName,
        string? informationalVersion,
        IReadOnlyDictionary<string, string?>? metadata)
    {
        metadata ??= new Dictionary<string, string?>();
        var publicVersion = GetMetadata(metadata, PublicVersionMetadataName);
        if (!PublicVersionPattern.IsMatch(publicVersion ?? string.Empty))
        {
            publicVersion = UnknownValue;
        }
        publicVersion ??= UnknownValue;

        var releaseClass = GetMetadata(metadata, ReleaseClassMetadataName);
        if (!string.Equals(releaseClass, "Preview", StringComparison.Ordinal)
            && !string.Equals(releaseClass, "Stable", StringComparison.Ordinal))
        {
            releaseClass = UnknownValue;
        }
        releaseClass ??= UnknownValue;

        var sourceCommit = NormalizeSourceCommit(GetMetadata(metadata, SourceRevisionMetadataName))
            ?? ExtractSourceCommit(informationalVersion)
            ?? UnknownValue;
        var verification = FormatBuildVerification(
            GetMetadata(metadata, BuildVerificationMetadataName),
            sourceCommit != UnknownValue);

        return new ReleaseBuildInformation(
            NormalizeScalar(productName, maximumLength: 100),
            publicVersion,
            sourceCommit,
            releaseClass,
            verification,
            TryCreatePublicLink(GetMetadata(metadata, DistributionPageUrlMetadataName), out var distributionPage)
                ? distributionPage
                : null,
            TryCreatePublicLink(GetMetadata(metadata, IssueReportUrlMetadataName), out var issueReport)
                ? issueReport
                : null);
    }

    internal static ReleaseInformationSnapshot CreateSnapshot(
        ReleaseBuildInformation build,
        ApplicationChannel channel,
        string? processArchitecture,
        string? windowsVersion,
        string? dotNetRuntimeVersion,
        IEnumerable<string?>? existingWebView2RuntimeVersions)
    {
        ArgumentNullException.ThrowIfNull(build);
        return new ReleaseInformationSnapshot(
            build,
            channel == ApplicationChannel.Development ? "開発版" : "常用版",
            NormalizeScalar(processArchitecture, maximumLength: 32),
            NormalizeScalar(windowsVersion, maximumLength: 160),
            NormalizeScalar(dotNetRuntimeVersion, maximumLength: 160),
            FormatWebView2RuntimeVersions(existingWebView2RuntimeVersions));
    }

    public static async Task<ReleaseInformationActionResult> CopyBasicInformationAsync(
        ReleaseInformationSnapshot snapshot,
        Func<string, Task<ClipboardWriteResult>> copyTextAsync)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(copyTextAsync);
        try
        {
            var result = await copyTextAsync(snapshot.FormatForClipboard());
            return result.IsSuccess
                ? new ReleaseInformationActionResult(true, "診断用基本情報をコピーしました")
                : new ReleaseInformationActionResult(false, "診断用基本情報をコピーできませんでした");
        }
        catch
        {
            return new ReleaseInformationActionResult(false, "診断用基本情報をコピーできませんでした");
        }
    }

    public static ReleaseInformationActionResult OpenConfiguredPage(
        Uri? uri,
        string pageName,
        Func<Uri, ExternalBrowserLaunchResult> openPage)
    {
        ArgumentNullException.ThrowIfNull(openPage);
        if (uri is null)
        {
            return new ReleaseInformationActionResult(false, $"{pageName}は準備中です");
        }

        if (!TryCreatePublicLink(uri.AbsoluteUri, out var validatedUri)
            || validatedUri is null)
        {
            return new ReleaseInformationActionResult(false, $"{pageName}は準備中です");
        }

        try
        {
            var result = openPage(validatedUri);
            return result.Succeeded
                ? new ReleaseInformationActionResult(true, $"{pageName}を開きました")
                : new ReleaseInformationActionResult(false, $"{pageName}を開けませんでした");
        }
        catch
        {
            return new ReleaseInformationActionResult(false, $"{pageName}を開けませんでした");
        }
    }

    internal static bool TryCreatePublicLink(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Contains('<')
            || candidate.Contains('>')
            || candidate.Contains('{')
            || candidate.Contains('}')
            || candidate.Contains("未決定", StringComparison.Ordinal)
            || candidate.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("todo", StringComparison.OrdinalIgnoreCase)
            || candidate.Contains("tbd", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Host)
            || parsed.IsLoopback
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || IsExampleHost(parsed.Host))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string? GetMetadata(IReadOnlyDictionary<string, string?> metadata, string name)
    {
        return metadata.TryGetValue(name, out var value) ? value : null;
    }

    private static string FormatBuildVerification(string? value, bool hasKnownSourceCommit)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "verified-clean" when hasKnownSourceCommit => VerifiedCleanBuildMessage,
            "dirty" => DirtyBuildMessage,
            _ => UnverifiedBuildMessage
        };
    }

    private static string? ExtractSourceCommit(string? informationalVersion)
    {
        var match = SourceCommitPattern.Match(informationalVersion ?? string.Empty);
        return match.Success ? NormalizeSourceCommit(match.Groups["sha"].Value) : null;
    }

    private static string? NormalizeSourceCommit(string? value)
    {
        var candidate = value?.Trim();
        return candidate is { Length: 40 }
            && candidate.All(static character => Uri.IsHexDigit(character))
                ? candidate.ToLowerInvariant()
                : null;
    }

    private static string NormalizeScalar(string? value, int maximumLength)
    {
        var candidate = value?.Trim();
        return string.IsNullOrEmpty(candidate)
            || candidate.Length > maximumLength
            || candidate.Any(static character => character is '\r' or '\n' or '\0')
                ? UnknownValue
                : candidate;
    }

    private static string FormatWebView2RuntimeVersions(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return UnknownValue;
        }

        try
        {
            var versions = values
                .Select(value => NormalizeScalar(value, maximumLength: 100))
                .Where(static value => value != UnknownValue)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return versions.Count == 0
                ? UnknownValue
                : string.Join(" / ", versions);
        }
        catch
        {
            return UnknownValue;
        }
    }

    private static bool IsExampleHost(string host)
    {
        var reservedHosts = new[] { "example.com", "example.net", "example.org" };
        var reservedTopLevelDomains = new[] { ".example", ".invalid", ".localhost", ".test" };
        return reservedHosts.Any(example =>
                string.Equals(host, example, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith('.' + example, StringComparison.OrdinalIgnoreCase))
            || reservedTopLevelDomains.Any(suffix =>
                string.Equals(host, suffix[1..], StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
