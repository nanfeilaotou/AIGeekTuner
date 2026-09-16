using AIGeekTuner.Configuration;
using AIGeekTuner.Models;
using AIGeekTuner.Models.Sessions;
using AIGeekTuner.Services.Safety.Rules;

namespace AIGeekTuner.Services.Safety
{
    public sealed class SafetyGuardService : ISafetyService
    {
        private readonly IReadOnlyList<ISafetyRule> _rules;

        public SafetyGuardService(SafetyGuardOptions? options = null)
            : this(CreateDefaultRules(options ?? new SafetyGuardOptions()))
        {
        }

        public SafetyGuardService(IEnumerable<ISafetyRule> rules)
        {
            ArgumentNullException.ThrowIfNull(rules);
            _rules = rules.ToArray();

            if (_rules.Count == 0)
            {
                throw new ArgumentException("SafetyGuard 至少需要一条规则。", nameof(rules));
            }
        }

        public Task<SafetyResult> ValidateAsync(
            DiagnosticResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            ValidateDiagnosticCollections(result);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(Evaluate(result, cancellationToken));
        }

        public async Task<SessionSafetyOutcome> ValidateSessionAsync(
            SessionAnalysisResult result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(result);
            cancellationToken.ThrowIfCancellationRequested();

            // Adapt Session AI to the existing rule contract. This is a contract
            // adapter only: it does not claim that an evidence ID proves the
            // finding, and it does not add a second copy of the safety rules.
            var original = Evaluate(ToDiagnosticResult(result, result.Recommendations), cancellationToken);
            var safeRecommendations = new List<SessionRecommendation>(result.Recommendations.Count);
            var transformed = false;
            var messages = new List<string>(original.Warnings);

            foreach (var recommendation in result.Recommendations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var single = Evaluate(
                    ToDiagnosticResult(result, [recommendation]),
                    cancellationToken);
                if (single.Status == SafetyStatus.Rejected)
                {
                    transformed = true;
                    messages.Add("Session AI 的一条高风险硬件建议已被 SafetyGuard 阻止。" );
                    continue;
                }

                safeRecommendations.Add(recommendation);
            }

            // spokenSummary is also model output. A TTS call must never bypass
            // the same recommendation rules by reading an unsafe summary aloud.
            var spokenCheck = Evaluate(
                ToDiagnosticResult(
                    result,
                    [new SessionRecommendation(result.SpokenSummary)]),
                cancellationToken);
            var safeSpokenSummary = result.SpokenSummary;
            if (spokenCheck.Status == SafetyStatus.Rejected)
            {
                transformed = true;
                safeSpokenSummary = "本次分析包含不安全操作建议，相关语音内容已隐藏。";
                messages.Add("Session AI 的语音摘要包含高风险操作内容，已被 SafetyGuard 隐藏。" );
            }

            var safeResult = result with
            {
                Recommendations = safeRecommendations.ToArray(),
                SpokenSummary = safeSpokenSummary,
            };

            var status = original.Status == SafetyStatus.Rejected
                || spokenCheck.Status == SafetyStatus.Rejected
                ? SafetyStatus.Rejected
                : original.Status;
            var finalSafety = new SafetyResult
            {
                Status = status,
                Warnings = messages.Distinct(StringComparer.Ordinal).ToArray(),
            };
            return await Task.FromResult(new SessionSafetyOutcome(
                safeResult,
                finalSafety,
                transformed));
        }

        private SafetyResult Evaluate(
            DiagnosticResult result,
            CancellationToken cancellationToken)
        {
            ValidateDiagnosticCollections(result);
            cancellationToken.ThrowIfCancellationRequested();

            var evaluations = new List<SafetyRuleResult>(_rules.Count);
            foreach (var rule in _rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                evaluations.Add(rule.Evaluate(result));
            }

            var messages = evaluations
                .SelectMany(evaluation => evaluation.Messages)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var status = evaluations.Any(evaluation =>
                evaluation.Decision == SafetyRuleDecision.Reject)
                ? SafetyStatus.Rejected
                : evaluations.Any(evaluation =>
                    evaluation.Decision == SafetyRuleDecision.Warning)
                    ? SafetyStatus.ApprovedWithWarnings
                    : SafetyStatus.Approved;

            return new SafetyResult
            {
                Status = status,
                Warnings = messages
            };
        }

        private static DiagnosticResult ToDiagnosticResult(
            SessionAnalysisResult result,
            IReadOnlyList<SessionRecommendation> recommendations) =>
            new()
            {
                Summary = result.Summary,
                RootCause = string.Join(
                    Environment.NewLine,
                    result.Findings.Select(finding => finding.Explanation)),
                Confidence = result.Confidence,
                RiskLevel = result.OverallAssessment switch
                {
                    SessionOverallAssessment.PotentialIssue => DiagnosticRiskLevel.High,
                    SessionOverallAssessment.Attention => DiagnosticRiskLevel.Medium,
                    _ => DiagnosticRiskLevel.Low,
                },
                Evidence = result.Findings.Select(finding => new DiagnosticEvidence
                {
                    Kind = EvidenceKind.Inference,
                    Description = finding.Explanation,
                }).ToArray(),
                Recommendations = recommendations.Select(recommendation => new Recommendation
                {
                    Action = recommendation.Text,
                    Reason = "Session AI recommendation; safety review does not treat evidence IDs as causal proof.",
                    RiskLevel = DiagnosticRiskLevel.Medium,
                }).ToArray(),
            };

        private static IReadOnlyList<ISafetyRule> CreateDefaultRules(
            SafetyGuardOptions options)
        {
            return
            [
                new DangerousVoltageRule(options),
                new DangerousOperationRule(options),
                new OvercertaintyRule(options)
            ];
        }

        private static void ValidateDiagnosticCollections(DiagnosticResult result)
        {
            if (result.Evidence is null || result.Recommendations is null)
            {
                throw new ArgumentException(
                    "DiagnosticResult 的 Evidence 和 Recommendations 不能为空。",
                    nameof(result));
            }

            if (result.Recommendations.Any(recommendation => recommendation is null) ||
                result.Evidence.Any(evidence => evidence is null))
            {
                throw new ArgumentException(
                    "DiagnosticResult 包含空的 Evidence 或 Recommendation。",
                    nameof(result));
            }
        }
    }
}
