# AIGeekTuner 2.0.0 — Windows Release 说明

## 支持范围与运行方式

首个 Release 仅承诺 **Windows 10/11 x64（win-x64）**。不承诺 Windows x86、Windows ARM64、Linux 或 macOS。

发布包采用 **self-contained .NET 8 WPF**。目标机器不需要另外安装 .NET Desktop Runtime；必须完整解压 ZIP 后运行 `AIGeekTuner.exe`，不能只复制 `bin/Release` 中的 exe，也不能从 ZIP 内直接运行。

应用数据不会写入发布目录，而是写入 `%LOCALAPPDATA%\AI-GeekTuner`。其中包括设置、Provider 配置、由 Windows DPAPI 保护的 Provider 凭据、诊断历史、Sessions、导出报告和仅含异常元数据的本地日志。

## 可选依赖与降级行为

应用的静态硬件信息、导航和本地历史不依赖下列外部组件。缺少组件或权限时，对应能力应显示为未检测到、需要配置、部分可用或操作失败，不应导致应用退出。

| 能力 | 可选依赖与要求 | 缺少时的行为 |
| --- | --- | --- |
| 内置实时传感器 | LibreHardwareMonitor 0.9.6；某些主板/CPU 低层传感器还取决于 PawnIO、硬件驱动和系统权限 | 保留应用与其它来源；相关指标可能为空或该来源显示错误/不可用。不要为此关闭 Windows 安全能力 |
| AIDA64 | 用户自行安装并运行；在 `File → Preferences → Hardware Monitoring → External Applications` 启用 `WMI Sensor Values` | 未运行显示未检测到；已运行但未导出显示需要配置 |
| HWiNFO | 用户自行安装并运行 Sensors；启用 `Shared Memory Support` | 未运行显示未检测到；已运行但无共享内存显示需要配置。免费版连续共享时限由 HWiNFO 自身决定，本应用不规避 |
| AI 诊断 | 已保存并激活的 Ollama Native 或 OpenAI-compatible Provider，以及可用模型 | 应用仍可浏览本地功能；诊断/Session AI 显示未配置、连接失败、鉴权失败、模型不可用或超时 |
| Session 语音 | 可选 GPT-SoVITS HTTP 服务、有效参考音频路径和可用的 Windows 音频输出设备/权限 | 文本分析与 Session 持久化不受影响；语音生成或播放显示失败/未配置，可重试 |

PawnIO 不是应用启动前置条件。它缺失时，LibreHardwareMonitor 仍按其自身支持范围工作，但需要低层访问的传感器可能不可见。不要通过关闭驱动签名、Defender、SmartScreen、内存完整性或其它 Windows 安全能力来换取更多传感器数据。

### Intel GPU / LibreHardwareMonitor 限制

当前固定使用分离的 CPU 与 GPU LibreHardwareMonitor 实例，以避开 LibreHardwareMonitor 0.9.6 已知的 Intel GPU 组合枚举风险。Intel 集成显卡可能只有部分指标或没有实时指标；本版本不承诺完整的 Intel GPU/LHM 覆盖，也不会把缺失值伪造成有效读数。

## AI 数据发送目标

AI 请求只发送到用户当前保存并激活的 Provider `BaseUrl`：

- Ollama Native 使用其 `/api/chat`；OpenAI-compatible Provider 使用其 `/chat/completions`。
- 诊断请求可能包含用户导入/粘贴的故障日志、用户描述、已采集的硬件上下文和相关证据。
- Session AI 请求可能包含已 finalize 的 Session 遥测摘要、事件证据和分析上下文。
- 使用 `localhost` Provider 时请求留在本机；使用局域网或互联网 URL 时，上述上下文会发送到该地址，并受该服务的日志、保留和隐私政策约束。
- API Key 只用于其配置的同源 Provider 请求，使用 Windows DPAPI 本地保存，不随 Settings、Provider 或 Evidence 导出。

因此，本应用不做“所有处理完全本地”或“绝对隐私”的承诺。发送前请确认 Provider 地址及其运营方。

