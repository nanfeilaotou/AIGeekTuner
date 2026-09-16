using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Diagnostics;

namespace AIGeekTuner.Services.Telemetry.Recording
{
    /// <summary>
    /// analysis.json 信封：deterministic session.json 保持稳定，AI 结果独立持久化（§24/§25）。
    /// V2-M5.1B（Gate L）：additive ProviderId / ProviderName（旧文件缺省 null，向后兼容）。
    /// </summary>
    public sealed record SessionAnalysisEnvelope(
        int SchemaVersion,
        string SessionId,
        DateTimeOffset AnalyzedAtUtc,
        string ModelName,
        long DurationMs,
        bool RepairUsed,
        SessionAnalysisResult Result,
        string ContextJson,
        string? ProviderId = null,
        string? ProviderName = null);

    public interface ISessionAnalysisStore
    {
        void Save(SessionAnalysisEnvelope envelope);

        SessionAnalysisEnvelope? Load(string sessionId);

        /// <summary>轻量存在性检查（只 File.Exists，不反序列化）——历史列表“已分析”标记用。</summary>
        bool AnalysisExists(string sessionId);

        /// <summary>M4.5E.2 Gate G：语音缓存只读事实源——存在且 RIFF/WAVE 头有效。</summary>
        bool HasCachedVoice(string sessionId, string? analysisIdentity = null);

        string VoiceWavPathOf(string sessionId);

        bool VoiceWavExists(string sessionId);

        void SaveVoiceWav(string sessionId, byte[] wavBytes, string? analysisIdentity = null);

        /// <summary>
        /// 只有当前 analysis.json 仍对应 identity 时才提交语音缓存。
        /// 过期/取消的在途任务应把 false 当作“不提交”，而不是删除旧缓存。
        /// </summary>
        bool TrySaveVoiceWav(string sessionId, byte[] wavBytes, string analysisIdentity);

        byte[]? TryLoadVoiceWav(string sessionId, string? analysisIdentity = null);

        /// <summary>从当前 analysis.json 计算稳定的分析版本标识。</summary>
        string? TryGetAnalysisIdentity(string sessionId);
    }

    public sealed class SessionAnalysisStore : ISessionAnalysisStore
    {
        private const int SchemaVersion = 1;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _rootDirectory;
        // Shared across store instances so a stale ViewModel cannot race a
        // replacement analysis performed by a newly created ViewModel.
        private static readonly object VoiceCommitGate = new();

        public SessionAnalysisStore(string sessionsDirectory)
        {
            _rootDirectory = SessionPathGuard.RequireSafeRootDirectory(sessionsDirectory);
        }

        // ---- V2-M4.5D：会话级语音缓存（Sessions/{id}/voice.wav）——
        // 分析完成即预生成；播放时命中缓存则不再调用 GPT-SoVITS。

        public string VoiceWavPathOf(string sessionId) =>
            Path.Combine(
                SessionPathGuard.RequireSessionDirectory(_rootDirectory, sessionId),
                "voice.wav");

        public bool VoiceWavExists(string sessionId) =>
            SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory)
            && File.Exists(Path.Combine(directory, "voice.wav"));

        public void SaveVoiceWav(
            string sessionId,
            byte[] wavBytes,
            string? analysisIdentity = null)
        {
            ArgumentNullException.ThrowIfNull(wavBytes);
            lock (VoiceCommitGate)
            {
                var effectiveIdentity = analysisIdentity ?? TryGetAnalysisIdentityLocked(sessionId);
                if (analysisIdentity is not null
                    && !string.Equals(
                        TryGetAnalysisIdentityLocked(sessionId),
                        analysisIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("语音缓存对应的分析已发生变化，拒绝提交过期结果。");
                }

                if (effectiveIdentity is not null
                    && !WriteVoiceCacheLocked(sessionId, wavBytes, effectiveIdentity))
                {
                    throw new InvalidOperationException("语音缓存对应的分析已发生变化，拒绝提交过期结果。");
                }

                if (effectiveIdentity is null)
                {
                    WriteVoiceCacheLocked(sessionId, wavBytes, identity: null);
                }
            }
        }

        public bool TrySaveVoiceWav(
            string sessionId,
            byte[] wavBytes,
            string analysisIdentity)
        {
            ArgumentNullException.ThrowIfNull(wavBytes);
            if (string.IsNullOrWhiteSpace(analysisIdentity)
                || !SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out _))
            {
                return false;
            }

            lock (VoiceCommitGate)
            {
                // The identity check and the temp-file commit are serialized with
                // analysis.json replacement. A stale task can never commit after
                // a newer analysis has acquired this gate.
                if (!string.Equals(
                        TryGetAnalysisIdentityLocked(sessionId),
                        analysisIdentity,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return WriteVoiceCacheLocked(sessionId, wavBytes, analysisIdentity);
            }
        }

        public byte[]? TryLoadVoiceWav(string sessionId, string? analysisIdentity = null)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out _))
            {
                return null;
            }

            var file = VoiceWavPathOf(sessionId);
            if (!File.Exists(file)
                || (analysisIdentity is not null && !VoiceIdentityMatches(sessionId, analysisIdentity)))
            {
                return null;
            }

