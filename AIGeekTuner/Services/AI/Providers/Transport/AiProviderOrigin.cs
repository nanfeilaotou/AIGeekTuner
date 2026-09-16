namespace AIGeekTuner.Services.AI.Providers.Transport
{
    /// <summary>Provider endpoint 的规范化 origin：scheme/host/effective-port。</summary>
    public static class AiProviderOrigin
    {
        public static bool TryNormalize(string? rawBaseUrl, out string origin)
        {
            origin = string.Empty;
            if (!AiEndpointUriBuilder.TryCreateRoot(rawBaseUrl, out var uri))
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();
            if (host.IndexOf(':') >= 0 && (host.Length == 0 || host[0] != '['))
            {
                host = "[" + host + "]";
            }

            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
            origin = uri.Scheme.ToLowerInvariant() + "://" + host + port;
            return true;
        }

        public static bool Equals(string? leftBaseUrl, string? rightBaseUrl)
        {
            return TryNormalize(leftBaseUrl, out var left)
                && TryNormalize(rightBaseUrl, out var right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
