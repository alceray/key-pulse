using System.Dynamic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using KeyPulse.Configuration;
using KeyPulse.ViewModels.Dashboard;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace KeyPulse.Tests.Views;

// Theme resources are process-wide; run separately from tests that assert light-palette defaults.
[CollectionDefinition("WPF theme", DisableParallelization = true)]
public class ThemeTestCollection;

[Collection("WPF theme")]
public class ThemeTests
{
    [Fact]
    public void ActualViews_RenderInBothThemes_WithoutStartingCaptureOrOpeningADatabase()
    {
        RunSta(() =>
        {
            System.Reflection.Assembly.Load("MahApps.Metro.IconPacks.PhosphorIcons");
            System.Reflection.Assembly.Load("MahApps.Metro.IconPacks.SimpleIcons");
            foreach (
                var name in new[]
                {
                    "SettingsView",
                    "DeviceListView",
                    "CalendarView",
                    "DashboardView",
                    "TroubleshootingView",
                    "DatabaseSetupWindow",
                    "DatabaseTransferWindow",
                    "CloseToTrayHintWindow",
                }
            )
            {
                var view = XElement.Load(Path.Combine(AppContext.BaseDirectory, "ThemeViewSources", name + ".xaml"));
                XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var events = new HashSet<string>
                {
                    "Class",
                    "Click",
                    "Checked",
                    "PasswordChanged",
                    "SelectionChanged",
                    "IsVisibleChanged",
                    "PreviewKeyDown",
                    "PreviewKeyUp",
                    "KeyDown",
                    "MouseLeftButtonUp",
                    "PreviewMouseMove",
                };
                // Exercise the actual view markup with sample data, without executing its service-owning code-behind.
                foreach (var attribute in view.DescendantsAndSelf().Attributes().ToList())
                {
                    if (events.Contains(attribute.Name.LocalName))
                        attribute.Remove();
                    else if (attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("clr-namespace:"))
                        attribute.Value += ";assembly=KeyPulse Signal";
                }
                view.Descendants(wpf + "EventSetter").Remove();
                foreach (var element in view.DescendantsAndSelf())
                    if (element.Name.NamespaceName.StartsWith("clr-namespace:"))
                        element.Name = XName.Get(
                            element.Name.LocalName,
                            element.Name.NamespaceName + ";assembly=KeyPulse Signal"
                        );
                if (view.Name.LocalName == "Window")
                {
                    view.Name = wpf + "UserControl";
                    foreach (
                        var attribute in new[]
                        {
                            "Style",
                            "SizeToContent",
                            "ResizeMode",
                            "WindowStartupLocation",
                            "ShowInTaskbar",
                            "Title",
                        }
                    )
                        view.Attribute(attribute)?.Remove();
                }
                var gallery = (Border)
                    XamlReader.Parse(
                        """
                        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:config="clr-namespace:KeyPulse.Configuration;assembly=KeyPulse Signal"
                                Background="{DynamicResource WindowBackgroundBrush}"
                                TextElement.Foreground="{DynamicResource PrimaryTextBrush}">
                          <Border.Resources>
                            <ResourceDictionary>
                              <ResourceDictionary.MergedDictionaries>
                                <config:ThemeResources />
                                <ResourceDictionary Source="/KeyPulse Signal;component/Views/Styles/ThemeControls.xaml" />
                                <ResourceDictionary Source="/KeyPulse Signal;component/Views/Styles/AppStyles.xaml" />
                              </ResourceDictionary.MergedDictionaries>
                            </ResourceDictionary>
                          </Border.Resources>
                        """
                            + view
                            + "</Border>"
                    );
                dynamic sample = new ExpandoObject();
                sample.DarkMode = false;
                sample.LaunchOnLogin = false;
                sample.CloseToTray = true;
                sample.ShowCloseToTrayOption = true;
                sample.AutoInstallUpdates = true;
                sample.UseSqliteStorage = false;
                sample.UsePostgreSqlStorage = true;
                sample.IsSqliteActive = false;
                sample.IsPostgreSqlActive = true;
                sample.ShowPostgreSqlSummary = false;
                sample.ShowPostgreSqlForm = true;
                sample.ShowDatabaseActions = true;
                sample.CanEditConnectionTarget = true;
                sample.PostgreSqlHost = "localhost";
                sample.PostgreSqlPort = 5432;
                sample.PostgreSqlDatabase = "keypulse_test";
                sample.PostgreSqlUsername = "keypulse";
                sample.PostgreSqlSslModeChoices = Enum.GetValues<KeyPulse.Models.PostgreSqlSslMode>();
                sample.PostgreSqlSslMode = KeyPulse.Models.PostgreSqlSslMode.Prefer;
                if (name == "SettingsView")
                    ((PasswordBox)((UserControl)gallery.Child).FindName("PostgreSqlPasswordBox")).Password =
                        "sample-password";
                sample.CurrentVersionDisplay = "KeyPulse Signal 1.3.2";
                sample.UpdateActionButtonText = "Check for updates";
                sample.RetentionChoices = KeyPulse.ViewModels.Settings.RetentionOptions.All;
                sample.SelectedRetentionOption = KeyPulse.ViewModels.Settings.RetentionOptions.FromMonths(24);
                sample.ToastVisibility = Visibility.Collapsed;
                sample.ShowAllDevices = true;
                sample.CurrentSessionTime = "2 hours";
                sample.DeviceListCollection = new[]
                {
                    new KeyPulse.Models.Device
                    {
                        DeviceId = "keyboard",
                        DeviceName = "USB Keyboard",
                        DeviceType = KeyPulse.Models.DeviceTypes.Keyboard,
                    },
                    new KeyPulse.Models.Device
                    {
                        DeviceId = "mouse",
                        DeviceName = "Wireless Mouse",
                        DeviceType = KeyPulse.Models.DeviceTypes.Mouse,
                    },
                };
                sample.IsLoading = false;
                sample.HasSelectedDay = false;
                sample.MonthTitle = "September 2026";
                sample.CanGoPrevious = true;
                sample.CanGoNext = false;
                sample.CalendarGridItems = Enumerable
                    .Range(1, 30)
                    .Select(day => new
                    {
                        Day = new DateOnly(2026, 9, day),
                        HasData = day % 3 == 0,
                        IsSelected = day == 6,
                        IsToday = day == 7,
                        Devices = new[]
                        {
                            new
                            {
                                DeviceName = "USB Keyboard",
                                TypeIcon = "⌨",
                                IsConnected = day == 7,
                            },
                        },
                    })
                    .ToArray();
                sample.RangeOptions = new[] { "1 Day", "1 Week", "1 Month" };
                sample.SelectedRange = "1 Day";
                sample.RangeDisplayText = "September 7, 2026";
                sample.ConnectedDevices = 2;
                sample.ConnectedDevicesBreakdown = "1 keyboard, 1 mouse";
                sample.TopKeyboardsSummary = "1. USB Keyboard";
                sample.TopMiceSummary = "1. Wireless Mouse";
                sample.LastUpdatedText = "Last updated: 14:30";
                sample.IsTrackingPaused = false;
                gallery.DataContext = sample;
                try
                {
                    foreach (var dark in new[] { false, true })
                    {
                        sample.DarkMode = dark;
                        AppColorPalette.Apply(gallery.Resources, dark);
                        if (name == "DashboardView")
                        {
                            PlotModel Pie(string title, OxyColor color)
                            {
                                var model = new PlotModel { Title = title };
                                var series = new PieSeries();
                                series.Slices.Add(new PieSlice("USB device", 75) { Fill = color });
                                series.Slices.Add(new PieSlice("Other device", 25) { Fill = OxyColors.Gray });
                                model.Series.Add(series);
                                DashboardPlotTheme.Apply(model);
                                return model;
                            }
                            sample.KeyboardPiePlot = Pie("Keyboards", OxyColor.FromRgb(46, 111, 191));
                            sample.MousePiePlot = Pie("Mice", OxyColor.FromRgb(192, 52, 42));
                            var activity = new PlotModel { Title = "Input Activity" };
                            activity.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Hour" });
                            activity.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Inputs" });
                            var line = new LineSeries { Color = OxyColor.FromRgb(46, 111, 191) };
                            line.Points.AddRange(
                                new[]
                                {
                                    new DataPoint(8, 0),
                                    new DataPoint(9, 100),
                                    new DataPoint(10, 40),
                                    new DataPoint(11, 0),
                                }
                            );
                            activity.Series.Add(line);
                            DashboardPlotTheme.Apply(activity);
                            sample.InputActivityPlot = activity;
                        }
                        Layout(gallery);
                        gallery.ActualWidth.ShouldBeGreaterThan(0);
                        SavePreview(gallery, name + (dark ? "-dark" : "-light"));
                    }
                }
                finally
                {
                    AppColorPalette.Apply(gallery.Resources, false);
                }
            }
        });
    }