## Session AI SafetyGuard 与语音

Provider 返回的 Session AI 内容会先经过本地 SafetyGuard。危险电压、禁用保护机制等建议会被拦截或改写；页面展示、持久化的最终安全内容以及由其生成的 TTS 文本保持一致。已完成的 `voice.wav` 缓存在对应 `Sessions/{id}` 内，切换页面或重新打开 Session 会复用；只有删除该 Session 才清理对应语音。

## 录制持久化语义

当前语义是：**正常 Stop/Finalize 后持久化**。点击停止并完成 Finalize 后，`session.json` 与后续证据/分析文件写入 `%LOCALAPPDATA%\AI-GeekTuner\Sessions\{id}`。

当前不是 crash-safe / BSOD black-box recorder。异常断电、蓝屏、进程崩溃或强制结束进程，可能丢失当前尚未 finalize 的整个 Session；不要把它当作取证级飞行记录器。

## 可复现发布命令

在仓库根目录运行：

```powershell
dotnet test AIGeekTuner.sln -c Release
dotnet publish AIGeekTuner/AIGeekTuner.csproj -p:PublishProfile=win-x64-self-contained -o artifacts/publish/AIGeekTuner-2.0.0-win-x64
```

发布目录应包含以下交付内容：

- `AIGeekTuner.exe`
- `AIGeekTuner.dll`、`.deps.json`、`.runtimeconfig.json`
- publish 生成的 .NET/WPF 运行时与第三方依赖文件（必须整体保留）
- `KnowledgeBase/*.json`
- `README.md`
- `RELEASE.md`

发布目录不应包含 `settings.json`、`ai-providers.json`、`credentials.json`、`.env`、用户 Sessions、Logs、开发缓存、PDB、源码或个人绝对路径配置。

## 干净 Windows 验收清单

- [ ] 使用未安装 .NET Desktop Runtime 的 Windows 10/11 x64 测试机，完整解压 ZIP。
- [ ] 不关闭 Defender、SmartScreen、内存完整性或驱动签名策略；验证包来源与 SHA-256 后按组织策略运行。
- [ ] 首次启动能进入主界面，不要求 PawnIO、AIDA64、HWiNFO、Ollama、云端 Provider、GPT-SoVITS 或音频设备。
- [ ] `%LOCALAPPDATA%\AI-GeekTuner` 在需要时创建，发布目录保持只读也能运行。
- [ ] AIDA64/HWiNFO 均未安装时，来源状态明确为未检测到；其它页面仍可使用。
- [ ] 安装但未开启 AIDA64 WMI / HWiNFO Shared Memory 时，状态明确提示需要配置。
- [ ] PawnIO 不存在或传感器权限不足时，LHM 失败被隔离，应用和静态硬件信息仍可使用。
- [ ] 未配置 AI Provider 时，诊断与 Session AI 给出可操作提示；配置 Ollama 与 OpenAI-compatible Provider 后分别验证成功、超时、服务离线和鉴权失败。
- [ ] 验证 OpenAI-compatible Provider 的实际请求只到保存的 `BaseUrl`，跨源重定向不携带凭据。
- [ ] 无音频输出设备、禁止相关权限、GPT-SoVITS 离线或参考音频无效时，语音失败不影响 Session 文本与持久化。
- [ ] 开始录制后正常 Stop/Finalize，重启应用仍能打开 Session；强制结束进程的未 finalize Session 不作为可恢复承诺。
- [ ] Session AI 的页面内容、保存内容与最终 TTS 文本均为 SafetyGuard 后的版本。
- [ ] 切换页面与重启应用后复用已完成语音；生成中的旧 TTS 不覆盖新 Session；删除 Session 后对应语音被清理。
- [ ] Windows Event Log 无权限、部分通道不可用或事件过多时，Session 仍完成，UI 显示降级/截断状态。
- [ ] 检查显示缩放、窗口最小化/最大化、导航、滚轮、关闭时后台任务退出。

当前发布包未配置代码签名；是否允许运行由目标机器/组织的 Windows 策略决定。不要要求用户绕过安全策略。
