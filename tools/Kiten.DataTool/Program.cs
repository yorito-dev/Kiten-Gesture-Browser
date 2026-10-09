using TabBrowser.Services;
using TabBrowser.Services.DataMaintenance;

namespace Kiten.DataTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var command = CommandLine.Parse(args);
            var service = new KitenDataMaintenanceService();
            var result = command.Name switch
            {
                "backup" => RunBackup(service, command, cancellation.Token),
                "restore" => RunRestore(service, command, cancellation.Token),
                "recover" => RunRecovery(service, command, cancellation.Token),
                _ => throw new ArgumentException("backup、restore、recoverのいずれかを指定してください。")
            };
            Console.WriteLine(result.Message);
            if (!string.IsNullOrWhiteSpace(result.OutputPath))
            {
                Console.WriteLine("出力: " + result.OutputPath);
            }

            if (!string.IsNullOrWhiteSpace(result.RecoveryPath))
            {
                Console.WriteLine("復旧記録: " + result.RecoveryPath);
            }

            if (!string.IsNullOrWhiteSpace(result.VerifiedArchiveSha256))
            {
                Console.WriteLine("確認済みZIP SHA-256: " + result.VerifiedArchiveSha256);
            }

            return result.Succeeded ? 0 : 2;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            PrintUsage();
            return 64;
        }
        catch
        {
            Console.Error.WriteLine("保守ツールを開始できませんでした。データは変更していません。");
            return 1;
        }
    }

    private static KitenDataMaintenanceResult RunBackup(
        KitenDataMaintenanceService service,
        CommandLine command,
        CancellationToken cancellationToken)
    {
        command.EnsureOnlyKnownOptions(
            "channel", "data-root", "destination", "release-info", "confirm",
            "plan", "include-browsing-history", "include-recent-items", "include-download-history");
        var request = new KitenBackupRequest(
            ParseChannel(command.Required("channel")),
            command.Required("data-root"),
            command.Required("destination"),
            command.Optional("release-info"),
            command.HasFlag("include-browsing-history"),
            command.HasFlag("include-recent-items"),
            command.HasFlag("include-download-history"),
            command.Optional("confirm"));
        return command.HasFlag("plan")
            ? service.PlanBackup(request)
            : service.CreateBackup(request, cancellationToken);
    }

    private static KitenDataMaintenanceResult RunRestore(
        KitenDataMaintenanceService service,
        CommandLine command,
        CancellationToken cancellationToken)
    {
        command.EnsureOnlyKnownOptions(
            "channel", "data-root", "archive", "confirm", "expected-sha256", "plan", "apply");
        var request = new KitenRestoreRequest(
            ParseChannel(command.Required("channel")),
            command.Required("data-root"),
            command.Required("archive"),
            command.HasFlag("apply"),
            command.Optional("confirm"),
            command.Optional("expected-sha256"));
        return command.HasFlag("apply")
            ? service.Restore(request, cancellationToken)
            : service.PlanRestore(request, cancellationToken);
    }

    private static KitenDataMaintenanceResult RunRecovery(
        KitenDataMaintenanceService service,
        CommandLine command,
        CancellationToken cancellationToken)
    {
        command.EnsureOnlyKnownOptions("channel", "data-root", "confirm", "plan");
        var request = new KitenRecoveryRequest(
            ParseChannel(command.Required("channel")),
            command.Required("data-root"),
            command.Optional("confirm"));
        return command.HasFlag("plan")
            ? service.PlanRollbackInterruptedRestore(request, cancellationToken)
            : service.RollbackInterruptedRestore(request, cancellationToken);
    }

    private static ApplicationChannel ParseChannel(string value) => value.ToLowerInvariant() switch
    {
        "stable" => ApplicationChannel.Stable,
        "development" => ApplicationChannel.Development,
        _ => throw new ArgumentException("--channelにはstableまたはdevelopmentを指定してください。")
    };

    private static void PrintUsage()
    {
        Console.Error.WriteLine("使用例:");
        Console.Error.WriteLine("  Kiten.DataTool.exe backup --channel stable --data-root <絶対パス> --destination <絶対パス> --plan");
        Console.Error.WriteLine("  Kiten.DataTool.exe backup --channel stable --data-root <絶対パス> --destination <絶対パス> --confirm BACKUP");
        Console.Error.WriteLine("    履歴は --include-browsing-history、--include-recent-items、--include-download-history で個別指定");
        Console.Error.WriteLine("  Kiten.DataTool.exe restore --channel stable --data-root <絶対パス> --archive <ZIP> --plan");
        Console.Error.WriteLine("  Kiten.DataTool.exe restore --channel stable --data-root <絶対パス> --archive <ZIP> --apply --confirm RESTORE --expected-sha256 <計画で確認したSHA-256>");
        Console.Error.WriteLine("  Kiten.DataTool.exe recover --channel stable --data-root <絶対パス> --plan");
        Console.Error.WriteLine("  Kiten.DataTool.exe recover --channel stable --data-root <絶対パス> --confirm ROLLBACK");
    }

    private sealed class CommandLine
    {
        private static readonly HashSet<string> FlagNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "plan",
            "apply",
            "include-browsing-history",
            "include-recent-items",
            "include-download-history"
        };

        private readonly Dictionary<string, string?> _options;

        private CommandLine(string name, Dictionary<string, string?> options)
        {
            Name = name;
            _options = options;
        }

        public string Name { get; }

        public static CommandLine Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                throw new ArgumentException("操作を指定してください。");
            }

            var command = args[0].Trim().ToLowerInvariant();
            var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < args.Count; index++)
            {
                var argument = args[index];
                if (!argument.StartsWith("--", StringComparison.Ordinal) || argument.Length <= 2)
                {
                    throw new ArgumentException("オプションは--名前の形式で指定してください。");
                }

                var name = argument[2..];
                if (options.ContainsKey(name))
                {
                    throw new ArgumentException("同じオプションを複数回指定できません: --" + name);
                }

                if (FlagNames.Contains(name))
                {
                    options.Add(name, null);
                    continue;
                }

                if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("オプションの値がありません: --" + name);
                }

                options.Add(name, args[++index]);
            }

            return new CommandLine(command, options);
        }

        public string Required(string name)
        {
            var value = Optional(name);
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("必須オプションがありません: --" + name)
                : value;
        }

        public string? Optional(string name) =>
            _options.TryGetValue(name, out var value) ? value : null;

        public bool HasFlag(string name) => _options.ContainsKey(name);

        public void EnsureOnlyKnownOptions(params string[] names)
        {
            var known = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = _options.Keys.FirstOrDefault(name => !known.Contains(name));
            if (unknown is not null)
            {
                throw new ArgumentException("未対応のオプションです: --" + unknown);
            }

            if (HasFlag("plan") && HasFlag("apply"))
            {
                throw new ArgumentException("--planと--applyは同時に指定できません。");
            }
        }
    }
}