    [Fact]
    public void SwitchingTheme_UpdatesExistingControlsAndLogRuns_AndRestoresLightColors()
    {
        RunSta(() =>
        {
            var gallery = CreateGallery();
            var editor = (TextBox)gallery.FindName("Editor");
            var checkBox = (CheckBox)gallery.FindName("Checked");
            var combo = (ComboBox)gallery.FindName("Choices");
            var log = (RichTextBox)gallery.FindName("Log");
            var run = new Run("A connected keyboard recorded input. Search highlights stay readable.");
            run.SetResourceReference(TextElement.ForegroundProperty, nameof(AppColorPalette.PrimaryTextBrush));
            var match = new Run(" input ");
            match.SetResourceReference(TextElement.ForegroundProperty, "SearchHighlightTextBrush");
            match.SetResourceReference(TextElement.BackgroundProperty, nameof(AppColorPalette.SearchHighlightBrush));
            var paragraph = new Paragraph(run);
            paragraph.Inlines.Add(match);
            log.Document = new FlowDocument(paragraph);
            editor.Text = "Keep my text and selection";
            editor.Select(5, 7);
            combo.SelectedIndex = 1;
            var grid = (DataGrid)gallery.FindName("Devices");
            grid.ItemsSource = new[]
            {
                new
                {
                    Device = "USB Keyboard",
                    Status = "Connected",
                    Inputs = 12450,
                },
                new
                {
                    Device = "Wireless Mouse",
                    Status = "Disconnected",
                    Inputs = 2861,
                },
            };

            try
            {
                foreach (var dark in new[] { false, true, false })
                {
                    AppColorPalette.Apply(gallery.Resources, dark);
                    Layout(gallery);
                    ((SolidColorBrush)editor.Foreground).Color.ShouldBe(
                        ((SolidColorBrush)AppColorPalette.PrimaryTextBrush).Color
                    );
                    ((SolidColorBrush)run.Foreground).Color.ShouldBe(
                        ((SolidColorBrush)AppColorPalette.PrimaryTextBrush).Color
                    );
                    ((SolidColorBrush)match.Background).Color.ShouldBe(
                        ((SolidColorBrush)AppColorPalette.SearchHighlightBrush).Color
                    );
                    editor.SelectionStart.ShouldBe(5);
                    editor.SelectionLength.ShouldBe(7);
                    combo.SelectedIndex.ShouldBe(1);
                    checkBox.IsChecked.ShouldBe(true);
                    editor.Template.FindName("PART_ContentHost", editor).ShouldNotBeNull();
                    combo.Template.FindName("PART_Popup", combo).ShouldBeOfType<Popup>();
                    SavePreview(gallery, dark ? "controls-dark" : "controls-light");
                }
            }
            finally
            {
                AppColorPalette.Apply(gallery.Resources, false);
            }
        });
    }

