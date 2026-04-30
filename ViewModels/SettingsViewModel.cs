using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using Serilog;

namespace KeyPulse.ViewModels;

public class SettingsViewModel : ObservableObject, IDisposable
{
    public event Action? LogsRefreshed;

    private static readonly Regex TimestampPattern = new(
        AppConstants.Logging.TimestampPatternRegex,
        RegexOptions.Compiled
    );

    private static readonly (string Name, bool DefaultSelected)[] FilterDefinitions =
    [
        (AppConstants.Logging.AllLabel, false),
        ("Fatal", true),
        ("Error", true),
        ("Warning", false),
        ("Information", false),
        ("Debug", false),
    ];

    private readonly AppSettingsService _appSettingsService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly LogAccessService _logAccessService;
    private readonly UpdateService _updateService;
    private bool _launchOnLogin;
    private bool _autoInstallUpdates;
    private bool _isCheckingUpdates;
    private bool _isUpdateAvailable;
    private string? _latestUpdateVersion;
    private bool _isLogsVisible = false;
    private bool _suppressAutoSave;
    private bool _syncingFilters;
    private string? _selectedLogFile;
    private string _searchQuery = string.Empty;
    private string _rawLogContent = string.Empty;
    private string _logContent = string.Empty;
    private string _statusMessage = "";
    private readonly DispatcherTimer _statusTimer;

    public SettingsViewModel(
        AppSettingsService appSettingsService,
        StartupRegistrationService startupRegistrationService,
        LogAccessService logAccessService,
        UpdateService updateService
    )
    {
        _appSettingsService = appSettingsService;
        _startupRegistrationService = startupRegistrationService;
        _logAccessService = logAccessService;
        _updateService = updateService;

        RefreshLogsCommand = new RelayCommand(_ => RefreshLogs());
        CopyLogsCommand = new RelayCommand(_ => CopyLogs(), _ => !string.IsNullOrEmpty(LogContent));
        OpenLogsFolderCommand = new RelayCommand(_ => OpenLogsFolder());
        UpdateActionCommand = new RelayCommand(async _ => await RunUpdateActionAsync(), _ => !_isCheckingUpdates);
        _appSettingsService.SettingsChanged += OnSettingsChanged;
        _updateService.UpdateStatusChanged += OnUpdateStatusChanged;

        _isUpdateAvailable = _updateService.UpdateAvailable;
        _latestUpdateVersion = _updateService.LatestVersion;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusMessage = "";
        };

        LogFiles = new ObservableCollection<string>();
        LogFilters = new ObservableCollection<LogFilterItem>(
            FilterDefinitions.Select(f => new LogFilterItem { Name = f.Name, IsSelected = f.DefaultSelected })
        );
        foreach (var item in LogFilters)
            item.PropertyChanged += OnFilterItemChanged;

