using System.IO;
using AIGeekTuner.Services.AI.Providers.Configuration;
using AIGeekTuner.Services.AI.Providers.Credentials;
using AIGeekTuner.Services.AI.Providers.Runtime;
using AIGeekTuner.Services.AI.Providers.Transport;

namespace AIGeekTuner.Services.AI.Providers
{
    /// <summary>
    /// <see cref="IAiProviderManager"/> 的默认实现。
    /// 草稿语义（Gate M）：FetchModels / TestConnection 只使用调用方传入的草稿与明文 Key，
    /// 永不写盘；只有 SaveProfileAsync / DeleteProfileAsync 触碰持久化。
    /// 保存原子性（Gate N）：凭据先写、配置后写；配置失败回滚凭据，
    /// 内存快照由 store 在磁盘写成功后整体换入，绝不出现 half-applied。
    /// </summary>
    public sealed class AiProviderManager : IAiProviderManager
    {
        private readonly IAiProviderProfileStore _store;
        private readonly IAiCredentialStore _credentials;
        private readonly IOpenAiCompatibleClient _openAiClient;
        private readonly IOllamaNativeClient _ollamaClient;

        public AiProviderManager(
            IAiProviderProfileStore store,
            IAiCredentialStore credentials,
            IOpenAiCompatibleClient openAiClient,
            IOllamaNativeClient ollamaClient)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _openAiClient = openAiClient ?? throw new ArgumentNullException(nameof(openAiClient));
            _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        }

        public IReadOnlyList<AiProviderProfile> ListProfiles()
        {
            return _store.Snapshot().Profiles;
        }