    [Fact]
    public void Palettes_HaveMatchingFrozenResources_AndReadableText()
    {
        RunSta(() =>
        {
            var light = AppColorPalette.CreateResources(false);
            var dark = AppColorPalette.CreateResources(true);
            light
                .Keys.Cast<object>()
                .OrderBy(key => key.ToString())
                .ShouldBe(dark.Keys.Cast<object>().OrderBy(key => key.ToString()));
            foreach (var resources in new[] { light, dark })
            {
                foreach (var brush in resources.Values.OfType<Brush>())
                    brush.IsFrozen.ShouldBeTrue();
                foreach (var foreground in new[] { "PrimaryTextBrush", "SecondaryTextBrush", "MutedBrush" })
                foreach (var background in new[] { "WindowBackgroundBrush", "SurfaceBrush" })
                    Contrast((SolidColorBrush)resources[foreground], (SolidColorBrush)resources[background])
                        .ShouldBeGreaterThanOrEqualTo(4.5);
                Contrast(
                        (SolidColorBrush)resources["SearchHighlightTextBrush"],
                        (SolidColorBrush)resources["SearchHighlightBrush"]
                    )
                    .ShouldBeGreaterThanOrEqualTo(4.5);
            }
        });
    }

    [Fact]
    public void ChartTheme_PreservesDataZoomAndSelectionAlpha()
    {
        RunSta(() =>
        {
            var resources = new ResourceDictionary();
            try
            {
                AppColorPalette.Apply(resources, true);
                var model = new PlotModel();
                var axis = new LinearAxis { Position = AxisPosition.Bottom };
                model.Axes.Add(axis);
                axis.Zoom(12, 28);
                var line = new LineSeries { Color = OxyColor.FromArgb(60, 46, 111, 191) };
                line.Points.Add(new DataPoint(15, 10));
                model.Series.Add(line);
                DashboardPlotTheme.Apply(model);
                ((IPlotModel)model).Update(true);
                axis.ActualMinimum.ShouldBe(12);
                axis.ActualMaximum.ShouldBe(28);
                line.Points.ShouldHaveSingleItem().ShouldBe(new DataPoint(15, 10));
                line.Color.A.ShouldBe((byte)60);
                line.Color.B.ShouldBeGreaterThan((byte)191);
                model.TextColor.ShouldNotBe(OxyColors.Black);
            }
            finally
            {
                AppColorPalette.Apply(resources, false);
            }
        });
    }

