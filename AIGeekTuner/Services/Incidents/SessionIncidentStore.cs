using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Incidents;
using AIGeekTuner.Services.Diagnostics;
using AIGeekTuner.Services.Telemetry.Recording;

namespace AIGeekTuner.Services.Incidents
{
    /// <summary>Session 的 Windows 事件证据独立持久化（Sessions/{id}/incidents.json）。</summary>
    public interface ISessionIncidentStore
    {
        /// <summary>原子保存：写临时文件后覆盖式移动。返回最终文件路径。</summary>
        string Save(SessionIncidentEnvelope envelope);

        /// <summary>
        /// 加载；文件不存在（旧 Session 未采集过 Windows 证据，属合法状态）
        /// 或损坏时返回 null，绝不让 Session 页面崩溃。
        /// </summary>
        SessionIncidentEnvelope? Load(string sessionId);

        string PathOf(string sessionId);
    }

    /// <summary>
    /// 文件存储：Sessions/{id}/incidents.json。行为参照 TelemetrySessionStore /
    /// SessionAnalysisStore（tmp + move 原子写），不引入数据库或文件系统抽象。
    /// Windows Incident 是独立 evidence domain，不落进 session.json / analysis.json。
    /// </summary>
    public sealed class SessionIncidentStore : ISessionIncidentStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _rootDirectory;

        public SessionIncidentStore(string sessionsDirectory)
        {
            _rootDirectory = SessionPathGuard.RequireSafeRootDirectory(sessionsDirectory);
        }

        public string PathOf(string sessionId) =>
            Path.Combine(
                SessionPathGuard.RequireSessionDirectory(_rootDirectory, sessionId),
                "incidents.json");

        public string Save(SessionIncidentEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var directory = SessionPathGuard.RequireSessionDirectory(_rootDirectory, envelope.SessionId);
            Directory.CreateDirectory(directory);
            var finalPath = Path.Combine(directory, "incidents.json");
            var tempPath = finalPath + ".tmp";

            File.WriteAllText(tempPath, JsonSerializer.Serialize(envelope, SerializerOptions));
            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }

        public SessionIncidentEnvelope? Load(string sessionId)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory))
            {
                return null;
            }

            var file = Path.Combine(directory, "incidents.json");
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<SessionIncidentEnvelope>(
                    File.ReadAllText(file), SerializerOptions);
                return envelope is not null
                    && string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(Path.GetFileName(directory), envelope.SessionId, StringComparison.Ordinal)
                    ? envelope
                    : null;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "Incidents/load");
                return null;
            }
        }
    }
}
