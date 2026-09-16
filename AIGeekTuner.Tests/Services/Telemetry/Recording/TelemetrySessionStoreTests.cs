using System.IO;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Models.Telemetry;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Tests.Services.Telemetry.Recording
{
    /// <summary>§41 存储测试：临时目录、原子写、损坏容忍。</summary>
    public class TelemetrySessionStoreTests : IDisposable
    {
        private readonly string _dir;

        public TelemetrySessionStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "agt-store-" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private static TelemetryRecordingSession Sample(string id) =>
            new(id,
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                null,
                2000,
                RecordingStatus.Completed,
                [], [], null, []);

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            var store = new TelemetrySessionStore(_dir);
            var session = Sample("abc") with
            {
                Samples = new List<TelemetrySample>
                {
                    new(1, DateTimeOffset.UtcNow, 12,
                        new List<TelemetryReading>
                        {
                            new(TelemetryMetricKey.CpuPackageTemperature, 71.5,
                                TelemetryUnit.Celsius,
                                TelemetryDeviceIdentity.Cpu("CPU"),
                                TelemetrySourceKind.HwInfo, "raw", null, DateTimeOffset.UtcNow),
                        }),
                }.ToArray(),
            };

            store.Save(session);
            var loaded = store.Load("abc");

            Assert.NotNull(loaded);
            Assert.Equal(session.Id, loaded.Id);
            Assert.Single(loaded.Samples);
            Assert.Equal(71.5, loaded.Samples[0].Readings[0].Value);
            Assert.Equal(TelemetrySourceKind.HwInfo, loaded.Samples[0].Readings[0].Source);
        }

        [Fact]
        public void Save_IsAtomicOverwrite_NoTempLeftBehind()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("id1"));
            store.Save(Sample("id1")); // 覆盖

            var file = store.PathOf("id1");
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".tmp"));
        }

        [Fact]
        public void Load_Missing_ReturnsNull()
        {
            var store = new TelemetrySessionStore(_dir);
            Assert.Null(store.Load("nope"));
        }

        [Fact]
        public void LoadAll_SkipsCorruptFile_AndReportsIt()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("good"));
            var badDir = Path.Combine(_dir, "bad");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "session.json"), "{ this is not json");

            var sessions = store.LoadAll(out var errors);

            var good = Assert.Single(sessions);
            Assert.Equal("good", good.Id);
            Assert.Contains("bad", errors);
        }

        [Fact]
        public void LoadAll_TolerantToUnknownFutureFields()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("future"));
            var file = store.PathOf("future");
            File.WriteAllText(file,
                File.ReadAllText(file)[..^1] + ",\"SomeFutureField\":123}");

            var sessions = store.LoadAll(out var errors);

            Assert.Empty(errors);
            Assert.Single(sessions);
        }

        [Fact]
        public void LoadAll_OrdersByStartedAtDescending()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("old") with { StartedAtUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) });
            store.Save(Sample("new") with { StartedAtUtc = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) });

            var sessions = store.LoadAll(out _);

            Assert.Equal("new", sessions[0].Id);
            Assert.Equal("old", sessions[1].Id);
        }

        [Fact]
    public void Delete_RemovesDirectory()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("del"));

            Assert.True(store.Delete("del"));
            Assert.False(store.Delete("del"));
            Assert.Null(store.Load("del"));
        }

        [Fact]
        public void MaliciousIds_NeverEscapeSessionRoot()
        {
            var store = new TelemetrySessionStore(_dir);
            var ids = new[]
            {
                "..",
                "../escape",
                "..\\escape",
                "C:\\outside",
                "C:relative",
                "\\root-relative",
                "\\\\server\\share",
                "\\\\?\\C:\\device",
                "name:stream",
                "name.",
                "name ",
                "CON",
                "PRN",
                "AUX",
                "NUL",
                "COM1",
                "LPT9",
                "/tmp/outside",
                "nested/name",
            };

            foreach (var id in ids)
            {
                Assert.Null(store.Load(id));
                Assert.False(store.Delete(id));
                Assert.Throws<ArgumentException>(() => store.PathOf(id));
            }

            Assert.False(File.Exists(Path.Combine(_dir, "escape", "session.json")));
        }

        [Fact]
        public void Load_RejectsDirectoryAndJsonIdentityMismatch()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("json-id"));
            Directory.Move(
                Path.Combine(_dir, "json-id"),
                Path.Combine(_dir, "directory-id"));

            Assert.Null(store.Load("directory-id"));
            Assert.Empty(store.LoadAll(out var errors));
            Assert.Contains("directory-id", errors);
        }

        [Fact]
        public void RootBoundary_IsStrict_AndTargetRootIsRejected()
        {
            var root = Path.Combine(_dir, "Sessions");
            Directory.CreateDirectory(root);

            Assert.False(SessionPathGuard.IsWithinRoot(root, root));
            Assert.True(SessionPathGuard.IsWithinRoot(root, Path.Combine(root, "abc")));
            Assert.False(SessionPathGuard.IsWithinRoot(root, root + "2"));
            Assert.False(SessionPathGuard.TryGetSessionDirectory(root, ".", out _));
        }

        [Fact]
        public void ReparseSessionDirectory_IsRejectedAndNeverRecursivelyDeleted()
        {
            var store = new TelemetrySessionStore(_dir);
            var outside = Path.Combine(
                Path.GetTempPath(),
                "aigt-reparse-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "keep");
            var link = Path.Combine(_dir, "linked");

            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                               or IOException
                                               or PlatformNotSupportedException)
            {
                return;
            }

            try
            {
                Assert.Null(store.Load("linked"));
                Assert.False(store.Delete("linked"));
                Assert.True(File.Exists(Path.Combine(outside, "sentinel.txt")));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(link))
                    {
                        Directory.Delete(link);
                    }
                }
                catch
                {
                    // TempDirectory cleanup remains best effort.
                }

                try
                {
                    if (Directory.Exists(outside))
                    {
                        Directory.Delete(outside, recursive: true);
                    }
                }
                catch
                {
                    // TempDirectory cleanup remains best effort.
                }
            }
        }

        [Fact]
        public void LoadMetadata_ReadsHistoryFieldsWithoutReturningFullSessions()
        {
            var store = new TelemetrySessionStore(_dir);
            store.Save(Sample("metadata") with
            {
                CompletedAtUtc = new DateTimeOffset(2024, 1, 1, 0, 0, 2, TimeSpan.Zero),
                Samples = Enumerable.Range(1, 100)
                    .Select(i => new TelemetrySample(
                        i,
                        DateTimeOffset.UtcNow,
                        1,
                        Array.Empty<TelemetryReading>()))
                    .ToArray()
            });

            var metadata = store.LoadMetadata(out var errors);

            Assert.Empty(errors);
            var item = Assert.Single(metadata);
            Assert.Equal("metadata", item.Id);
            Assert.Equal(100, item.SampleCount);
        }
    }
}