    private static Border CreateGallery()
    {
        var gallery = (Border)
            XamlReader.Parse(
                """
                <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Background="{DynamicResource WindowBackgroundBrush}"
                        TextElement.Foreground="{DynamicResource PrimaryTextBrush}" Padding="20">
                  <StackPanel>
                    <TextBlock Text="KeyPulse Signal · theme controls" FontSize="22" Margin="0,0,0,12" />
                    <TabControl Height="65" Margin="0,0,0,12">
                      <TabItem Header="Dashboard"><TextBlock Text="Selected tab" Margin="8" /></TabItem>
                      <TabItem Header="Calendar" /><TabItem Header="Devices" /><TabItem Header="Settings" />
                    </TabControl>
                    <WrapPanel Margin="0,0,0,12">
                      <CheckBox x:Name="Checked" IsChecked="True" Content="Dark mode" Margin="0,0,20,0" />
                      <CheckBox Content="Launch on login" Margin="0,0,20,0" />
                      <CheckBox Content="Disabled" IsEnabled="False" Margin="0,0,20,0" />
                      <RadioButton Content="SQLite" IsChecked="True" Margin="0,0,20,0" />
                      <RadioButton Content="PostgreSQL" />
                    </WrapPanel>
                    <WrapPanel Margin="0,0,0,12">
                      <Button Content="Test connection" Margin="0,0,8,0" />
                      <Button Content="Disabled" IsEnabled="False" Margin="0,0,8,0" />
                      <ToggleButton Content="Filter" IsChecked="True" Margin="0,0,8,0" />
                      <ComboBox x:Name="Choices" Width="130" Margin="0,0,8,0">
                        <ComboBoxItem Content="1 Day" /><ComboBoxItem Content="1 Week" />
                      </ComboBox>
                      <ComboBox Width="150" IsEditable="True" Text="Editable dropdown" />
                    </WrapPanel>
                    <TextBox x:Name="Editor" Margin="0,0,0,8" />
                    <PasswordBox Password="sample-password" Margin="0,0,0,8" />
                    <TextBox Text="Disabled database host" IsEnabled="False" Margin="0,0,0,12" />
                    <DataGrid x:Name="Devices" Height="120" AutoGenerateColumns="True" IsReadOnly="True" Margin="0,0,0,12" />
                    <RichTextBox x:Name="Log" Height="90" Margin="0,0,0,12" />
                    <WrapPanel>
                      <MenuItem Header="Rename Device" Margin="0,0,8,0" />
                      <MenuItem Header="Keyboard" IsCheckable="True" IsChecked="True" Margin="0,0,8,0" />
                      <MenuItem Header="Change Device Type"><MenuItem Header="Mouse" /></MenuItem>
                      <Button Content="Hover hint" ToolTip="Tooltips follow the theme" />
                    </WrapPanel>
                    <ScrollViewer Height="65" VerticalScrollBarVisibility="Visible" HorizontalScrollBarVisibility="Visible">
                      <TextBlock Text="Scrollable content" Width="1200" Height="200" />
                    </ScrollViewer>
                  </StackPanel>
                </Border>
                """
            );
        gallery.Resources.MergedDictionaries.Add(new ThemeResources());
        gallery.Resources.MergedDictionaries.Add(
            new ResourceDictionary
            {
                Source = new Uri("/KeyPulse Signal;component/Views/Styles/ThemeControls.xaml", UriKind.Relative),
            }
        );
        return gallery;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(980, 760));
        element.Arrange(new Rect(0, 0, 980, 760));
        element.UpdateLayout();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.ContextIdle
        );
        element.UpdateLayout();
    }

    private static void SavePreview(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("KEYPULSE_THEME_PREVIEW_DIR");
        if (string.IsNullOrEmpty(directory))
            return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(980, 760, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static double Contrast(SolidColorBrush foreground, SolidColorBrush background)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var c = channel / 255.0;
                return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(foreground.Color);
        var b = Luminance(background.Color);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
