using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using TabBrowser.Models;

namespace TabBrowser.Views;

// 専用設定Windowの編集値を所有し、OKで結果を作るまで元の設定を変更しない。
internal sealed class SuperDragSettingsEditor
{
    private readonly CheckBox _enabled;
    private readonly TextBox _excludedHosts;
    internal ObservableCollection<MappingRow> Mappings { get; } = [];
    internal ObservableCollection<SearchProviderRow> SearchProviders { get; } = [];

    internal sealed class MappingRow : INotifyPropertyChanged
    {
        private string _action = "None";
        private IReadOnlyList<SuperDragActionOption> _options = [];
        private IReadOnlyDictionary<string, SuperDragSearchProvider> _providers = new Dictionary<string, SuperDragSearchProvider>();
        public required string Kind { get; init; }
        public required string Direction { get; init; }
        public required string Label { get; init; }
        public required string Action
        {
            get => _action;
            set
            {
                // ItemsSourceの更新でSelectedValueが一時的にnullになっても選択を失わない。
                if (string.IsNullOrWhiteSpace(value) || _action == value) return;
                _action = value;
                if (_options.Count > 0) UpdateOptions(_providers);
                else PropertyChanged?.Invoke(this, new(nameof(Action)));
            }
        }
        public IReadOnlyList<SuperDragActionOption> Options => _options;
        public bool IsAdditional => Direction.Length > 5;
        public event PropertyChangedEventHandler? PropertyChanged;

        internal void UpdateOptions(IReadOnlyDictionary<string, SuperDragSearchProvider> providers)
        {
            _providers = providers;
            _options = SuperDragSettingsProfile.GetSelectableActions(Kind, providers);
            // 以前に保存したサイト別IDは、この方向の現在値としてだけ残す。
            if (Kind == "text" && SuperDragSettingsProfile.GetSearchPreset(_action) is not null)
            {
                var legacy = SuperDragSettingsProfile.GetActions(Kind).Single(option => option.Value == _action);
                _options = [.. _options, legacy with { Label = "既存の割り当て: " + legacy.Label }];
            }
            if (!_options.Any(option => option.Value == _action)) _action = "None";
            PropertyChanged?.Invoke(this, new(nameof(Options)));
            PropertyChanged?.Invoke(this, new(nameof(Action)));
        }
    }

