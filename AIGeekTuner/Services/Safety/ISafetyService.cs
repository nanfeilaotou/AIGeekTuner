using AIGeekTuner.Models;
using AIGeekTuner.Models.Sessions;

namespace AIGeekTuner.Services.Safety
{
    public interface ISafetyService
    {
        Task<SafetyResult> ValidateAsync(
            DiagnosticResult result,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// 使用同一组 SafetyGuard 规则审核并安全化 Session AI 结果。
        /// 结果中的 evidence ID 只由 Session parser 做存在性校验；本方法不把
        /// ID 存在误当成因果支持。
        /// </summary>
        Task<SessionSafetyOutcome> ValidateSessionAsync(
            SessionAnalysisResult result,
            CancellationToken cancellationToken = default);
    }
}
