using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace KeyPulse.Tests.Services;

[CollectionDefinition("Database switch diagnostics", DisableParallelization = true)]
public class DatabaseSwitchDiagnosticsCollection;

[Collection("Database switch diagnostics")]
public class DatabaseSwitchFeedbackTests
{
    [PostgreSqlFact]
    public async Task FreshCopy_LogsRoutesAndDuration_AndReportsTransferStages()
    {
        await using var server = new PostgreSqlTestServer();
        await server.InitializeAsync();
        using var scope = new DatabaseSwitchTestScope();
        var destination = await server.CreateDatabaseAsync();
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, destination);
        scope.Credentials.Password = "";
        using (var source = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(source);
        using var logs = new CompletionCapture();
        var stages = new List<DatabaseTransferStage>();
        scope.Switch.TransferStageChanged += stages.Add;

        await Task.Run(() => scope.Switch.ProcessPendingSwitchAsync());

        var completion = logs.Completions.ShouldHaveSingleItem();
        completion.Properties["Source"].ShouldBe(new ScalarValue($"SQLite ({scope.SqlitePath})"));
        completion.Properties["Destination"].ShouldBe(new ScalarValue($"PostgreSQL ({destination.Describe()})"));
        completion.Properties["CompletionDetail"].ShouldBe(new ScalarValue(""));
        completion.RenderMessage().ShouldEndWith("ms.");
        completion
            .RenderMessage()
            .ShouldStartWith(
                $"Database change completed from SQLite ({scope.SqlitePath}) to PostgreSQL ({destination.Describe()})"
            );
        ((long)((ScalarValue)completion.Properties["ElapsedMs"]).Value!).ShouldBeGreaterThanOrEqualTo(0);
        AssertNoRowCounts(completion);
        stages
            .IndexOf(DatabaseTransferStage.Copying)
            .ShouldBeGreaterThan(stages.IndexOf(DatabaseTransferStage.Preparing));
        stages
            .IndexOf(DatabaseTransferStage.Verifying)
            .ShouldBeGreaterThan(stages.IndexOf(DatabaseTransferStage.Copying));
        stages
            .IndexOf(DatabaseTransferStage.Activating)
            .ShouldBeGreaterThan(stages.IndexOf(DatabaseTransferStage.Verifying));
        stages.Last().ShouldBe(DatabaseTransferStage.Idle);
        (await scope.Switch.ProcessPendingSwitchAsync()).ShouldBeFalse();
        logs.Completions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Resume_ExplainsActivation_WithoutCopyingOrSourceAccess()
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        using (var target = scope.CreateDatabase())
        {
            DatabaseSwitchTestScope.Seed(target);
            AppMetaStore.Write(target, DatabaseSwitchService.ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
        }
        using var logs = new CompletionCapture();
        var stages = new List<DatabaseTransferStage>();
        scope.Switch.TransferStageChanged += stages.Add;

        await scope.Switch.ProcessPendingSwitchAsync();

        var completion = logs.Completions.ShouldHaveSingleItem();
        completion.Properties["Source"].ShouldBe(new ScalarValue($"PostgreSQL ({settings.PostgreSql.Describe()})"));
        completion.Properties["Destination"].ShouldBe(new ScalarValue($"SQLite ({scope.SqlitePath})"));
        completion
            .Properties["CompletionDetail"]
            .ShouldBe(new ScalarValue(" Resumed database activation using previously copied data."));
        completion.RenderMessage().ShouldEndWith("ms. Resumed database activation using previously copied data.");
        AssertNoRowCounts(completion);
        stages.ShouldNotContain(DatabaseTransferStage.Copying);
        stages.ShouldContain(DatabaseTransferStage.Activating);
        stages.Last().ShouldBe(DatabaseTransferStage.Idle);
    }

    [Fact]
    public async Task CancelledResume_DoesNotActivateOrLogCompletion()
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        using (var target = scope.CreateDatabase())
            AppMetaStore.Write(target, DatabaseSwitchService.ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
        using var logs = new CompletionCapture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => scope.Switch.TryCompletePendingSwitchAsync(cancellation.Token)
        );

        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
        scope.Settings.GetSettings().PendingDatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        logs.Completions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failure_EndsProgressWithoutLoggingCompletion()
    {
        using var scope = new DatabaseSwitchTestScope();
        scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        using var logs = new CompletionCapture();
        var stages = new List<DatabaseTransferStage>();
        scope.Switch.TransferStageChanged += stages.Add;

        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());

        stages.Last().ShouldBe(DatabaseTransferStage.Idle);
        logs.Completions.ShouldBeEmpty();
        scope.Settings.GetSettings().PendingDatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
    }

    private static void AssertNoRowCounts(LogEvent completion)
    {
        completion.Properties.ContainsKey("RowSummary").ShouldBeFalse();
        completion.Properties.ContainsKey("RowCounts").ShouldBeFalse();
        completion.Properties.ContainsKey("Resumed").ShouldBeFalse();
        completion.RenderMessage().ShouldNotContain("Rows:");
    }

    private sealed class CompletionCapture : IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly Logger _logger;
        internal List<LogEvent> Completions { get; } = new();

        internal CompletionCapture()
        {
            _logger = new LoggerConfiguration().WriteTo.Sink(new CompletionSink(this)).CreateLogger();
            Log.Logger = _logger;
        }

        private sealed class CompletionSink(CompletionCapture owner) : ILogEventSink
        {
            public void Emit(LogEvent logEvent)
            {
                if (
                    logEvent.MessageTemplate.Text.StartsWith("Database change completed from", StringComparison.Ordinal)
                )
                    owner.Completions.Add(logEvent);
            }
        }

        public void Dispose()
        {
            Log.Logger = _previous;
            _logger.Dispose();
        }
    }
}
