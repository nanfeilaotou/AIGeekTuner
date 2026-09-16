namespace AIGeekTuner.Configuration
{
    /// <summary>
    /// 持久化的用户设置。属性全部 init-only：磁盘加载与保存替换都以整体快照方式进行，
    /// 运行中的诊断只持有旧快照，不会被中途修改影响。
    /// </summary>
    public sealed class ApplicationSettings
    {
        public bool AutoSaveDiagnosisHistory { get; init; } = true;

        public string OllamaBaseUrl { get; init; } = OllamaOptions.DefaultBaseUrl;

        public string OllamaModelName { get; init; } = OllamaOptions.DefaultModelName;

        /// <summary>
        /// Global AI request timeout. The property name is retained in the
        /// runtime persistence schema for migration; portable v2 calls it
        /// aiTimeoutSeconds.
        /// </summary>
        public int OllamaTimeoutSeconds { get; init; } = OllamaOptions.DefaultTimeoutSeconds;

        public int MaxFaultLogCharacters { get; init; } =
            DiagnosisInputOptions.DefaultMaxFaultLogCharacters;

        public bool UseJsonFormat { get; init; } = OllamaOptions.DefaultUseJsonFormat;

        /// <summary>诊断录制采样间隔（毫秒）。合法值由 ApplicationSettingsValidator 定义。</summary>
        public int RecordingIntervalMs { get; init; } = 2000;

        public VoiceSettings Voice { get; init; } = new();

        /// <summary>Hardware 页自动刷新开关与间隔（UI 偏好，非录制采样）。</summary>
        public bool HardwareAutoRefresh { get; init; } = true;

        public int HardwareRefreshIntervalMs { get; init; } = 2000;
    }

    /// <summary>GPT-SoVITS 语音摘要配置。ReferenceAudioPath 必须是 GSV 服务进程可访问的路径。</summary>
    public sealed class VoiceSettings
    {
        public bool Enabled { get; init; } = false;

        public string Endpoint { get; init; } = "http://127.0.0.1:9880";

        public string ReferenceAudioPath { get; init; } = string.Empty;

        /// <summary>参考音频的转写文本。</summary>
        public string PromptText { get; init; } = string.Empty;

        /// <summary>参考音频语言；合成正文语言固定 zh（§38）。</summary>
        public string PromptLang { get; init; } = "zh";

        public double SpeedFactor { get; init; } = 1.0;

        /// <summary>可选：按官方 /set_gpt_weights 下发的权重路径（用户显式配置才生效）。</summary>
        public string GptModelPath { get; init; } = string.Empty;

        public string SovitsModelPath { get; init; } = string.Empty;
    }
}
