using Dapper;
using Microsoft.Data.Sqlite;
using Pat.Core.Categorization;
using Pat.Core.Events;
using Pat.Core.Sessionization;
using Pat.Infrastructure.Collectors;
using Pat.Infrastructure.Database;
using Pat.Infrastructure.Ollama;
using Pat.Infrastructure.Settings;
using System.Threading.Channels;

namespace Pat.Core.Tests;

public sealed class AuditRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pat-regression-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly EventRepository _events;
    private readonly SessionRepository _sessions;
    private readonly SettingsStore _settings;
    private readonly SessionMaterializer _materializer;
    private static readonly long T = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static ActivityEvent F(long ts, string title = "code") => ActivityEvent.Foreground(ts, @"C:\audit\code.exe", "code", title);
    private static ActivityEvent S(ActivityEventKind kind, long ts) => ActivityEvent.System(kind, ts);

    public AuditRegressionTests()
    {
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "activity.db"));
        new MigrationRunner(_factory).Run();
        _events = new(_factory);
        _sessions = new(_factory);
        _settings = new(_root);
        _materializer = new(_events, _sessions, _settings);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(ActivityEventKind.SessionLocked)]
    [InlineData(ActivityEventKind.SystemSuspend)]
    [InlineData(ActivityEventKind.TrackingPaused)]
    public void ForegroundAndInputCannotReopenBlockedSession(ActivityEventKind boundary)
    {
        var segments = new SessionizationEngine().Build([F(T), S(boundary, T + 60_000),
            F(T + 120_000, "new title"), S(ActivityEventKind.IdleEnded, T + 180_000)], T + 600_000);
        Assert.Equal(60, Assert.Single(segments).DurationSeconds);
    }

    [Fact]
    public void TitleChangeDoesNotEndIdleAndReturnUsesUpdatedContext()
    {
        var segments = new SessionizationEngine().Build([F(T), S(ActivityEventKind.IdleBegan, T + 60_000),
            F(T + 120_000, "updated"), S(ActivityEventKind.IdleEnded, T + 180_000)], T + 240_000);
        Assert.Equal(3, segments.Count);
        Assert.True(segments[1].IsIdle);
        Assert.Equal(120, segments[1].DurationSeconds);
        Assert.Equal("updated", segments[2].WindowTitle);
    }

    [Fact]
    public void ResumeDoesNotRemoveIndependentLock()
    {
        var segments = new SessionizationEngine().Build([F(T), S(ActivityEventKind.SessionLocked, T + 60_000),
            S(ActivityEventKind.SystemSuspend, T + 120_000), S(ActivityEventKind.SystemResume, T + 180_000),
            F(T + 190_000), S(ActivityEventKind.SessionUnlocked, T + 240_000)], T + 300_000);
        Assert.Equal(120_000, segments.Sum(x => x.EndUtcMs - x.StartUtcMs));
    }

    [Fact]
    public void StoppedAndCrashedRunsDoNotBridgeOfflineTime()
    {
        var segments = new SessionizationEngine().Build([
            S(ActivityEventKind.TrackingStarted, T), F(T), S(ActivityEventKind.TrackingHeartbeat, T + 60_000),
            S(ActivityEventKind.TrackingStarted, T + 600_000), F(T + 600_000),
            S(ActivityEventKind.TrackingStopped, T + 660_000)], T + 900_000);
        Assert.Equal(120_000, segments.Sum(x => x.EndUtcMs - x.StartUtcMs));
    }

    [Fact]
    public void AbandonedLastRunExpiresAtHeartbeatLease()
    {
        var segments = new SessionizationEngine().Build([
            S(ActivityEventKind.TrackingStarted, T), F(T), S(ActivityEventKind.TrackingHeartbeat, T + 60_000)], T + 900_000);
        Assert.Equal(T + 75_000, Assert.Single(segments).EndUtcMs);
    }

    [Fact]
    public void FutureEventsDoNotEscapeEndBoundary()
    {
        var seg = Assert.Single(new SessionizationEngine().Build([F(T), F(T + 600_000)], T + 60_000));
        Assert.Equal(T + 60_000, seg.EndUtcMs);
    }

    [Fact]
    public void MidnightRecoversLongRunningForegroundAndRejectsYesterdayOnlyRows()
    {
        _events.AppendBatch([F(T - 3 * 3_600_000)]);
        _materializer.Refresh(T, T + 60_000);
        var row = Assert.Single(_sessions.GetRange(T, T + 60_000, _materializer.CurrentRules()));
        Assert.Equal((T, T + 60_000), (row.StartedUtcMs, row.EndedUtcMs));
        _events.AppendBatch([S(ActivityEventKind.TrackingPaused, T - 60_000)]);
        _materializer.Refresh(T, T + 60_000);
        _materializer.Refresh(T, T + 60_000);
        using var db = _factory.Open();
        Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM sessions"));
        Assert.Throws<SqliteException>(() => db.Execute("INSERT INTO sessions(started_utc,ended_utc) VALUES(20,10)"));
    }

    [Fact]
    public void StatsAndExportSourceAgreeWithoutSessionCacheAndDoNotCountFuture()
    {
        _events.AppendBatch([F(T)]);
        var now = T + 3_600_000;
        var stats = new StatsService(_materializer, new FixedClock(now));
        var day = Assert.Single(stats.GetDailyTotals(new DateTime(2026, 1, 10), new DateTime(2026, 1, 12), TimeZoneInfo.Utc));
        var rows = _materializer.BuildRows(T, now);
        Assert.Equal(TimeSpan.FromHours(1), day.Active);
        Assert.Equal(day.Active.TotalMilliseconds, rows.Sum(r => r.EndedUtcMs - r.StartedUtcMs));
        Assert.Empty(_sessions.GetRange(T, now, _materializer.CurrentRules()));
    }

    [Fact]
    public void FailedTransactionDoesNotRetainApplicationIds()
    {
        using var db = _factory.Open();
        db.Execute("CREATE TRIGGER audit_failure BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        Assert.Throws<SqliteException>(() => _events.AppendBatch([F(T)]));
        db.Execute("DROP TRIGGER audit_failure;");
        _events.AppendBatch([F(T)]);
        Assert.Single(_events.GetRange(T, T + 1));
    }

    [Fact]
    public async Task FailedWriterPersistsAndReplaysWithoutDuplicates()
    {
        using var db = _factory.Open();
        db.Execute("CREATE TRIGGER audit_failure BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT,'test failure'); END;");
        var spool = Path.Combine(_root, "pending");
        await using (var writer = new BufferedEventWriter(_events, spool))
        {
            Assert.True(writer.Writer.TryWrite(F(T)));
            await Assert.ThrowsAsync<SqliteException>(() => writer.FlushAsync());
            Assert.NotEmpty(Directory.GetFiles(spool, "*.json"));
        }
        db.Execute("DROP TRIGGER audit_failure;");
        var file = Assert.Single(Directory.GetFiles(spool, "*.json"));
        // Simulate commit succeeding followed by a crash before deleting the spool.
        _events.AppendBatch([F(T)], Path.GetFileNameWithoutExtension(file));
        await using (var recovered = new BufferedEventWriter(_events, spool))
            await recovered.FlushAsync();
        Assert.Single(_events.GetRange(T, T + 1));
        Assert.Empty(Directory.GetFiles(spool, "*.json"));
    }

    [Theory]
    [InlineData(3, 8, 23)]
    [InlineData(11, 1, 25)]
    public void DstDayUsesTwoLocalMidnights(int month, int day, int hours)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var date = new DateTime(2026, month, day);
        var (start, end) = LocalDay.Bounds(date, tz);
        Assert.Equal(hours, (end - start) / 3_600_000);
        Assert.Equal(end, LocalDay.Bounds(date.AddDays(1), tz).StartUtcMs);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1.evil.test")]
    [InlineData("file:///C:/data")]
    [InlineData("http://127.0.0.1/path")]
    public void RemoteOrAmbiguousOllamaAddressIsRejected(string host) => Assert.Throws<ArgumentException>(() => new OllamaClient(host));

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://[::1]:11434")]
    public void LoopbackOllamaAddressIsAccepted(string host) => Assert.NotNull(new OllamaClient(host));

    [Fact]
    public void SettingsRecoverBackupAndDoNotPublishInvalidOrMutableState()
    {
        var first = new PatSettings { RetentionDays = 30 };
        _settings.Save(first);
        _settings.Save(new PatSettings { RetentionDays = 60 });
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{broken");
        Assert.Equal(30, _settings.Load().RetentionDays);
        Assert.NotNull(_settings.LastError);
        var copy = _settings.Settings;
        copy.ExcludedProcesses.Clear();
        Assert.NotEmpty(_settings.Settings.ExcludedProcesses);
        Assert.Throws<ArgumentException>(() => _settings.Save(new PatSettings { IdleThresholdSeconds = -1 }));
        Assert.Equal(30, _settings.Settings.RetentionDays);
        _settings.Save(_settings.Settings);
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{broken again");
        Assert.Equal(30, _settings.Load().RetentionDays);
        Assert.True(_settings.HasUsableSettings);
    }

    [Fact]
    public void NullCollectionsAreNormalizedAndInvalidRegexIsRejected()
    {
        _settings.Save(new PatSettings { ExcludedProcesses = null!, WatchedRoots = null! });
        Assert.NotEmpty(_settings.Settings.ExcludedProcesses);
        Assert.Empty(_settings.Settings.WatchedRoots);
        Assert.ThrowsAny<ArgumentException>(() => _settings.Save(new PatSettings
        { CategoryRules = [new("Bad", "#123456", RuleField.WindowTitle, "(")] }));
    }

    [Fact]
    public void PrivacyMatchesExeSuffixAndRedactsParentPayload()
    {
        Assert.True(PrivacyPolicy.IsExcluded(_settings.Settings, "KeePass.EXE"));
        var collector = new ProcessSnapshotCollector(_settings);
        var method = typeof(ProcessSnapshotCollector).GetMethod("Journal", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var ev = (ActivityEvent)method.Invoke(collector, [ActivityEventKind.ProcessStarted, T, 1u, 2u, "child.exe", "keepass.exe"])!;
        Assert.DoesNotContain("keepass", ev.Payload!, StringComparison.OrdinalIgnoreCase);
        var custom = new PatSettings { ExcludedProcesses = ["private.app"] };
        Assert.True(PrivacyPolicy.IsExcluded(custom, "private.app.exe"));
    }

    [Fact]
    public void CorruptSettingsWithoutBackupStopAutomaticTracking()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{broken");
        _settings.Load();
        Assert.False(_settings.HasUsableSettings);
        _settings.Save(new PatSettings());
        Assert.True(_settings.HasUsableSettings);
    }

    [Fact]
    public async Task WriterRecoversBatchFlushedBeforeAtomicRename()
    {
        var spool = Path.Combine(_root, "rename-pending");
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "valid.json.tmp"), System.Text.Json.JsonSerializer.Serialize(new[] { F(T) }));
        File.WriteAllText(Path.Combine(spool, "partial.json.tmp"), "{partial");
        await using var writer = new BufferedEventWriter(_events, spool);
        await writer.FlushAsync();
        Assert.Single(_events.GetRange(T, T + 1));
        Assert.Single(Directory.GetFiles(spool, "*.corrupt"));
        Assert.Empty(Directory.GetFiles(spool, "*.json"));
    }

    [Fact]
    public async Task LocalOllamaDoesNotFollowRedirect()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);
                var contentLength = 0;
                while (await reader.ReadLineAsync() is { Length: > 0 } line)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line[15..].Trim());
                var body = new char[contentLength];
                if (await reader.ReadBlockAsync(body, 0, body.Length) != body.Length) throw new EndOfStreamException();
                var reply = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(reply);
            });
            var client = new OllamaClient($"http://127.0.0.1:{port}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => client.ChatAsync("test", "test", "test", timeout.Token));
            await server;
            Assert.False(listener.Pending());
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void ForegroundHookStopsAfterRepeatedRestart()
    {
        IActivityCollector collector = new ForegroundCollector(_settings);
        var field = typeof(ForegroundCollector).GetField("_hookThread", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        for (var i = 0; i < 100; i++)
        {
            collector.Start(Channel.CreateUnbounded<ActivityEvent>().Writer);
            var thread = (Thread)field.GetValue(collector)!;
            collector.Stop();
            Assert.False(thread.IsAlive);
        }
    }

    [Fact]
    public void FileCollectorStopFlushesPendingEventsAndDoesNotReplayThemOnResume()
    {
        var channel = Channel.CreateUnbounded<ActivityEvent>();
        var concrete = new FileActivityCollector(_settings);
        IActivityCollector collector = concrete;
        var field = typeof(FileActivityCollector).GetField("_buffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        collector.Start(channel.Writer);
        ((FileEventBuffer)field.GetValue(concrete)!).Add(new(ActivityEventKind.FileModified, @"C:\work\file.cs", null, T));
        collector.Stop();
        Assert.True(channel.Reader.TryRead(out var ev));
        Assert.Equal(ActivityEventKind.FileModified, ev.Kind);
        collector.Start(channel.Writer);
        collector.Stop();
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task TrackingPauseAndExitFlushLifecycleBoundaries()
    {
        var tracking = new Pat.App.Tracking.TrackingService(_events, _materializer,
            new(_settings), new(_settings), new(), new(_settings), new(), new(_settings), _settings);
        try
        {
            tracking.Start();
            await tracking.TogglePauseAsync();
            Assert.Equal(Pat.App.Tracking.TrackingState.Paused, tracking.State);
            Assert.True(File.Exists(Path.Combine(_root, "tracking.paused")));
            var journal = _events.GetRange(0, long.MaxValue);
            var pause = Assert.Single(journal, e => e.Kind == ActivityEventKind.TrackingPaused);
            Assert.Contains(journal, e => e.Kind == ActivityEventKind.TrackingStarted);
            using (var db = _factory.Open())
                Assert.Equal(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM sessions WHERE ended_utc > @pause", new { pause = pause.TimestampUtcMs }));
            await tracking.TogglePauseAsync();
            Assert.Equal(Pat.App.Tracking.TrackingState.Running, tracking.State);
            Assert.False(File.Exists(Path.Combine(_root, "tracking.paused")));
        }
        finally { await tracking.StopAsync(); }
        Assert.Contains(_events.GetRange(0, long.MaxValue), e => e.Kind == ActivityEventKind.TrackingStopped);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "pending-events"), "*.json"));
    }

    [Fact]
    public void WpfChartRendersWithCompatibleSkiaAssets()
    {
        Exception? failure = null;
        var path = Path.Combine(_root, "chart.png");
        var thread = new Thread(() =>
        {
            try
            {
                var control = new ScottPlot.WPF.WpfPlot();
                control.Plot.Add.Scatter(new double[] { 0, 1 }, new double[] { 1, 2 });
                control.Plot.SavePng(path, 400, 300);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        Assert.True(new FileInfo(path).Length > 100);
    }

    private sealed class FixedClock(long utcMs) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(utcMs);
    }
}
