using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KeyPulse.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KeyPulse.Views;

public partial class TroubleshootingView
{
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
        var logContent = _viewModel?.LogContent ?? string.Empty;
        var searchQuery = _viewModel?.SearchQuery ?? string.Empty;
        _matchRanges.Clear();
        _matchRuns.Clear();
        _currentMatchIndex = -1;

        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);

        if (string.IsNullOrEmpty(logContent))
        {
            LogViewer.Document = document;
            UpdateSearchCounter();
            return;
        }

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            paragraph.Inlines.Add(new Run(logContent));
            LogViewer.Document = document;
            UpdateSearchCounter();
            return;
        }

        var search = searchQuery.Trim();
        var startIndex = 0;

        while (startIndex < logContent.Length)
        {
            var matchIndex = logContent.IndexOf(search, startIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                paragraph.Inlines.Add(new Run(logContent[startIndex..]));
                break;
            }

            if (matchIndex > startIndex)
                paragraph.Inlines.Add(new Run(logContent[startIndex..matchIndex]));

            var matchRun = new Run(logContent.Substring(matchIndex, search.Length))
            {
                Background = Brushes.Yellow,
                Foreground = Brushes.Black,
            };
            paragraph.Inlines.Add(matchRun);
            _matchRanges.Add((matchIndex, search.Length));
            _matchRuns.Add(matchRun);

            startIndex = matchIndex + search.Length;
        }

        LogViewer.Document = document;
        UpdateSearchCounter();
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
