using TabBrowser.Models;
using TabBrowser.Services;
using Xunit;

namespace TabBrowser.Tests;

public sealed class SessionServiceTests
{
    [Fact]
    public async Task SaveAsyncPersistsSessionWithoutTemporaryFiles()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var tabId = Guid.NewGuid();
        var tabContainerId = Guid.NewGuid().ToString("N");
        var closedTabContainerId = Guid.NewGuid().ToString("N");
        var closedAt = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero);
        var lastActivatedAt = new DateTimeOffset(2026, 6, 27, 23, 0, 0, TimeSpan.Zero);

        await service.SaveAsync(new SessionData
        {
            LastActiveTabId = tabId,
            Tabs =
            [
                new SessionTabState
                {
                    Id = tabId,
                    Url = "https://example.com",
                    Title = "Example",
                    NavigationHistory = ["https://example.com", "https://example.com/article"],
                    NavigationHistoryIndex = 1,
                    IsPinned = true,
                    LastActivatedAt = lastActivatedAt,
                    ContainerProfileId = tabContainerId,
                    SplitViewId = "split-1",
                    SplitViewOrientation = SplitViewOrientation.TopBottom,
                    SplitViewRatio = 0.68
                }
            ],
            ClosedTabs =
            [
                new ClosedTabState
                {
                    Url = "https://example.com/closed",
                    Title = "Closed",
                    ClosedAt = closedAt,
                    ContainerProfileId = closedTabContainerId
                }
            ]
        });

        var loaded = await service.LoadAsync();

        Assert.Equal(tabId, loaded.LastActiveTabId);
        var tab = Assert.Single(loaded.Tabs);
        Assert.Equal("https://example.com", tab.Url);
        Assert.Equal(1, tab.NavigationHistoryIndex);
        Assert.Equal(2, tab.NavigationHistory.Count);
        Assert.True(tab.IsPinned);
        Assert.Equal(lastActivatedAt, tab.LastActivatedAt);
        Assert.Equal(tabContainerId, tab.ContainerProfileId);
        Assert.Equal("split-1", tab.SplitViewId);
        Assert.Equal(SplitViewOrientation.TopBottom, tab.SplitViewOrientation);
        Assert.Equal(0.68, tab.SplitViewRatio);
        var closedTab = Assert.Single(loaded.ClosedTabs);
        Assert.Equal(closedAt, closedTab.ClosedAt);
        Assert.Equal(closedTabContainerId, closedTab.ContainerProfileId);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task SaveAsyncSerializesConcurrentSessionWrites()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        var saveTasks = Enumerable.Range(0, 16)
            .Select(index => service.SaveAsync(new SessionData
            {
                Tabs =
                [
                    new SessionTabState
                    {
                        Url = $"https://example.com/concurrent-{index}"
                    }
                ]
            }))
            .ToArray();

        await Task.WhenAll(saveTasks);

        var loaded = await service.LoadAsync();
        var tab = Assert.Single(loaded.Tabs);

        Assert.StartsWith("https://example.com/concurrent-", tab.Url);
        Assert.Empty(Directory.GetFiles(directory.DirectoryPath, "*.tmp"));
        Assert.True(Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json").Length <= 6);
    }

    [Fact]
    public async Task EmptyOverwritePreservesPreviousSessionAsProtectedRecoveryPoint()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    Tabs = [new SessionTabState { Url = "https://example.com/protected" }]
                }
            ]
        });
        await service.SaveAsync(new SessionData());

        Assert.True(service.HasProtectedRecoveryPoint());
        var protectedSession = await service.LoadProtectedRecoveryPointAsync();
        Assert.NotNull(protectedSession);
        Assert.Equal(
            "https://example.com/protected",
            Assert.Single(Assert.Single(protectedSession.Windows).Tabs).Url);
        Assert.Empty((await service.LoadAsync()).Windows);
    }

    [Fact]
    public async Task FirstNonEmptyLoadCreatesInitialProtectedRecoveryPoint()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        writer.Save(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/initial-protected" }]
        });
        Assert.False(writer.HasProtectedRecoveryPoint());

        var reader = directory.CreateSessionService();
        var loaded = await reader.LoadAsync();

        Assert.Equal("https://example.com/initial-protected", Assert.Single(loaded.Tabs).Url);
        Assert.True(reader.HasProtectedRecoveryPoint());
        var protectedSession = await reader.LoadProtectedRecoveryPointAsync();
        Assert.NotNull(protectedSession);
        Assert.Equal("https://example.com/initial-protected", Assert.Single(protectedSession.Tabs).Url);
    }

    [Fact]
    public async Task UnexpectedExitCanRestoreProtectedSessionAfterEmptyOverwrite()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/before-crash" }]
        });
        await service.SaveAsync(new SessionData());

        var recovered = await service.LoadAsync(recoverEmptySessionFromProtected: true);

        Assert.Equal("https://example.com/before-crash", Assert.Single(recovered.Tabs).Url);
        Assert.Equal("protected", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.Recovered);
        Assert.Equal(
            "https://example.com/before-crash",
            Assert.Single((await service.LoadAsync()).Tabs).Url);
    }

    [Fact]
    public async Task SynchronousEmptyOverwriteAlsoPreservesProtectedRecoveryPoint()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        service.Save(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/synchronous" }]
        });
        service.Save(new SessionData());

        var protectedSession = await service.LoadProtectedRecoveryPointAsync();
        Assert.NotNull(protectedSession);
        Assert.Equal("https://example.com/synchronous", Assert.Single(protectedSession.Tabs).Url);
    }

    [Fact]
    public async Task ProtectedRecoveryPointsKeepAtMostTwelveNonEmptyGenerations()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        for (var index = 0; index < 14; index++)
        {
            await service.SaveAsync(new SessionData
            {
                Tabs = [new SessionTabState { Url = $"https://example.com/protected-{index}" }]
            });
            await service.SaveAsync(new SessionData());
        }

        var protectedFiles = Directory.GetFiles(
            directory.ProtectedSessionDirectory,
            "protected-session-*.json");
        Assert.Equal(12, protectedFiles.Length);
        Assert.All(
            protectedFiles,
            path => Assert.Contains("https://example.com/protected-", File.ReadAllText(path)));
    }

    [Fact]
    public async Task SaveAndLoadPreservesClosedYouTubeMiniPlayerHistory()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var youTubeId = Guid.NewGuid();
        var localVideoId = Guid.NewGuid();
        var closedAt = new DateTimeOffset(2026, 7, 30, 10, 20, 30, TimeSpan.Zero);

        await service.SaveAsync(new SessionData
        {
            ClosedYouTubeMiniPlayers =
            [
                new YouTubeMiniPlayerState
                {
                    Id = youTubeId,
                    WindowId = 2,
                    SourceKind = MiniPlayerSourceKind.YouTube,
                    ContainerProfileId = "default",
                    Url = "https://www.youtube.com/watch?v=history1",
                    Title = "履歴動画",
                    LastPositionSeconds = 42,
                    PlaybackRate = 1.5,
                    IsMuted = true,
                    UpdatedAt = closedAt
                },
                new YouTubeMiniPlayerState
                {
                    Id = localVideoId,
                    WindowId = 3,
                    SourceKind = MiniPlayerSourceKind.LocalFile,
                    LocalFilePath = @"C:\Videos\history.webm",
                    Title = "history",
                    LastPositionSeconds = 12,
                    UpdatedAt = closedAt.AddMinutes(1)
                }
            ]
        });

        var loaded = await service.LoadAsync();

        Assert.Collection(
            loaded.ClosedYouTubeMiniPlayers,
            item =>
            {
                Assert.Equal(youTubeId, item.Id);
                Assert.Equal("https://www.youtube.com/watch?v=history1", item.Url);
                Assert.Equal(42, item.LastPositionSeconds);
                Assert.Equal(1.5, item.PlaybackRate);
                Assert.True(item.IsMuted);
            },
            item =>
            {
                Assert.Equal(localVideoId, item.Id);
                Assert.Equal(MiniPlayerSourceKind.LocalFile, item.SourceKind);
                Assert.Equal(@"C:\Videos\history.webm", item.LocalFilePath);
                Assert.Equal(12, item.LastPositionSeconds);
            });
    }

    [Fact]
    public async Task SaveAndLoadPreservesClosedWindowAndLinkedTabHistory()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var closedWindowId = Guid.NewGuid();
        var firstTabId = Guid.NewGuid();
        var secondTabId = Guid.NewGuid();
        var closedAt = DateTimeOffset.UtcNow;

        await service.SaveAsync(new SessionData
        {
            ClosedWindows =
            [
                new ClosedWindowState
                {
                    Id = closedWindowId,
                    ClosedAt = closedAt,
                    WindowState = new SessionWindowState
                    {
                        LastActiveTabId = secondTabId,
                        Placement = new WindowPlacementSettings
                        {
                            Left = 120,
                            Top = 80,
                            Width = 1200,
                            Height = 800,
                            State = "Normal"
                        },
                        IsAlwaysOnTop = true,
                        Tabs =
                        [
                            new SessionTabState
                            {
                                Id = firstTabId,
                                Url = "https://example.com/first",
                                Title = "First",
                                SplitViewId = "split-1",
                                SplitViewOrientation = SplitViewOrientation.LeftRight,
                                SplitViewRatio = 0.37
                            },
                            new SessionTabState
                            {
                                Id = secondTabId,
                                Url = "https://example.com/second",
                                Title = "Second",
                                SplitViewId = "split-1",
                                SplitViewOrientation = SplitViewOrientation.LeftRight,
                                SplitViewRatio = 0.37
                            }
                        ]
                    }
                }
            ],
            ClosedTabs =
            [
                new ClosedTabState
                {
                    ClosedWindowId = closedWindowId,
                    SourceTabId = firstTabId,
                    ClosedWindowTabIndex = 0,
                    Url = "https://example.com/first",
                    Title = "First",
                    ClosedAt = closedAt
                }
            ]
        });

        var loaded = await service.LoadAsync();

        var closedWindow = Assert.Single(loaded.ClosedWindows);
        Assert.Equal(closedWindowId, closedWindow.Id);
        Assert.Equal(secondTabId, closedWindow.WindowState.LastActiveTabId);
        Assert.Equal(2, closedWindow.WindowState.Tabs.Count);
        Assert.All(closedWindow.WindowState.Tabs, tab => Assert.Equal(0.37, tab.SplitViewRatio));
        Assert.True(closedWindow.WindowState.IsAlwaysOnTop);
        Assert.Equal(1200, closedWindow.WindowState.Placement?.Width);
        var linkedTab = Assert.Single(loaded.ClosedTabs);
        Assert.Equal(closedWindowId, linkedTab.ClosedWindowId);
        Assert.Equal(firstTabId, linkedTab.SourceTabId);
        Assert.Equal(0, linkedTab.ClosedWindowTabIndex);
    }

    [Fact]
    public async Task LoadClearsClosedTabLinkWhenParentWindowDoesNotExist()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            ClosedTabs =
            [
                new ClosedTabState
                {
                    ClosedWindowId = Guid.NewGuid(),
                    SourceTabId = Guid.NewGuid(),
                    ClosedWindowTabIndex = 2,
                    Url = "https://example.com/orphan",
                    ClosedAt = DateTimeOffset.UtcNow
                }
            ]
        });

        var closedTab = Assert.Single((await service.LoadAsync()).ClosedTabs);

        Assert.Null(closedTab.ClosedWindowId);
        Assert.Null(closedTab.ClosedWindowTabIndex);
        Assert.NotNull(closedTab.SourceTabId);
    }

    [Fact]
    public async Task LoadLegacySessionWithoutClosedMiniPlayerHistoryUsesEmptyHistory()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(
            directory.SessionFile,
            """{"version":2,"tabs":[],"closedTabs":[],"windows":[]}""");

        var loaded = await directory.CreateSessionService().LoadAsync();

        Assert.Empty(loaded.ClosedYouTubeMiniPlayers);
        Assert.Empty(loaded.ClosedWindows);
    }

    [Fact]
    public async Task SaveAndLoadPreservesClosedFolderTabState()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var folderPath = Path.Combine(directory.DirectoryPath, "Documents");

        await service.SaveAsync(new SessionData
        {
            ClosedTabs =
            [
                new ClosedTabState
                {
                    Url = folderPath,
                    Title = "Documents",
                    IsFolderTab = true,
                    FolderTabViewMode = FolderTabViewMode.Icons,
                    FolderTabSortMode = FolderTabSortMode.Size,
                    FolderTabSortDirection = FolderTabSortDirection.Descending,
                    FolderTabState = new FolderTabSessionState
                    {
                        NavigationHistory = [@"C:\work", folderPath],
                        NavigationHistoryIndex = 1,
                        SelectedPaths = [Path.Combine(folderPath, "report.txt")],
                        SelectionAnchorPath = Path.Combine(folderPath, "report.txt"),
                        DetailScrollOffset = 48,
                        IconScrollOffset = 96,
                        DetailColumnOrder = ["TypeText", "Name", "LastWriteTime", "SizeBytes"]
                    }
                }
            ]
        });

        var closedTab = Assert.Single((await service.LoadAsync()).ClosedTabs);
        Assert.True(closedTab.IsFolderTab);
        Assert.Equal(folderPath, closedTab.Url);
        Assert.Equal(FolderTabViewMode.Icons, closedTab.FolderTabViewMode);
        Assert.Equal(FolderTabSortMode.Size, closedTab.FolderTabSortMode);
        Assert.Equal(FolderTabSortDirection.Descending, closedTab.FolderTabSortDirection);
        Assert.Equal([@"C:\work", folderPath], closedTab.FolderTabState?.NavigationHistory);
        Assert.Equal(1, closedTab.FolderTabState?.NavigationHistoryIndex);
        Assert.Equal(48d, closedTab.FolderTabState?.DetailScrollOffset);
        Assert.Equal(96d, closedTab.FolderTabState?.IconScrollOffset);
        Assert.Equal(
            ["TypeText", "Name", "LastWriteTime", "SizeBytes"],
            closedTab.FolderTabState?.DetailColumnOrder);
    }

    [Fact]
    public async Task SaveAsyncPreservesFolderTabKind()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var tabId = Guid.NewGuid();

        await service.SaveAsync(new SessionData
        {
            LastActiveTabId = tabId,
            Tabs =
            [
                new SessionTabState
                {
                    Id = tabId,
                    Url = @"C:\work\documents",
                    Title = "documents",
                    IsFolderTab = true,
                    FolderTabViewMode = FolderTabViewMode.Icons,
                    FolderTabSortMode = FolderTabSortMode.LastWriteTime,
                    FolderTabSortDirection = FolderTabSortDirection.Descending,
                    FolderTabState = new FolderTabSessionState
                    {
                        NavigationHistory = [@"C:\work", @"C:\work\documents"],
                        NavigationHistoryIndex = 1,
                        SelectedPaths = [@"C:\work\documents\one.txt", @"C:\work\documents\two.txt"],
                        SelectionAnchorPath = @"C:\work\documents\two.txt",
                        DetailScrollOffset = 120,
                        IconScrollOffset = 240,
                        DetailColumnOrder = ["LastWriteTime", "Name", "TypeText", "SizeBytes"]
                    },
                    IsPinned = true
                }
            ]
        });

        var loaded = await service.LoadAsync();

        var tab = Assert.Single(loaded.Tabs);
        Assert.Equal(tabId, tab.Id);
        Assert.Equal(@"C:\work\documents", tab.Url);
        Assert.True(tab.IsFolderTab);
        Assert.True(tab.IsPinned);
        Assert.Equal(FolderTabViewMode.Icons, tab.FolderTabViewMode);
        Assert.Equal(FolderTabSortMode.LastWriteTime, tab.FolderTabSortMode);
        Assert.Equal(FolderTabSortDirection.Descending, tab.FolderTabSortDirection);
        Assert.Equal(2, tab.FolderTabState?.NavigationHistory.Count);
        Assert.Equal(2, tab.FolderTabState?.SelectedPaths.Count);
        Assert.Equal(@"C:\work\documents\two.txt", tab.FolderTabState?.SelectionAnchorPath);
        Assert.Equal(120d, tab.FolderTabState?.DetailScrollOffset);
        Assert.Equal(240d, tab.FolderTabState?.IconScrollOffset);
        Assert.Equal(
            ["LastWriteTime", "Name", "TypeText", "SizeBytes"],
            tab.FolderTabState?.DetailColumnOrder);
        Assert.Equal(tabId, loaded.LastActiveTabId);
    }

    [Fact]
    public async Task LoadNormalizesInvalidFolderTabSessionState()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = @"C:\work\documents",
                    IsFolderTab = true,
                    FolderTabState = new FolderTabSessionState
                    {
                        NavigationHistory = [" ", @" C:\work ", @"C:\work\documents"],
                        NavigationHistoryIndex = 99,
                        SelectedPaths = [@" C:\work\documents\one.txt ", @"C:\work\documents\one.txt"],
                        SelectionAnchorPath = " ",
                        DetailScrollOffset = -1,
                        IconScrollOffset = -10,
                        DetailColumnOrder = [" LastWriteTime ", "Unknown", "Name", "Name"]
                    }
                }
            ]
        });

        var state = Assert.Single((await service.LoadAsync()).Tabs).FolderTabState;

        Assert.NotNull(state);
        Assert.Equal([@"C:\work", @"C:\work\documents"], state.NavigationHistory);
        Assert.Equal(1, state.NavigationHistoryIndex);
        Assert.Equal([@"C:\work\documents\one.txt"], state.SelectedPaths);
        Assert.Null(state.SelectionAnchorPath);
        Assert.Equal(0d, state.DetailScrollOffset);
        Assert.Equal(0d, state.IconScrollOffset);
        Assert.Equal(["LastWriteTime", "Name", "TypeText", "SizeBytes"], state.DetailColumnOrder);
    }

    [Fact]
    public async Task LoadCapsFolderTabNavigationHistoryWhileKeepingCurrentEntry()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var history = Enumerable.Range(0, FolderTabService.MaximumNavigationHistoryCount + 10)
            .Select(index => $@"C:\history\{index:D3}")
            .ToList();
        const int currentIndex = 55;
        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = history[currentIndex],
                    IsFolderTab = true,
                    FolderTabState = new FolderTabSessionState
                    {
                        NavigationHistory = history,
                        NavigationHistoryIndex = currentIndex
                    }
                }
            ]
        });

        var state = Assert.Single((await service.LoadAsync()).Tabs).FolderTabState;

        Assert.NotNull(state);
        Assert.Equal(FolderTabService.MaximumNavigationHistoryCount, state.NavigationHistory.Count);
        Assert.Equal(history[currentIndex], state.NavigationHistory[state.NavigationHistoryIndex]);
    }

    [Fact]
    public async Task WindowMemoryUsesSeparateFile()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveWindowMemoryAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    Tabs =
                    [
                        new SessionTabState
                        {
                            Url = "https://example.com/window"
                        }
                    ]
                }
            ]
        });

        var loaded = await service.LoadWindowMemoryAsync();

        Assert.True(service.HasWindowMemory());
        Assert.Empty((await service.LoadAsync()).Tabs);
        Assert.Single(loaded.Windows);
        Assert.Equal("https://example.com/window", loaded.Windows[0].Tabs[0].Url);
    }

    [Fact]
    public async Task LoadWindowMemoryAsyncTreatsLegacyJsonWithoutMiniPlayersAsEmpty()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(
            directory.WindowMemoryFile,
            """
            {
              "version": 2,
              "windows": [
                {
                  "windowId": "11111111-1111-1111-1111-111111111111",
                  "tabs": [
                    {
                      "url": "https://example.com/legacy"
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await directory.CreateSessionService().LoadWindowMemoryAsync();

        Assert.Single(loaded.Windows);
        Assert.Null(loaded.WindowMemoryYouTubeMiniPlayers);
    }

    [Fact]
    public async Task WindowMemoryPersistsYouTubeMiniPlayerSnapshotWithoutTransientData()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var windowId = Guid.NewGuid();
        var player = new YouTubeMiniPlayerState
        {
            Id = Guid.NewGuid(),
            WindowId = 4,
            OwnerSessionWindowId = windowId,
            SourceKind = MiniPlayerSourceKind.YouTube,
            ContainerProfileId = "profile-1",
            Url = "https://www.youtube.com/watch?v=memory123",
            Title = "Memory",
            IsShorts = true,
            IsLiveBroadcast = true,
            ContentZoomFactor = 1.6,
            PreviousContentZoomFactor = 2.2,
            ContentPanX = 0.2,
            ContentPanY = -0.3,
            Left = 50,
            Top = 60,
            Width = 600,
            Height = 340,
            IsMuted = true,
            Volume = 0.45,
            PlaybackRate = 1.25,
            PreviousPlaybackRate = 1.75,
            LastPositionSeconds = 123,
            IsPaused = true,
            IsLooping = false,
            AbRepeatStartSeconds = 100,
            AbRepeatEndSeconds = 120,
            IsAbRepeatEnabled = true,
            IsMinimized = true,
            IsTopmost = false,
            LiveChatWindowLeft = 700,
            LiveChatWindowTop = 100,
            LiveChatWindowWidth = 420,
            LiveChatWindowHeight = 640,
            IsLiveChatWindowTopmost = true,
            IsLiveChatWindowDocked = true,
            LiveChatWindowDockSide = YouTubeLiveChatDockSide.Left,
            ListOrder = 2
        };

        await service.SaveWindowMemoryAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    WindowId = windowId,
                    Tabs = [new SessionTabState { Url = "https://example.com/window" }]
                }
            ],
            WindowMemoryYouTubeMiniPlayers = [player]
        });

        var loaded = Assert.Single(
            (await service.LoadWindowMemoryAsync()).WindowMemoryYouTubeMiniPlayers!);
        var json = await File.ReadAllTextAsync(directory.WindowMemoryFile);

        Assert.Equal(player.OwnerSessionWindowId, loaded.OwnerSessionWindowId);
        Assert.Equal((player.Left, player.Top, player.Width, player.Height),
            (loaded.Left, loaded.Top, loaded.Width, loaded.Height));
        Assert.Equal((player.ContentZoomFactor, player.ContentPanX, player.ContentPanY),
            (loaded.ContentZoomFactor, loaded.ContentPanX, loaded.ContentPanY));
        Assert.Equal((player.IsMuted, player.Volume, player.PlaybackRate, player.LastPositionSeconds),
            (loaded.IsMuted, loaded.Volume, loaded.PlaybackRate, loaded.LastPositionSeconds));
        Assert.Equal((player.AbRepeatStartSeconds, player.AbRepeatEndSeconds, player.IsAbRepeatEnabled),
            (loaded.AbRepeatStartSeconds, loaded.AbRepeatEndSeconds, loaded.IsAbRepeatEnabled));
        Assert.Equal(player.ListOrder, loaded.ListOrder);
        Assert.DoesNotContain("previousContentZoomFactor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("previousPlaybackRate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isLiveBroadcast", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("webStorage", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalSessionOmitsWindowMemoryMiniPlayersAndWindowMemorySaveDoesNotTouchPlayerStore()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var miniPlayerFile = Path.Combine(directory.DirectoryPath, "youtube-mini-players.json");
        var miniPlayerStore = new YouTubeMiniPlayerStore(miniPlayerFile);
        miniPlayerStore.Save(
        [
            new YouTubeMiniPlayerState
            {
                Id = Guid.NewGuid(),
                SourceKind = MiniPlayerSourceKind.YouTube,
                Url = "https://www.youtube.com/watch?v=store123",
                Title = "Store"
            }
        ]);
        var originalStoreJson = await File.ReadAllTextAsync(miniPlayerFile);

        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/session" }]
        });
        await service.SaveWindowMemoryAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    Tabs = [new SessionTabState { Url = "https://example.com/window" }]
                }
            ],
            WindowMemoryYouTubeMiniPlayers = []
        });

        var sessionJson = await File.ReadAllTextAsync(directory.SessionFile);
        var currentStoreJson = await File.ReadAllTextAsync(miniPlayerFile);
        Assert.DoesNotContain("windowMemoryYouTubeMiniPlayers", sessionJson, StringComparison.Ordinal);
        Assert.Equal(originalStoreJson, currentStoreJson);
    }

    [Fact]
    public async Task LoadWindowMemoryAsyncFallsBackToBackupAndRepairsBrokenJson()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveWindowMemoryAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    Tabs =
                    [
                        new SessionTabState
                        {
                            Url = "https://example.com/backup-window"
                        }
                    ]
                }
            ]
        });
        await service.SaveWindowMemoryAsync(new SessionData
        {
            Windows =
            [
                new SessionWindowState
                {
                    Tabs =
                    [
                        new SessionTabState
                        {
                            Url = "https://example.com/current-window"
                        }
                    ]
                }
            ]
        });
        await File.WriteAllTextAsync(directory.WindowMemoryFile, "{ broken json");

        var loaded = await service.LoadWindowMemoryAsync();

        Assert.Equal("https://example.com/backup-window", loaded.Windows[0].Tabs[0].Url);

        File.Delete(directory.WindowMemoryBackupFile);
        var repaired = await service.LoadWindowMemoryAsync();

        Assert.Equal("https://example.com/backup-window", repaired.Windows[0].Tabs[0].Url);
    }

    [Fact]
    public async Task SaveAsyncKeepsPreviousSessionBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/first"
                }
            ]
        });

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/second"
                }
            ]
        });

        var loaded = await service.LoadAsync();
        var backup = await new SessionService(directory.SessionBackupFile, directory.WindowMemoryFile).LoadAsync();

        Assert.Equal("https://example.com/second", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("https://example.com/first", Assert.Single(backup.Tabs).Url);
    }

    [Fact]
    public async Task LoadAsyncFallsBackToSessionBackupForBrokenJson()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/backup"
                }
            ]
        });
        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/current"
                }
            ]
        });
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken json");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Tabs).Url);
        Assert.True(service.LastLoadInfo.Recovered);
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.False(service.LastLoadInfo.RepairFailed);

        File.Delete(directory.SessionBackupFile);
        var repaired = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(repaired.Tabs).Url);
        Assert.False(service.LastLoadInfo.Recovered);
        Assert.Equal("primary", service.LastLoadInfo.Source);
    }

    [Fact]
    public async Task RepairPreservesExactCorruptPrimaryBytesOutsideRecoverySources()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/backup"));
        await service.SaveAsync(CreateSession("https://example.com/current"));
        var original = new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0xFF, 0x00 };
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Tabs).Url);
        Assert.False(service.LastLoadInfo.RepairFailed);
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        var preserved = Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"));
        Assert.Equal(original, await File.ReadAllBytesAsync(preserved));
        Assert.NotEqual(original, await File.ReadAllBytesAsync(directory.SessionFile));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ incomplete")]
    public async Task InvalidPrimaryFormsArePreservedExactly(string invalidJson)
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/backup"));
        await service.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes(invalidJson);
        await File.WriteAllBytesAsync(directory.SessionFile, original);

        await service.LoadAsync();

        Assert.Equal(CorruptPrimaryPreservationStatus.Preserved,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidWindowElementRecoversWithoutReplacingGoodBackup(bool synchronousSave)
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("""{"version":1,"windows":[null]}""");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);

        var service = directory.CreateSessionService();
        var recovered = await service.LoadAsync();
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.Equal("https://example.com/backup", Assert.Single(recovered.Tabs).Url);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));

        if (synchronousSave) service.Save(recovered);
        else await service.SaveAsync(recovered);
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.Equal("https://example.com/backup", Assert.Single(
            (await directory.CreateSessionService().LoadAsync()).Tabs).Url);
    }

    [Fact]
    public async Task InvalidWindowElementWithoutRecoverySourceBlocksLaterSave()
    {
        using var directory = new TempDirectory();
        var original = System.Text.Encoding.UTF8.GetBytes("""{"version":1,"windows":[null]}""");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var service = directory.CreateSessionService();

        var loaded = await service.LoadAsync();
        Assert.Equal("empty", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.Empty(loaded.Windows);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(CreateSession("https://example.com/new")));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.False(File.Exists(directory.SessionBackupFile));
    }

    [Fact]
    public async Task InvalidWindowElementsInPrimaryAndBackupRecoverFromSnapshot()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/snapshot"));
        var invalid = System.Text.Encoding.UTF8.GetBytes("""{"version":1,"windows":[null]}""");
        await File.WriteAllBytesAsync(directory.SessionFile, invalid);
        await File.WriteAllBytesAsync(directory.SessionBackupFile, invalid);

        var service = directory.CreateSessionService();
        var recovered = await service.LoadAsync();

        Assert.Equal("snapshot", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.True(service.LastLoadInfo.BackupLoadFailed);
        Assert.Equal("https://example.com/snapshot", Assert.Single(recovered.Tabs).Url);
        Assert.Equal(invalid, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
    }

    [Fact]
    public async Task InvalidWindowMemoryElementUsesValidBackupAndPreservesOriginal()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/window-backup"));
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/window-current"));
        var original = System.Text.Encoding.UTF8.GetPreamble().Concat(
            System.Text.Encoding.UTF8.GetBytes("""{"version":1,"windows":[null]}""")).ToArray();
        await File.WriteAllBytesAsync(directory.WindowMemoryFile, original);
        var backup = await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile);

        var service = directory.CreateSessionService();
        var recovered = await service.LoadWindowMemoryAsync();
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.Equal("https://example.com/window-backup", Assert.Single(recovered.Tabs).Url);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptWindowMemoryDirectory, "*.bin"))));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
        await service.SaveWindowMemoryAsync(recovered);
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
    }

    [Fact]
    public async Task AnotherServiceCannotOverwriteInvalidWindowElementWithoutLoading()
    {
        using var directory = new TempDirectory();
        var original = System.Text.Encoding.UTF8.GetBytes("""{"version":1,"windows":[null]}""");
        await File.WriteAllBytesAsync(directory.SessionFile, original);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            directory.CreateSessionService().SaveAsync(CreateSession("https://example.com/new")));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
    }

    [Theory]
    [InlineData("\"version\":1,")]
    [InlineData("\"version\":2,")]
    [InlineData("")]
    public async Task Utf8BomSupportedSessionIsReadAsPrimary(string versionField)
    {
        using var directory = new TempDirectory();
        var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(
            System.Text.Encoding.UTF8.GetBytes("{" + versionField
                + "\"tabs\":[{\"url\":\"https://example.com/bom\"}]}")).ToArray();
        await File.WriteAllBytesAsync(directory.SessionFile, bytes);

        var service = directory.CreateSessionService();
        var loaded = await service.LoadAsync();
        Assert.Equal("primary", service.LastLoadInfo.Source);
        Assert.Equal("https://example.com/bom", Assert.Single(loaded.Tabs).Url);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedVersionWithDifferentStructureIsNotReplacedByBackup(bool withBom)
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var json = System.Text.Encoding.UTF8.GetBytes("""{"version":99,"windows":"future-shape"}""");
        var original = withBom ? System.Text.Encoding.UTF8.GetPreamble().Concat(json).ToArray() : json;
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var service = directory.CreateSessionService();

        var ex = await Assert.ThrowsAnyAsync<IOException>(() => service.LoadAsync());
        Assert.Equal("UnsupportedSessionVersionException", ex.GetType().Name);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(CreateSession("https://example.com/new")));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreservationFailureBlocksRepairAndBothSavePaths(int failureStageValue)
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ broken primary");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var failureStage = (CorruptPrimaryPreservationStage)failureStageValue;
        var fail = true;
        var service = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePreservationStage: stage =>
            {
                if (fail && stage == failureStage) throw new IOException("injected preservation failure");
            });

        var recovered = await service.LoadAsync();
        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal(CorruptPrimaryPreservationStatus.Failed,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(recovered));
        Assert.ThrowsAny<IOException>(() => service.Save(recovered));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        if (Directory.Exists(directory.CorruptSessionDirectory))
        {
            Assert.Empty(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"));
            Assert.Empty(Directory.GetFiles(directory.CorruptSessionDirectory, "*.tmp"));
        }

        fail = false;
        await service.SaveAsync(recovered);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
    }

    [Fact]
    public async Task RepairFailureAfterPreservationKeepsOnlyOneVerifiedOriginalAcrossRetry()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ broken primary");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var service = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePrimaryRepair: () => throw new IOException("injected repair failure"));

        var recovered = await service.LoadAsync();
        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal(CorruptPrimaryPreservationStatus.Preserved,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        await service.SaveAsync(recovered);

        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        var afterSave = await File.ReadAllBytesAsync(directory.SessionFile);
        await service.SaveAsync(recovered);
        Assert.Equal(afterSave, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"));
    }

    [Fact]
    public async Task ChangedPrimaryDuringRepairIsNotOverwritten()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken primary");
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var replacement = System.Text.Encoding.UTF8.GetBytes("{ different primary");
        var service = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePrimaryRepair: () => File.WriteAllBytes(directory.SessionFile, replacement));

        var recovered = await service.LoadAsync();

        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(recovered));
        Assert.Equal(replacement, await File.ReadAllBytesAsync(directory.SessionFile));
    }

    [Fact]
    public async Task AnotherServiceSaveWaitsForSamePathRepair()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile, "{ damaged primary");
        var enteredRepair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRepair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveringService = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePrimaryRepair: () =>
            {
                enteredRepair.TrySetResult();
                releaseRepair.Task.GetAwaiter().GetResult();
            });
        var loadTask = Task.Run(() => recoveringService.LoadAsync());
        await enteredRepair.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task laterSave;
        try
        {
            laterSave = directory.CreateSessionService().SaveAsync(
                CreateSession("https://example.com/later"));
            Assert.False(laterSave.IsCompleted);
        }
        finally { releaseRepair.TrySetResult(); }
        var recovered = await loadTask.WaitAsync(TimeSpan.FromSeconds(5));
        await laterSave.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("https://example.com/backup", Assert.Single(recovered.Tabs).Url);
        Assert.Equal("https://example.com/later", Assert.Single(
            (await directory.CreateSessionService().LoadAsync()).Tabs).Url);
        Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"));
    }

    [Fact]
    public async Task ReadFailureIsNotTreatedAsCorruptPrimary()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var primary = await File.ReadAllBytesAsync(directory.SessionFile);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        using (new FileStream(directory.SessionFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => directory.CreateSessionService().LoadAsync());
        }
        Assert.Equal(primary, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task MissingAndValidEmptyPrimaryDoNotCreateCorruptOriginal()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.LoadAsync();
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
        await service.SaveAsync(new SessionData());
        await service.LoadAsync();
        Assert.Equal("primary", service.LastLoadInfo.Source);
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task MissingPrimaryRecoveredFromBackupDoesNotInventCorruptOriginal()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/backup"));
        await service.SaveAsync(CreateSession("https://example.com/current"));
        File.Delete(directory.SessionFile);

        var recovered = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(recovered.Tabs).Url);
        Assert.Equal(CorruptPrimaryPreservationStatus.NotNeeded,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task MismatchedExistingPreservationDoesNotAllowRepair()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ damaged session");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        Directory.CreateDirectory(directory.CorruptSessionDirectory);
        var preservedPath = Path.Combine(directory.CorruptSessionDirectory,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)) + ".bin");
        await File.WriteAllTextAsync(preservedPath, "different data");

        var service = directory.CreateSessionService();
        await service.LoadAsync();

        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal(CorruptPrimaryPreservationStatus.Failed,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.Equal("different data", await File.ReadAllTextAsync(preservedPath));
    }

    [Fact]
    public async Task ModifiedTemporaryPreservationFailsVerificationAndLeavesPrimaryUntouched()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ damaged session");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var backup = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var service = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePreservationStage: stage =>
            {
                if (stage == CorruptPrimaryPreservationStage.Verify)
                    File.WriteAllText(Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.tmp")),
                        "wrong bytes");
            });

        await service.LoadAsync();

        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal(CorruptPrimaryPreservationStatus.Failed,
            service.LastLoadInfo.CorruptPrimaryPreservation);
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.Empty(Directory.GetFiles(directory.CorruptSessionDirectory, "*"));
    }

    [Fact]
    public async Task CorruptPrimaryWithoutValidSourceIsNotReplacedByEmptyExitSave()
    {
        using var directory = new TempDirectory();
        var original = System.Text.Encoding.UTF8.GetBytes("{ only broken copy");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var service = directory.CreateSessionService();

        var loaded = await service.LoadAsync();

        Assert.Equal("empty", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(loaded));
        Assert.ThrowsAny<IOException>(() => service.Save(loaded));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task SnapshotRecoveryPreservesCorruptPrimaryOutsideSnapshotDirectory()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/snapshot"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ damaged session");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        await File.WriteAllTextAsync(directory.SessionBackupFile, "{ damaged backup");

        await service.LoadAsync();

        Assert.Equal("snapshot", service.LastLoadInfo.Source);
        var preserved = Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"));
        Assert.Equal(original, await File.ReadAllBytesAsync(preserved));
        Assert.DoesNotContain(preserved, Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json"));
        Assert.Equal("{ damaged backup", await File.ReadAllTextAsync(directory.SessionBackupFile));
    }

    [Fact]
    public async Task ProtectedRecoveryPreservesCorruptPrimary()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/protected"));
        await service.SaveAsync(new SessionData());
        var original = System.Text.Encoding.UTF8.GetBytes("{ damaged session");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        await File.WriteAllTextAsync(directory.SessionBackupFile, "{ damaged backup");
        foreach (var snapshot in Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json"))
            File.Delete(snapshot);

        var loaded = await service.LoadAsync(recoverEmptySessionFromProtected: true);

        Assert.Equal("protected", service.LastLoadInfo.Source);
        Assert.Equal("https://example.com/protected", Assert.Single(loaded.Tabs).Url);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptSessionDirectory, "*.bin"))));
    }

    [Fact]
    public async Task WindowMemoryRecoveryPreservesItsOwnCorruptOriginal()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveWindowMemoryAsync(CreateSession("https://example.com/window-backup"));
        await service.SaveWindowMemoryAsync(CreateSession("https://example.com/window-current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ broken window memory");
        await File.WriteAllBytesAsync(directory.WindowMemoryFile, original);
        var backup = await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile);

        await service.LoadWindowMemoryAsync();

        Assert.Equal(original, await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(directory.CorruptWindowMemoryDirectory, "*.bin"))));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task FailedWindowMemoryPreservationBlocksLaterSave()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/backup"));
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ broken window memory");
        await File.WriteAllBytesAsync(directory.WindowMemoryFile, original);
        var backup = await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile);
        var service = directory.CreateSessionService(new SessionRecoveryDiagnosticState(),
            beforePreservationStage: stage =>
            {
                if (stage == CorruptPrimaryPreservationStage.Copy)
                    throw new IOException("injected preservation failure");
            });

        var recovered = await service.LoadWindowMemoryAsync();
        Assert.True(service.LastLoadInfo.RepairFailed);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SaveWindowMemoryAsync(recovered));
        Assert.Equal(original, await File.ReadAllBytesAsync(directory.WindowMemoryFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
    }

    [Fact]
    public async Task UnloadedWindowMemoryServiceCannotOverwriteCorruptPrimary()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/backup"));
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/current"));
        var original = System.Text.Encoding.UTF8.GetBytes("{ broken window memory");
        await File.WriteAllBytesAsync(directory.WindowMemoryFile, original);
        var backup = await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile);

        await Assert.ThrowsAnyAsync<IOException>(() => directory.CreateSessionService()
            .SaveWindowMemoryAsync(CreateSession("https://example.com/new")));

        Assert.Equal(original, await File.ReadAllBytesAsync(directory.WindowMemoryFile));
        Assert.Equal(backup, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
    }

    [Fact]
    public async Task FirstSaveWithoutLoadCannotReplaceExistingCorruptPrimary()
    {
        using var directory = new TempDirectory();
        var original = System.Text.Encoding.UTF8.GetBytes("{ existing damaged primary");
        await File.WriteAllBytesAsync(directory.SessionFile, original);
        var service = directory.CreateSessionService();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.SaveAsync(CreateSession("https://example.com/new")));

        Assert.Equal(original, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.False(File.Exists(directory.SessionBackupFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task PreservationDiagnosticDoesNotExposeOriginalBytesOrLocationInStartupStatus()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        const string secret = "secret-example-user@example.com";
        await File.WriteAllTextAsync(directory.SessionFile, "{ " + secret);

        var service = directory.CreateSessionService();
        await service.LoadAsync();

        var status = StartupWindowRestoreController.CreateStartupSessionStatus(service.LastLoadInfo, 0);
        Assert.Contains("破損したセッション原本を別に保全しました", status);
        Assert.DoesNotContain(secret, status);
        Assert.DoesNotContain(directory.DirectoryPath, status);
        var diagnostic = await File.ReadAllTextAsync(directory.DiagnosticFile);
        Assert.Contains("operation=corrupt-primary-preserved", diagnostic);
        Assert.DoesNotContain(secret, diagnostic);
    }

    [Fact]
    public async Task LoadAsyncFallsBackToSessionBackupForEmptyPrimaryFile()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/backup" }]
        });
        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/current" }]
        });
        await File.WriteAllTextAsync(directory.SessionFile, string.Empty);

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Tabs).Url);
        Assert.True(service.LastLoadInfo.Recovered);
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.False(service.LastLoadInfo.RepairFailed);
    }

    [Fact]
    public async Task LoadAsyncTreatsNullPrimaryAsInvalidAndRepairsFromBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/backup"));
        await service.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile, "null");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.False(service.LastLoadInfo.RepairFailed);
        Assert.Equal("https://example.com/backup", Assert.Single((await directory.CreateSessionService().LoadAsync()).Tabs).Url);
    }

    [Fact]
    public async Task LoadAsyncTreatsNullPrimaryAndInvalidBackupAsSnapshotRecovery()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/snapshot"));
        await File.WriteAllTextAsync(directory.SessionFile, "null");
        await File.WriteAllTextAsync(directory.SessionBackupFile, "null");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/snapshot", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("snapshot", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.True(service.LastLoadInfo.BackupLoadFailed);
    }

    [Fact]
    public async Task LoadAsyncKeepsValidEmptyPrimaryInsteadOfOlderBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveAsync(CreateSession("https://example.com/older"));
        await service.SaveAsync(new SessionData());

        var loaded = await service.LoadAsync();

        Assert.Empty(loaded.Tabs);
        Assert.Equal("primary", service.LastLoadInfo.Source);
        Assert.False(service.LastLoadInfo.Recovered);
    }

    [Fact]
    public async Task LoadAsyncWithNoSessionFilesReturnsInitialEmptyState()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        var loaded = await service.LoadAsync();

        Assert.Empty(loaded.Tabs);
        Assert.Equal("empty", service.LastLoadInfo.Source);
        Assert.False(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.False(service.LastLoadInfo.BackupLoadFailed);
        Assert.False(File.Exists(directory.SessionFile));
    }

    [Fact]
    public async Task LoadWindowMemoryTreatsNullPrimaryAsInvalidAndUsesBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await service.SaveWindowMemoryAsync(CreateSession("https://example.com/window-backup"));
        await service.SaveWindowMemoryAsync(CreateSession("https://example.com/window-current"));
        await File.WriteAllTextAsync(directory.WindowMemoryFile, "null");

        var loaded = await service.LoadWindowMemoryAsync();

        Assert.Equal("https://example.com/window-backup", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAfterFailedBackupRepairRetriesSameContentWithoutLosingBackup(bool synchronousSave)
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/only-good-backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken primary");
        var backupBefore = await File.ReadAllBytesAsync(directory.SessionBackupFile);

        var failRepair = true;
        var normalSaveAttempts = 0;
        var service = directory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.NormalSave)
                    normalSaveAttempts++;
            },
            () =>
            {
                if (failRepair) throw new IOException("injected primary repair failure");
            });
        var loaded = await service.LoadAsync();
        Assert.True(service.LastLoadInfo.RepairFailed);
        Assert.Equal("backup", service.LastLoadInfo.Source);
        Assert.Equal("https://example.com/only-good-backup", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("{ broken primary", await File.ReadAllTextAsync(directory.SessionFile));
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(directory.SessionBackupFile));

        failRepair = false;
        if (synchronousSave) service.Save(loaded);
        else await service.SaveAsync(loaded);

        Assert.Equal(1, normalSaveAttempts);
        Assert.Equal("https://example.com/only-good-backup", Assert.Single((await directory.CreateSessionService().LoadAsync()).Tabs).Url);
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        if (synchronousSave) service.Save(loaded);
        else await service.SaveAsync(loaded);
        Assert.Equal(1, normalSaveAttempts);
    }

    [Fact]
    public async Task FailedRetryAfterBackupRecoveryPreservesPrimaryAndOnlyGoodBackup()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken primary");
        var backupBefore = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var service = directory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.NormalSave)
                    throw new IOException("injected normal save failure");
            },
            () => throw new IOException("injected primary repair failure"));
        var loaded = await service.LoadAsync();

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(loaded));

        Assert.Equal(SessionPersistenceDiagnosticResult.Failed,
            service.CaptureRecoveryDiagnosticSnapshot().NormalSave.LastResult);
        Assert.Equal("{ broken primary", await File.ReadAllTextAsync(directory.SessionFile));
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(directory.SessionBackupFile));
    }

    [Fact]
    public async Task SaveWindowMemoryAfterFailedRepairPreservesGoodBackup()
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/window-backup"));
        await writer.SaveWindowMemoryAsync(CreateSession("https://example.com/window-current"));
        await File.WriteAllTextAsync(directory.WindowMemoryFile, "{ broken window memory");
        var backupBefore = await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile);
        var service = directory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            beforePrimaryRepair: () => throw new IOException("injected window memory repair failure"));

        var loaded = await service.LoadWindowMemoryAsync();
        Assert.True(service.LastLoadInfo.RepairFailed);
        await service.SaveWindowMemoryAsync(loaded);

        Assert.Equal("https://example.com/window-backup", Assert.Single((await directory.CreateSessionService().LoadWindowMemoryAsync()).Tabs).Url);
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(directory.WindowMemoryBackupFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedPrimaryVersionBlocksLoadAndLaterSaveWithoutChangingFiles(bool synchronousSave)
    {
        using var directory = new TempDirectory();
        var writer = directory.CreateSessionService();
        await writer.SaveAsync(CreateSession("https://example.com/backup"));
        await writer.SaveAsync(CreateSession("https://example.com/current"));
        await File.WriteAllTextAsync(directory.SessionFile,
            """{"version":99,"tabs":[{"url":"https://example.com/future"}]}""");
        var primaryBefore = await File.ReadAllBytesAsync(directory.SessionFile);
        var backupBefore = await File.ReadAllBytesAsync(directory.SessionBackupFile);
        var service = directory.CreateSessionService();

        var loadFailure = await Assert.ThrowsAnyAsync<IOException>(() => service.LoadAsync());
        Assert.Equal("UnsupportedSessionVersionException", loadFailure.GetType().Name);
        Assert.Equal("unsupported", service.LastLoadInfo.Source);
        if (synchronousSave)
            Assert.ThrowsAny<IOException>(() => service.Save(CreateSession("https://example.com/new")));
        else
            await Assert.ThrowsAnyAsync<IOException>(() => service.SaveAsync(CreateSession("https://example.com/new")));

        Assert.Equal(SessionPersistenceDiagnosticResult.Failed,
            service.CaptureRecoveryDiagnosticSnapshot().NormalSave.LastResult);
        Assert.Equal(primaryBefore, await File.ReadAllBytesAsync(directory.SessionFile));
        Assert.Equal(backupBefore, await File.ReadAllBytesAsync(directory.SessionBackupFile));
        Assert.False(Directory.Exists(directory.CorruptSessionDirectory));
    }

    [Fact]
    public async Task AnotherSessionServiceCannotOverwriteUnsupportedPrimaryWithoutLoading()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.SessionFile,
            """{"version":99,"tabs":[{"url":"https://example.com/future"}]}""");
        var before = await File.ReadAllBytesAsync(directory.SessionFile);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            directory.CreateSessionService().SaveAsync(CreateSession("https://example.com/new")));

        Assert.Equal(before, await File.ReadAllBytesAsync(directory.SessionFile));
    }

    [Fact]
    public async Task UnsupportedWindowMemoryVersionCannotBeReplaced()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.WindowMemoryFile,
            """{"version":99,"windows":[{"tabs":[{"url":"https://example.com/future"}]}]}""");
        var before = await File.ReadAllBytesAsync(directory.WindowMemoryFile);
        var service = directory.CreateSessionService();

        await Assert.ThrowsAnyAsync<IOException>(() => service.LoadWindowMemoryAsync());
        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.SaveWindowMemoryAsync(CreateSession("https://example.com/new")));

        Assert.Equal(before, await File.ReadAllBytesAsync(directory.WindowMemoryFile));
    }

    [Theory]
    [InlineData("\"version\":1,")]
    [InlineData("\"version\":2,")]
    [InlineData("")]
    public async Task SupportedAndVersionlessSessionsRemainReadable(string versionField)
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(directory.SessionFile,
            "{" + versionField + "\"tabs\":[{\"url\":\"https://example.com/compatible\"}]}");

        var service = directory.CreateSessionService();
        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/compatible", Assert.Single(loaded.Tabs).Url);
        Assert.Equal("primary", service.LastLoadInfo.Source);
    }

    [Fact]
    public async Task LoadAsyncWritesDiagnosticWhenRecoveringFromBackup()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/backup"
                }
            ]
        });
        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/current"
                }
            ]
        });
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken json");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/backup", Assert.Single(loaded.Tabs).Url);
        var diagnostic = await File.ReadAllTextAsync(directory.DiagnosticFile);
        Assert.Contains("runner=session-service", diagnostic);
        Assert.Contains("operation=load-failed", diagnostic);
        Assert.Contains("source=primary", diagnostic);
        Assert.Contains("operation=load-recovered", diagnostic);
        Assert.Contains("source=backup", diagnostic);
    }

    [Fact]
    public async Task LoadAsyncFallsBackToNewestSnapshotWhenSessionAndBackupAreBroken()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/first"
                }
            ]
        });
        await service.SaveAsync(new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/latest"
                }
            ]
        });
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken session json");
        await File.WriteAllTextAsync(directory.SessionBackupFile, "{ broken backup json");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/latest", Assert.Single(loaded.Tabs).Url);
        Assert.True(service.LastLoadInfo.Recovered);
        Assert.Equal("snapshot", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.True(service.LastLoadInfo.BackupLoadFailed);

        Directory.Delete(directory.SessionSnapshotDirectory, recursive: true);
        File.Delete(directory.SessionBackupFile);
        var repaired = await service.LoadAsync();

        Assert.Equal("https://example.com/latest", Assert.Single(repaired.Tabs).Url);
    }

    [Fact]
    public async Task LoadAsyncSkipsBrokenNewestSnapshotAndUsesPreviousSnapshot()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/previous" }]
        });
        await service.SaveAsync(new SessionData
        {
            Tabs = [new SessionTabState { Url = "https://example.com/latest" }]
        });

        var latestSnapshot = Directory
            .GetFiles(directory.SessionSnapshotDirectory, "session-*.json")
            .Single(path => File.ReadAllText(path).Contains("https://example.com/latest", StringComparison.Ordinal));
        await File.WriteAllTextAsync(latestSnapshot, "{ broken newest snapshot");
        File.SetCreationTimeUtc(latestSnapshot, DateTime.UtcNow.AddMinutes(1));
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken session json");
        await File.WriteAllTextAsync(directory.SessionBackupFile, "{ broken backup json");

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/previous", Assert.Single(loaded.Tabs).Url);
        Assert.True(service.LastLoadInfo.Recovered);
        Assert.Equal("snapshot", service.LastLoadInfo.Source);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
        Assert.True(service.LastLoadInfo.BackupLoadFailed);
    }

    [Fact]
    public async Task SaveAsyncTrimsOldSessionSnapshots()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();

        for (var i = 0; i < 9; i++)
        {
            await service.SaveAsync(new SessionData
            {
                Tabs =
                [
                    new SessionTabState
                    {
                        Url = $"https://example.com/{i}"
                    }
                ]
            });
        }

        Assert.Equal(6, Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json").Length);
    }

    [Fact]
    public async Task SaveAsyncSkipsUnchangedSessionSnapshot()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var session = new SessionData
        {
            Tabs =
            [
                new SessionTabState
                {
                    Url = "https://example.com/same"
                }
            ]
        };

        await service.SaveAsync(session);
        await service.SaveAsync(session);

        Assert.Single(Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json"));
    }

    [Fact]
    public async Task SaveAsyncReportsSnapshotFailureWithoutFailingNormalSave()
    {
        using var directory = new TempDirectory();
        var state = new SessionRecoveryDiagnosticState();
        var service = directory.CreateSessionService(
            state,
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation)
                {
                    throw new IOException("test snapshot failure");
                }
            });

        await service.SaveAsync(CreateSession("https://example.com/snapshot-failure"));

        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.NormalSave.LastResult);
        Assert.Equal(SessionPersistenceDiagnosticResult.Failed, diagnostic.AutomaticSnapshot.LastResult);
        Assert.Equal(SessionPersistenceFailureCategory.InputOutput, diagnostic.AutomaticSnapshot.FailureCategory);
        Assert.Null(diagnostic.AutomaticSnapshot.LastSuccessAt);
        Assert.True(File.Exists(directory.SessionFile));
    }

    [Fact]
    public async Task SaveAsyncReportsProtectedRecoveryFailureWithoutFailingNormalSave()
    {
        using var directory = new TempDirectory();
        var state = new SessionRecoveryDiagnosticState();
        var service = directory.CreateSessionService(
            state,
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation)
                {
                    throw new IOException("test protected failure");
                }
            });
        await service.SaveAsync(CreateSession("https://example.com/before-protected-failure"));

        await service.SaveAsync(CreateSession("https://example.com/after-protected-failure"));

        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.NormalSave.LastResult);
        Assert.Equal(SessionPersistenceDiagnosticResult.Failed, diagnostic.ProtectedRecoveryPoint.LastResult);
        Assert.Equal(
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCreation,
            diagnostic.LastFailedOperation);
        Assert.Equal(
            "https://example.com/after-protected-failure",
            Assert.Single((await service.LoadAsync()).Tabs).Url);
    }

    [Fact]
    public async Task SaveAsyncReportsNormalSaveFailureAndStillPropagatesIt()
    {
        using var directory = new TempDirectory();
        var state = new SessionRecoveryDiagnosticState();
        var service = directory.CreateSessionService(
            state,
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.NormalSave)
                {
                    throw new UnauthorizedAccessException("test normal save failure");
                }
            });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SaveAsync(CreateSession("https://example.com/normal-failure")));

        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Failed, diagnostic.NormalSave.LastResult);
        Assert.Equal(SessionPersistenceFailureCategory.AccessDenied, diagnostic.NormalSave.FailureCategory);
        Assert.Equal(SessionPersistenceDiagnosticResult.Unknown, diagnostic.AutomaticSnapshot.LastResult);
        Assert.False(File.Exists(directory.SessionFile));
    }

    [Fact]
    public async Task ProtectedRecoveryCreationRemainsSuccessfulWhenOnlyCleanupFails()
    {
        using var directory = new TempDirectory();
        var state = new SessionRecoveryDiagnosticState();
        var service = directory.CreateSessionService(
            state,
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup)
                {
                    throw new IOException("test protected cleanup failure");
                }
            });
        await service.SaveAsync(CreateSession("https://example.com/protected-cleanup-source"));

        await service.SaveAsync(new SessionData());

        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.ProtectedRecoveryPoint.LastResult);
        Assert.NotNull(diagnostic.ProtectedRecoveryPoint.LastSuccessAt);
        Assert.Equal(SessionPersistenceDiagnosticResult.Failed, diagnostic.ProtectedRecoveryPointCleanup.LastResult);
        Assert.Equal(
            SessionPersistenceDiagnosticOperation.ProtectedRecoveryPointCleanup,
            diagnostic.ProtectedRecoveryPointCleanup.Operation);
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.AutomaticSnapshotCleanup.LastResult);
        Assert.True(service.HasProtectedRecoveryPoint());
    }

    [Fact]
    public async Task SnapshotDiagnosticRecoversAfterARealSuccessfulCreation()
    {
        using var directory = new TempDirectory();
        var failSnapshot = true;
        var state = new SessionRecoveryDiagnosticState();
        var service = directory.CreateSessionService(
            state,
            operation =>
            {
                if (failSnapshot
                    && operation == SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation)
                {
                    throw new IOException("test transient snapshot failure");
                }
            });
        await service.SaveAsync(CreateSession("https://example.com/first"));
        Assert.Equal(
            SessionPersistenceDiagnosticResult.Failed,
            service.CaptureRecoveryDiagnosticSnapshot().AutomaticSnapshot.LastResult);

        failSnapshot = false;
        await service.SaveAsync(CreateSession("https://example.com/second"));

        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.AutomaticSnapshot.LastResult);
        Assert.Equal(SessionPersistenceFailureCategory.None, diagnostic.AutomaticSnapshot.FailureCategory);
        Assert.NotNull(diagnostic.AutomaticSnapshot.LastSuccessAt);
        Assert.Single(Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json"));
    }

    [Fact]
    public async Task UnchangedSaveIsReportedAsSkippedWithoutUpdatingSuccessTimes()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var session = CreateSession("https://example.com/unchanged");
        await service.SaveAsync(session);
        var first = service.CaptureRecoveryDiagnosticSnapshot();

        await service.SaveAsync(session);

        var second = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Skipped, second.NormalSave.LastResult);
        Assert.Equal(first.NormalSave.LastSuccessAt, second.NormalSave.LastSuccessAt);
        Assert.Equal(first.AutomaticSnapshot, second.AutomaticSnapshot);
    }

    [Fact]
    public async Task SynchronousAndAsynchronousSaveReportEquivalentSnapshotFailure()
    {
        using var asyncDirectory = new TempDirectory();
        using var syncDirectory = new TempDirectory();
        static void FailSnapshot(SessionPersistenceDiagnosticOperation operation)
        {
            if (operation == SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation)
            {
                throw new IOException("test snapshot failure");
            }
        }

        var asyncService = asyncDirectory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            FailSnapshot);
        var syncService = syncDirectory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            FailSnapshot);

        await asyncService.SaveAsync(CreateSession("https://example.com/async"));
        syncService.Save(CreateSession("https://example.com/sync"));

        var asyncDiagnostic = asyncService.CaptureRecoveryDiagnosticSnapshot();
        var syncDiagnostic = syncService.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(asyncDiagnostic.NormalSave.LastResult, syncDiagnostic.NormalSave.LastResult);
        Assert.Equal(asyncDiagnostic.AutomaticSnapshot.LastResult, syncDiagnostic.AutomaticSnapshot.LastResult);
        Assert.Equal(asyncDiagnostic.AutomaticSnapshot.FailureCategory, syncDiagnostic.AutomaticSnapshot.FailureCategory);
    }

    [Fact]
    public void NewerDiagnosticResultIsNotOverwrittenByOlderCompletion()
    {
        var state = new SessionRecoveryDiagnosticState();
        var older = state.Begin(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);
        var newer = state.Begin(SessionPersistenceDiagnosticOperation.AutomaticSnapshotCreation);

        state.CompleteSucceeded(newer);
        state.CompleteFailed(older, new IOException("late old failure"));

        var diagnostic = state.Capture();
        Assert.Equal(SessionPersistenceDiagnosticResult.Succeeded, diagnostic.AutomaticSnapshot.LastResult);
        Assert.Equal(SessionPersistenceFailureCategory.None, diagnostic.AutomaticSnapshot.FailureCategory);
    }

    [Fact]
    public async Task DiagnosticObserverFailureDoesNotAffectSessionSave()
    {
        using var directory = new TempDirectory();
        var state = new SessionRecoveryDiagnosticState(_ => throw new InvalidOperationException("observer failure"));
        var service = directory.CreateSessionService(state);

        await service.SaveAsync(CreateSession("https://example.com/observer-failure"));

        Assert.True(File.Exists(directory.SessionFile));
        Assert.Equal(
            SessionPersistenceDiagnosticResult.Succeeded,
            service.CaptureRecoveryDiagnosticSnapshot().NormalSave.LastResult);
    }

    [Fact]
    public async Task InitialProtectedRecoveryFailureIsReportedWithoutFailingLoad()
    {
        using var directory = new TempDirectory();
        await File.WriteAllTextAsync(
            directory.SessionFile,
            System.Text.Json.JsonSerializer.Serialize(CreateSession("https://example.com/initial-load"), new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            }));
        var service = directory.CreateSessionService(
            new SessionRecoveryDiagnosticState(),
            operation =>
            {
                if (operation == SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation)
                {
                    throw new IOException("test initial protected failure");
                }
            });

        var loaded = await service.LoadAsync();

        Assert.Equal("https://example.com/initial-load", Assert.Single(loaded.Tabs).Url);
        var diagnostic = service.CaptureRecoveryDiagnosticSnapshot();
        Assert.Equal(SessionPersistenceDiagnosticResult.Failed, diagnostic.ProtectedRecoveryPoint.LastResult);
        Assert.Equal(
            SessionPersistenceDiagnosticOperation.InitialProtectedRecoveryPointCreation,
            diagnostic.ProtectedRecoveryPoint.Operation);
    }

    [Fact]
    public async Task LoadAsyncReturnsEmptySessionForBrokenJson()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await File.WriteAllTextAsync(directory.SessionFile, "{ broken json");

        var loaded = await service.LoadAsync();

        Assert.Empty(loaded.Tabs);
        Assert.Empty(loaded.ClosedTabs);
        Assert.Equal("empty", service.LastLoadInfo.Source);
        Assert.False(service.LastLoadInfo.Recovered);
        Assert.True(service.LastLoadInfo.PrimaryLoadFailed);
    }

    [Fact]
    public async Task LoadAsyncNormalizesNullSessionCollections()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await File.WriteAllTextAsync(
            directory.SessionFile,
            """
            {
              "tabs": [
                {
                  "url": "https://example.com",
                  "navigationHistory": null,
                  "navigationHistoryIndex": 4
                }
              ],
              "closedTabs": null,
              "windows": [
                {
                  "tabs": null,
                  "closedTabs": [
                    {
                      "url": "https://example.com/closed",
                      "navigationHistory": null,
                      "navigationHistoryIndex": 3
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await service.LoadAsync();

        var tab = Assert.Single(loaded.Tabs);
        Assert.Empty(tab.NavigationHistory);
        Assert.Equal(-1, tab.NavigationHistoryIndex);
        Assert.Empty(loaded.ClosedTabs);
        var window = Assert.Single(loaded.Windows);
        Assert.NotEqual(Guid.Empty, window.WindowId);
        Assert.Empty(window.Tabs);
        var closedTab = Assert.Single(window.ClosedTabs);
        Assert.Empty(closedTab.NavigationHistory);
        Assert.Equal(-1, closedTab.NavigationHistoryIndex);
    }

    [Fact]
    public async Task LoadAsyncDropsUnusableTabsAndNormalizesNavigationState()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        await File.WriteAllTextAsync(
            directory.SessionFile,
            """
            {
              "lastActiveTabId": "11111111-1111-1111-1111-111111111111",
              "tabs": [
                {
                  "id": "00000000-0000-0000-0000-000000000000",
                  "url": "  https://example.com/kept  ",
                  "navigationHistory": [
                    " ",
                    " https://example.com/kept ",
                    null
                  ],
                  "navigationHistoryIndex": 99,
                  "audioPan": 999
                },
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "url": " "
                }
              ],
              "closedTabs": [
                {
                  "url": " "
                },
                {
                  "url": " https://example.com/closed ",
                  "navigationHistory": [
                    "https://example.com/closed"
                  ],
                  "navigationHistoryIndex": -9,
                  "audioPan": 999
                }
              ],
              "windows": [
                {
                  "lastActiveTabId": "22222222-2222-2222-2222-222222222222",
                  "tabs": [
                    {
                      "id": "22222222-2222-2222-2222-222222222222",
                      "url": " "
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await service.LoadAsync();

        var tab = Assert.Single(loaded.Tabs);
        Assert.NotEqual(Guid.Empty, tab.Id);
        Assert.Equal("https://example.com/kept", tab.Url);
        Assert.Equal(TabAudioPan.Stereo, tab.AudioPan);
        Assert.Single(tab.NavigationHistory);
        Assert.Equal("https://example.com/kept", tab.NavigationHistory[0]);
        Assert.Equal(0, tab.NavigationHistoryIndex);
        Assert.Null(loaded.LastActiveTabId);

        var closedTab = Assert.Single(loaded.ClosedTabs);
        Assert.Equal("https://example.com/closed", closedTab.Url);
        Assert.Equal(TabAudioPan.Stereo, closedTab.AudioPan);
        Assert.Equal(-1, closedTab.NavigationHistoryIndex);

        var window = Assert.Single(loaded.Windows);
        Assert.Empty(window.Tabs);
        Assert.Null(window.LastActiveTabId);
    }

    [Fact]
    public async Task LoadAsyncRemapsDuplicateTabIdsAcrossWindowsWithoutLosingTabState()
    {
        using var directory = new TempDirectory();
        var duplicateTabId = Guid.NewGuid();
        var primaryWindowId = Guid.NewGuid();
        var secondaryWindowId = Guid.NewGuid();
        var containerProfileId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            directory.SessionFile,
            $$"""
            {
              "version": 2,
              "lastActiveTabId": "{{duplicateTabId}}",
              "tabs": [
                {
                  "id": "{{duplicateTabId}}",
                  "url": "https://example.com/primary"
                }
              ],
              "windows": [
                {
                  "windowId": "{{primaryWindowId}}",
                  "lastActiveTabId": "{{duplicateTabId}}",
                  "tabs": [
                    {
                      "id": "{{duplicateTabId}}",
                      "url": "https://example.com/primary"
                    }
                  ]
                },
                {
                  "windowId": "{{secondaryWindowId}}",
                  "lastActiveTabId": "{{duplicateTabId}}",
                  "tabs": [
                    {
                      "id": "{{duplicateTabId}}",
                      "url": "https://example.com/secondary",
                      "title": "Secondary",
                      "navigationHistory": [
                        "https://example.com",
                        "https://example.com/secondary"
                      ],
                      "navigationHistoryIndex": 1,
                      "audioBoostDecibels": 6,
                      "containerProfileId": "{{containerProfileId}}",
                      "youTubePlaybackVideoId": "video-id",
                      "youTubePlaybackPositionSeconds": 42.5,
                      "youTubePlaybackSpeed": 1.25,
                      "youTubePlaybackIsPaused": true
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await directory.CreateSessionService().LoadAsync();

        var primaryTab = Assert.Single(loaded.Windows[0].Tabs);
        var secondaryWindow = loaded.Windows[1];
        var secondaryTab = Assert.Single(secondaryWindow.Tabs);
        Assert.Equal(duplicateTabId, primaryTab.Id);
        Assert.NotEqual(Guid.Empty, secondaryTab.Id);
        Assert.NotEqual(primaryTab.Id, secondaryTab.Id);
        Assert.Equal(secondaryTab.Id, secondaryWindow.LastActiveTabId);
        Assert.Equal("https://example.com/secondary", secondaryTab.Url);
        Assert.Equal("Secondary", secondaryTab.Title);
        Assert.Equal(1, secondaryTab.NavigationHistoryIndex);
        Assert.Equal(2, secondaryTab.NavigationHistory.Count);
        Assert.Equal(6, secondaryTab.AudioBoostDecibels);
        Assert.Equal(containerProfileId, secondaryTab.ContainerProfileId);
        Assert.Equal("video-id", secondaryTab.YouTubePlaybackVideoId);
        Assert.Equal(42.5, secondaryTab.YouTubePlaybackPositionSeconds);
        Assert.Equal(1.25, secondaryTab.YouTubePlaybackSpeed);
        Assert.True(secondaryTab.YouTubePlaybackIsPaused);
        Assert.Equal(duplicateTabId, Assert.Single(loaded.Tabs).Id);
        Assert.Equal(duplicateTabId, loaded.LastActiveTabId);
    }

    [Fact]
    public async Task SaveAsyncRemapsDuplicateWindowAndClosedWindowTabReferences()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var duplicateTabId = Guid.NewGuid();
        var primaryWindowId = Guid.NewGuid();
        var secondaryWindowId = Guid.NewGuid();
        var closedWindowId = Guid.NewGuid();
        var closedSessionWindowId = Guid.NewGuid();
        var secondaryTab = new SessionTabState
        {
            Id = duplicateTabId,
            Url = "https://example.com/secondary"
        };
        var closedTab = new SessionTabState
        {
            Id = duplicateTabId,
            Url = "https://example.com/closed"
        };
        var miniPlayer = new YouTubeMiniPlayerState
        {
            OwnerSessionWindowId = secondaryWindowId,
            SourceTabId = duplicateTabId,
            SourceKind = MiniPlayerSourceKind.YouTube,
            Url = "https://www.youtube.com/watch?v=duplicate"
        };
        var session = new SessionData
        {
            Version = 2,
            Tabs = [new SessionTabState { Id = duplicateTabId, Url = "https://example.com/primary" }],
            Windows =
            [
                new SessionWindowState
                {
                    WindowId = primaryWindowId,
                    LastActiveTabId = duplicateTabId,
                    Tabs = [new SessionTabState { Id = duplicateTabId, Url = "https://example.com/primary" }]
                },
                new SessionWindowState
                {
                    WindowId = secondaryWindowId,
                    LastActiveTabId = duplicateTabId,
                    Tabs = [secondaryTab]
                }
            ],
            ClosedWindows =
            [
                new ClosedWindowState
                {
                    Id = closedWindowId,
                    WindowState = new SessionWindowState
                    {
                        WindowId = closedSessionWindowId,
                        LastActiveTabId = duplicateTabId,
                        Tabs = [closedTab]
                    }
                }
            ],
            ClosedTabs =
            [
                new ClosedTabState
                {
                    ClosedWindowId = closedWindowId,
                    ClosedWindowTabIndex = 0,
                    SourceTabId = duplicateTabId,
                    Url = closedTab.Url
                }
            ],
            WindowMemoryYouTubeMiniPlayers = [miniPlayer]
        };

        await service.SaveAsync(session);

        var windowTabIds = session.Windows.SelectMany(static window => window.Tabs)
            .Concat(session.ClosedWindows.SelectMany(static window => window.WindowState.Tabs))
            .Select(static tab => tab.Id)
            .ToList();
        Assert.Equal(windowTabIds.Count, windowTabIds.Distinct().Count());
        Assert.Equal(duplicateTabId, session.Windows[0].Tabs[0].Id);
        Assert.NotEqual(duplicateTabId, secondaryTab.Id);
        Assert.Equal(secondaryTab.Id, session.Windows[1].LastActiveTabId);
        Assert.NotEqual(duplicateTabId, closedTab.Id);
        Assert.Equal(closedTab.Id, session.ClosedWindows[0].WindowState.LastActiveTabId);
        Assert.Equal(closedTab.Id, Assert.Single(session.ClosedTabs).SourceTabId);
        Assert.Equal(secondaryTab.Id, miniPlayer.SourceTabId);

        var persisted = await service.LoadAsync();
        var persistedIds = persisted.Windows.SelectMany(static window => window.Tabs)
            .Concat(persisted.ClosedWindows.SelectMany(static window => window.WindowState.Tabs))
            .Select(static tab => tab.Id)
            .ToList();
        Assert.Equal(persistedIds.Count, persistedIds.Distinct().Count());
    }

    [Fact]
    public async Task SaveAndLoadKeepsWindowSetForRestoreSmoke()
    {
        using var directory = new TempDirectory();
        var service = directory.CreateSessionService();
        var primaryTabId = Guid.NewGuid();
        var secondaryTabId = Guid.NewGuid();
        var primaryWindowId = Guid.NewGuid();
        var secondaryWindowId = Guid.NewGuid();
        var lastActivatedAt = new DateTimeOffset(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);
        var containerProfileId = Guid.NewGuid().ToString("N");

        await service.SaveAsync(new SessionData
        {
            Version = 2,
            LastActiveTabId = primaryTabId,
            Tabs =
            [
                new SessionTabState
                {
                    Id = primaryTabId,
                    Url = "https://example.com/main",
                    Title = "Main",
                    NavigationHistory =
                    [
                        "https://example.com",
                        "https://example.com/main"
                    ],
                    NavigationHistoryIndex = 1,
                    AudioPan = TabAudioPan.Left,
                    IsMuted = true,
                    AudioBoostDecibels = 6,
                    IsPinned = true,
                    LastActivatedAt = lastActivatedAt,
                    ContainerProfileId = containerProfileId,
                    SplitViewId = "split-main",
                    SplitViewOrientation = SplitViewOrientation.TopBottom,
                    SplitViewRatio = 0.63,
                    YouTubePlaybackVideoId = "main-video",
                    YouTubePlaybackPositionSeconds = 12.5,
                    YouTubePlaybackSpeed = 1.25,
                    YouTubePlaybackIsPaused = true,
                    YouTubePlaybackIsLooping = true,
                    YouTubePlaybackIsTheaterMode = true
                }
            ],
            ClosedTabs =
            [
                new ClosedTabState
                {
                    SourceWindowId = primaryWindowId,
                    Url = "https://example.com/closed",
                    Title = "Closed",
                    NavigationHistory =
                    [
                        "https://example.com/closed"
                    ],
                    NavigationHistoryIndex = 0,
                    AudioPan = TabAudioPan.Right,
                    IsMuted = true,
                    AudioBoostDecibels = 9,
                    YouTubePlaybackVideoId = "closed-video",
                    YouTubePlaybackPositionSeconds = 5,
                    YouTubePlaybackSpeed = 1.5,
                    YouTubePlaybackIsPaused = false,
                    YouTubePlaybackIsLooping = true,
                    YouTubePlaybackIsTheaterMode = true
                }
            ],
            Windows =
            [
                new SessionWindowState
                {
                    WindowId = primaryWindowId,
                    LastActiveTabId = primaryTabId,
                    IsAlwaysOnTop = true,
                    Tabs =
                    [
                        new SessionTabState
                        {
                            Id = primaryTabId,
                            Url = "https://example.com/main",
                            Title = "Main",
                            NavigationHistory =
                            [
                                "https://example.com",
                                "https://example.com/main"
                            ],
                            NavigationHistoryIndex = 1,
                            AudioPan = TabAudioPan.Left,
                            IsMuted = true,
                            AudioBoostDecibels = 3,
                            YouTubePlaybackVideoId = "main-video",
                            YouTubePlaybackPositionSeconds = 12.5,
                            YouTubePlaybackSpeed = 1.25,
                            YouTubePlaybackIsPaused = true,
                            YouTubePlaybackIsLooping = true,
                            YouTubePlaybackIsTheaterMode = true
                        }
                    ]
                },
                new SessionWindowState
                {
                    WindowId = secondaryWindowId,
                    LastActiveTabId = secondaryTabId,
                    IsAlwaysOnTop = false,
                    Tabs =
                    [
                        new SessionTabState
                        {
                            Id = secondaryTabId,
                            Url = "https://example.com/second",
                            Title = "Second",
                            NavigationHistory =
                            [
                                "https://example.com/second"
                            ],
                            NavigationHistoryIndex = 0,
                            IsMuted = true
                        }
                    ]
                }
            ]
        });

        var loaded = await service.LoadAsync();

        Assert.Equal(2, loaded.Version);
        Assert.Equal(primaryTabId, loaded.LastActiveTabId);
        Assert.Equal(primaryTabId, Assert.Single(loaded.Tabs).Id);
        Assert.Equal("https://example.com/main", loaded.Tabs[0].Url);
        Assert.Equal("Main", loaded.Tabs[0].Title);
        Assert.False(loaded.Tabs[0].IsFolderTab);
        Assert.Equal(
            ["https://example.com", "https://example.com/main"],
            loaded.Tabs[0].NavigationHistory);
        Assert.Equal(TabAudioPan.Left, loaded.Tabs[0].AudioPan);
        Assert.True(loaded.Tabs[0].IsMuted);
        Assert.Equal(6, loaded.Tabs[0].AudioBoostDecibels);
        Assert.True(loaded.Tabs[0].IsPinned);
        Assert.Equal(lastActivatedAt, loaded.Tabs[0].LastActivatedAt);
        Assert.Equal(containerProfileId, loaded.Tabs[0].ContainerProfileId);
        Assert.Equal("split-main", loaded.Tabs[0].SplitViewId);
        Assert.Equal(SplitViewOrientation.TopBottom, loaded.Tabs[0].SplitViewOrientation);
        Assert.Equal(0.63, loaded.Tabs[0].SplitViewRatio);
        Assert.Equal("main-video", loaded.Tabs[0].YouTubePlaybackVideoId);
        Assert.Equal(12.5, loaded.Tabs[0].YouTubePlaybackPositionSeconds);
        Assert.Equal(1.25, loaded.Tabs[0].YouTubePlaybackSpeed);
        Assert.True(loaded.Tabs[0].YouTubePlaybackIsPaused);
        Assert.True(loaded.Tabs[0].YouTubePlaybackIsLooping);
        Assert.True(loaded.Tabs[0].YouTubePlaybackIsTheaterMode);
        Assert.Equal(1, loaded.Tabs[0].NavigationHistoryIndex);
        Assert.Equal("https://example.com/closed", Assert.Single(loaded.ClosedTabs).Url);
        Assert.Equal(TabAudioPan.Right, loaded.ClosedTabs[0].AudioPan);
        Assert.True(loaded.ClosedTabs[0].IsMuted);
        Assert.Equal(9, loaded.ClosedTabs[0].AudioBoostDecibels);
        Assert.Equal("closed-video", loaded.ClosedTabs[0].YouTubePlaybackVideoId);
        Assert.Equal(5, loaded.ClosedTabs[0].YouTubePlaybackPositionSeconds);
        Assert.Equal(1.5, loaded.ClosedTabs[0].YouTubePlaybackSpeed);
        Assert.False(loaded.ClosedTabs[0].YouTubePlaybackIsPaused);
        Assert.True(loaded.ClosedTabs[0].YouTubePlaybackIsLooping);
        Assert.True(loaded.ClosedTabs[0].YouTubePlaybackIsTheaterMode);
        Assert.Equal(primaryWindowId, loaded.ClosedTabs[0].SourceWindowId);
        Assert.Equal(2, loaded.Windows.Count);
        Assert.Equal(primaryWindowId, loaded.Windows[0].WindowId);
        Assert.Equal(secondaryWindowId, loaded.Windows[1].WindowId);
        Assert.Equal(primaryTabId, loaded.Windows[0].LastActiveTabId);
        Assert.Equal(secondaryTabId, loaded.Windows[1].LastActiveTabId);
        Assert.True(loaded.Windows[0].IsAlwaysOnTop);
        Assert.False(loaded.Windows[1].IsAlwaysOnTop);
        Assert.True(loaded.Windows[0].Tabs[0].IsMuted);
        Assert.Equal(3, loaded.Windows[0].Tabs[0].AudioBoostDecibels);
        Assert.True(loaded.Windows[0].Tabs[0].YouTubePlaybackIsLooping);
        Assert.True(loaded.Windows[0].Tabs[0].YouTubePlaybackIsTheaterMode);
        Assert.Equal("https://example.com/second", Assert.Single(loaded.Windows[1].Tabs).Url);
        Assert.True(loaded.Windows[1].Tabs[0].IsMuted);
        Assert.Single(Directory.GetFiles(directory.SessionSnapshotDirectory, "session-*.json"));
    }

    [Fact]
    public async Task LoadLegacyWindowStateWithoutAlwaysOnTopKeepsFallbackUnset()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            directory.SessionFile,
            """
            {
              "windows": [
                {
                  "tabs": [
                    {
                      "url": "https://example.com/legacy-window"
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await directory.CreateSessionService().LoadAsync();

        Assert.Null(Assert.Single(loaded.Windows).IsAlwaysOnTop);
    }

    [Fact]
    public async Task LoadAsyncNormalizesMissingContainerProfileIdsToDefault()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(
            directory.SessionFile,
            """
            {
              "tabs": [
                {
                  "url": "https://example.com",
                  "containerProfileId": "   "
                }
              ],
              "closedTabs": [
                {
                  "url": "https://example.com/closed"
                }
              ],
              "windows": [
                {
                  "tabs": [
                    {
                      "url": "https://example.com/window"
                    }
                  ],
                  "closedTabs": [
                    {
                      "url": "https://example.com/window-closed",
                      "containerProfileId": "   "
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = await directory.CreateSessionService().LoadAsync();

        Assert.Equal(ContainerProfile.DefaultId, Assert.Single(loaded.Tabs).ContainerProfileId);
        Assert.Equal(ContainerProfile.DefaultId, Assert.Single(loaded.ClosedTabs).ContainerProfileId);
        var window = Assert.Single(loaded.Windows);
        Assert.Equal(ContainerProfile.DefaultId, Assert.Single(window.Tabs).ContainerProfileId);
        Assert.Equal(ContainerProfile.DefaultId, Assert.Single(window.ClosedTabs).ContainerProfileId);
    }

    [Fact]
    public async Task LoadLegacyTabWithOnlyCoreFieldsUsesCurrentPersistedDefaults()
    {
        using var directory = new TempDirectory();
        var tabId = Guid.NewGuid();
        File.WriteAllText(
            directory.SessionFile,
            $$"""
            {
              "version": 1,
              "tabs": [
                {
                  "id": "{{tabId}}",
                  "url": "https://example.com/legacy",
                  "title": "Legacy",
                  "navigationHistory": [
                    "https://example.com",
                    "https://example.com/legacy"
                  ],
                  "navigationHistoryIndex": 1
                }
              ]
            }
            """);

        var tab = Assert.Single((await directory.CreateSessionService().LoadAsync()).Tabs);

        Assert.Equal(tabId, tab.Id);
        Assert.Equal("https://example.com/legacy", tab.Url);
        Assert.Equal("Legacy", tab.Title);
        Assert.Equal(
            ["https://example.com", "https://example.com/legacy"],
            tab.NavigationHistory);
        Assert.Equal(1, tab.NavigationHistoryIndex);
        Assert.False(tab.IsFolderTab);
        Assert.Equal(TabAudioPan.Stereo, tab.AudioPan);
        Assert.False(tab.IsMuted);
        Assert.Equal(0, tab.AudioBoostDecibels);
        Assert.False(tab.IsPinned);
        Assert.Null(tab.LastActivatedAt);
        Assert.Equal(ContainerProfile.DefaultId, tab.ContainerProfileId);
        Assert.Null(tab.SplitViewId);
        Assert.Null(tab.SplitViewOrientation);
        Assert.Null(tab.SplitViewRatio);
        Assert.Null(tab.YouTubePlaybackVideoId);
        Assert.Null(tab.YouTubePlaybackPositionSeconds);
        Assert.Null(tab.YouTubePlaybackSpeed);
        Assert.Null(tab.YouTubePlaybackIsPaused);
        Assert.Null(tab.YouTubePlaybackIsLooping);
        Assert.Null(tab.YouTubePlaybackIsTheaterMode);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(),
            "TabBrowserSessionServiceTests",
            Guid.NewGuid().ToString("N"));

        public string SessionFile => Path.Combine(DirectoryPath, "session.json");

        public string SessionBackupFile => Path.Combine(DirectoryPath, "session.backup.json");

        public string SessionSnapshotDirectory => Path.Combine(DirectoryPath, "session.snapshots");

        public string CorruptSessionDirectory => Path.Combine(DirectoryPath, "session.corrupt");

        public string ProtectedSessionDirectory => Path.Combine(DirectoryPath, "session.protected");

        public string WindowMemoryFile => Path.Combine(DirectoryPath, "window-memory.json");

        public string CorruptWindowMemoryDirectory => Path.Combine(DirectoryPath, "window-memory.corrupt");

        public string WindowMemoryBackupFile => Path.Combine(DirectoryPath, "window-memory.backup.json");

        public string DiagnosticDirectory => Path.Combine(DirectoryPath, "diagnostic");

        public string DiagnosticFile => Path.Combine(DiagnosticDirectory, "diagnostic.log");

        public SessionService CreateSessionService()
        {
            return new SessionService(SessionFile, WindowMemoryFile, DiagnosticDirectory);
        }

        public SessionService CreateSessionService(
            SessionRecoveryDiagnosticState diagnosticState,
            Action<SessionPersistenceDiagnosticOperation>? beforeOperation = null,
            Action? beforePrimaryRepair = null,
            Action<CorruptPrimaryPreservationStage>? beforePreservationStage = null)
        {
            return new SessionService(
                SessionFile,
                WindowMemoryFile,
                DiagnosticDirectory,
                diagnosticState,
                beforeOperation,
                beforePrimaryRepair,
                beforePreservationStage);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private static SessionData CreateSession(string url)
        => new()
        {
            Tabs = [new SessionTabState { Url = url }]
        };
}
