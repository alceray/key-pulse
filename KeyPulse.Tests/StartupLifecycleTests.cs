using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace KeyPulse.Tests;

public class StartupLifecycleTests
{
    [Fact]
    public async Task ClosingPreflightWindows_AllowsAsyncStartupToResume_AndExitBeforeServicesIsSafe()
    {
        const string childFlag = "KEYPULSE_STARTUP_LIFECYCLE_CHILD";
        if (Environment.GetEnvironmentVariable(childFlag) == "1")
        {
            ExerciseLifecycle();
            return;
        }

        // WPF Application shutdown clears process-wide resource state. Keep that lifecycle in its
        // own test process so other WPF tests can still load their resources afterwards.
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment[childFlag] = "1";
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(StartupLifecycleTests).Assembly.Location);
        start.ArgumentList.Add(
            $"/Tests:{typeof(StartupLifecycleTests).FullName}.{nameof(ClosingPreflightWindows_AllowsAsyncStartupToResume_AndExitBeforeServicesIsSafe)}"
        );
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        process.ExitCode.ShouldBe(0, (await output) + (await error));
    }

    private static void ExerciseLifecycle()
    {
        Exception? failure = null;
        var resumed = false;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new StartupTestApp();
                app.ExerciseStartup = async () =>
                {
                    // Canceling setup can exit before the service provider has been constructed.
                    var dispose = typeof(App).GetMethod(
                        "DisposeServicesForExitPath",
                        BindingFlags.Instance | BindingFlags.NonPublic
                    )!;
                    Should.NotThrow(() => dispose.Invoke(app, null));

                    // Setup and repeated recovery attempts close the only window, then await the
                    // next database operation. Hidden windows exercise WPF's real close handling.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var preflight = new Window { ShowInTaskbar = false, ShowActivated = false };
                        new WindowInteropHelper(preflight).EnsureHandle();
                        preflight.Close();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        app.Dispatcher.HasShutdownStarted.ShouldBeFalse();
                    }

                    resumed = true;
                    var main = new Window { ShowInTaskbar = false, ShowActivated = false };
                    app.MainWindow = main;
                    app.ShutdownMode = App.ResolveShutdownMode(runInBackground: false);
                    new WindowInteropHelper(main).EnsureHandle();
                    main.Close();
                };
                app.Run();
                failure = app.Failure;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (!Dispatcher.CurrentDispatcher.HasShutdownFinished)
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(20)).ShouldBeTrue("Startup and main-window shutdown must complete");
        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        resumed.ShouldBeTrue("Closing preflight windows must not shut down the application");
    }

    private sealed class StartupTestApp : App
    {
        internal Func<Task> ExerciseStartup { get; set; } = null!;
        internal Exception? Failure { get; private set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Never run production startup, capture, credentials, or database access in this test.
            try
            {
                await ExerciseStartup();
            }
            catch (Exception ex)
            {
                Failure = ex;
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e) { }
    }
}