    internal sealed class SearchProviderRow : INotifyPropertyChanged
    {
        private string _name = "";
        private string _template = "";
        public required string Id { get; init; }
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); } }
        public string SearchUrlTemplate { get => _template; set { _template = value; PropertyChanged?.Invoke(this, new(nameof(SearchUrlTemplate))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    internal SuperDragSettingsEditor(CheckBox enabled, ItemsControl mappings, TextBox excludedHosts, SuperDragSettings? settings)
    {
        _enabled = enabled;
        _excludedHosts = excludedHosts;
        mappings.ItemsSource = Mappings;
        Load(settings);
    }

    internal void Load(SuperDragSettings? value)
    {
        var settings = SuperDragSettingsProfile.Normalize(value);
        _enabled.IsChecked = settings.IsEnabled;
        _excludedHosts.Text = string.Join(Environment.NewLine, settings.ExcludedHosts);
        Mappings.Clear();
        foreach (var row in SearchProviders) row.PropertyChanged -= SearchProviderChanged;
        SearchProviders.Clear();
        foreach (var pair in settings.CustomSearchProviders)
            AddSearchProvider(new SearchProviderRow { Id = pair.Key, Name = pair.Value.Name, SearchUrlTemplate = pair.Value.SearchUrlTemplate });
        foreach (var (kind, label) in new[] { ("text", "テキスト"), ("link", "リンク"), ("image", "画像") })
            foreach (var route in SuperDragSettingsProfile.Routes.Where(route => route.Value.Length <= 5
                || SuperDragSettingsProfile.GetAction(settings, kind, route.Value) != "None"))
                Mappings.Add(new()
                {
                    Kind = kind,
                    Direction = route.Value,
                    Label = label + "  " + route.Label,
                    Action = SuperDragSettingsProfile.GetAction(settings, kind, route.Value)!
                });
        RefreshOptions();
    }

    internal SuperDragSettings BuildResult()
    {
        if (!TryBuildResult(out var result, out var error)) throw new InvalidOperationException(error);
        return result;
    }

    internal bool TryBuildResult(out SuperDragSettings result, out string? error)
    {
        result = new();
        error = null;
        foreach (var row in SearchProviders)
        {
            if (string.IsNullOrWhiteSpace(row.Name)) error = "追加する検索先の名前を入力してください。";
            else if (!SearchAliasSettingsProfile.IsValidSearchUrlTemplate(row.SearchUrlTemplate))
                error = $"「{row.Name.Trim()}」の検索URLには、http:// または https:// で始まり、検索語の位置に {{0}} を含むURLを入力してください。";
            if (error is not null) return false;
        }
        var settings = new SuperDragSettings
        {
            IsEnabled = _enabled.IsChecked == true,
            ExcludedHosts = _excludedHosts.Text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).ToList(),
            CustomSearchProviders = ReadProviders()
        };
        foreach (var row in Mappings)
        {
            var directions = row.Kind switch { "text" => settings.Text, "link" => settings.Link, _ => settings.Image };
            switch (row.Direction)
            {
                case "up": directions.Up = row.Action; break;
                case "right": directions.Right = row.Action; break;
                case "down": directions.Down = row.Action; break;
                case "left": directions.Left = row.Action; break;
                case "upRight": directions.UpRight = row.Action; break;
                case "upLeft": directions.UpLeft = row.Action; break;
                case "rightUp": directions.RightUp = row.Action; break;
                case "rightDown": directions.RightDown = row.Action; break;
                case "downRight": directions.DownRight = row.Action; break;
                case "downLeft": directions.DownLeft = row.Action; break;
                case "leftUp": directions.LeftUp = row.Action; break;
                case "leftDown": directions.LeftDown = row.Action; break;
            }
        }
        result = SuperDragSettingsProfile.Normalize(settings);
        return true;
    }

    internal SearchProviderRow AddSearchProvider()
    {
        var row = new SearchProviderRow { Id = Guid.NewGuid().ToString("N"), Name = "新しい検索先" };
        AddSearchProvider(row);
        RefreshOptions();
        return row;
    }

    internal void AddMapping(string kind, string route)
    {
        if (kind is not ("text" or "link" or "image") || route.Length <= 5
            || !SuperDragSettingsProfile.Routes.Any(option => option.Value == route)
            || Mappings.Any(row => row.Kind == kind && row.Direction == route)) return;
        var row = new MappingRow
        {
            Kind = kind,
            Direction = route,
            Label = (kind == "text" ? "テキスト" : kind == "link" ? "リンク" : "画像") + "  "
                + SuperDragSettingsProfile.Routes.Single(option => option.Value == route).Label,
            Action = "None"
        };
        row.UpdateOptions(ReadProviders());
        Mappings.Add(row);
    }

    internal MappingRow? SelectMapping(string kind, string route)
    {
        var existing = Mappings.FirstOrDefault(row => row.Kind == kind && row.Direction == route);
        if (existing is not null) return existing;
        AddMapping(kind, route);
        return Mappings.FirstOrDefault(row => row.Kind == kind && row.Direction == route);
    }

    internal void RemoveMapping(MappingRow row)
    {
        if (row.IsAdditional) Mappings.Remove(row);
    }

    internal SearchProviderRow AddSearchProvider(SuperDragSearchPreset preset)
    {
        var existing = SearchProviders.FirstOrDefault(row => row.SearchUrlTemplate.Trim() == preset.SearchUrlTemplate);
        if (existing is not null) return existing;
        var row = new SearchProviderRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = preset.Name,
            SearchUrlTemplate = preset.SearchUrlTemplate
        };
        AddSearchProvider(row);
        RefreshOptions();
        return row;
    }

    private void AddSearchProvider(SearchProviderRow row)
    {
        SearchProviders.Add(row);
        row.PropertyChanged += SearchProviderChanged;
    }

    internal void RemoveSearchProvider(SearchProviderRow row)
    {
        if (!SearchProviders.Remove(row)) return;
        row.PropertyChanged -= SearchProviderChanged;
        RefreshOptions();
    }

    private Dictionary<string, SuperDragSearchProvider> ReadProviders() => SearchProviders.ToDictionary(row => row.Id,
        row => new SuperDragSearchProvider { Name = row.Name, SearchUrlTemplate = row.SearchUrlTemplate });

    private void SearchProviderChanged(object? sender, PropertyChangedEventArgs args) => RefreshOptions();
    private void RefreshOptions()
    {
        var providers = ReadProviders();
        foreach (var row in Mappings) row.UpdateOptions(providers);
    }

    internal void ApplyTo(AppSettings result) => result.SuperDrag = BuildResult();

    internal static void RunWhenLoaded(FrameworkElement owner, Action action)
    {
        if (owner.IsLoaded)
        {
            action();
            return;
        }
        void Loaded(object sender, RoutedEventArgs args)
        {
            owner.Loaded -= Loaded;
            action();
        }
        owner.Loaded += Loaded;
    }
}
