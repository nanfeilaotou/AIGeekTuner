namespace AIGeekTuner.Services.AI.Providers.Credentials
{
    /// <summary>Marker for stores that persist non-secret credential origin metadata.</summary>
    public interface IAiCredentialOriginStore
    {
    }

    /// <summary>
    /// 按 Provider 稳定 Id 存取密钥。实现必须保证：
    /// 磁盘上只有受保护 blob（绝无明文）；missing 返回 null；
    /// 损坏只留痕并视为不可用，绝不允许把应用启动拖垮。
    /// </summary>
    public interface IAiCredentialStore
    {
        /// <summary>写入（或替换）一个 Provider 的密钥。plainTextSecret 不能为空。</summary>
        Task SaveAsync(string providerId, string plainTextSecret, CancellationToken cancellationToken = default);

        /// <summary>
        /// 写入凭据并记录其规范化目标 origin。origin 是非敏感元数据，
        /// 不进入 Provider 普通配置；旧实现可通过默认实现保持兼容。
        /// </summary>
        Task SaveAsync(
            string providerId,
            string plainTextSecret,
            string? origin,
            CancellationToken cancellationToken = default) =>
            SaveAsync(providerId, plainTextSecret, cancellationToken);

        /// <summary>读取密钥明文；不存在或不可解密时返回 null。</summary>
        Task<string?> LoadAsync(string providerId, CancellationToken cancellationToken = default);

        /// <summary>读取凭据绑定的规范化 origin；旧凭据没有绑定时返回 null。</summary>
        Task<string?> LoadOriginAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        /// <summary>删除一个 Provider 的密钥；本来就不存在时是成功 no-op。</summary>
        Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
    }
}
