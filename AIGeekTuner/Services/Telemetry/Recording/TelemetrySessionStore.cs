using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Storage;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    public interface ITelemetrySessionStore
    {
        /// <summary>原子保存：写临时文件后覆盖式移动。返回最终文件路径。</summary>
        string Save(TelemetryRecordingSession session);

        /// <summary>加载全部会话；损坏的文件被跳过并计入 errors，绝不让页面炸掉。</summary>
        IReadOnlyList<TelemetryRecordingSession> LoadAll(out IReadOnlyList<string> errors);

        /// <summary>
        /// 读取历史列表所需的轻量元数据；不得反序列化完整 Samples。
        /// 默认实现保留旧替身兼容性，生产文件 store 覆盖为轻量 JSON 读取。
        /// </summary>
        IReadOnlyList<TelemetrySessionMetadata> LoadMetadata(out IReadOnlyList<string> errors)
        {
            var sessions = LoadAll(out errors);
            return sessions.Select(session => new TelemetrySessionMetadata(
                session.Id,
                session.StartedAtUtc,
                session.CompletedAtUtc,
                session.Summary?.SampleCount ?? session.Samples.Count)).ToArray();
        }

        TelemetryRecordingSession? Load(string sessionId);

        bool Delete(string sessionId);

        string PathOf(string sessionId);
    }

    public sealed record TelemetrySessionMetadata(
        string Id,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        int SampleCount);

    /// <summary>
    /// 文件存储：Sessions/{id}/session.json。M2 只记录 canonical 指标，
    /// 一个会话一个 JSON 足够（§10）；不做数据库与多格式。
    /// </summary>
    public sealed class TelemetrySessionStore : ITelemetrySessionStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly string _rootDirectory;

        public TelemetrySessionStore(string sessionsDirectory)
        {
            _rootDirectory = SessionPathGuard.RequireSafeRootDirectory(sessionsDirectory);
        }

        public TelemetrySessionStore(ApplicationDataPaths paths)
            : this(paths.SessionsDirectory)
        {
        }

        public string PathOf(string sessionId) =>
            Path.Combine(
                SessionPathGuard.RequireSessionDirectory(_rootDirectory, sessionId),
                "session.json");

        public string Save(TelemetryRecordingSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var directory = SessionPathGuard.RequireSessionDirectory(_rootDirectory, session.Id);
            Directory.CreateDirectory(directory);
            var finalPath = Path.Combine(directory, "session.json");
            var tempPath = finalPath + ".tmp";

            File.WriteAllText(tempPath, JsonSerializer.Serialize(session, SerializerOptions));
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }

        public IReadOnlyList<TelemetryRecordingSession> LoadAll(out IReadOnlyList<string> errors)
        {
            var sessions = new List<TelemetryRecordingSession>();
            var errorList = new List<string>();

            foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
            {
                var directoryName = Path.GetFileName(directory);
                if (!SessionPathGuard.TryGetSessionDirectory(
                        _rootDirectory,
                        directoryName,
                        out var safeDirectory)
                    || !SessionPathGuard.IsWithinRoot(_rootDirectory, safeDirectory))
                {
                    errorList.Add(directoryName);
                    continue;
                }

                var normalizedId = Path.GetFileName(safeDirectory);
                var file = Path.Combine(safeDirectory, "session.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var session = JsonSerializer.Deserialize<TelemetryRecordingSession>(
                        File.ReadAllText(file), SerializerOptions);
                    if (session is not null
                        && string.Equals(session.Id, normalizedId, StringComparison.Ordinal))
                    {
                        sessions.Add(session);
                    }
                    else
                    {
                        errorList.Add(directoryName);
                    }
                }
                catch (Exception exception)
                {
                    // 单个坏文件只记录并跳过（§41）。
                    ExceptionLogWriter.Write(exception, "Telemetry/session load");
                    errorList.Add(Path.GetFileName(directory));
                }
            }

            errors = errorList;
            return sessions.OrderByDescending(session => session.StartedAtUtc).ToList();
        }

        public TelemetryRecordingSession? Load(string sessionId)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory))
            {
                return null;
            }

            var file = Path.Combine(directory, "session.json");
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var session = JsonSerializer.Deserialize<TelemetryRecordingSession>(
                    File.ReadAllText(file), SerializerOptions);
                return session is not null
                    && string.Equals(session.Id, sessionId, StringComparison.Ordinal)
                    && string.Equals(
                        Path.GetFileName(directory),
                        session.Id,
                        StringComparison.Ordinal)
                    ? session
                    : null;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Telemetry/session load");
                return null;
            }
        }

        public bool Delete(string sessionId)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory)
                || !SessionPathGuard.IsWithinRoot(_rootDirectory, directory)
                || !Directory.Exists(directory)
                || SessionPathGuard.ContainsReparsePoint(directory))
            {
                return false;
            }

            Directory.Delete(directory, recursive: true);
            return true;
        }

        public IReadOnlyList<TelemetrySessionMetadata> LoadMetadata(
            out IReadOnlyList<string> errors)
        {
            var metadata = new List<TelemetrySessionMetadata>();
            var errorList = new List<string>();

            foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
            {
                var directoryName = Path.GetFileName(directory);
                if (!SessionPathGuard.TryGetSessionDirectory(
                        _rootDirectory,
                        directoryName,
                        out var safeDirectory)
                    || !SessionPathGuard.IsWithinRoot(_rootDirectory, safeDirectory))
                {
                    errorList.Add(directoryName);
                    continue;
                }

                var normalizedId = Path.GetFileName(safeDirectory);
                var file = Path.Combine(safeDirectory, "session.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    var root = document.RootElement;
                    if (!TryGetString(root, "Id", "id", out var id)
                        || !string.Equals(id, normalizedId, StringComparison.Ordinal)
                        || !TryGetDateTimeOffset(root, "StartedAtUtc", "startedAtUtc", out var started))
                    {
                        errorList.Add(directoryName);
                        continue;
                    }

                    DateTimeOffset? completed = null;
                    if (TryGetDateTimeOffset(root, "CompletedAtUtc", "completedAtUtc", out var completedValue))
                    {
                        completed = completedValue;
                    }

                    var sampleCount = 0;
                    if (TryGetObject(root, "Summary", "summary", out var summary)
                        && TryGetInt32(summary, "SampleCount", "sampleCount", out var summarizedCount))
                    {
                        sampleCount = summarizedCount;
                    }
                    else if (TryGetArray(root, "Samples", "samples", out var samples))
                    {
                        // This enumerates only array tokens; it never materializes
                        // TelemetrySample/readings for the history list.
                        sampleCount = samples.GetArrayLength();
                    }

                    metadata.Add(new TelemetrySessionMetadata(
                        normalizedId, started, completed, sampleCount));
                }
                catch (Exception exception)
                {
                    ExceptionLogWriter.Write(exception, "Telemetry/session metadata load");
                    errorList.Add(directoryName);
                }
            }

            errors = errorList;
            return metadata.OrderByDescending(item => item.StartedAtUtc).ToArray();
        }

        private static bool TryGetString(
            JsonElement root, string primary, string fallback, out string value)
        {
            value = string.Empty;
            return root.TryGetProperty(primary, out var element)
                && element.ValueKind == JsonValueKind.String
                && (value = element.GetString() ?? string.Empty).Length > 0
                || root.TryGetProperty(fallback, out element)
                && element.ValueKind == JsonValueKind.String
                && (value = element.GetString() ?? string.Empty).Length > 0;
        }

        private static bool TryGetDateTimeOffset(
            JsonElement root, string primary, string fallback, out DateTimeOffset value)
        {
            value = default;
            return (root.TryGetProperty(primary, out var element)
                    || root.TryGetProperty(fallback, out element))
                && element.ValueKind == JsonValueKind.String
                && element.TryGetDateTimeOffset(out value);
        }

        private static bool TryGetInt32(
            JsonElement root, string primary, string fallback, out int value)
        {
            value = 0;
            return (root.TryGetProperty(primary, out var element)
                    || root.TryGetProperty(fallback, out element))
                && element.TryGetInt32(out value);
        }

        private static bool TryGetObject(
            JsonElement root, string primary, string fallback, out JsonElement value)
        {
            value = default;
            return (root.TryGetProperty(primary, out value)
                    || root.TryGetProperty(fallback, out value))
                && value.ValueKind == JsonValueKind.Object;
        }

        private static bool TryGetArray(
            JsonElement root, string primary, string fallback, out JsonElement value)
        {
            value = default;
            return (root.TryGetProperty(primary, out value)
                    || root.TryGetProperty(fallback, out value))
                && value.ValueKind == JsonValueKind.Array;
        }
    }
}
