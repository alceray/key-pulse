using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyPulse.Configuration;
using KeyPulse.Helpers;
using KeyPulse.Services;
using Serilog;

namespace KeyPulse.ViewModels;

public class TroubleshootingViewModel : ObservableObject, IDisposable
{
    public event Action? LogsRefreshed;

    private static readonly Regex TimestampPattern = new(
        AppConstants.Troubleshooting.TimestampPatternRegex,
        RegexOptions.Compiled
    );

    private static readonly IReadOnlyList<string> FilterDefinitions = AppConstants.Troubleshooting.FilterNames;

    private readonly LogAccessService _logAccessService;
    private readonly DispatcherTimer _statusTimer;
    private bool _syncingFilters;
    private LogFileOption? _selectedLogFile;
    private string _searchQuery = string.Empty;
    private string _rawLogContent = string.Empty;
    private string _logContent = string.Empty;
    private string _statusMessage = string.Empty;

    public TroubleshootingViewModel(LogAccessService logAccessService)
    {
        _logAccessService = logAccessService;

        RefreshLogsCommand = new RelayCommand(_ => RefreshLogs());
        CopyLogsCommand = new RelayCommand(_ => CopyLogs(), _ => !string.IsNullOrEmpty(LogContent));
        OpenLogsFolderCommand = new RelayCommand(_ => OpenLogsFolder());

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusMessage = string.Empty;
        };

        LogFiles = new ObservableCollection<LogFileOption>();
        LogFilters = new ObservableCollection<LogFilterItem>(
            FilterDefinitions.Select(name => new LogFilterItem
            {
                Name = name,
                IsSelected = false,
                LevelBrush = AppStyles.GetLogLevelBrush(name),
            })
        );

        foreach (var item in LogFilters)
            item.PropertyChanged += OnFilterItemChanged;

