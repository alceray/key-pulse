using System.Diagnostics;
using KeyPulse.Services;

namespace KeyPulse.Tests.Services;

public class AppRestartServiceTests
{
    [Fact]
    public void Relaunch_PreservesArgumentsWithoutShellParsing_AndReplacesOldParent()
    {
        var executable = @"C:\Program Files\KeyPulse Signal\KeyPulse Signal.exe";
        var arguments = new[] { executable, "--tray", AppRestartService.ParentArgument, "123", "a b\"&c" };

        var start = AppRestartService.CreateStartInfo(executable, arguments, 456);

        start.FileName.ShouldBe(executable);
        start.UseShellExecute.ShouldBeFalse();
        start.CreateNoWindow.ShouldBeTrue();
        start.ArgumentList.ShouldBe(new[] { "--tray", "a b\"&c", AppRestartService.ParentArgument, "456" });
        start.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void Relaunch_DotnetHostIncludesApplicationDll()
    {
        var host = @"C:\Program Files\dotnet\dotnet.exe";
        var assembly = @"D:\A project\KeyPulse Signal.dll";

        var start = AppRestartService.CreateStartInfo(host, new[] { assembly, "--tray" }, 456);

        start.FileName.ShouldBe(host);
        start.ArgumentList.ShouldBe(new[] { assembly, "--tray", AppRestartService.ParentArgument, "456" });
    }

    [Fact]
    public async Task OrdinaryLaunch_DoesNotWaitForAnotherProcess()
    {
        (await AppRestartService.WaitForPreviousInstanceAsync(new[] { "--tray" })).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not-a-process-id")]
    public async Task InvalidRestartRequest_IsRejected(string parentId)
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => AppRestartService.WaitForPreviousInstanceAsync(new[] { AppRestartService.ParentArgument, parentId })
        );
    }

    [Fact]
    public async Task RestartCannotWaitForItself()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () =>
                AppRestartService.WaitForPreviousInstanceAsync(
                    new[] { AppRestartService.ParentArgument, Environment.ProcessId.ToString() }
                )
        );
    }

    [Fact]
    public async Task RestartWaitsForFullProcessExit_AndCancellationDoesNotTerminateTheParent()
    {
        // Stand in for an app still flushing its history, without opening any user database or app.
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "[Console]::ReadLine() | Out-Null" }
        )
            start.ArgumentList.Add(argument);
        using var previous = Process.Start(start)!;
        try
        {
            var arguments = new[] { AppRestartService.ParentArgument, previous.Id.ToString() };
            using (var canceledWait = new CancellationTokenSource())
            {
                var waiting = AppRestartService.WaitForPreviousInstanceAsync(arguments, canceledWait.Token);
                waiting.IsCompleted.ShouldBeFalse();
                canceledWait.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(() => waiting);
                previous.HasExited.ShouldBeFalse();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var restart = AppRestartService.WaitForPreviousInstanceAsync(arguments, timeout.Token);
            restart.IsCompleted.ShouldBeFalse();
            await previous.StandardInput.WriteLineAsync("finish shutdown");
            (await restart).ShouldBeTrue();
            previous.HasExited.ShouldBeTrue();
            // Also cover the old process already being gone before the replacement begins waiting.
            (await AppRestartService.WaitForPreviousInstanceAsync(arguments, timeout.Token)).ShouldBeTrue();
        }
        finally
        {
            if (!previous.HasExited)
                previous.Kill(entireProcessTree: true);
        }
    }
}
