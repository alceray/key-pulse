using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyPulse.Configuration;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class TroubleshootingView
{
    // ── Level colours ────────────────────────────────────────────────────────
    private static readonly Regex EntryTimestampRegex = new(
        AppConstants.Troubleshooting.TimestampPatternRegex,
        RegexOptions.Compiled
    );

    private static readonly Regex LevelTokenRegex = new(
        "\\[(FTL|ERR|WRN|INF|DBG)\\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    // ReSharper disable once InconsistentNaming
    private const char DividerChar = AppConstants.Troubleshooting.DividerChar;

    private static bool IsSessionStart(string firstLine) =>
        firstLine.Contains(AppConstants.Troubleshooting.SessionStartMarker, StringComparison.OrdinalIgnoreCase);

    private record EntryInfo(string Text, bool IsAppSessionStart);

    private static List<EntryInfo> ParseColoredEntries(string content)
    {
        var result = new List<EntryInfo>();
        if (string.IsNullOrEmpty(content))
            return result;

        var current = new List<string>();

        void Flush()
        {
            if (current.Count == 0)
                return;
            var text = string.Join("\n", current);
            result.Add(new EntryInfo(text, IsSessionStart(current[0])));
            current.Clear();
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (EntryTimestampRegex.IsMatch(line) && current.Count > 0)
                Flush();
            current.Add(line);
        }
        Flush();
        return result;
    }

    // ── Search / match tracking ───────────────────────────────────────────────
    private TroubleshootingViewModel? _viewModel;
    private readonly List<(int Start, int Length)> _matchRanges = [];
    private readonly List<Run> _matchRuns = [];
    private int _currentMatchIndex = -1;

    public TroubleshootingView()
    {
        InitializeComponent();
        DataContext = App.ServiceProvider.GetRequiredService<TroubleshootingViewModel>();
        DataContextChanged += (_, e) =>
        {
            SyncViewModel(e.NewValue as TroubleshootingViewModel);
            UpdateHighlightedLogContent();
        };
        Loaded += (_, _) =>
        {
            SyncViewModel(DataContext as TroubleshootingViewModel);
            UpdateHighlightedLogContent();
        };
        SizeChanged += (_, _) => UpdateHighlightedLogContent();
        Unloaded += (_, _) => SyncViewModel(null);

        SyncViewModel(DataContext as TroubleshootingViewModel);
        UpdateHighlightedLogContent();
    }

    private void SyncViewModel(TroubleshootingViewModel? next)
    {
        if (ReferenceEquals(_viewModel, next))
            return;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
            _viewModel.LogsRefreshed -= OnLogsRefreshed;
        }

        _viewModel = next;

        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
            _viewModel.LogsRefreshed += OnLogsRefreshed;
        }
    }

    private void OnLogsRefreshed()
    {
        Dispatcher.BeginInvoke(new Action(() => LogViewer.ScrollToEnd()), DispatcherPriority.Background);
    }

    private void ViewModelOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName
            is not nameof(TroubleshootingViewModel.LogContent)
                and not nameof(TroubleshootingViewModel.SearchQuery)
        )
            return;

        UpdateHighlightedLogContent();
    }

    private void UpdateHighlightedLogContent()
    {
        var rawContent = _viewModel?.LogContent ?? string.Empty;
        var searchQuery = (_viewModel?.SearchQuery ?? string.Empty).Trim();
        _matchRanges.Clear();
        _matchRuns.Clear();
        _currentMatchIndex = -1;

        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);

        if (string.IsNullOrEmpty(rawContent))
        {
            LogViewer.Document = document;
            UpdateSearchCounter();
            return;
        }

        // ── Parse entries and build the flat "document string" used for searching ──
        var entries = ParseColoredEntries(rawContent);
        var sb = new StringBuilder();
        var first = true;

        var dividerLine = BuildDividerLine();

        foreach (var entry in entries)
        {
            if (entry.IsAppSessionStart && !first)
            {
                sb.Append('\n');
                sb.Append(dividerLine);
            }
            if (!first)
                sb.Append('\n');
            sb.Append(entry.Text);
            first = false;
        }

        var docString = sb.ToString();

        // ── Locate all search matches inside the flat document string ──────────
        if (!string.IsNullOrEmpty(searchQuery))
        {
            var idx = 0;
            while (true)
            {
                var m = docString.IndexOf(searchQuery, idx, StringComparison.OrdinalIgnoreCase);
                if (m < 0)
                    break;
                _matchRanges.Add((m, searchQuery.Length));
                idx = m + searchQuery.Length;
            }
        }

        // ── Render using the same traversal order so _matchRuns aligns 1:1 ────
        first = true;
        foreach (var entry in entries)
        {
            if (entry.IsAppSessionStart && !first)
                EmitRuns(paragraph, "\n" + dividerLine, AppStyles.DividerBrush, searchQuery);

            if (!first)
                EmitRuns(paragraph, "\n", AppStyles.BlackBrush, searchQuery);

            EmitTokenColoredRuns(paragraph, entry.Text, searchQuery);
            first = false;
        }

        LogViewer.Document = document;
        UpdateSearchCounter();
    }

    private string BuildDividerLine()
    {
        var scrollViewer = FindDescendant<ScrollViewer>(LogViewer);
        var width = scrollViewer?.ViewportWidth;
        if (width is null || double.IsNaN(width.Value) || width <= 0)
            width = LogViewer.ActualWidth - 24; // Account for border and padding.

        var charCount = (int)Math.Ceiling(width.Value / AppConstants.Troubleshooting.DividerCharWidth);
        if (charCount < AppConstants.Troubleshooting.DividerMinChars)
            charCount = AppConstants.Troubleshooting.DividerMinChars;

        return new string(DividerChar, charCount);
    }

    private void EmitTokenColoredRuns(Paragraph paragraph, string text, string searchQuery)
    {
        var cursor = 0;
        foreach (Match match in LevelTokenRegex.Matches(text))
        {
            if (match.Index > cursor)
                EmitRuns(paragraph, text[cursor..match.Index], AppStyles.BlackBrush, searchQuery);

            EmitRuns(paragraph, match.Value, AppStyles.GetLogTokenBrush(match.Value), searchQuery);
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
            EmitRuns(paragraph, text[cursor..], AppStyles.BlackBrush, searchQuery);
    }

    /// <summary>
    /// Appends <paramref name="text"/> to <paramref name="paragraph"/> as one or more
    /// <see cref="Run"/> elements.  When <paramref name="searchQuery"/> is non-empty,
    /// matching segments receive a yellow background and are registered in
    /// <see cref="_matchRuns"/> (in document order, matching <see cref="_matchRanges"/>).
    /// </summary>
    private void EmitRuns(Paragraph paragraph, string text, Brush baseBrush, string searchQuery)
    {
        if (string.IsNullOrEmpty(searchQuery))
        {
            paragraph.Inlines.Add(new Run(text) { Foreground = baseBrush });
            return;
        }

        var local = 0;
        while (local < text.Length)
        {
            var match = text.IndexOf(searchQuery, local, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                paragraph.Inlines.Add(new Run(text[local..]) { Foreground = baseBrush });
                break;
            }

            if (match > local)
                paragraph.Inlines.Add(new Run(text[local..match]) { Foreground = baseBrush });

            var matchRun = new Run(text.Substring(match, searchQuery.Length))
            {
                Foreground = baseBrush,
                Background = Brushes.Yellow,
            };
            paragraph.Inlines.Add(matchRun);
            _matchRuns.Add(matchRun);

            local = match + searchQuery.Length;
        }
    }

    private void OnLogSearchTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            LogSearchTextBox.Clear();
            LogSearchTextBox.Focus();
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        AdvanceToNextMatch();
    }

    private void AdvanceToNextMatch()
    {
        if (_matchRanges.Count == 0)
            return;

        _currentMatchIndex = (_currentMatchIndex + 1) % _matchRanges.Count;
        UpdateActiveMatchHighlight();
        var (start, length) = _matchRanges[_currentMatchIndex];
        SelectAndCenterMatch(start, length);
        UpdateSearchCounter();
    }

    private void UpdateActiveMatchHighlight()
    {
        for (var i = 0; i < _matchRuns.Count; i++)
            _matchRuns[i].Background = i == _currentMatchIndex ? Brushes.Orange : Brushes.Yellow;
    }

    private void UpdateSearchCounter()
    {
        if (_matchRanges.Count == 0)
        {
            SearchResultCounterText.Text = string.Empty;
            SearchResultCounterText.Visibility = Visibility.Collapsed;
            return;
        }

        var currentDisplayIndex = _currentMatchIndex >= 0 ? _currentMatchIndex + 1 : 1;
        SearchResultCounterText.Text = $"{currentDisplayIndex}/{_matchRanges.Count}";
        SearchResultCounterText.Visibility = Visibility.Visible;
    }

    private void SelectAndCenterMatch(int start, int length)
    {
        var rangeStart = GetTextPointerAtOffset(LogViewer.Document.ContentStart, start);
        var rangeEnd = GetTextPointerAtOffset(rangeStart, length);
        LogViewer.Selection.Select(rangeStart, rangeEnd);
        LogViewer.Focus();

        var scrollViewer = FindDescendant<ScrollViewer>(LogViewer);
        if (scrollViewer == null)
        {
            LogSearchTextBox.Focus();
            return;
        }

        var targetRect = LogViewer.Selection.Start.GetCharacterRect(LogicalDirection.Forward);
        var targetOffset = scrollViewer.VerticalOffset + targetRect.Top - scrollViewer.ViewportHeight / 2;
        if (double.IsNaN(targetOffset))
            targetOffset = scrollViewer.VerticalOffset;

        if (targetOffset < 0)
            targetOffset = 0;

        scrollViewer.ScrollToVerticalOffset(targetOffset);
        LogSearchTextBox.Focus();
    }

    private static TextPointer GetTextPointerAtOffset(TextPointer start, int charOffset)
    {
        var current = start;
        var remaining = charOffset;

        while (current != null)
        {
            if (remaining <= 0)
                return current;

            if (current.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var textRun = current.GetTextInRun(LogicalDirection.Forward);
                if (textRun.Length >= remaining)
                    return current.GetPositionAtOffset(remaining) ?? current;

                remaining -= textRun.Length;
                current = current.GetPositionAtOffset(textRun.Length, LogicalDirection.Forward);
                continue;
            }

            current = current.GetNextContextPosition(LogicalDirection.Forward);
        }

        return start.DocumentEnd;
    }

    private static T? FindDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root == null)
            return null;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                return typed;

            var nested = FindDescendant<T>(child);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        LogSearchTextBox.Focus();
        LogSearchTextBox.SelectAll();
        e.Handled = true;
    }
}