        LoadLogFiles();
    }

    public ObservableCollection<LogFileOption> LogFiles { get; }

    public ObservableCollection<LogFilterItem> LogFilters { get; }

    public LogFileOption? SelectedLogFile
    {
        get => _selectedLogFile;
        set
        {
            if (ReferenceEquals(_selectedLogFile, value))
                return;

            if (_selectedLogFile?.FileName == value?.FileName)
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
            if (_searchQuery == value)
                return;

            _searchQuery = value;
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

    public Visibility StatusVisibility =>
        string.IsNullOrEmpty(_statusMessage) ? Visibility.Collapsed : Visibility.Visible;

    public ICommand RefreshLogsCommand { get; }

    public ICommand CopyLogsCommand { get; }

    public ICommand OpenLogsFolderCommand { get; }

    private void RefreshLogs()
    {
        LoadLogFiles();

        // Force re-read even when the selected file hasn't changed.
        if (SelectedLogFile != null)
            LoadSelectedLogContent();

        LogsRefreshed?.Invoke();
    }

    private int CountLogEntries(string token)
    {
        return ParseLogEntries()
            .Count(entry => entry.Any(line => line.Contains(token, StringComparison.OrdinalIgnoreCase)));
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
        {
            if (TimestampPattern.IsMatch(line))
            {
                current = [line];
                entries.Add(current);
                continue;
            }

            current?.Add(line);
        }

        return entries;
    }

    private static string GetTokenForLevel(string levelName)
    {
        return levelName switch
        {
            "Fatal" => AppConstants.Troubleshooting.FatalToken,
            "Error" => AppConstants.Troubleshooting.ErrorToken,
            "Warning" => AppConstants.Troubleshooting.WarningToken,
            "Information" => AppConstants.Troubleshooting.InformationToken,
            "Debug" => AppConstants.Troubleshooting.DebugToken,
            _ => string.Empty,
        };
    }

    private void UpdateFilterCounts()
    {
        foreach (var filter in LogFilters)
            filter.Count =
                filter.Name == AppConstants.Troubleshooting.AllLabel
                    ? CountTotalEntries()
                    : CountLogEntries(GetTokenForLevel(filter.Name));
    }

    private void LoadLogFiles()
    {
        var currentSelection = SelectedLogFile?.FileName;
        var logFiles = _logAccessService.GetLogFiles().Select(CreateLogFileOption).ToList();

        LogFiles.Clear();
        foreach (var logFile in logFiles)
            LogFiles.Add(logFile);

        SelectedLogFile =
            logFiles.FirstOrDefault(logFile => logFile.FileName == currentSelection) ?? logFiles.FirstOrDefault();

        if (SelectedLogFile != null)
            return;

        _rawLogContent = string.Empty;
        LogContent = "No log files found.";
        UpdateFilterCounts();
    }

    private void LoadSelectedLogContent()
    {
        if (string.IsNullOrWhiteSpace(SelectedLogFile?.FileName))
        {
            _rawLogContent = string.Empty;
            LogContent = "No log file selected.";
            UpdateFilterCounts();
            return;
        }

        try
        {
            _rawLogContent = _logAccessService.ReadLogContent(SelectedLogFile.FileName);
            if (string.IsNullOrWhiteSpace(_rawLogContent))
            {
                LogContent = "Selected log is empty.";
                UpdateFilterCounts();
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
            Log.Error(ex, "Failed to read log content for {LogFile}", SelectedLogFile.FileName);
            UpdateFilterCounts();
        }
    }

    private void ApplyLogFilter()
    {
        UpdateFilterCounts();

        if (string.IsNullOrWhiteSpace(_rawLogContent))
            return;

        var selectedNames = LogFilters.Where(filter => filter.IsSelected).Select(filter => filter.Name).ToList();
        if (selectedNames.Count == 0)
            selectedNames = [AppConstants.Troubleshooting.AllLabel];

        var hasAll = selectedNames.Contains(AppConstants.Troubleshooting.AllLabel);
        if (hasAll)
        {
            LogContent = _rawLogContent;
            return;
        }

        var entries = ParseLogEntries();
        IEnumerable<List<string>> matchingEntries = entries;

        var filterTokens = selectedNames
            .Select(GetTokenForLevel)
            .Where(token => !string.IsNullOrEmpty(token))
            .Distinct()
            .ToArray();

        if (filterTokens.Length > 0)
            matchingEntries = matchingEntries.Where(entry =>
                entry.Any(line => filterTokens.Any(token => line.Contains(token, StringComparison.OrdinalIgnoreCase)))
            );

        var result = string.Join(Environment.NewLine, matchingEntries.SelectMany(entry => entry));
        LogContent = result; // empty string when nothing matches — show nothing
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
            Log.Error(ex, "Failed to open logs folder from TroubleshootingView");
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
        if (e.PropertyName != nameof(LogFilterItem.IsSelected) || _syncingFilters)
            return;

        _syncingFilters = true;
        try
        {
            if (sender is LogFilterItem { Name: var name } allItem && name == AppConstants.Troubleshooting.AllLabel)
            {
                foreach (var filter in LogFilters.Where(filter => filter.Name != AppConstants.Troubleshooting.AllLabel))
                    filter.IsSelected = allItem.IsSelected;
            }
            else if (
                sender is LogFilterItem { Name: var senderName }
                && senderName != AppConstants.Troubleshooting.AllLabel
            )
            {
                var masterAllItem = LogFilters.FirstOrDefault(filter =>
                    filter.Name == AppConstants.Troubleshooting.AllLabel
                );
                if (masterAllItem != null)
                    masterAllItem.IsSelected = LogFilters
                        .Where(filter => filter.Name != AppConstants.Troubleshooting.AllLabel)
                        .All(filter => filter.IsSelected);
            }
        }
        finally
        {
            _syncingFilters = false;
        }

        ApplyLogFilter();
    }

    private LogFileOption CreateLogFileOption(string fileName)
    {
        return new LogFileOption { FileName = fileName, DisplayDateText = BuildLogDisplayDate(fileName) };
    }

    private string BuildLogDisplayDate(string fileName)
    {
        var dateSegment = Path.GetFileNameWithoutExtension(fileName);
        const string filePrefix = AppConstants.Troubleshooting.LogFilePrefix;
        if (dateSegment.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            dateSegment = dateSegment[filePrefix.Length..];

        if (
            DateTime.TryParseExact(
                dateSegment,
                AppConstants.Troubleshooting.LogFileDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate
            )
        )
            return parsedDate.ToString(AppConstants.Troubleshooting.LogDisplayDateFormat, CultureInfo.InvariantCulture);

        var filePath = Path.Combine(_logAccessService.LogDirectory, fileName);
        if (File.Exists(filePath))
            return File.GetLastWriteTime(filePath)
                .ToString(AppConstants.Troubleshooting.LogDisplayDateFormat, CultureInfo.InvariantCulture);

        return fileName;
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        foreach (var item in LogFilters)
            item.PropertyChanged -= OnFilterItemChanged;
    }

    public class LogFilterItem : ObservableObject
    {
        private bool _isSelected;
        private int _count;

        public required string Name { get; init; }
        public required Brush LevelBrush { get; init; }

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

    public class LogFileOption
    {
        public required string FileName { get; init; }
        public required string DisplayDateText { get; init; }
    }
}
