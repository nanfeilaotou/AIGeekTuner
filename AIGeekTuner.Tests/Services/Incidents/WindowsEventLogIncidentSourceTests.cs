using System.Diagnostics.Eventing.Reader;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Incidents;
using Xunit;

namespace AIGeekTuner.Tests.Services.Incidents
{
    /// <summary>Gate G（Query）：失败隔离、排序、去重、上限、取消。不读真实 Event Log。</summary>
    public sealed class WindowsEventLogIncidentSourceTests
    {
        private static readonly DateTimeOffset Base =
            new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

        private static RawWindowsEvent Raw(
            string provider, int eventId, DateTimeOffset time,
            long? recordId = null, string channel = "System", int? level = 2) =>
            new(time, provider, eventId, level, channel, recordId,
                $"[{provider}] test message {eventId}");

        private static IncidentQuery Query(int maxResults = 100) =>
            new(Base.AddHours(-1), Base.AddHours(1), maxResults);

        private sealed class FakeReader : IWindowsEventRecordReader
        {
            public Dictionary<string, IReadOnlyList<RawWindowsEvent>> Results { get; } = [];
            public Dictionary<string, Exception> Errors { get; } = [];
            public List<string> Calls { get; } = [];
            public IReadOnlyList<WindowsEventQueryTarget>? LastTargets { get; private set; }
            public HashSet<string> TruncatedChannels { get; } = new(StringComparer.Ordinal);

            public Task<IReadOnlyList<RawWindowsEvent>> ReadAsync(
                string channel, DateTimeOffset startUtc, DateTimeOffset endUtc,
                int maxResults, CancellationToken cancellationToken)
            {
                Calls.Add(channel);
                cancellationToken.ThrowIfCancellationRequested();
                if (Errors.TryGetValue(channel, out var error))
                {
                    throw error;
                }

                return Task.FromResult(
                    Results.TryGetValue(channel, out var list)
                        ? list
                        : (IReadOnlyList<RawWindowsEvent>)[]);
            }

            public async Task<WindowsEventReadResult> ReadDetailedAsync(
                string channel, DateTimeOffset startUtc, DateTimeOffset endUtc,
                int maxResults, IReadOnlyList<WindowsEventQueryTarget> targets,
                CancellationToken cancellationToken)
            {
                LastTargets = targets;
                var events = await ReadAsync(
                    channel, startUtc, endUtc, maxResults, cancellationToken);
                return new WindowsEventReadResult(
                    events, TruncatedChannels.Contains(channel));
            }
        }

        private static WindowsEventLogIncidentSource Source(FakeReader reader) => new(reader);

