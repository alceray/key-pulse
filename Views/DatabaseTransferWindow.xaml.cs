using System.ComponentModel;
using System.Windows;
using KeyPulse.Services;

namespace KeyPulse.Views;

public partial class DatabaseTransferWindow : Window
{
    private bool _finished;
    private bool _cancelRequested;
    public event Action? CancelRequested;

    public DatabaseTransferWindow(string caption)
    {
        InitializeComponent();
        Title = caption;
    }

    public void SetStage(DatabaseTransferStage stage)
    {
        if (_finished || _cancelRequested)
            return;
        if (stage == DatabaseTransferStage.Idle)
        {
            Hide();
            return;
        }
        StatusText.Text = stage switch
        {
            DatabaseTransferStage.Copying => "Copying history...",
            DatabaseTransferStage.Verifying => "Verifying data...",
            DatabaseTransferStage.Activating => "Finishing database change...",
            _ => "Preparing database...",
        };
        if (!IsVisible)
            Show();
    }

    public void Complete()
    {
        _finished = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => RequestCancellation();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancellation();
        }
        base.OnClosing(e);
    }

    private void RequestCancellation()
    {
        if (_cancelRequested)
            return;
        _cancelRequested = true;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Stopping...";
        CancelRequested?.Invoke();
    }
}
