using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RobloxAltClient.Models;
using RobloxAltClient.Services;

namespace RobloxAltClient;

public partial class SettingsDialog : Window
{
    private readonly LauncherSettings _workingSettings;
    private readonly ObservableCollection<GamePreset> _games = [];
    private readonly ObservableCollection<AccountProfile> _profiles = [];
    private readonly ICollectionView _gamesView;
    private GamePreset? _activeGame;
    private AccountProfile? _activeProfile;
    private bool _restoringSelection;

    public LauncherSettings Settings => _workingSettings;
    public IReadOnlyList<AccountProfile> Profiles => _profiles;
    public bool ClearToolsRequested => ClearToolsBox.IsChecked == true;
    public bool ClearSessionsRequested => ClearSessionsBox.IsChecked == true;

    public SettingsDialog(
        LauncherSettings settings,
        IEnumerable<GamePreset> games,
        IEnumerable<AccountProfile> profiles)
    {
        InitializeComponent();
        WindowAppearance.ApplyModernChrome(this);

        _workingSettings = CloneSettings(settings);
        foreach (var game in games.Where(game => game.Url.Length > 0))
        {
            var clone = CloneGame(game);
            var key = NormalizeGameUrl(clone.Url);
            if (_workingSettings.GameOverrides.TryGetValue(key, out var gameSettings))
            {
                clone.Settings = gameSettings.Clone();
            }

            _games.Add(clone);
        }

        foreach (var profile in profiles)
        {
            _profiles.Add(CloneProfile(profile));
        }

        GamesList.ItemsSource = _games;
        ProfilesList.ItemsSource = _profiles;
        _gamesView = CollectionViewSource.GetDefaultView(_games);
        _gamesView.Filter = FilterGame;

        UpdateChecksBox.IsChecked = _workingSettings.UpdateChecksEnabled;
        ContinueOnFailureBox.IsChecked = _workingSettings.ContinueOnFailure;
        RememberSelectionsBox.IsChecked = _workingSettings.RememberSelections;
        SelectByTag(TimeoutBox, _workingSettings.LaunchTimeoutSeconds.ToString());
        SelectByTag(DelayBox, _workingSettings.LaunchDelaySeconds.ToString());
        SelectByTag(LauncherBox, _workingSettings.PreferredLauncher);
        GlobalSettingsEditor.LoadSettings(_workingSettings.GameSettings);

        if (_games.Count > 0)
        {
            GamesList.SelectedIndex = 0;
        }

        if (_profiles.Count > 0)
        {
            ProfilesList.SelectedIndex = 0;
        }
    }

    private void GameSearchBox_TextChanged(object sender, TextChangedEventArgs e) => _gamesView.Refresh();

    private bool FilterGame(object item)
    {
        if (item is not GamePreset game || string.IsNullOrWhiteSpace(GameSearchBox.Text))
        {
            return true;
        }

        var search = GameSearchBox.Text.Trim();
        return game.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               game.Url.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }

        if (!CommitActiveGame())
        {
            RestoreSelection(GamesList, _activeGame);
            return;
        }

        _activeGame = GamesList.SelectedItem as GamePreset;
        GameScopeText.Text = _activeGame is null ? "Select a saved game" : $"{_activeGame.Name} settings";
        GameSettingsEditor.LoadSettings(_activeGame?.Settings);
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
        {
            return;
        }

        if (!CommitActiveProfile())
        {
            RestoreSelection(ProfilesList, _activeProfile);
            return;
        }