        [Fact]
        public async Task SystemOnly_Results_ReturnSuccess()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("Kernel-Power", 41, Base.AddMinutes(-5), recordId: 10),
            ];
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(IncidentQueryStatus.Success, result.Status);
            Assert.Single(result.Incidents);
            Assert.Equal(2, result.Channels.Count);
        }

        [Fact]
        public async Task MergedResults_SortedAscending()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("EventLog", 6008, Base.AddMinutes(-10), recordId: 1),
            ];
            reader.Results["Application"] =
            [
                Raw("Application Error", 1000, Base.AddMinutes(-1), recordId: 2, channel: "Application"),
            ];
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(IncidentQueryStatus.Success, result.Status);
            Assert.Equal(2, result.Incidents.Count);
            Assert.True(result.Incidents[0].OccurredAtUtc <= result.Incidents[1].OccurredAtUtc);
        }

        [Fact]
        public async Task ApplicationOnlyFailing_SystemSucceeds_IsPartial()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("Kernel-Power", 41, Base.AddMinutes(-5), recordId: 10),
            ];
            reader.Errors["Application"] = new InvalidOperationException("boom");
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(IncidentQueryStatus.Partial, result.Status);
            Assert.Single(result.Incidents);
            Assert.Equal(IncidentQueryStatus.Error, result.Channels[1].Status);
        }

        [Fact]
        public async Task PermissionDenied_IsReportedPerChannel()
        {
            var reader = new FakeReader();
            reader.Errors["System"] = new UnauthorizedAccessException("denied");
            reader.Errors["Application"] = new UnauthorizedAccessException("denied");
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(IncidentQueryStatus.PermissionDenied, result.Status);
            Assert.Empty(result.Incidents);
        }

        [Fact]
        public async Task UnavailableChannels_AggregateToUnavailable()
        {
            var reader = new FakeReader();
            reader.Errors["System"] = new EventLogNotFoundException();
            reader.Errors["Application"] = new EventLogNotFoundException();
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(IncidentQueryStatus.Unavailable, result.Status);
        }

        [Fact]
        public async Task DuplicateEvents_DedupByChannelAndRecordId()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("Kernel-Power", 41, Base.AddMinutes(-5), recordId: 7),
                Raw("Kernel-Power", 41, Base.AddMinutes(-5), recordId: 7),
            ];
            var result = await Source(reader).QueryAsync(Query());

            Assert.Single(result.Incidents);
        }

        [Fact]
        public async Task MaxResults_KeepsMostRecent()
        {
            var reader = new FakeReader();
            var events = Enumerable.Range(1, 10)
                .Select(i => Raw("EventLog", 6008, Base.AddMinutes(-i), recordId: i))
                .ToArray();
            reader.Results["System"] = events;
            var result = await Source(reader).QueryAsync(Query(maxResults: 3));

            Assert.Equal(3, result.Incidents.Count);
            // 保留最近 3 条，且输出仍为时间升序。
            Assert.Equal(Base.AddMinutes(-3), result.Incidents[0].OccurredAtUtc);
            Assert.Equal(Base.AddMinutes(-1), result.Incidents[^1].OccurredAtUtc);
        }

        [Fact]
        public async Task MergedCandidatesExceedLimit_AreMarkedTruncated()
        {
            var reader = new FakeReader();
            reader.Results["System"] = Enumerable.Range(1, 300)
                .Select(i => Raw("Kernel-Power", 41, Base.AddSeconds(-i), i, "System"))
                .ToArray();
            reader.Results["Application"] = Enumerable.Range(1, 300)
                .Select(i => Raw("Application Error", 1000, Base.AddSeconds(-i), i, "Application"))
                .ToArray();

            var result = await Source(reader).QueryAsync(Query(maxResults: 500));

            Assert.Equal(500, result.Incidents.Count);
            Assert.DoesNotContain(result.Channels, channel => channel.MayBeTruncated);
            Assert.True(result.MayBeTruncated);
        }

        [Fact]
        public async Task MergedCandidatesWithinLimit_AreNotMarkedTruncated()
        {
            var reader = new FakeReader();
            reader.Results["System"] = Enumerable.Range(1, 200)
                .Select(i => Raw("Kernel-Power", 41, Base.AddSeconds(-i), i, "System"))
                .ToArray();
            reader.Results["Application"] = Enumerable.Range(1, 300)
                .Select(i => Raw("Application Error", 1000, Base.AddSeconds(-i), i, "Application"))
                .ToArray();

            var result = await Source(reader).QueryAsync(Query(maxResults: 500));

            Assert.Equal(500, result.Incidents.Count);
            Assert.False(result.MayBeTruncated);
        }

        [Fact]
        public async Task SingleChannelTruncated_IsPreserved()
        {
            var reader = new FakeReader();
            reader.Results["System"] = [Raw("Kernel-Power", 41, Base)];
            reader.TruncatedChannels.Add("System");

            var result = await Source(reader).QueryAsync(Query());

            Assert.True(result.MayBeTruncated);
        }

        [Fact]
        public async Task ChannelAndMergedTruncation_AreCombined()
        {
            var reader = new FakeReader();
            reader.Results["System"] = Enumerable.Range(1, 300)
                .Select(i => Raw("Kernel-Power", 41, Base.AddSeconds(-i), i, "System"))
                .ToArray();
            reader.Results["Application"] = Enumerable.Range(1, 300)
                .Select(i => Raw("Application Error", 1000, Base.AddSeconds(-i), i, "Application"))
                .ToArray();
            reader.TruncatedChannels.Add("System");

            var result = await Source(reader).QueryAsync(Query(maxResults: 500));

            Assert.Equal(500, result.Incidents.Count);
            Assert.True(result.MayBeTruncated);
        }

        [Fact]
        public async Task EvidenceIds_AreStableAndOrdered()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("EventLog", 6008, Base.AddMinutes(-10), recordId: 1),
                Raw("Kernel-Power", 41, Base.AddMinutes(-2), recordId: 2),
            ];
            var result = await Source(reader).QueryAsync(Query());

            Assert.Equal(["incident:0001", "incident:0002"],
                result.Incidents.Select(i => i.EvidenceId).ToArray());
            Assert.Equal(IncidentCategory.UnexpectedShutdown, result.Incidents[0].Category);
        }

        [Fact]
        public async Task UnmappedEvents_AreFilteredOut()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("Service Control Manager", 7040, Base.AddMinutes(-3), recordId: 1),
                Raw("Kernel-Power", 41, Base.AddMinutes(-5), recordId: 2),
            ];
            var result = await Source(reader).QueryAsync(Query());

            Assert.Single(result.Incidents);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var reader = new FakeReader();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Source(reader).QueryAsync(Query(), cts.Token));
        }

        [Fact]
        public async Task InvalidQuery_IsRejectedBeforeAnyReaderCall()
        {
            var reader = new FakeReader();
            var invalid = new IncidentQuery(Base, Base.AddHours(-1), 100);
            await Assert.ThrowsAsync<ArgumentException>(
                () => Source(reader).QueryAsync(invalid));
            Assert.Empty(reader.Calls);
        }

        [Fact]
        public async Task BothChannelsAreAlwaysQueried()
        {
            var reader = new FakeReader();
            await Source(reader).QueryAsync(Query());
            Assert.Equal(["System", "Application"], reader.Calls);
        }

        [Fact]
        public async Task QueryPushesKnownTargets_AndExposesTruncation()
        {
            var reader = new FakeReader();
            reader.Results["System"] =
            [
                Raw("Service Control Manager", 7040, Base.AddMinutes(-3), recordId: 1),
                Raw("Microsoft-Windows-WHEA-Logger", 18, Base.AddMinutes(-1), recordId: 2),
            ];
            reader.TruncatedChannels.Add("System");

            var result = await Source(reader).QueryAsync(Query());

            Assert.NotNull(reader.LastTargets);
            Assert.Contains(reader.LastTargets!, target =>
                target.ProviderName == "Microsoft-Windows-WHEA-Logger");
            Assert.True(result.MayBeTruncated);
            Assert.True(result.Channels.Single(channel => channel.Channel == "System").MayBeTruncated);
            Assert.Single(result.Incidents);
        }

        [Fact]
        public async Task NoTargetEvents_IsSuccessfulAndNotTruncated()
        {
            var reader = new FakeReader();
            reader.Results["System"] = [Raw("Service Control Manager", 7040, Base)];

            var result = await Source(reader).QueryAsync(Query());

            Assert.Empty(result.Incidents);
            Assert.False(result.MayBeTruncated);
            Assert.Equal(IncidentQueryStatus.Success, result.Status);
        }
    }
}
