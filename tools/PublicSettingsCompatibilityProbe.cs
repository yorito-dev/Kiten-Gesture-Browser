using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// 製品のApp／Windowを生成せず、明示されたDLLと新しい架空設定だけを試験する。
internal static class PublicSettingsCompatibilityProbe
{
    private static readonly string[] RootKeys =
    [
        "enableOnePasswordIntegration", "onePasswordCliPath", "ytDlpPath",
        "enableYouTubePoTokenProvider", "youTubePoTokenPluginDirectory",
        "youTubePoTokenProviderBaseUrl", "useMwebClient",
        "chatGptReadability", "fiveChLiveReadability", "enableXReadableMode"
    ];

    private static readonly List<string> Cases = [];

    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var internalAssembly = new ProbeContext(args[0]).LoadFromAssemblyPath(Path.GetFullPath(args[0]));
        var publicAssembly = new ProbeContext(args[1]).LoadFromAssemblyPath(Path.GetFullPath(args[1]));
        Require(internalAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } version &&
            version == publicAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            "内部用と公開用DLLの版・埋込みsource SHAが一致");
        var root = Path.Combine(Path.GetFullPath(args[2]), "fixtures");
        if (Directory.Exists(root)) throw new IOException("既存の設定fixtureは上書きしません。");
        Directory.CreateDirectory(root);
        var internalModel = internalAssembly.GetType("TabBrowser.Models.AppSettings", throwOnError: true)!;
        var publicModel = publicAssembly.GetType("TabBrowser.Models.AppSettings", throwOnError: true)!;
        var toolbarModel = publicAssembly.GetType("TabBrowser.Models.ToolbarVisibilitySettings", throwOnError: true)!;
        foreach (var name in new[]
        {
            "EnableOnePasswordIntegration", "OnePasswordCliPath", "YtDlpPath", "EnableYouTubePoTokenProvider",
            "YouTubePoTokenPluginDirectory", "YouTubePoTokenProviderBaseUrl", "UseMwebClient",
            "YouTubePoTokenProviderValidatedConfigurationKey", "ChatGptReadability", "FiveChLiveReadability", "EnableXReadableMode"
        }) Require(publicModel.GetProperty(name) is null, "公開用property除外: " + name);
        Require(toolbarModel.GetProperty("ShowOnePasswordCandidateButton") is null, "公開用toolbar property除外");

        var freshPath = Path.Combine(root, "fresh", "settings.json");
        var fresh = Create(publicModel);
        Save(publicAssembly, freshPath, fresh);
        var freshJson = Read(freshPath);
        Require(RootKeys.All(key => !freshJson.ContainsKey(key)) &&
            !freshJson["toolbarVisibility"]!.AsObject().ContainsKey("showOnePasswordCandidateButton"), "新規設定に互換キーを生成しない");

        var path = Path.Combine(root, "roundtrip", "settings.json");
        var original = Create(internalModel);
        Set(original, "EnableXReadableMode", false);
        Set(original, "EnableOnePasswordIntegration", true);
        Set(original, "OnePasswordCliPath", @"C:\架空試験\one,tool.exe");
        Set(original, "YtDlpPath", @"C:\架空試験\download.exe");
        Set(original, "EnableYouTubePoTokenProvider", true);
        Set(original, "YouTubePoTokenPluginDirectory", @"C:\架空試験\plugin");
        Set(original, "YouTubePoTokenProviderBaseUrl", "http://127.0.0.1:4416/fictional");
        Set(original, "UseMwebClient", false);
        Set(original, "YouTubePoTokenProviderValidatedConfigurationKey", "fictional-transient-do-not-save");
        Set(Get(original, "ToolbarVisibility")!, "ShowOnePasswordCandidateButton", true);
        Set(Get(original, "ChatGptReadability")!, "IsEnabled", true);
        Set(Get(original, "ChatGptReadability")!, "FontScalePercent", 175);
        Set(Get(original, "ChatGptReadability")!, "ContentWidth", "wide");
        Set(Get(original, "FiveChLiveReadability")!, "AutoRefresh", false);
        Set(Get(original, "FiveChLiveReadability")!, "RefreshIntervalSeconds", 17);
        Set(Get(original, "FiveChLiveReadability")!, "FontSize", 22);
        Save(internalAssembly, path, original);
        var expected = Read(path);
        var input = Read(path);
        input["fictionalUnknown"] = "do-not-preserve";
        input["youTubePoTokenProviderValidatedConfigurationKey"] = "fictional-transient-do-not-save";
        input["toolbarVisibility"]!["fictionalUnknown"] = true;
        File.WriteAllText(path, input.ToJsonString());
        var loaded = Load(publicAssembly, path);
        var clone = InvokeClone(publicAssembly, "TabBrowser.SettingsWindow", "CloneSettings", loaded);
        Require(!ReferenceEquals(Get(loaded, "PreservedCompatibilitySettings"), Get(clone, "PreservedCompatibilitySettings")), "設定Cloneの互換辞書を分離");
        Require(!ReferenceEquals(Get(Get(loaded, "ToolbarVisibility")!, "PreservedCompatibilitySettings"),
            Get(Get(clone, "ToolbarVisibility")!, "PreservedCompatibilitySettings")), "設定Cloneのtoolbar互換辞書を分離");
        var toolbarClone = InvokeClone(publicAssembly, "TabBrowser.MainWindow", "CloneToolbarVisibility", Get(loaded, "ToolbarVisibility")!);
        Require(!ReferenceEquals(Get(Get(loaded, "ToolbarVisibility")!, "PreservedCompatibilitySettings"),
            Get(toolbarClone, "PreservedCompatibilitySettings")), "MainWindowのtoolbar Cloneを分離");
        Set(clone, "SearchEngine", "FictionalPublicEdit");
        Save(publicAssembly, path, clone, asynchronous: true);
        Require(SameLegacy(Read(path), expected), "内部→公開Clone／非同期保存の互換値保持");
        Require(!Read(path).ContainsKey("fictionalUnknown") &&
            !Read(path).ContainsKey("youTubePoTokenProviderValidatedConfigurationKey") &&
            !Read(path)["toolbarVisibility"]!.AsObject().ContainsKey("fictionalUnknown"), "未知キーと一時検証値を保存しない");
        var returned = Load(internalAssembly, path);
        var returnedPath = Path.Combine(root, "returned", "settings.json");
        var wrongPathRejected = false;
        try { Save(internalAssembly, returnedPath, returned); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            wrongPathRejected = true;
        }
        Require(wrongPathRejected && !File.Exists(returnedPath), "読込元と異なるpathへの保存を拒否し既存設定を混在させない");
        // 互換値の往復は架空ファイルを移してから、その保存先を基点として読み直す。
        Directory.CreateDirectory(Path.GetDirectoryName(returnedPath)!);
        File.Copy(path, returnedPath);
        returned = Load(internalAssembly, returnedPath);
        Save(internalAssembly, returnedPath, returned);
        Require(SameLegacy(Read(returnedPath), expected) &&
            Read(returnedPath)["searchEngine"]!.GetValue<string>() == "FictionalPublicEdit", "公開保存→内部版の往復と通常設定の編集");

        var service = publicAssembly.GetType("TabBrowser.Services.SettingsService", throwOnError: true)!;
        var captured = (Action)service.GetMethod("CaptureSaveOperation", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [clone, path])!;
        Set(clone, "SearchEngine", "ChangedAfterSnapshot");
        ((IDictionary)Get(clone, "PreservedCompatibilitySettings")!).Clear();
        captured();
        Require(SameLegacy(Read(path), expected) && Read(path)["searchEngine"]!.GetValue<string>() == "FictionalPublicEdit", "終了用snapshotが互換値を捕捉");

        var nullPath = Path.Combine(root, "null-value", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nullPath)!);
        var nullInput = expected.DeepClone().AsObject();
        nullInput["ytDlpPath"] = null;
        File.WriteAllText(nullPath, nullInput.ToJsonString());
        Save(publicAssembly, nullPath, Load(publicAssembly, nullPath));
        Require(Read(nullPath).ContainsKey("ytDlpPath") && Read(nullPath)["ytDlpPath"] is null, "既存null値をdefaultで上書きしない");

        var backupPath = Path.Combine(root, "backup-repair", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        var backupFile = (string)publicAssembly.GetType("TabBrowser.Services.AtomicFileService", throwOnError: true)!
            .GetMethod("CreateBackupFilePath")!.Invoke(null, [backupPath])!;
        File.WriteAllText(backupFile, expected.ToJsonString());
        File.WriteAllText(backupPath, "{broken-primary");
        _ = Load(publicAssembly, backupPath, asynchronous: true);
        Require(SameLegacy(Read(backupPath), expected) &&
            File.ReadAllText(backupFile) == expected.ToJsonString(), "backupからの非同期復旧が互換値とbackupを保持");

        var brokenPath = Path.Combine(root, "partial-recovery", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(brokenPath)!);
        var complete = expected.ToJsonString();
        var broken = complete[..^1] + ",\"incompleteFixture\":";
        File.WriteAllText(brokenPath, broken);
        _ = Load(publicAssembly, brokenPath);
        Require(SameLegacy(Read(brokenPath), expected), "破損JSONの救済がscalar・表示設定object・toolbar値を保持");
        Require(Directory.GetFiles(Path.GetDirectoryName(brokenPath)!, "settings.json.broken-*.bak")
            .Any(file => File.ReadAllText(file) == broken), "破損原本を保全");
        Require(!Directory.Exists(Path.Combine(Path.GetDirectoryName(brokenPath)!, "1password")), "公開用の設定救済は私用診断folderを生成しない");

        Require(JsonNode.DeepEquals(Read(returnedPath)["chatGptReadability"], expected["chatGptReadability"])
            && JsonNode.DeepEquals(Read(returnedPath)["fiveChLiveReadability"], expected["fiveChLiveReadability"]),
            "ChatGPT・5chの非既定設定が内部版への往復後も一致");
        var objectPath = Path.Combine(root, "site-object", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        var objectInput = expected.DeepClone().AsObject();
        objectInput["chatGptReadability"]!["futureNested"] = JsonNode.Parse("{\"text\":\"架空}\\\"設定\",\"items\":[1,true,null]}");
        objectInput["fiveChLiveReadability"] = null;
        File.WriteAllText(objectPath, objectInput.ToJsonString());
        Save(publicAssembly, objectPath, Load(publicAssembly, objectPath));
        Require(JsonNode.DeepEquals(Read(objectPath)["chatGptReadability"], objectInput["chatGptReadability"])
            && Read(objectPath).ContainsKey("fiveChLiveReadability") && Read(objectPath)["fiveChLiveReadability"] is null,
            "表示設定の未知の入れ子・配列・nullを原形保持");

        var readabilityType = publicAssembly.GetType("TabBrowser.Services.ReadabilityController", true)!;
        var readability = Activator.CreateInstance(readabilityType)!;
        var excluded = publicAssembly.GetType("TabBrowser.Views.LinkPreviewController", true)!;
        var preview = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(excluded);
        excluded.GetField("_readabilityController", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(preview, readability);
        var excludedMethod = excluded.GetMethod("IsLinkPreviewExcludedSite", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Require((bool)excludedMethod.Invoke(preview, ["https://chatgpt.com/c/fictional"])!
            && !(bool)excludedMethod.Invoke(preview, ["https://chatgpt.com.example.invalid/"])!,
            "配布版のリンク先プレビューがChatGPTを除外し偽装hostを除外しない");
        var fiveChType = publicAssembly.GetType("TabBrowser.Services.FiveChLiveReadabilityService", true)!;
        var fiveCh = Activator.CreateInstance(fiveChType)!;
        var reply = fiveChType.GetMethod("IsReplyAnchorLink")!;
        const string thread = "https://example.5ch.io/test/read.cgi/livejupiter/1234567890/";
        Require((bool)reply.Invoke(fiveCh, [thread, thread + "12", ">>12"])!
            && (bool)reply.Invoke(fiveCh, [thread, thread + "#12", "返信"])!
            && !(bool)reply.Invoke(fiveCh, [thread, "https://example.invalid/", "外部リンク"])!,
            "配布版の5ch返信アンカー抑止と通常リンクの区別を保持");

        Require(publicAssembly.GetType("TabBrowser.Services.XReadableScriptBuilder") is null,
            "配布版にX整形スクリプトの生成型が存在しない");
        Require((bool)excludedMethod.Invoke(preview, ["https://x.com/fictional"])!
            && (bool)excludedMethod.Invoke(preview, ["https://twitter.com/fictional"])!
            && !(bool)excludedMethod.Invoke(preview, ["https://x.com.example.invalid/"])!,
            "配布版のリンク先プレビューがX・Twitterを除外し偽装hostを除外しない");
        foreach (var enabled in new[] { true, false })
        {
            var xPath = Path.Combine(root, "x-recovery-" + enabled, "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(xPath)!);
            File.WriteAllText(xPath, "{\"enableXReadableMode\":" + enabled.ToString().ToLowerInvariant() + ",\"incompleteFixture\":");
            var rescued = Load(publicAssembly, xPath);
            Save(publicAssembly, xPath, InvokeClone(publicAssembly, "TabBrowser.SettingsWindow", "CloneSettings", rescued));
            Require(Read(xPath)["enableXReadableMode"]!.GetValue<bool>() == enabled,
                "破損JSONの救済・Clone保存でX設定を保持: " + enabled);
        }
        Require(Read(returnedPath)["enableXReadableMode"]!.GetValue<bool>() == false,
            "Xの非既定設定が内部版への往復後も一致");
        var addressType = publicAssembly.GetType("TabBrowser.Services.AddressService", true)!;
        var address = Activator.CreateInstance(addressType, [Create(publicModel)])!;
        Require((string)addressType.GetMethod("Resolve")!.Invoke(address, ["x fixture"])!
            == "https://x.com/search?q=fixture&src=typed_query"
            && publicAssembly.GetType("TabBrowser.MainWindow", true)!.GetMethod("OpenSelectedTextXSearch",
                BindingFlags.NonPublic | BindingFlags.Instance) is not null, "配布版のX検索と右クリックの入口を維持");
        var internalXScript = (string)internalAssembly.GetType("TabBrowser.Services.XReadableScriptBuilder", true)!
            .GetMethod("Build")!.Invoke(null, [true])!;
        Require(internalXScript.Contains("hasPromotedLabel", StringComparison.Ordinal)
            && internalXScript.Contains("hidden-ad", StringComparison.Ordinal), "通常構成のX広告非表示を維持");
        Require(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(internalXScript.Replace("\r\n", "\n", StringComparison.Ordinal))))
            == "DC710CDF85838AEB4F34097E00F7C2D8FE02AA638475D32E1FB2D63C67CD0AC2", "通常構成のXスクリプトの従来出力が完全一致");
        var streaming = publicAssembly.GetType("TabBrowser.Services.StreamingPlaybackCompatibilityService", true)!;
        bool Eligible(string url) => (bool)streaming.GetMethod("IsEligiblePageUrl")!.Invoke(null, [url])!;
        Require(Eligible("https://www.amazon.co.jp/gp/video/watch/B012345678")
            && Eligible("https://example.invalid/video") && !Eligible("https://www.youtube.com/watch?v=fixture")
            && !Eligible("http://127.0.0.1/video"), "配布用失敗検出はPrimeと一般動画を受け付け、YouTubeとloopbackは維持");
        Require(!(bool)streaming.GetMethod("IsTrustedTopLevelSource")!.Invoke(null,
            ["https://example.invalid/video", "https://other.invalid/frame"])!, "配布用失敗通知で別originを拒否");
        var failureScript = (string)streaming.GetMethod("BuildFailureBridgeScript")!.Invoke(null, ["fixture-token"])!;
        Require(failureScript.Contains("postMessage", StringComparison.Ordinal)
            && !failureScript.Contains("delete ", StringComparison.Ordinal), "配布用の失敗通知はbridgeを削除しない");
        var amazon = publicAssembly.GetType("TabBrowser.Services.AmazonPrimeVideoService", true)!;
        var readinessScript = (string)amazon.GetMethod("BuildReadinessProbeScript")!.Invoke(null, null)!;
        Require(readinessScript.Contains("spinnerVisible", StringComparison.Ordinal)
            && !readinessScript.Contains("chrome.webview", StringComparison.Ordinal)
            && !readinessScript.Contains("reload", StringComparison.Ordinal), "Primeの準備確認は互換処理と独立");
        var coordinator = publicAssembly.GetType("TabBrowser.Services.StreamingPlaybackTabStateCoordinator", true)!;
        var state = Activator.CreateInstance(coordinator, true)!;
        var tabId = Guid.NewGuid();
        object? StateCall(string method, params object?[] values) => coordinator.GetMethod(method)!.Invoke(state, values);
        Require(!(bool)StateCall("HasPlaybackFailure", tabId)!, "配布用の通常ページには失敗案内を生成しない");
        var probeVersion = (int)StateCall("BeginReadinessCheck", tabId)!;
        StateCall("MarkPlaybackFailed", tabId);
        StateCall("DismissNotice", tabId);
        Require((bool)StateCall("HasPlaybackFailure", tabId)! && (bool)StateCall("IsNoticeDismissed", tabId)!, "配布用の失敗状態と案内を閉じた状態を保持");
        StateCall("MarkNavigationStarting", tabId, null);
        Require(!(bool)StateCall("HasPlaybackFailure", tabId)! && !(bool)StateCall("IsNoticeDismissed", tabId)!
            && !(bool)StateCall("IsCurrentReadinessCheck", tabId, probeVersion)!, "配布用のページ遷移で失敗案内と古い準備確認を破棄");
        StateCall("MarkPlaybackFailed", tabId);
        StateCall("Remove", tabId);
        Require(!(bool)StateCall("HasPlaybackFailure", tabId)!, "配布用のタブ破棄で失敗状態を解放");

        File.WriteAllText(Path.Combine(args[2], "internal-x-script.js"), internalXScript, new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(args[2], "public-settings-compatibility.json"), JsonSerializer.Serialize(new
        {
            result = "passed",
            cases = Cases,
            internalAssemblySha256 = Hash(args[0]),
            publicAssemblySha256 = Hash(args[1]),
            internalProductVersion = internalAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            publicProductVersion = publicAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            realUserDataUsed = false,
            productGuiUsed = false,
            legacyKeysRetainedOnlyWhenPresent = true
        }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"公開用設定の実DLL往復: {Cases.Count} 件成功");
    }

    private static object Create(Type model)
    {
        var settings = Activator.CreateInstance(model)!;
        Set(Get(settings, "Downloads")!, "DefaultSaveDirectory", @"C:\KitenFictional\Downloads");
        return settings;
    }

    private static object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static object Load(Assembly assembly, string path, bool asynchronous = false)
    {
        var service = assembly.GetType("TabBrowser.Services.SettingsService", throwOnError: true)!;
        var instance = Activator.CreateInstance(service, path)!;
        if (!asynchronous) return service.GetMethod("Load")!.Invoke(instance, null)!;
        var task = (Task)service.GetMethod("LoadAsync")!.Invoke(instance, null)!;
        task.GetAwaiter().GetResult();
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static void Save(Assembly assembly, string path, object settings, bool asynchronous = false)
    {
        var service = assembly.GetType("TabBrowser.Services.SettingsService", throwOnError: true)!;
        var method = service.GetMethod(asynchronous ? "SaveToFileAsync" : "SaveToFile", BindingFlags.Public | BindingFlags.Static)!;
        var result = method.Invoke(null, [path, settings]);
        if (asynchronous) ((Task)result!).GetAwaiter().GetResult();
    }

    private static object InvokeClone(Assembly assembly, string type, string method, object settings)
        => assembly.GetType(type, throwOnError: true)!.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [settings])!;

    private static bool SameLegacy(JsonObject actual, JsonObject expected)
        => RootKeys.All(key => actual.ContainsKey(key) && JsonNode.DeepEquals(actual[key], expected[key]))
            && JsonNode.DeepEquals(actual["toolbarVisibility"]!["showOnePasswordCandidateButton"],
                expected["toolbarVisibility"]!["showOnePasswordCandidateButton"]);

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("設定互換試験の失敗: " + name);
        Cases.Add(name);
    }

    private sealed class ProbeContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(assemblyPath));

        protected override Assembly? Load(AssemblyName name)
        {
            // Frameworkは試験hostから共有し、製品DLLは別々のcontextで読込む。
            if (name.Name is null || name.Name.StartsWith("System", StringComparison.Ordinal) ||
                name.Name.StartsWith("Microsoft.Win32", StringComparison.Ordinal) ||
                name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase") return null;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
