using AIGeekTuner.Models;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.Safety;

/// <summary>
/// Session AI 的最终安全结果。Result 是可保存、可展示、可送入 TTS 的版本；
/// Safety 保留原始规则判定，便于 UI/日志知道发生过阻止或降级。
/// </summary>
public sealed record SessionSafetyOutcome(
    SessionAnalysisResult Result,
    SafetyResult Safety,
    bool WasTransformed);