        _activeProfile = ProfilesList.SelectedItem as AccountProfile;
        ProfileScopeText.Text = _activeProfile is null ? "Select an account profile" : $"{_activeProfile.Label} settings";
        ProfileSettingsEditor.LoadSettings(_activeProfile?.GameSettings);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!GlobalSettingsEditor.TryReadSettings(out var globalSettings, out var globalError))
        {
            MessageBox.Show(this, globalError, "Global defaults", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!CommitActiveGame() || !CommitActiveProfile())
        {
            return;
        }

        if (ClearSessionsRequested)
        {
            var answer = MessageBox.Show(
                this,
                "This will sign every saved profile out of Roblox after the next restart. Continue?",
                "Clear all browser sessions",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        _workingSettings.UpdateChecksEnabled = UpdateChecksBox.IsChecked == true;
        _workingSettings.ContinueOnFailure = ContinueOnFailureBox.IsChecked == true;
        _workingSettings.RememberSelections = RememberSelectionsBox.IsChecked == true;
        _workingSettings.LaunchTimeoutSeconds = ReadIntTag(TimeoutBox, 45);
        _workingSettings.LaunchDelaySeconds = ReadIntTag(DelayBox, 0);
        _workingSettings.PreferredLauncher = ReadTag(LauncherBox, "Auto");
        _workingSettings.GameSettings = globalSettings;
        DialogResult = true;
    }

    private bool CommitActiveGame()
    {
        if (_activeGame is null)
        {
            return true;
        }

        if (!GameSettingsEditor.TryReadSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, "Game settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _activeGame.Settings = settings.HasOverrides ? settings : null;
        var key = NormalizeGameUrl(_activeGame.Url);
        if (_activeGame.Settings is null)
        {
            _workingSettings.GameOverrides.Remove(key);
        }
        else
        {
            _workingSettings.GameOverrides[key] = _activeGame.Settings.Clone();
        }

        return true;
    }

    private bool CommitActiveProfile()
    {
        if (_activeProfile is null)
        {
            return true;
        }

        if (!ProfileSettingsEditor.TryReadSettings(out var settings, out var error))
        {
            MessageBox.Show(this, error, "Profile settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _activeProfile.GameSettings = settings.HasOverrides ? settings : null;
        return true;
    }

    private void RestoreSelection(ListBox list, object? item)
    {
        _restoringSelection = true;
        try
        {
            list.SelectedItem = item;
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static LauncherSettings CloneSettings(LauncherSettings source) => new()
    {
        UpdateChecksEnabled = source.UpdateChecksEnabled,
        LaunchTimeoutSeconds = source.LaunchTimeoutSeconds,
        LaunchDelaySeconds = source.LaunchDelaySeconds,
        ContinueOnFailure = source.ContinueOnFailure,
        RememberSelections = source.RememberSelections,
        PreferredLauncher = source.PreferredLauncher,
        LastSelectedProfileIds = source.LastSelectedProfileIds?.ToList() ?? [],
        LastGameName = source.LastGameName,
        RecentGameNames = source.RecentGameNames?.ToList() ?? [],
        ClearBrowserDataOnNextStart = source.ClearBrowserDataOnNextStart,
        MasterVolumeMigrationCompleted = source.MasterVolumeMigrationCompleted,
        GameSettings = source.GameSettings?.Clone() ?? new GameSettings(),
        GameOverrides = (source.GameOverrides ?? new Dictionary<string, GameSettings>())
            .Where(pair => pair.Value is not null && pair.Value.HasOverrides)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase)
    };

    private static GamePreset CloneGame(GamePreset source) => new(source.Name, source.Url, source.IsBuiltIn)
    {
        Settings = source.Settings?.Clone()
    };

    private static AccountProfile CloneProfile(AccountProfile source) => new()
    {
        Id = source.Id,
        Label = source.Label,
        CreatedUtc = source.CreatedUtc,
        Group = source.Group,
        IsFavorite = source.IsFavorite,
        SortOrder = source.SortOrder,
        GameSettings = source.GameSettings?.HasOverrides == true ? source.GameSettings.Clone() : null
    };

    private static string NormalizeGameUrl(string url) =>
        GamePreset.TryNormalizeRobloxGameUrl(url, out var normalized) ? normalized : url.Trim();

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items[0];
    }

    private static string ReadTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static int ReadIntTag(ComboBox comboBox, int fallback) =>
        int.TryParse(ReadTag(comboBox, fallback.ToString()), out var value) ? value : fallback;
}