        LoadSettings();
    }

    public bool LaunchOnLogin
    {
        get => _launchOnLogin;
        set
        {
            if (_launchOnLogin == value)
                return;

            _launchOnLogin = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings();
        }
    }

    public bool AutoInstallUpdates
    {
        get => _autoInstallUpdates;
        set
        {
            if (_autoInstallUpdates == value)
                return;

            _autoInstallUpdates = value;
            OnPropertyChanged();

            if (!_suppressAutoSave)
                SaveSettings();
        }
    }

    public bool IsLogsVisible
    {
        get => _isLogsVisible;
        set
        {
            if (_isLogsVisible == value)
                return;
            _isLogsVisible = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LogsColumnWidth));
        }
    }

    public ObservableCollection<string> LogFiles { get; }

    public ObservableCollection<LogFilterItem> LogFilters { get; }

    public string FilterLabel
    {
        get
        {
            var selected = LogFilters.Where(f => f.IsSelected).Select(f => f.Name).ToList();
            if (selected.Count == 0)
                return AppConstants.Logging.AllLabel;

            return selected.Contains(AppConstants.Logging.AllLabel)
                ? AppConstants.Logging.AllLabel
                : string.Join(", ", selected);
        }
    }

    public string? SelectedLogFile
    {
        get => _selectedLogFile;
        set
        {
            if (_selectedLogFile == value)
                return;

            _selectedLogFile = value;
            OnPropertyChanged();
            LoadSelectedLogContent();
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            var normalized = value;
            if (_searchQuery == normalized)
                return;

            _searchQuery = normalized;
            OnPropertyChanged();
        }
    }

    public string LogContent
    {
        get => _logContent;
        private set
        {
            if (_logContent == value)
                return;

            _logContent = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
                return;

            _statusMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusVisibility));

            if (!string.IsNullOrEmpty(value))
            {
                _statusTimer.Stop();
                _statusTimer.Start();
            }
        }
    }

    public GridLength LogsColumnWidth => _isLogsVisible ? new GridLength(3, GridUnitType.Star) : new GridLength(0);

    public Visibility StatusVisibility =>
        string.IsNullOrEmpty(_statusMessage) ? Visibility.Collapsed : Visibility.Visible;

    public string LogsButtonText
    {
        get
        {
            if (string.IsNullOrEmpty(_rawLogContent))
                return "Show Logs";

            var fatalCount = CountLogEntries(AppConstants.Logging.FatalToken);
            var errorCount = CountLogEntries(AppConstants.Logging.ErrorToken);

            var counts = new List<string>();
            if (fatalCount > 0)
                counts.Add($"{fatalCount} Fatal");
            if (errorCount > 0)
                counts.Add($"{errorCount} Error");

            return counts.Count > 0 ? $"Show Logs ({string.Join(", ", counts)})" : "Show Logs";
        }
    }

    public string CurrentVersionDisplay => $"Version: v{_updateService.CurrentVersion}";

    public string UpdateActionButtonText =>
        _isUpdateAvailable && !string.IsNullOrWhiteSpace(_latestUpdateVersion)
            ? $"Update to v{_latestUpdateVersion}"
            : "Check for Updates";

    private int CountLogEntries(string token)
    {
        return ParseLogEntries().Count(entry => entry.Any(l => l.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private int CountTotalEntries()
    {
        return ParseLogEntries().Count;
    }

    private List<List<string>> ParseLogEntries()
    {
        if (string.IsNullOrEmpty(_rawLogContent))
            return [];

        var entries = new List<List<string>>();
        List<string>? current = null;
        using var reader = new StringReader(_rawLogContent);
        string? line;
        while ((line = reader.ReadLine()) != null)
            if (TimestampPattern.IsMatch(line))
            {
                current = [line];
                entries.Add(current);
            }
            else
            {
                current?.Add(line);
            }

        return entries;
    }

    private string GetTokenForLevel(string levelName)
    {
        return levelName switch
        {
            "Fatal" => AppConstants.Logging.FatalToken,
            "Error" => AppConstants.Logging.ErrorToken,
            "Warning" => AppConstants.Logging.WarningToken,
            "Information" => AppConstants.Logging.InformationToken,
            "Debug" => AppConstants.Logging.DebugToken,
            _ => "",
        };
    }

    private void UpdateFilterCounts()
    {
        if (LogFilters == null)
            return;

        foreach (var filter in LogFilters)
            filter.Count =
                filter.Name == AppConstants.Logging.AllLabel
                    ? CountTotalEntries()
                    : CountLogEntries(GetTokenForLevel(filter.Name));
    }

    public ICommand RefreshLogsCommand { get; }

    public ICommand CopyLogsCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    public ICommand UpdateActionCommand { get; }

    private void RefreshLogs()
    {
        LoadLogFiles();

        // Force re-read even when the selected file hasn't changed.
        if (!string.IsNullOrWhiteSpace(SelectedLogFile))
            LoadSelectedLogContent();

        LogsRefreshed?.Invoke();
    }

    private async Task RunUpdateActionAsync()
    {
        if (_isCheckingUpdates)
            return;

        if (_isUpdateAvailable && !string.IsNullOrWhiteSpace(_latestUpdateVersion))
        {
            _updateService.InstallUpdate();
            return;
        }

        try
        {
            _isCheckingUpdates = true;
            CommandManager.InvalidateRequerySuggested();
            await _updateService.CheckForUpdatesAsync();
            SyncUpdateStateFromService();

            if (!_isUpdateAvailable)
                StatusMessage = "No new updates available.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Update check failed. Check logs for details.";
            Log.Error(ex, "Manual update check failed from SettingsView");
        }
        finally
        {
            _isCheckingUpdates = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void OnUpdateStatusChanged(UpdateService.UpdateAvailableEventArgs args)
    {
        void Apply()
        {
            _isUpdateAvailable = args.Available;
            _latestUpdateVersion = args.LatestVersion;
            OnPropertyChanged(nameof(UpdateActionButtonText));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Apply();
            return;
        }

        dispatcher.BeginInvoke(new Action(Apply));
    }

    private void SyncUpdateStateFromService()
    {
        _isUpdateAvailable = _updateService.UpdateAvailable;
        _latestUpdateVersion = _updateService.LatestVersion;
        OnPropertyChanged(nameof(UpdateActionButtonText));
    }

    private void LoadSettings()
    {
        _suppressAutoSave = true;
        try
        {
            var settings = _appSettingsService.GetSettings();
            LaunchOnLogin = settings.LaunchOnLogin;
            AutoInstallUpdates = settings.AutoInstallUpdates;

            // Reflect the actual registration state so the UI matches the machine state.
            if (!_startupRegistrationService.IsEnabled() && LaunchOnLogin)
                LaunchOnLogin = false;

            LoadLogFiles();
            StatusMessage = "";
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppUserSettings
            {
                LaunchOnLogin = LaunchOnLogin,
                AutoInstallUpdates = AutoInstallUpdates,
            };

            _appSettingsService.SaveSettings(settings);

            if (settings.LaunchOnLogin)
                _startupRegistrationService.Enable();
            else
                _startupRegistrationService.Disable();

            StatusMessage = "Settings saved.";
            Log.Information(
                "Settings updated from SettingsView: LaunchOnLogin={LaunchOnLogin}, AutoInstallUpdates={AutoInstallUpdates}",
                settings.LaunchOnLogin,
                settings.AutoInstallUpdates
            );
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to save settings. Check logs for details.";
            Log.Error(ex, "Failed to save settings from SettingsView");
        }
    }

    private void OnSettingsChanged(AppUserSettings settings)
    {
        _suppressAutoSave = true;
        try
        {
            LaunchOnLogin = settings.LaunchOnLogin;
            AutoInstallUpdates = settings.AutoInstallUpdates;
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private void LoadLogFiles()
    {
        var currentSelection = SelectedLogFile;
        var logFiles = _logAccessService.GetLogFiles();

        LogFiles.Clear();
        foreach (var logFile in logFiles)
            LogFiles.Add(logFile);

        SelectedLogFile = logFiles.Contains(currentSelection) ? currentSelection : logFiles.FirstOrDefault();

        if (SelectedLogFile == null)
        {
            _rawLogContent = string.Empty;
            LogContent = "No log files found.";
        }
    }

    private void LoadSelectedLogContent()
    {
        if (string.IsNullOrWhiteSpace(SelectedLogFile))
        {
            _rawLogContent = string.Empty;
            LogContent = "No log file selected.";
            UpdateFilterCounts();
            OnPropertyChanged(nameof(LogsButtonText));
            return;
        }

        try
        {
            _rawLogContent = _logAccessService.ReadLogContent(SelectedLogFile);
            if (string.IsNullOrWhiteSpace(_rawLogContent))
            {
                LogContent = "Selected log is empty.";
                UpdateFilterCounts();
                OnPropertyChanged(nameof(LogsButtonText));
            }
            else
            {
                ApplyLogFilter();
            }
        }
        catch (Exception ex)
        {
            _rawLogContent = string.Empty;
            LogContent = "Failed to read selected log file.";
            Log.Error(ex, "Failed to read log content for {LogFile}", SelectedLogFile);
            UpdateFilterCounts();
            OnPropertyChanged(nameof(LogsButtonText));
        }
    }

    private void ApplyLogFilter()
    {
        OnPropertyChanged(nameof(FilterLabel));
        UpdateFilterCounts();
        OnPropertyChanged(nameof(LogsButtonText));

        if (string.IsNullOrWhiteSpace(_rawLogContent))
            return;

        var selectedNames = LogFilters.Where(f => f.IsSelected).Select(f => f.Name).ToList();
        if (selectedNames.Count == 0)
            selectedNames = [AppConstants.Logging.AllLabel];

        var hasAll = selectedNames.Contains(AppConstants.Logging.AllLabel);

        if (hasAll)
        {
            LogContent = _rawLogContent;
            return;
        }

        var entries = ParseLogEntries();
        IEnumerable<List<string>> matchingEntries = entries;

        if (!hasAll)
        {
            var filterTokens = selectedNames
                .Select(GetTokenForLevel)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .ToArray();

            if (filterTokens.Length > 0)
                matchingEntries = matchingEntries.Where(entry =>
                    entry.Any(l => filterTokens.Any(token => l.Contains(token, StringComparison.OrdinalIgnoreCase)))
                );
        }

        var result = string.Join(Environment.NewLine, matchingEntries.SelectMany(e => e));
        if (result.Length > 0)
        {
            LogContent = result;
            return;
        }

        var filterLabel = string.Join(", ", selectedNames).ToLowerInvariant();
        LogContent = $"No {filterLabel} entries found in selected log.";
    }

    private void OpenLogsFolder()
    {
        try
        {
            _logAccessService.OpenLogsFolder();
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to open logs folder. Check logs for details.";
            Log.Error(ex, "Failed to open logs folder from SettingsView");
        }
    }

    private void CopyLogs()
    {
        try
        {
            Clipboard.SetText(LogContent);
            StatusMessage = "Logs copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Failed to copy logs.";
            Log.Error(ex, "Failed to copy log content to clipboard");
        }
    }

    private void OnFilterItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LogFilterItem.IsSelected))
            return;

        if (_syncingFilters)
            return;

        _syncingFilters = true;
        try
        {
            if (sender is LogFilterItem { Name: var name } allItem && name == AppConstants.Logging.AllLabel)
            {
                foreach (var f in LogFilters.Where(f => f.Name != AppConstants.Logging.AllLabel))
                    f.IsSelected = allItem.IsSelected;
            }
            else if (sender is LogFilterItem { Name: var senderName } && senderName != AppConstants.Logging.AllLabel)
            {
                var masterAllItem = LogFilters.FirstOrDefault(f => f.Name == AppConstants.Logging.AllLabel);
                if (masterAllItem != null)
                    masterAllItem.IsSelected = LogFilters
                        .Where(f => f.Name != AppConstants.Logging.AllLabel)
                        .All(f => f.IsSelected);
            }
        }
        finally
        {
            _syncingFilters = false;
        }

        ApplyLogFilter();
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _appSettingsService.SettingsChanged -= OnSettingsChanged;
        _updateService.UpdateStatusChanged -= OnUpdateStatusChanged;
        foreach (var item in LogFilters)
            item.PropertyChanged -= OnFilterItemChanged;
        GC.SuppressFinalize(this);
    }

    public class LogFilterItem : ObservableObject
    {
        private bool _isSelected;
        private int _count;

        public required string Name { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                if (_count == value)
                    return;
                _count = value;
                OnPropertyChanged();
            }
        }
    }
}
