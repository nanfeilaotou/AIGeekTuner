namespace AIGeekTuner.Models.Telemetry
{
    /// <summary>数据源状态模型；刻意保持少量、语义明确。</summary>
    public enum TelemetrySourceStatus
    {
        /// <summary>本次读取成功。</summary>
        Ready = 0,

        /// <summary>来源不存在或当前不可用（未安装 / 未运行 / 接口缺失）。</summary>
        Unavailable = 1,

        /// <summary>来源存在但需要用户手动启用数据导出。</summary>
        NeedsConfiguration = 2,

        /// <summary>部分成功：返回了数据但可信度受限。</summary>
        Degraded = 3,

        /// <summary>读取失败；技术细节只进日志。</summary>
        Error = 4,

        /// <summary>调用方等待期限已到；底层读取可能仍在结束过程中。</summary>
        Timeout = 5,

        /// <summary>同一来源已有真实读取在进行，本轮未启动第二个读取。</summary>
        Busy = 6
    }
}