            try
            {
                var bytes = File.ReadAllBytes(file);
                return bytes.Length >= 12 && IsRiffWavHeader(bytes) ? bytes : null;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis voice load");
                return null;
            }
        }

        public void Save(SessionAnalysisEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            lock (VoiceCommitGate)
            {
                var directory = SessionPathGuard.RequireSessionDirectory(_rootDirectory, envelope.SessionId);
                Directory.CreateDirectory(directory);
                var wrapped = new EnvelopeFile(SchemaVersion, envelope);
                var temp = Path.Combine(directory, $".analysis.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(wrapped, SerializerOptions));
                    File.Move(temp, Path.Combine(directory, "analysis.json"), overwrite: true);
                }
                finally
                {
                    TryDelete(temp);
                }
            }
        }

        public SessionAnalysisEnvelope? Load(string sessionId)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory))
            {
                return null;
            }

            var file = Path.Combine(directory, "analysis.json");
            if (!File.Exists(file))
            {
                return null;
            }

            try
            {
                var wrapped = JsonSerializer.Deserialize<EnvelopeFile>(File.ReadAllText(file), SerializerOptions);
                var analysis = wrapped?.Analysis;
                return analysis is not null
                    && string.Equals(analysis.SessionId, sessionId, StringComparison.Ordinal)
                    && string.Equals(Path.GetFileName(directory), analysis.SessionId, StringComparison.Ordinal)
                    ? analysis
                    : null;
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis load");
                return null;
            }
        }

        /// <summary>M4.5E.1 补充：历史列表“已分析/未分析”标记——只查文件存在，绝不触发分析。</summary>
        public bool AnalysisExists(string sessionId) =>
            SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out var directory)
            && File.Exists(Path.Combine(directory, "analysis.json"));

        /// <summary>
        /// M4.5E.2 Gate G/H：语音缓存的只读事实源——存在且 RIFF/WAVE 头有效。
        /// 直接 File.Exists + 头校验定位（确定性文件名，无 index、无 lazy 初始化），
        /// 重启后第一次查询即读真实磁盘状态。analysis 存在 ≠ 语音存在。
        /// </summary>
        public bool HasCachedVoice(string sessionId, string? analysisIdentity = null)
        {
            if (!SessionPathGuard.TryGetSessionDirectory(_rootDirectory, sessionId, out _))
            {
                return false;
            }

            var file = VoiceWavPathOf(sessionId);
            if (!File.Exists(file)
                || (analysisIdentity is not null && !VoiceIdentityMatches(sessionId, analysisIdentity)))
            {
                return false;
            }

            try
            {
                using var stream = File.OpenRead(file);
                if (stream.Length < 12)
                {
                    return false;
                }

                var header = new byte[12];
                stream.ReadExactly(header);
                return IsRiffWavHeader(header);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis voice cache probe");
                return false;
            }
        }

        public string? TryGetAnalysisIdentity(string sessionId)
        {
            if (!SessionPathGuard.TryNormalizeId(sessionId, out _))
            {
                return null;
            }

            lock (VoiceCommitGate)
            {
                return TryGetAnalysisIdentityLocked(sessionId);
            }
        }

        private string? TryGetAnalysisIdentityLocked(string sessionId)
        {
            var analysis = Load(sessionId);
            return analysis is null ? null : ComputeAnalysisIdentity(analysis);
        }

        public static string ComputeAnalysisIdentity(SessionAnalysisEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, SerializerOptions));
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private bool VoiceIdentityMatches(string sessionId, string analysisIdentity)
        {
            var directory = SessionPathGuard.RequireSessionDirectory(_rootDirectory, sessionId);
            var metaPath = Path.Combine(directory, "voice.wav.meta.json");
            if (!File.Exists(metaPath))
            {
                // Old unversioned voice files are intentionally not accepted by
                // identity-aware VM paths. Legacy callers without an identity
                // still retain the old session-level lookup behavior.
                return false;
            }

            try
            {
                var meta = JsonSerializer.Deserialize<VoiceCacheMeta>(
                    File.ReadAllText(metaPath), SerializerOptions);
                return meta is not null
                    && string.Equals(meta.AnalysisIdentity, analysisIdentity, StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                ExceptionLogWriter.Write(exception, "SessionAnalysis voice metadata load");
                return false;
            }
        }

        private bool WriteVoiceCacheLocked(
            string sessionId,
            byte[] wavBytes,
            string? identity)
        {
            var directory = SessionPathGuard.RequireSessionDirectory(_rootDirectory, sessionId);
            Directory.CreateDirectory(directory);
            var wavPath = Path.Combine(directory, "voice.wav");
            var metaPath = Path.Combine(directory, "voice.wav.meta.json");
            var wavTemp = Path.Combine(directory, $".voice.{Guid.NewGuid():N}.wav.tmp");
            var metaTemp = Path.Combine(directory, $".voice.{Guid.NewGuid():N}.meta.tmp");

            try
            {
                File.WriteAllBytes(wavTemp, wavBytes);
                File.Move(wavTemp, wavPath, overwrite: true);

                if (identity is null)
                {
                    TryDelete(metaPath);
                }
                else
                {
                    File.WriteAllText(
                        metaTemp,
                        JsonSerializer.Serialize(new VoiceCacheMeta(identity), SerializerOptions));
                    File.Move(metaTemp, metaPath, overwrite: true);
                }

                return true;
            }
            finally
            {
                TryDelete(wavTemp);
                TryDelete(metaTemp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Preserve the primary cache operation error.
            }
        }

        private static bool IsRiffWavHeader(ReadOnlySpan<byte> header) =>
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';

        private sealed record EnvelopeFile(int SchemaVersion, SessionAnalysisEnvelope Analysis);

        private sealed record VoiceCacheMeta(string AnalysisIdentity);
    }
}