        public AiProviderProfile? GetProfile(string providerId)
        {
            return _store.Snapshot().Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, providerId, StringComparison.Ordinal));
        }

        public IReadOnlyList<string> ValidateDraft(AiProviderProfile draft)
        {
            var errors = new List<string>(AiProviderProfileValidator.Validate(draft));
            if (errors.Count == 0)
            {
                var conflict = _store.Snapshot().Profiles.FirstOrDefault(profile =>
                    !string.Equals(profile.Id, draft.Id, StringComparison.Ordinal)
                    && string.Equals(profile.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
                if (conflict is not null)
                {
                    errors.Add($"Provider ID {draft.Id} 已被其他 Provider 使用（ID 创建后不可修改）。");
                }
            }

            return errors;
        }

        public Task<AiModelDiscoveryResult> FetchModelsAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ListModelsAsync(draft.BaseUrl, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ListModelsAsync(draft.BaseUrl, key, cancellationToken)),
                _ => Task.FromResult(new AiModelDiscoveryResult(
                    AiModelDiscoveryStatus.ConnectionUnavailable,
                    "未知的 Provider 协议类型。",
                    Array.Empty<string>()))
            };
        }

        public Task<AiConnectionTestResult> TestConnectionAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var model = AiProviderModelId.Normalize(modelId)
                ?? AiProviderModelId.Normalize(draft.DefaultModelId);
            if (model is null)
            {
                return Task.FromResult(new AiConnectionTestResult(
                    AiConnectionTestStatus.MissingModel,
                    "未指定模型，无法测试连接。请先选择或手动添加模型。"));
            }

            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ProbeChatAsync(draft.BaseUrl, model, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ProbeChatAsync(draft.BaseUrl, key, model, cancellationToken)),
                _ => Task.FromResult(new AiConnectionTestResult(
                    AiConnectionTestStatus.ConnectionUnavailable,
                    "未知的 Provider 协议类型。"))
            };
        }

        public Task<AiStructuredOutputProbeResult> TestStructuredOutputAsync(
            AiProviderProfile draft,
            string? plainApiKey = null,
            string? modelId = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            var model = AiProviderModelId.Normalize(modelId)
                ?? AiProviderModelId.Normalize(draft.DefaultModelId);
            if (model is null)
            {
                return Task.FromResult(new AiStructuredOutputProbeResult(
                    false,
                    "未指定模型，无法探测结构化输出能力。"));
            }

            return draft.Kind switch
            {
                AiProviderKind.OllamaNative => _ollamaClient.ProbeStructuredOutputAsync(draft.BaseUrl, model, cancellationToken),
                AiProviderKind.OpenAiCompatible => ResolveKeyAndRunAsync(
                    draft, plainApiKey,
                    key => _openAiClient.ProbeStructuredOutputAsync(draft.BaseUrl, key, model, cancellationToken)),
                _ => Task.FromResult(new AiStructuredOutputProbeResult(
                    false,
                    "未知的 Provider 协议类型。"))
            };
        }

        public async Task<AiProviderSaveResult> SaveProfileAsync(
            AiProviderProfile draft,
            AiCredentialChange credentialChange,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(draft);
            ArgumentNullException.ThrowIfNull(credentialChange);

            var errors = ValidateDraft(draft);
            if (errors.Count > 0)
            {
                return AiProviderSaveResult.Fail(string.Join(" ", errors));
            }

            if (credentialChange.Mode == AiCredentialChangeMode.Replace
                && string.IsNullOrEmpty(credentialChange.PlainText))
            {
                return AiProviderSaveResult.Fail("新凭据内容不能为空。");
            }

            var snapshot = _store.Snapshot();
            var existingProfile = snapshot.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, draft.Id, StringComparison.Ordinal));
            var profiles = snapshot.Profiles
                .Where(profile => !string.Equals(profile.Id, draft.Id, StringComparison.Ordinal))
                .Concat(new[] { draft })
                .ToArray();
            var configuration = new AiProviderConfiguration
            {
                Version = snapshot.Version,
                Profiles = profiles,
                ActiveProviderId = snapshot.ActiveProviderId
            };

            try
            {
                // 1. 先写凭据（DPAPI 加密 + 原子落盘）。
                var credentialMutation = await ApplyCredentialChangeAsync(
                    existingProfile,
                    draft,
                    credentialChange,
                    cancellationToken);

                // 2. 再原子写配置；失败则回滚凭据，避免 half-applied。
                try
                {
                    await _store.SaveAsync(configuration, cancellationToken);
                }
                catch (Exception)
                {
                    await RollbackCredentialAsync(draft.Id, credentialMutation);
                    throw;
                }

                return AiProviderSaveResult.Ok();
            }
            catch (Exception exception) when (exception is AiProviderStoreException or IOException)
            {
                return AiProviderSaveResult.Fail(exception.Message);
            }
        }

        public async Task<AiProviderSaveResult> DeleteProfileAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return AiProviderSaveResult.Fail("Provider ID 不能为空。");
            }

            var snapshot = _store.Snapshot();
            var remaining = snapshot.Profiles
                .Where(profile => !string.Equals(profile.Id, providerId, StringComparison.Ordinal))
                .ToArray();
            if (remaining.Length == snapshot.Profiles.Count)
            {
                return AiProviderSaveResult.Fail($"Provider {providerId} 不存在。");
            }

            try
            {
                // 1. 先删凭据；失败则整个删除操作中止，配置保持完整。
                var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                var oldOrigin = await _credentials.LoadOriginAsync(providerId, cancellationToken);
                await _credentials.DeleteAsync(providerId, cancellationToken);

                // 2. 再原子写“少了一个 Provider”的配置；失败则回滚凭据。
                // 删除的是“当前使用”的 Provider 时清除 ActiveProviderId；
                // 下一次请求按 fallback 策略重新解析（Gate C）。
                var activeProviderId = string.Equals(
                        snapshot.ActiveProviderId,
                        providerId,
                        StringComparison.Ordinal)
                    ? null
                    : snapshot.ActiveProviderId;
                try
                {
                    await _store.SaveAsync(
                        new AiProviderConfiguration
                        {
                            Version = snapshot.Version,
                            Profiles = remaining,
                            ActiveProviderId = activeProviderId
                        },
                        cancellationToken);
                }
                catch (Exception)
                {
                    await RollbackCredentialAsync(
                        providerId,
                        oldPlainText is null
                            ? CredentialMutation.None
                            : new CredentialMutation(true, true, oldPlainText, oldOrigin));
                    throw;
                }

                return AiProviderSaveResult.Ok();
            }
            catch (Exception exception) when (exception is AiProviderStoreException or IOException)
            {
                return AiProviderSaveResult.Fail(exception.Message);
            }
        }

        /// <summary>
        /// 执行凭据变更。Changed 与 HadPreviousCredential 分开表达，
        /// 避免把 KeepExisting 或“原来没有凭据”误当成同一种状态。
        /// </summary>
        private async Task<CredentialMutation> ApplyCredentialChangeAsync(
            AiProviderProfile? existingProfile,
            AiProviderProfile draft,
            AiCredentialChange change,
            CancellationToken cancellationToken)
        {
            var providerId = draft.Id;
            switch (change.Mode)
            {
                case AiCredentialChangeMode.KeepExisting:
                    if (existingProfile is null
                        || existingProfile.Kind != AiProviderKind.OpenAiCompatible
                        || draft.Kind != AiProviderKind.OpenAiCompatible
                        || AiProviderOrigin.Equals(existingProfile.BaseUrl, draft.BaseUrl))
                    {
                        return CredentialMutation.None;
                    }

                    return await BindExistingCredentialToOldOriginAsync(
                        existingProfile,
                        draft,
                        cancellationToken);
                case AiCredentialChangeMode.Replace:
                {
                    var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                    var oldOrigin = await _credentials.LoadOriginAsync(providerId, cancellationToken);
                    var newOrigin = AiProviderOrigin.TryNormalize(draft.BaseUrl, out var normalized)
                        ? normalized
                        : null;
                    await _credentials.SaveAsync(
                        providerId, change.PlainText!, newOrigin, cancellationToken);
                    return new CredentialMutation(
                        Changed: true,
                        HadPreviousCredential: oldPlainText is not null,
                        PreviousPlainText: oldPlainText,
                        PreviousOrigin: oldOrigin);
                }
                case AiCredentialChangeMode.Delete:
                {
                    var oldPlainText = await _credentials.LoadAsync(providerId, cancellationToken);
                    var oldOrigin = await _credentials.LoadOriginAsync(providerId, cancellationToken);
                    await _credentials.DeleteAsync(providerId, cancellationToken);
                    return oldPlainText is null
                        ? CredentialMutation.None
                        : new CredentialMutation(true, true, oldPlainText, oldOrigin);
                }
                default:
                    throw new InvalidOperationException("未知的凭据变更类型。");
            }
        }

        private async Task<CredentialMutation> BindExistingCredentialToOldOriginAsync(
            AiProviderProfile existingProfile,
            AiProviderProfile draft,
            CancellationToken cancellationToken)
        {
            var oldPlainText = await _credentials.LoadAsync(draft.Id, cancellationToken);
            if (oldPlainText is null)
            {
                return CredentialMutation.None;
            }

            var oldOrigin = await _credentials.LoadOriginAsync(draft.Id, cancellationToken);
            var previous = new CredentialMutation(
                Changed: false,
                HadPreviousCredential: true,
                PreviousPlainText: oldPlainText,
                PreviousOrigin: oldOrigin);

            // KeepExisting on a cross-origin edit must not leave an unbound secret
            // that the new profile can silently consume. Prefer retaining it with
            // the old origin; stores without origin metadata fall back to deleting
            // the credential, which is the safer compatibility behavior.
            var oldNormalizedOrigin = AiProviderOrigin.TryNormalize(existingProfile.BaseUrl, out var normalized)
                ? normalized
                : null;
            await _credentials.SaveAsync(
                draft.Id, oldPlainText, oldNormalizedOrigin, cancellationToken);
            var storedOrigin = await _credentials.LoadOriginAsync(draft.Id, cancellationToken);
            if (!string.Equals(storedOrigin, oldNormalizedOrigin, StringComparison.OrdinalIgnoreCase))
            {
                await _credentials.DeleteAsync(draft.Id, cancellationToken);
            }

            return previous with { Changed = true };
        }

        /// <summary>失败回滚：恢复旧明文及其 origin；回滚自身失败只留痕，不掩盖原始错误。</summary>
        private async Task RollbackCredentialAsync(string providerId, CredentialMutation mutation)
        {
            if (!mutation.Changed)
            {
                return;
            }

            try
            {
                if (!mutation.HadPreviousCredential || mutation.PreviousPlainText is null)
                {
                    await _credentials.DeleteAsync(providerId);
                }
                else
                {
                    await _credentials.SaveAsync(
                        providerId,
                        mutation.PreviousPlainText,
                        mutation.PreviousOrigin);
                }
            }
            catch (Exception rollbackFailure)
            {
                Services.Diagnostics.ExceptionLogWriter.Write(
                    rollbackFailure,
                    "AI provider credential rollback");
            }
        }

        /// <inheritdoc cref="IAiProviderManager.ResolveActiveProvider" />
        public AiProviderProfile? ResolveActiveProvider()
        {
            return AiActiveProviderResolver.Resolve(_store.Snapshot());
        }

        /// <inheritdoc cref="IAiProviderManager.SetActiveProviderAsync" />
        public async Task<AiProviderSaveResult> SetActiveProviderAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return AiProviderSaveResult.Fail("Provider ID 不能为空。");
            }

            var snapshot = _store.Snapshot();
            var profile = snapshot.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, providerId, StringComparison.Ordinal));
            if (profile is null)
            {
                return AiProviderSaveResult.Fail("请先保存 Provider 配置，再设为当前使用。");
            }

            if (!AiActiveProviderResolver.IsUsable(profile))
            {
                return AiProviderSaveResult.Fail(
                    "该 Provider 未启用或未设置默认模型，无法设为当前使用。");
            }

            try
            {
                await _store.SaveAsync(
                    new AiProviderConfiguration
                    {
                        Version = snapshot.Version,
                        Profiles = snapshot.Profiles,
                        ActiveProviderId = profile.Id
                    },
                    cancellationToken);
                return AiProviderSaveResult.Ok();
            }
            catch (Exception exception) when (exception is AiProviderStoreException or IOException)
            {
                return AiProviderSaveResult.Fail(exception.Message);
            }
        }

        private async Task<T> ResolveKeyAndRunAsync<T>(
            AiProviderProfile draft,
            string? plainApiKey,
            Func<string?, Task<T>> run)
        {
            var key = !string.IsNullOrWhiteSpace(plainApiKey)
                ? plainApiKey
                : await LoadCredentialForDraftAsync(draft);
            return await run(key);
        }

        private async Task<string?> LoadCredentialForDraftAsync(AiProviderProfile draft)
        {
            var existing = GetProfile(draft.Id);
            if (existing is not null
                && existing.Kind == AiProviderKind.OpenAiCompatible
                && draft.Kind == AiProviderKind.OpenAiCompatible
                && !AiProviderOrigin.Equals(existing.BaseUrl, draft.BaseUrl))
            {
                return null;
            }

            return await _credentials.LoadAsync(draft.Id);
        }

        private sealed record CredentialMutation(
            bool Changed,
            bool HadPreviousCredential,
            string? PreviousPlainText,
            string? PreviousOrigin)
        {
            public static CredentialMutation None { get; } =
                new(false, false, null, null);
        }
    }
}
