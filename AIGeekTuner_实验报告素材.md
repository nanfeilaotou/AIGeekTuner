# AIGeekTuner 实验报告素材

> 本文件依据当前仓库源码、项目配置和一次实际 dotnet test 结果整理，不是完整实验报告。没有在代码中确认的内容标记为“需人工确认”。

## 0. 扫描范围与核实结果

- 主工程：AIGeekTuner/AIGeekTuner.csproj。
- 测试工程：AIGeekTuner.Tests/AIGeekTuner.Tests.csproj。
- 主工程：271 个目标文件，34,329 行物理行。
- 测试工程：98 个目标文件，20,538 行物理行。
- 合计：370 个收录文件，54,918 行物理行；合计包含根目录 AIGeekTuner.sln。
- 实际执行命令：dotnet test。
- 实际结果：909 passed，0 failed，0 skipped；未观察到 warning/error 输出。
- 主工程依赖中没有 LangChain、向量数据库、Embedding、RAG 框架或独立 Agent 框架。

## 一、项目整体事实

### 1.1 名称、定位、用户和功能

- 项目名称：AIGeekTuner，窗口标题为 AI-GeekTuner。
- 项目类型：Windows WPF 桌面应用，主工程为 WinExe，目标框架 net8.0-windows，启用 UseWPF。
- 代码直接体现的定位：采集 Windows PC 硬件静态信息和实时 Telemetry，读取故障日志，调用本地或 OpenAI Compatible AI 服务生成结构化诊断，并提供证据、风险控制、历史、Session 分析、报告导出和可选语音摘要。
- 目标用户：代码中的系统 Prompt 明确要求生成“适合普通用户执行的诊断建议”；结合故障日志导入、硬件监视和设置页，准确表述为需要定位 PC 硬件、系统稳定性或运行异常的 Windows PC 用户。
- 核心功能：
  1. WPF 页面导航：Dashboard、Hardware、Diagnosis、Sessions、Result、History、Settings。
  2. 静态 Hardware Inventory：CPU、主板、BIOS、操作系统、内存、GPU、磁盘、显示器、音频、网络、电池。
  3. HWiNFO、AIDA64、LibreHardwareMonitor 三路 Telemetry。
  4. Canonical Model、设备 Reconciliation 和固定 Source Priority。
  5. 手动 Session 录制、统计、事件窗口和历史保存。
  6. 故障日志 + 硬件 + 系统上下文 + 知识匹配的 AI 诊断。
  7. Ollama Native 和 OpenAI Compatible Provider 管理。
  8. JSON Parser、Evidence Grounding、SafetyGuard。
  9. Windows System/Application 事件证据。
  10. Diagnosis History、Markdown 报告和 GPT-SoVITS 语音总结。

### 1.2 技术栈与依赖

| 类别 | 当前代码事实 |
|---|---|
| UI | WPF、XAML、code-behind，目标框架 net8.0-windows |
| 架构 | MVVM 风格；View 绑定 ViewModel，业务逻辑由 Services 封装，组合根位于 Views/MainWindow.xaml.cs |
| 序列化 | System.Text.Json |
| HTTP | .NET HttpClient；没有官方 OpenAI SDK |
| 硬件监控 | LibreHardwareMonitorLib 0.9.6；HWiNFO 共享内存；AIDA64 WMI |
| Windows 系统能力 | System.Management 10.0.10；System.Diagnostics.EventLog 10.0.10 |
| 凭据保护 | System.Security.Cryptography.ProtectedData 10.0.10，使用 Windows DPAPI |
| 测试 | Microsoft.NET.Test.Sdk 17.12.0、xunit 2.9.2、xunit.runner.visualstudio 2.8.2 |
| SDK 版本 | 代码只确定目标为 .NET 8 Windows；具体 SDK patch/Visual Studio 版本需人工确认 |

主工程通过 AIGeekTuner.Tests.csproj 的 ProjectReference 被测试工程引用；主工程通过 InternalsVisibleTo 允许测试工程访问少量 internal 契约。

### 1.3 目录结构

~~~text
仓库根目录/
├─ AIGeekTuner.sln
├─ AIGeekTuner/
│  ├─ App.xaml / App.xaml.cs
│  ├─ Commands/                 RelayCommand、AsyncRelayCommand
│  ├─ Configuration/            ApplicationSettings、DiagnosticConfiguration、OllamaOptions
│  ├─ KnowledgeBase/            诊断知识 JSON
│  ├─ Models/                   诊断、硬件、Telemetry、Session、Incident 模型
│  ├─ Services/
│  │  ├─ AI/                    Ollama 与 Provider/Runtime
│  │  ├─ Diagnosis/             Prompt、诊断编排、Parser、Grounding
│  │  ├─ Diagnostics/           启动面包屑、异常日志
│  │  ├─ Hardware/              Inventory、WMI、LHM 传感器
│  │  ├─ Incidents/             Windows 事件查询、映射、关联
│  │  ├─ Knowledge/             JSON 关键词知识库
│  │  ├─ Navigation/            Frame 导航
│  │  ├─ Reports/               Markdown 导出
│  │  ├─ Safety/                SafetyGuard 与规则
│  │  ├─ SessionAnalysis/       Session AI 分析
│  │  ├─ Settings/              设置与可移植配置
│  │  ├─ Storage/               数据路径、原子写入
│  │  ├─ Telemetry/             Hub、Provider、Reconciler、Recording
│  │  └─ Voice/                 GPT-SoVITS、WAV 播放和缓存
│  ├─ ViewModels/
│  ├─ Views/
│  └─ Themes/
└─ AIGeekTuner.Tests/
   ├─ Configuration/ Models/
   ├─ Services/                 AI、Diagnosis、Hardware、History、Incident、Telemetry、Voice
   ├─ TestSupport/
   ├─ ViewModels/
   └─ Views/
~~~

### 1.4 启动入口

1. App.xaml 设置 StartupUri=Views/MainWindow.xaml，并加载 Themes/BlueToolboxTheme.xaml。
2. App.OnStartup 写入 PROCESS_START、APP_STARTUP，注册 Dispatcher、AppDomain、UnobservedTaskException 和命令错误处理。
3. MainWindow 构造函数是组合根：创建 ApplicationDataPaths、Hardware Inventory、Telemetry Hub、Recorder、Diagnosis、History、Provider、Runtime、Session Analysis、Incident、Voice 和 ViewModel。
4. MainWindow 创建 FrameNavigationService，构造 MainWindowViewModel，执行 ShowDashboardCommand，最终导航到 Dashboard。

### 1.5 数据保存位置

默认根目录由 Services/Storage/ApplicationDataPaths.cs 计算为：

~~~text
%LocalAppData%\AI-GeekTuner\
├─ History\
├─ Reports\
├─ Settings\
│  ├─ settings.json
│  ├─ ai-providers.json
│  └─ credentials.json
├─ Logs\
└─ Sessions\
   └─ {sessionId}\
      ├─ session.json
      ├─ incidents.json
      ├─ analysis.json
      └─ voice.wav
~~~

AIGEEKTUNER_DATA_DIR 可将全部数据重定向到隔离目录，主要用于测试。旧配置迁移使用 %AppData%\AI-GeekTuner 下的 legacy 路径。

### 1.6 AI Provider 配置

- AiProviderConfiguration 保存 Version、Profiles、ActiveProviderId。
- AiProviderProfile 保存 Id、DisplayName、Kind、BaseUrl、Models、DefaultModelId、StructuredOutputMode、Enabled。
- API Key 不放在 AiProviderProfile；由 IAiCredentialStore 按 Provider ID 单独保存。
- WindowsDpapiCredentialStore 将凭据写入 credentials.json 的 DPAPI 保护 blob。
- AiProviderProfileStore 负责加载、校验、原子保存和内存快照整体替换。
- AiProviderManager 负责 Profile 列表、草稿校验、模型探测、连接测试、结构化输出探测、保存/删除和激活。
- AiRuntimeSnapshotSource 在每次 AI 请求开始时解析活动 Profile，并只读取一次凭据，构造不可变 AiRuntimeSnapshot。

### 1.7 Ollama、本地模型和 OpenAI Compatible

1. Ollama Native：
   - 默认地址 http://localhost:11434。
   - 模型发现使用 /api/tags。
   - 对话使用 /api/chat。
   - readiness 通过 OllamaConnectionService 检查。
   - 结构化输出映射为 Ollama 原生 format。
2. OpenAI Compatible：
   - 对话为 /v1/chat/completions 形态。
   - 模型发现使用 /models。
   - 有 Key 时发送 Authorization: Bearer，无 Key 也允许本地服务。
   - 结构化输出模式支持 OpenAiJsonSchema、JsonObject、PromptOnly。
   - LM Studio 默认预设地址为 http://127.0.0.1:1234/v1，但 Kind 仍是 OpenAiCompatible。
3. 代码没有引入 OpenAI SDK；Provider Transport 使用 HttpClient 和 System.Text.Json。

## 二、按功能模块整理架构

### 2.1 主界面、导航与 MVVM

- 功能作用：MainWindow 作为组合根，通过 Frame 导航创建页面，并把 ViewModel 设置到 DataContext。
- 关键文件：App.xaml、App.xaml.cs、Views/MainWindow.xaml、Views/MainWindow.xaml.cs、ViewModels/MainWindowViewModel.cs、Services/Navigation/FrameNavigationService.cs、AppPage.cs。
- 核心类/方法：App.OnStartup；MainWindow.MainWindow、CreatePage；MainWindowViewModel.NavigateTo；FrameNavigationService.NavigateTo、OnFrameNavigated。
- 输入：菜单命令、AppPage、页面参数和窗口生命周期事件。
- 输出：具体 Page、DataContext、当前页面名称和选中态。
- 调用关系：MainWindowViewModel.ShowDiagnosisCommand -> FrameNavigationService.NavigateTo -> MainWindow.CreatePage -> DiagnosisPage(DataContext=DiagnosisViewModel)。

### 2.2 静态硬件 Inventory

- 功能作用：独立于 HWiNFO/AIDA64/LHM 采集一次共享的硬件静态快照。
- 关键文件：Services/Hardware/Inventory/HardwareInventoryService.cs、WmiInventorySource.cs、DxgiAdapterSource.cs、CoreAudioEndpointSource.cs、GdiDisplayModeSource.cs、WindowsNetworkAdapterSource.cs，以及各 InventoryMapper。
- 核心类/方法：HardwareInventoryService.CollectAsync、CollectParallelAsync、CollectMonitors、Section、ListSection。
- 输入：WMI、DXGI、CoreAudio、GDI 显示模式、EDID、网络适配器。
- 输出：HardwareInventorySnapshot。
- 调用关系：MainWindow -> HardwareInventoryService.CollectAsync -> 并行 Source -> Mapper -> HardwareInventorySnapshot -> HardwareInfoViewModel -> Dashboard/HardwareInfoPage。
- 容错：单类采集失败只记录日志并降级为空/null，不让整体采集失败。

### 2.3 Telemetry 适配器

- HWiNFO：HwInfoTelemetryProvider、HwInfoSharedMemoryReader、HwInfoCanonicalMapper、HwInfoProcessDetector。
- AIDA64：Aida64TelemetryProvider、Aida64WmiSensorReader、Aida64CanonicalMapper、Aida64ProcessDetector。
- LibreHardwareMonitor：LibreHardwareMonitorTelemetryProvider、LibreHardwareMonitorComputerFactory、LibreHardwareMonitorCanonicalMapper。
- 公共接口：ITelemetryProvider、TelemetryProviderResult、RawTelemetryReading、TelemetryReading。
- 核心方法：三个 Provider 的 ReadSnapshotAsync，以及 Reader/CanonicalMapper 的读取和转换方法。
- 输入：共享内存、WMI sensor row、LHM computer/sensor tree、进程状态。
- 输出：TelemetryProviderResult，包含 SourceStatus、Devices、CanonicalReadings、RawReadings。
- 调用关系：TelemetryHub.ReadAsync -> ITelemetryProvider.ReadSnapshotAsync -> Reader -> CanonicalMapper -> TelemetryProviderResult。

### 2.4 统一模型、Reconciliation 和 Source Priority

- 功能作用：把源侧设备统一为 TelemetryDeviceIdentity，再选择同一设备同一指标的最高优先级值。
- 关键文件：Services/Telemetry/TelemetryHub.cs、TelemetryDeviceReconciler.cs、Models/Telemetry/TelemetryDeviceIdentity.cs、SourceDeviceInfo.cs、TelemetryMetricKey.cs。
- 核心方法：TelemetryHub.ReadAsync、ReconcileDevices、SelectCanonicalReadings、ReadIsolatedAsync；TelemetryDeviceReconciler.Reconcile、ToLookup、NormalizeName。
- 输入：各 Provider 的 Devices、CanonicalReadings、源状态。
- 输出：TelemetrySnapshot，含 canonical readings、source reports、raw readings。
- 固定优先级：HWiNFO -> AIDA64 -> LibreHardwareMonitor。
- 设备合并证据：Strong ID、跨源单例、归一名称精确相等、互为唯一包含；无法确认时保留 src:{source}:{nativeId}。
- 调用关系：TelemetryHub.ReadAsync -> Reconcile -> ToLookup -> SelectCanonicalReadings -> claimKey(canonical device + metric) -> TelemetrySnapshot。

### 2.5 Hardware Display 和实时 ViewModel

- 功能作用：将 TelemetrySnapshot 转成页面可绑定的分组、标签、单位、范围和 meter。
- 关键文件：Services/Telemetry/Presentation/HardwareLiveViewBuilder.cs、LiveMetricMeterMath.cs、LiveMetricRangeTracker.cs、MemoryTemperaturePresentation.cs、HardwareDisplayPolicy.cs、ViewModels/HardwareInfoViewModel.cs。
- 输入：TelemetrySnapshot、显示策略、范围追踪器和刷新设置。
- 输出：HardwareSensorGroupViewModel、TelemetryDebugRow、TelemetrySourceStatusViewModel。
- 调用关系：LiveTelemetryCoordinator -> TelemetryHub.ReadAsync -> Publish(snapshot) -> HardwareInfoViewModel/HardwareLiveViewBuilder -> Dashboard/HardwareInfoPage。

### 2.6 Session 录制、保存和统计

- 功能作用：使用 PeriodicTimer 串行记录 canonical readings，生命周期独立于页面。
- 关键文件：TelemetryRecordingService.cs、TelemetrySessionStore.cs、Models/Sessions/TelemetryRecordingModels.cs、ViewModels/SessionsViewModel.cs。
- 核心方法：Start、CaptureOnceAsync、StopAsync、FinalizeIfRecordingAsync、FinalizeCoreLocked、SaveIfPossible；Store.Save、LoadAll、Load、Delete。
- 输入：200–5000 ms 采样间隔、TelemetryHub、停止命令、应用关闭事件。
- 输出：TelemetryRecordingSession、TelemetrySample、session.json、TelemetrySessionSummary。
- 保护：MaxSamples=21_600；单轮失败记录 SampleGap；保存先写临时文件再移动覆盖。
- 调用关系：StartRecording -> Recorder.Start -> PeriodicTimer -> CaptureOnceAsync -> Hub.ReadAsync -> AddSample；StopAndAnalyzeAsync -> StopAsync -> Analyze -> Store.Save。

### 2.7 曲线/统计分析

- 功能作用：按 Device + Metric 聚合采样，生成覆盖率、Min/Max/Avg/P50/P95/P99 和关键事件。
- 关键文件：Services/Telemetry/Recording/TelemetrySessionAnalyzer.cs。
- 核心方法：Analyze、BuildSeries、BuildStatistic、CoveragePercent、PercentileNearestRank、DetectSignificantChanges、DetectThrottle、BuildWindows、BuildAnalysisContext。
- 输入：TelemetryRecordingSession.Samples、单位、源和时间戳。
- 输出：TelemetrySessionSummary 和压缩的 TelemetryAnalysisContext。
- 调用关系：FinalizeCoreLocked -> TelemetrySessionAnalyzer.Analyze -> session.Summary；SessionsViewModel.AnalyzeAsync -> BuildAnalysisContext -> DiagnosticEvidenceContextBuilder。

### 2.8 Windows 事件与系统证据

- 功能作用：Session 完成后查询 System/Application 事件日志，形成带 EvidenceId 的确定性事件证据。
- 关键文件：SessionIncidentCorrelationService.cs、WindowsEventLogIncidentSource.cs、WindowsEventRecordReader.cs、WindowsIncidentMapper.cs、SessionIncidentStore.cs。
- 核心方法：CaptureAsync、QueryAsync、BuildIncidents、AggregateStatus、Map、MapCategory、MapSeverity。
- 输入：已完成 Session、前后各 30 秒查询窗口、最多 500 条结果。
- 输出：SessionIncidentEnvelope、incidents.json；稳定排序、去重、incident:0001 编号。
- 调用关系：StopAndAnalyzeAsync -> CaptureIncidentsAsync -> CorrelationService.CaptureAsync -> EventLogSource.QueryAsync -> Mapper -> Store.Save。
- 语义边界：correlation 不等于 causation；Kernel-Power 41 只表示非正常关机；WHEA/DisplayDriver/ApplicationCrash 不单独推出具体硬件损坏。

### 2.9 AI Diagnosis

- 功能作用：把 FaultLog、用户描述、硬件、系统和本地知识组合成一次可取消、可追溯的结构化诊断。
- 关键文件：ViewModels/DiagnosisViewModel.cs、Services/Diagnosis/DiagnosisService.cs、Models/DiagnosticRequest.cs、DiagnosisOutcome.cs、DiagnosticResult.cs、FileReaderService.cs、WmiHardwareDetectionService.cs、SystemContextCollector.cs、DiagnosticKnowledgeService.cs。
- 核心方法：DiagnosisViewModel.StartDiagnosisAsync、CreateFaultLog、CollectSystemContextSafelyAsync、FindKnowledgeSafelyAsync；DiagnosisService.DiagnoseAsync、RunChatWithSingleRepairAsync、ParseAndValidate。
- 输入：故障日志、用户描述、硬件检测、系统上下文、关键词知识匹配、配置快照。
- 输出：DiagnosisOutcome，包括 DiagnosticResult、SafetyResult、模型和 Provider 信息；随后保存 History 并导航 ResultPage。
- 调用关系：DiagnosisPage -> DiagnosisViewModel -> Collectors -> DiagnosticRequest -> DiagnosisService -> Outcome -> LocalDiagnosisHistoryService -> ResultPage。

### 2.10 Prompt 和知识库

- 功能作用：固定系统 Prompt、JSON schema 约束、Fact/Inference/Unknown 约束、安全建议顺序和 source block。
- 关键文件：Services/Diagnosis/DiagnosisPromptBuilder.cs、Services/Knowledge/DiagnosticKnowledgeService.cs、KnowledgeBase/*.json。
- 核心方法：BuildSystemPrompt、BuildContext、PrepareFaultLogContent；FindMatchesAsync、LoadEntriesAsync。
- 输入：DiagnosticRequest、MaxFaultLogCharacters、本地 JSON 知识条目。
- 输出：DiagnosticPromptContext，含 UserMessage 和 DiagnosticEvidenceSource[]；长日志包含 wasTruncated。
- 调用关系：DiagnosisService -> BuildSystemPrompt/BuildContext -> AiChatMessage -> Runtime；Grounding Validator 使用同一个 Sources。
- 真实性质：KnowledgeBase 是 JSON 关键词 Contains 匹配，不是向量数据库、Embedding 或 RAG 框架。

### 2.11 Provider Manager、配置和凭据

- 功能作用：管理 Profile 生命周期，分离非敏感配置和 Key。
- 关键文件：AiProviderManager.cs、AiProviderProfile.cs、AiProviderKind.cs、AiProviderPresets.cs、AiProviderProfileStore.cs、AiProviderConfiguration.cs、WindowsDpapiCredentialStore.cs、AiProviderSettingsViewModel.cs。
- 核心方法：ValidateDraft、FetchModelsAsync、TestConnectionAsync、TestStructuredOutputAsync、SaveProfileAsync、SetActiveProviderAsync、DeleteProfileAsync、Snapshot、SaveAsync、Credential Store 的 Save/Load/Delete。
- 输入：Settings 草稿、协议、地址、模型、Key、结构化模式。
- 输出：模型/连接/结构化能力结果，以及 ai-providers.json 和 credentials.json。
- 调用关系：AiProviderSettingsViewModel -> AiProviderManager -> OllamaNativeClient/OpenAiCompatibleClient -> HttpClient；保存时先处理凭据，再保存配置，配置失败回滚凭据。

### 2.12 Runtime Routing、Ollama 和 OpenAI Compatible

- 功能作用：把业务层和具体协议 Transport 解耦。
- 关键文件：AiRuntimeSnapshotSource.cs、AiChatRuntime.cs、AiRuntimeModels.cs、AiChatTransportDispatcher、OllamaNativeChatTransport、OpenAiCompatibleChatTransport、OllamaNativeClient、OpenAiCompatibleClient。
- 核心方法：AiActiveProviderResolver.Resolve、TryCaptureAsync、CaptureSnapshotAsync、CheckReadinessAsync、SendChatAsync、Dispatcher.SendAsync、两个 Transport.SendAsync。
- 输入：Provider Configuration、Active Profile、Key、超时、messages、structured output intent。
- 输出：AiRuntimeSnapshot、AiChatResponse 或脱敏 AiRuntimeException。
- 调用关系：DiagnosisService -> CaptureSnapshotAsync -> SnapshotSource -> Resolve -> CheckReadinessAsync -> SendChatAsync -> Dispatcher -> Protocol Transport -> HttpClient。

### 2.13 AI Parser、Grounding 和 Safety

- Parser：DiagnosticResultParser.Parse 删除 think block，寻找平衡 JSON 对象，执行 DiagnosticResult 字段校验。
- Grounding：DiagnosticGroundingValidator.Validate 要求 Fact 有合法 SourceId，并且 SourceQuote 在本次 Prompt source.Content 中逐字符出现。
- Repair：只有文本返回成功但 Parser/Grounding 失败时，DiagnosisService 才使用同一 Runtime Snapshot 进行一次 repair；HTTP、超时、取消、认证、模型拒绝不会 repair。
- Safety：SafetyGuardService 聚合 DangerousVoltageRule、DangerousOperationRule、OvercertaintyRule，输出 Approved、ApprovedWithWarnings 或 Rejected。
- 调用关系：Transport -> Parser -> Grounding -> 一次 repair（如需要）-> SafetyGuard -> DiagnosisOutcome。

### 2.14 History、配置、报告和语音

- History/配置：LocalDiagnosisHistoryService、JsonApplicationSettingsService、ApplicationSettingsPortabilityService、AiProviderConfigurationPortabilityService、ApplicationDataPaths、AtomicFileWriter。
- 报告：MarkdownReportExportService.ExportAsync 输出 Markdown。
- Session AI：DiagnosticEvidenceContextBuilder + OllamaSessionAnalysisService + SessionAnalysisPromptBuilder + SessionAnalysisJsonParser + SessionAnalysisStore。
- GPT-SoVITS：GptSoVitsVoiceSynthesisService、SoundPlayerWavPlaybackService、SpokenSummaryCache；结果缓存为 Sessions/{id}/voice.wav。
- 关系：ResultViewModel/HistoryViewModel -> ReportExport；SessionsViewModel -> SessionAnalysis -> analysis.json -> optional voice.wav。

## 三、关键运行流程

### 3.1 流程 A：程序启动

~~~text
App.xaml StartupUri=Views/MainWindow.xaml
 -> MainWindow.MainWindow
 -> InitializeComponent
 -> ApplicationDataPaths.Default
 -> HardwareInventoryService.CollectAsync
 -> TelemetryHub(HwInfo, Aida64, LibreHardwareMonitor)
 -> TelemetrySessionStore + TelemetryRecordingService + LiveTelemetryCoordinator
 -> JsonApplicationSettingsService.Current
 -> DiagnosticConfigurationStore
 -> AiProviderProfileStore + WindowsDpapiCredentialStore
 -> AiProviderManager
 -> AiRuntimeSnapshotSource + AiChatTransportDispatcher + AiChatRuntime
 -> OllamaSessionAnalysisService + SessionIncidentCorrelationService
 -> SessionsViewModel + SafetyGuardService + DiagnosisService
 -> FrameNavigationService + MainWindowViewModel
 -> ShowDashboardCommand.Execute
 -> NavigateTo(AppPage.Dashboard)
 -> FrameNavigationService.NavigateTo
 -> MainWindow.CreatePage
 -> Dashboard(DataContext=DashboardViewModel)
~~~

### 3.2 流程 B：硬件 / Telemetry 数据采集

~~~text
HWiNFO shared memory -> HwInfoSharedMemoryReader -> HwInfoCanonicalMapper
AIDA64 WMI -> Aida64WmiSensorReader -> Aida64CanonicalMapper
LHM sensor tree -> LibreHardwareMonitorTelemetryProvider -> LHM canonical mapper
 -> TelemetryProviderResult
 -> TelemetryHub.ReadIsolatedAsync
    （每源独立 timeout 和异常隔离）
 -> TelemetryDeviceReconciler.Reconcile
    （StrongId / singleton / normalized name）
 -> TelemetryHub.SelectCanonicalReadings
    （HWiNFO > AIDA64 > LHM，按 canonical device + metric 占位）
 -> TelemetrySnapshot
 -> LiveTelemetryCoordinator / TelemetryRecordingService
 -> HardwareLiveViewBuilder / HardwareInfoViewModel
 -> Dashboard / HardwareInfoPage
~~~

### 3.3 流程 C：一次 AI 诊断

~~~text
用户故障描述/日志
 -> DiagnosisViewModel.StartDiagnosisAsync
 -> CreateFaultLog
 -> ConfigurationStore.Snapshot
 -> WmiHardwareDetectionService.DetectAsync
 -> SystemContextCollector.CollectAsync
 -> DiagnosticKnowledgeService.FindMatchesAsync
 -> DiagnosticRequest
 -> DiagnosisService.DiagnoseAsync
 -> AiChatRuntime.CaptureSnapshotAsync
 -> AiRuntimeSnapshotSource.TryCaptureAsync
 -> AiActiveProviderResolver.Resolve
 -> DiagnosisPromptBuilder.BuildSystemPrompt/BuildContext
 -> AiChatRuntime.CheckReadinessAsync
 -> AiChatRuntime.SendChatAsync
 -> AiChatTransportDispatcher
 -> OllamaNativeChatTransport 或 OpenAiCompatibleChatTransport
 -> HTTP JSON response
 -> DiagnosticResultParser.Parse
 -> DiagnosticGroundingValidator.Validate
 -> 必要时使用同一快照 repair 一次
 -> SafetyGuardService.ValidateAsync
 -> DiagnosisOutcome
 -> LocalDiagnosisHistoryService.SaveSuccessAsync
 -> ResultPage / Report Export
~~~

### 3.4 流程 D：Provider 切换 / Runtime Routing

~~~text
SettingsPage
 -> AiProviderSettingsViewModel
 -> AiProviderManager.SaveProfileAsync/SetActiveProviderAsync
 -> AiProviderProfileStore.SaveAsync
 -> ai-providers.json
 -> WindowsDpapiCredentialStore
 -> credentials.json

下一次请求：
DiagnosisService 或 OllamaSessionAnalysisService
 -> AiRuntimeSnapshotSource.TryCaptureAsync
 -> AiActiveProviderResolver.Resolve
 -> 读取活动 Profile 和 OpenAI Key
 -> AiRuntimeSnapshot
 -> AiChatRuntime.SendChatAsync
 -> AiChatTransportDispatcher
    -> OllamaNativeChatTransport -> POST /api/chat
    -> OpenAiCompatibleChatTransport -> POST /v1/chat/completions
~~~

活动 Provider 解析顺序为：合法 ActiveProviderId -> 迁移出的 ollama Profile -> 第一个合法 Enabled Profile；没有可用 Profile 时返回 null，不创建假配置。

### 3.5 流程 E：Session 录制与分析

~~~text
SessionsViewModel.StartRecording
 -> TelemetryRecordingService.Start
 -> PeriodicTimer
 -> CaptureOnceAsync
 -> TelemetryHub.ReadAsync
 -> TelemetrySample
 -> session.AddSample
 -> RecordSourceEvents/RecordMetricSourceEvents
 -> SampleCaptured -> RefreshLive

SessionsViewModel.StopAndAnalyzeAsync
 -> Recorder.StopAsync
 -> FinalizeCoreLocked
 -> TelemetrySessionAnalyzer.Analyze
 -> TelemetrySessionStore.Save -> session.json
 -> SessionIncidentCorrelationService -> incidents.json
 -> TelemetrySessionAnalyzer.BuildAnalysisContext
 -> DiagnosticEvidenceContextBuilder.Build
 -> OllamaSessionAnalysisService.AnalyzeAsync
 -> SessionAnalysisJsonParser.Parse
 -> SessionAnalysisStore.Save -> analysis.json
 -> RenderAnalysis
 -> StartVoiceGenerationIfMissing -> GPT-SoVITS -> voice.wav
~~~

## 四、代表性代码片段候选

### 4.1 Prompt Builder

- 文件：AIGeekTuner/Services/Diagnosis/DiagnosisPromptBuilder.cs
- 类/方法：DiagnosisPromptBuilder.BuildContext(DiagnosticRequest, DiagnosisInputOptions)
- 建议范围：约第 203–230 行、第 294–310 行；完整系统模板约第 49–166 行。
- 代码：

~~~csharp
var faultLogContent = PrepareFaultLogContent(
    request.FaultLog.Content,
    inputOptions.MaxFaultLogCharacters);

var sources = new List<DiagnosticEvidenceSource>();
if (!string.IsNullOrWhiteSpace(request.UserDescription))
{
    sources.Add(new DiagnosticEvidenceSource(
        DiagnosticEvidenceSourceIds.UserDescription,
        "user-description",
        "用户描述",
        request.UserDescription.Trim()));
}

sources.Add(new DiagnosticEvidenceSource(
    DiagnosticEvidenceSourceIds.FaultLog,
    "fault-log",
    "故障日志",
    faultLogContent.Content));

if (HasUsableHardwareContext(request.Hardware))
{
    sources.Add(new DiagnosticEvidenceSource(
        DiagnosticEvidenceSourceIds.HardwareContext,
        "hardware-context",
        "硬件上下文",
        JsonSerializer.Serialize(request.Hardware, ContextJsonOptions)));
}

var message = new StringBuilder("请分析以下输入：\n");
message.AppendLine(JsonSerializer.Serialize(promptContext, ContextJsonOptions));
message.AppendLine();
foreach (var source in sources)
{
    message.Append('[').Append(source.Id).AppendLine("]");
    message.AppendLine(source.Content);
    message.AppendLine();
}
return new DiagnosticPromptContext(message.ToString(), sources.ToArray());
~~~

- 作用：构造真实发送的用户消息和可供 Grounding 使用的同源 source block。
- 输入/输出：输入为 DiagnosticRequest 和字符数限制；输出为 DiagnosticPromptContext。
- 推荐理由：直接体现 Prompt Template、输入裁剪和 Evidence 绑定。

### 4.2 AI Runtime / Provider 调度

- 文件：AIGeekTuner/Services/AI/Providers/Runtime/AiRuntimeSnapshotSource.cs、AiChatRuntime.cs
- 类/方法：AiActiveProviderResolver.Resolve、AiRuntimeSnapshotSource.TryCaptureAsync、AiChatRuntime.SendChatAsync
- 建议范围：SnapshotSource 第 52–81、109–141 行；AiChatRuntime 第 50–63、111–122 行。
- 代码：

~~~csharp
public static AiProviderProfile? Resolve(AiProviderConfiguration configuration)
{
    ArgumentNullException.ThrowIfNull(configuration);
    var candidates = configuration.Profiles
        .Where(IsUsable)
        .ToArray();

    if (!string.IsNullOrWhiteSpace(configuration.ActiveProviderId))
    {
        var active = candidates.FirstOrDefault(profile =>
            string.Equals(profile.Id, configuration.ActiveProviderId,
                StringComparison.Ordinal));
        if (active is not null)
        {
            return active;
        }
    }

    var legacyMigrated = candidates.FirstOrDefault(profile =>
        string.Equals(profile.Id, AiProviderPresets.OllamaPresetId,
            StringComparison.Ordinal));
    return legacyMigrated ?? candidates.FirstOrDefault();
}

public async Task<AiRuntimeSnapshot?> TryCaptureAsync(
    int timeoutSeconds, CancellationToken cancellationToken = default)
{
    var profile = AiActiveProviderResolver.Resolve(_store.Snapshot());
    if (profile is null) return null;

    var modelId = AiProviderModelId.Normalize(profile.DefaultModelId);
    if (modelId is null) return null;

    string? apiKey = null;
    if (profile.Kind == AiProviderKind.OpenAiCompatible)
    {
        apiKey = await _credentials.LoadAsync(profile.Id, cancellationToken);
    }

    return new AiRuntimeSnapshot(
        profile.Id, profile.DisplayName, profile.Kind, profile.BaseUrl,
        modelId, profile.StructuredOutputMode, apiKey, timeoutSeconds);
}
~~~

- 作用：把活动 Profile 和一次读取的凭据冻结成请求级快照，后续由 Dispatcher 选择 Ollama 或 OpenAI Compatible Transport。
- 推荐理由：这是设置页配置与实际 HTTP 请求之间的关键桥梁。

### 4.3 Telemetry 多数据源统一

- 文件：AIGeekTuner/Services/Telemetry/TelemetryHub.cs
- 类/方法：TelemetryHub.ReadAsync、SelectCanonicalReadings、ReadIsolatedAsync
- 建议范围：第 56–93、106–155、198–230 行。
- 代码：

~~~csharp
var tasks = _providers
    .Select(provider => ReadIsolatedAsync(provider, cancellationToken))
    .ToArray();
await Task.WhenAll(tasks);

var outcomes = new Dictionary<TelemetrySourceKind, ProviderOutcome>();
for (var i = 0; i < _providers.Length; i++)
{
    outcomes[_providers[i].SourceKind] = await tasks[i];
}

var reconciliation = TelemetryDeviceReconciler.ToLookup(
    ReconcileDevices(outcomes));
var canonicalReadings =
    SelectCanonicalReadings(outcomes, reconciliation);
var sourceReports = BuildSourceReports(outcomes);

return new TelemetrySnapshot(
    DateTimeOffset.UtcNow,
    canonicalReadings,
    sourceReports,
    rawReadings);
~~~

~~~csharp
foreach (var kind in FixedPriorityOrder)
{
    if (!outcomes.TryGetValue(kind, out var outcome))
        continue;

    var sourceResult = outcome.Result;
    if (sourceResult is null || !outcome.HasUsableData)
        continue;

    foreach (var reading in sourceResult.CanonicalReadings)
    {
        if (!lookup.TryResolve(kind, reading.Device.DeviceKey,
            out var canonical))
        {
            canonical = reading.Device;
        }

        var claimKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)canonical.Kind}\u001F{canonical.DeviceKey}\u001F{reading.MetricKey.Value}");
        if (claimedKeys.Add(claimKey))
        {
            selected.Add(new TelemetryReading(
                reading.MetricKey, reading.Value, reading.Unit, canonical,
                reading.Source, reading.SourceMetricId,
                reading.SourceLabel, reading.CapturedAtUtc));
        }
    }
}
~~~

- 作用：并行读取、设备归一化、固定优先级占位选择，不平均不同来源数值。
- 推荐理由：适合配合 Telemetry 数据流图说明 Adapter -> Canonical Model -> Reconciliation -> Priority -> UI。

### 4.4 AI 返回结果解析

- 文件：AIGeekTuner/Services/Diagnosis/DiagnosticResultParser.cs
- 类/方法：DiagnosticResultParser.Parse、ExtractFirstJsonObject
- 建议范围：第 12–39、41–71 行。
- 代码：

~~~csharp
public DiagnosticResult Parse(string modelResponse)
{
    if (string.IsNullOrWhiteSpace(modelResponse))
    {
        throw new DiagnosticResultParsingException("模型响应为空。");
    }

    var responseWithoutThinking = ThinkBlockRegex().Replace(
        modelResponse, string.Empty);
    var json = ExtractFirstJsonObject(responseWithoutThinking);

    try
    {
        var result = JsonSerializer.Deserialize<DiagnosticResult>(
            json, JsonOptions)
        ?? throw new DiagnosticResultParsingException(
            "模型 JSON 没有产生诊断对象。");

        Validate(result);
        return result;
    }
    catch (JsonException exception)
    {
        throw new DiagnosticResultParsingException(
            "模型返回的诊断 JSON 格式错误或缺少必需字段。",
            exception);
    }
}

private static string ExtractFirstJsonObject(string response)
{
    for (var startIndex = 0; startIndex < response.Length; startIndex++)
    {
        if (response[startIndex] != '{'
            || !TryFindObjectEnd(response, startIndex, out var endIndex))
            continue;

        var candidate = response[startIndex..(endIndex + 1)];
        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return candidate;
        }
        catch (JsonException) { }

        startIndex = endIndex;
    }

    throw new DiagnosticResultParsingException(
        "模型响应中未找到有效的 JSON 对象。");
}
~~~

- 作用：去除 think 内容，从可能带有前后文本的响应中提取平衡 JSON，并执行字段校验。
- 推荐理由：说明结构化输出后仍需要本地 Parser，不能把模型文本直接当成可信对象。

### 4.5 Evidence Grounding

- 文件：AIGeekTuner/Services/Diagnosis/DiagnosticGroundingValidator.cs
- 类/方法：DiagnosticGroundingValidator.Validate
- 建议范围：第 11–75 行。
- 代码：

~~~csharp
public bool Validate(
    DiagnosticResult result,
    DiagnosticPromptContext context)
{
    ArgumentNullException.ThrowIfNull(result);
    ArgumentNullException.ThrowIfNull(context);

    if (result.Evidence is null)
        throw new DiagnosticGroundingValidationException(["Evidence 为空"]);
    if (context.Sources is null)
        throw new DiagnosticGroundingValidationException(["Sources 为空"]);

    var errors = new List<string>();
    for (var index = 0; index < result.Evidence.Count; index++)
    {
        var evidence = result.Evidence[index];
        if (evidence is null)
        {
            errors.Add($"Fact[{index}] Evidence 为空");
            continue;
        }

        if (evidence.Kind != EvidenceKind.Fact)
            continue;
        if (string.IsNullOrWhiteSpace(evidence.SourceId))
        {
            errors.Add($"Fact[{index}] 缺少 SourceId");
            continue;
        }

        var source = context.FindSource(evidence.SourceId);
        if (source is null)
        {
            errors.Add($"Fact[{index}] Invalid SourceId");
            continue;
        }

        var quote = evidence.SourceQuote?.Trim();
        if (string.IsNullOrWhiteSpace(quote)
            || !source.Content.Contains(quote, StringComparison.Ordinal))
            errors.Add($"Fact[{index}] Quote not found in {source.Id}");
    }

    if (errors.Count > 0)
        throw new DiagnosticGroundingValidationException(errors);
    return true;
}
~~~

- 作用：Fact 必须能在本次 source snapshot 中逐字符找到 SourceQuote；Inference 不伪装为 Fact。
- 推荐理由：是本项目防止模型凭空给出事实结论的核心实现。

### 4.6 Safety

- 文件：AIGeekTuner/Services/Safety/SafetyGuardService.cs、Services/Safety/Rules/*.cs
- 类/方法：SafetyGuardService.ValidateAsync、DangerousVoltageRule.Evaluate
- 建议范围：SafetyGuardService 第 27–71 行；DangerousVoltageRule 第 33–57 行。
- 代码：

~~~csharp
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

return Task.FromResult(new SafetyResult
{
    Status = status,
    Warnings = messages
});
~~~

~~~csharp
foreach (Match match in _voltageRegex.Matches(recommendationText))
{
    var normalizedValue = match.Groups["value"].Value.Replace(',', '.');
    if (!decimal.TryParse(normalizedValue,
        NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture, out var volts))
        continue;

    if (volts > _options.RejectVoltageAboveVolts
        || volts <= _options.RejectVoltageAtOrBelowVolts)
    {
        var parameter = match.Groups["parameter"].Value;
        return SafetyRuleResult.Reject(
            $"检测到明显异常的硬件电压建议：{parameter} {volts}V。");
    }
}
return SafetyRuleResult.Pass;
~~~

- 作用：聚合危险操作、电压范围和过度确定性规则。
- 推荐理由：体现 AI 输出后的独立风险控制层，而非把安全性完全交给 Prompt。

### 4.7 Session / Statistics

- 文件：AIGeekTuner/Services/Telemetry/Recording/TelemetrySessionAnalyzer.cs
- 类/方法：TelemetrySessionAnalyzer.BuildStatistic、CoveragePercent、PercentileNearestRank
- 建议范围：第 104–141 行。
- 代码：

~~~csharp
internal static MetricSeriesStatistic BuildStatistic(
    SeriesItems items, int totalSamples)
{
    var values = items.Points
        .Select(point => point.Value)
        .OrderBy(value => value)
        .ToArray();
    var average = values.Length > 0 ? values.Average() : 0;

    return new MetricSeriesStatistic(
        items.Device.DeviceKey,
        items.Device.DisplayName,
        items.MetricKey.Value,
        items.Unit.ToString(),
        values.Length,
        CoveragePercent(values.Length, totalSamples),
        values.Length > 0 ? values[0] : 0,
        values.Length > 0 ? values[^1] : 0,
        average,
        values.Length > 0 ? PercentileNearestRank(values, 0.50) : 0,
        values.Length > 0 ? PercentileNearestRank(values, 0.95) : 0,
        values.Length > 0 ? PercentileNearestRank(values, 0.99) : 0,
        items.Points.Count > 0 ? items.Points[0].AtUtc : default,
        items.Points.Count > 0 ? items.Points[^1].AtUtc : default);
}

public static double CoveragePercent(int presentSamples, int totalSamples) =>
    totalSamples <= 0 ? 0 : Math.Min(100d,
        presentSamples * 100d / totalSamples);

public static double PercentileNearestRank(
    IReadOnlyList<double> orderedAscending, double percentile)
{
    var rank = (int)Math.Ceiling(percentile * orderedAscending.Count);
    rank = Math.Clamp(rank, 1, orderedAscending.Count);
    return orderedAscending[rank - 1];
}
~~~

- 作用：生成 Coverage、Min、Max、Avg、P50、P95、P99，并供 UI 和 AI 上下文使用。
- 推荐理由：展示原始采样到结构化统计的关键转换。

## 五、课程要求适配分析

| 课程项 | 当前项目判断 | 真实依据和建议 |
|---|---|---|
| 提示词模板使用 | 已真实实现 | DiagnosisPromptBuilder 和 SessionAnalysisPromptBuilder 有固定 Prompt、JSON 结构、证据引用和安全约束。 |
| LangChain 及 Chain | 不应声称实现 | 无 LangChain 依赖；项目使用自研 C# Service/Runtime/Transport 编排。 |
| 向量数据库 | 不应声称实现 | 无向量库、Embedding 或相似度索引；知识库是 JSON 关键词 Contains 匹配。 |
| RAG 框架部署或 Agent 应用 | 可以合理对应，但需限定 | 有本地知识条目和运行时证据注入，但不是标准向量 RAG；有受约束编排但不是自主 Agent。 |
| 前后端框架应用部署 | 不应声称传统 Web 前后端 | 项目是 WPF 桌面客户端；不存在传统 Web Backend。 |

### 5.1 推荐的三项

1. 提示词模板使用：展示 DiagnosisPromptBuilder 和 SessionAnalysisPromptBuilder。
2. 证据增强的 AI 应用：准确写成“本地 JSON 知识匹配 + 运行时证据注入 + Grounding”，不要写成向量 RAG。
3. 受约束的 AI 服务编排：展示 Provider Routing、Parser、一次 repair、Safety；明确不是自主 Agent。

若课程对 Agent 有严格定义，第三项改写为“WPF 桌面客户端架构与外部 Provider 接入”，不要声称传统前后端。

### 5.2 Agent 术语建议

不建议写“系统基于 Agent 框架，能够自主规划并调用工具完成诊断”。

建议写：

> 系统实现了一个受约束的 AI 诊断服务编排流程：先确定性采集硬件、系统和日志证据，再构造结构化 Prompt，调用可切换的 AI Provider，进行结果解析、证据绑定和安全审查。该流程具备任务编排和一次受限修复，但不属于具有自主规划、工具循环和长期目标的通用 Agent。

### 5.3 WPF/MVVM/Service/Model/Provider 的描述

- WPF：Windows 桌面窗口、XAML 布局、页面、绑定和 UI 线程。
- View：Views/*.xaml 和 code-behind，例如 DiagnosisPage、SessionsPage、SettingsPage、MainWindow。
- ViewModel：ViewModels/*.cs，暴露属性、命令和 UI 状态。
- Service：封装硬件、Telemetry、AI、配置、历史、报告、事件日志和语音能力。
- Model：Models/*.cs 中的 Request、Outcome、Telemetry、Session、Incident、Safety 数据契约。
- Provider：外部数据或 AI 协议适配器；Telemetry Provider 连接 HWiNFO/AIDA64/LHM，AI Provider 表示 Ollama Native/OpenAI Compatible。
- Backend：没有传统 Web Backend；HttpClient 访问的是用户配置的外部 AI/语音服务，调用仍在 WPF 客户端进程中。

## 六、测试体系

### 6.1 框架和执行结果

- 测试框架：xUnit 2.9.2。
- 运行器：Microsoft.NET.Test.Sdk 17.12.0、xunit.runner.visualstudio 2.8.2。
- 测试工程：AIGeekTuner.Tests/AIGeekTuner.Tests.csproj。
- 实际命令：dotnet test。
- 结果：总计 909，passed 909，failed 0，skipped 0。
- 构建目标：net8.0-windows。

### 6.2 覆盖模块

- Configuration：DiagnosticConfiguration、ApplicationSettings、Provider 配置。
- Models：TelemetryIdentity、TelemetryMetricCatalog。
- Services/AI：Ollama readiness、JSON/repair、Provider Manager、Profile Store、OpenAI Compatible、Runtime、DPAPI。
- Services/Diagnosis：Prompt、Parser、Grounding、失败策略、Runtime Routing。
- Services/Hardware/Inventory：音频、网络、电池、EDID、显示策略、Inventory、placeholder、presentation。
- Services/History：本地历史保存、加载和损坏恢复。
- Services/Incidents：Session 关联、Windows Event Log source/mapper/store。
- Services/Reports：Grounded evidence presentation。
- Services/Safety：危险操作、电压、过度确定性、规则聚合。
- Services/SessionAnalysis：上下文、AI grounding、统计分析、Runtime Routing。
- Services/Settings：可移植配置、JSON 设置、自动保存。
- Services/Telemetry：三种 Provider、mapping、Hub fallback、reconciliation、单位、实时显示、录制和统计。
- Services/Voice：GPT-SoVITS、缓存和播放。
- ViewModels/Views：Provider 设置、Diagnosis ownership、Sessions presentation、导航、Page construction、Window chrome。

### 6.3 测试类型和辅助设施

- Unit Test：Mapper、Parser、Reconciler、统计函数、Safety Rule、配置 Validator。
- Integration-like Test：使用自定义 HttpMessageHandler 验证 URL、请求体、重试、超时和响应解析，不依赖真实联网。
- UI/ViewModel Test：命令状态、DataContext、页面构造、导航和窗口行为 smoke test；不等于截图式端到端测试。
- 临时文件系统：TempDirectory 创建 %TEMP%\aigt-tests-{guid}，测试结束 best-effort 清理。
- HistoryTestFactory：用临时路径创建 ApplicationDataPaths，并生成最小合法 DiagnosisOutcome。
- ProviderTestSupport：ScriptedProviderStore 和 FakeAiCredentialStore，可断言配置保存和凭据读取次数。
- StubOllamaServer：HttpMessageHandler，按脚本返回文本、状态、延迟、异常或 gate 响应，记录 URL/请求体，提供合法诊断 JSON 和 /api/tags 数据。
- StubAiHttpHandler：Provider Runtime 的 HTTP 替身。
- TestWav：生成最小 RIFF/WAVE 头，供语音缓存和 WAV 校验测试。

## 七、安装与运行

### 7.1 开发环境

代码能确定的要求：

- Windows；目标为 net8.0-windows，并使用 WPF、WMI、Windows Event Log、DPAPI 和 Windows 音频/显示能力。
- .NET 8 Windows SDK/运行时。
- Visual Studio 或 .NET 8 SDK 命令行环境；具体版本需人工确认。
- Ollama/OpenAI Compatible/GPT-SoVITS 是否需要运行取决于用户选择的功能。

### 7.2 命令

~~~powershell
dotnet restore AIGeekTuner.sln
dotnet build AIGeekTuner.sln
dotnet test AIGeekTuner.sln
dotnet run --project AIGeekTuner/AIGeekTuner.csproj
dotnet publish AIGeekTuner/AIGeekTuner.csproj -c Release
~~~

已执行 `dotnet test AIGeekTuner.sln -c Release --no-restore` 并通过 909 项。发布使用 `AIGeekTuner/Properties/PublishProfiles/win-x64-self-contained.pubxml`，目标为 self-contained win-x64；ZIP 为本次 RC 验证产物。

### 7.3 首次使用配置

| 项目 | 必需/可选 | 当前代码依据 |
|---|---|---|
| AI Provider Profile | 使用 AI 诊断时必需 | Settings 中创建/激活 Profile，配置 BaseUrl 和默认模型。 |
| Ollama | 可选外部工具 | 选择 OllamaNative 时需要运行 Ollama；默认 http://localhost:11434，默认模型 qwen3:8b。 |
| OpenAI Compatible | 可选外部服务 | 可配置 LM Studio 或其他兼容端点；Key 单独 DPAPI 保存。 |
| HWiNFO | 可选外部工具 | 需要其共享内存可用；版本和权限需人工确认。 |
| AIDA64 | 可选外部工具 | 需要其 WMI 数据可用；版本和设置需人工确认。 |
| LibreHardwareMonitor | 可选能力 | 主工程引用 LibreHardwareMonitorLib 0.9.6；实际传感器支持取决于系统。 |
| GPT-SoVITS | 可选外部服务 | 需要 Endpoint、参考音频、PromptLang、SpeedFactor 和可选权重路径。 |
| 故障日志 | 诊断时必需输入 | 可粘贴或选文件；空日志会被拒绝。 |

## 八、流程图素材

### 8.1 系统整体架构

~~~mermaid
flowchart LR
    App[App.xaml / App.OnStartup] --> Window[MainWindow 组合根]
    Window --> Nav[FrameNavigationService]
    Nav --> Views[Views/*.xaml]
    Views --> VMs[ViewModels/*.cs]
    Window --> Inv[HardwareInventoryService]
    Window --> Hub[TelemetryHub]
    Window --> Diagnosis[DiagnosisService]
    Window --> SessionVM[SessionsViewModel]
    Window --> Provider[AiProviderManager]
    Hub --> Providers[HwInfo / Aida64 / LHM Providers]
    Diagnosis --> Runtime[AiChatRuntime]
    Runtime --> Transport[Protocol Transports]
    SessionVM --> Recorder[TelemetryRecordingService]
    Recorder --> Store[TelemetrySessionStore]
    Services[Other Services] --> Data[(Local AppData JSON)]
~~~

### 8.2 Telemetry 数据流

~~~mermaid
flowchart TD
    H[HwInfoSharedMemoryReader] --> HP[HwInfoTelemetryProvider]
    A[Aida64WmiSensorReader] --> AP[Aida64TelemetryProvider]
    L[LibreHardwareMonitor tree] --> LP[LibreHardwareMonitorTelemetryProvider]
    HP --> R[TelemetryProviderResult]
    AP --> R
    LP --> R
    R --> Hub[TelemetryHub.ReadAsync]
    Hub --> Recon[TelemetryDeviceReconciler.Reconcile]
    Recon --> Lookup[DeviceReconciliationLookup]
    Hub --> Select[SelectCanonicalReadings]
    Lookup --> Select
    Select --> Snapshot[TelemetrySnapshot]
    Snapshot --> Live[LiveTelemetryCoordinator]
    Snapshot --> Record[TelemetryRecordingService]
    Live --> VM[HardwareInfoViewModel]
    VM --> UI[Dashboard / HardwareInfoPage]
~~~

### 8.3 AI Diagnosis 数据流

~~~mermaid
flowchart TD
    Input[User description + FaultLog] --> DVM[DiagnosisViewModel.StartDiagnosisAsync]
    DVM --> Collect[Hardware/System/Knowledge collection]
    Collect --> Req[DiagnosticRequest]
    Req --> DS[DiagnosisService.DiagnoseAsync]
    DS --> PB[DiagnosisPromptBuilder]
    PB --> Source[DiagnosticEvidenceSource blocks]
    DS --> RT[AiChatRuntime]
    RT --> HTTP[Ollama or OpenAI Compatible Transport]
    HTTP --> Parser[DiagnosticResultParser]
    Parser --> Ground[DiagnosticGroundingValidator]
    Ground --> Safety[SafetyGuardService]
    Safety --> Outcome[DiagnosisOutcome]
    Outcome --> History[LocalDiagnosisHistoryService]
    Outcome --> Result[ResultPage / Report Export]
~~~

### 8.4 AI Provider Runtime Routing

~~~mermaid
flowchart TD
    Settings[SettingsPage] --> VM[AiProviderSettingsViewModel]
    VM --> Manager[AiProviderManager]
    Manager --> ProfileStore[AiProviderProfileStore]
    Manager --> Cred[WindowsDpapiCredentialStore]
    ProfileStore --> Config[ai-providers.json]
    Cred --> CredentialFile[credentials.json]
    Request[Diagnosis or Session Analysis] --> Snapshot[AiRuntimeSnapshotSource]
    Snapshot --> Resolve[AiActiveProviderResolver.Resolve]
    Resolve --> Runtime[AiRuntimeSnapshot]
    Runtime --> Dispatcher[AiChatTransportDispatcher]
    Dispatcher --> Ollama[OllamaNativeChatTransport /api/chat]
    Dispatcher --> OpenAI[OpenAiCompatibleChatTransport /v1/chat/completions]
~~~

### 8.5 Session Recording

~~~mermaid
flowchart TD
    Start[SessionsViewModel.StartRecording] --> Rec[TelemetryRecordingService.Start]
    Rec --> Timer[PeriodicTimer]
    Timer --> Capture[CaptureOnceAsync]
    Capture --> Hub[TelemetryHub.ReadAsync]
    Hub --> Sample[TelemetrySample canonical readings]
    Sample --> Event[Source/Metric change events]
    Sample --> Session[TelemetryRecordingSession]
    Stop[StopAndAnalyzeAsync] --> Finalize[StopAsync + FinalizeCoreLocked]
    Finalize --> Analyzer[TelemetrySessionAnalyzer.Analyze]
    Analyzer --> Json[TelemetrySessionStore -> session.json]
    Finalize --> Incidents[SessionIncidentCorrelationService -> incidents.json]
    Json --> Context[BuildAnalysisContext / EvidenceContextBuilder]
    Incidents --> Context
    Context --> AI[OllamaSessionAnalysisService]
    AI --> Analysis[SessionAnalysisStore -> analysis.json]
    Analysis --> Voice[GptSoVitsVoiceSynthesisService -> voice.wav]
~~~

### 8.6 AI Diagnosis 中文伪代码

~~~text
函数 Diagnose(请求, 配置快照):
    校验请求、故障日志和硬件上下文
    runtime = 捕获活动 Provider 的不可变快照
    如果没有 runtime：返回未配置

    如果 runtime 是 Ollama Native：
        readiness = 执行 Ollama readiness 检查
        如果未 Ready：返回 AI 不可用

    systemPrompt = BuildSystemPrompt()
    promptContext = BuildContext(请求, 配置快照.Input)
    messages = [systemPrompt, promptContext.UserMessage]

    response = runtime.SendChat(messages, JsonObject, think=false, temperature=0.2)
    尝试：
        result = DiagnosticResultParser.Parse(response)
        DiagnosticGroundingValidator.Validate(result, promptContext)
    如果 Parser 或 Grounding 失败：
        repair = 根据错误创建修复指令
        response = 使用同一 runtime 快照再次请求
        result = ParseAndValidate(response, promptContext)
    如果第二次失败：返回诊断结果无效

    safety = SafetyGuardService.ValidateAsync(result)
    如果 safety 为 Pending 或检查失败：返回 Safety 失败

    outcome = DiagnosisOutcome(request, result, safety, runtime 元数据)
    如果 AutoSaveDiagnosisHistory：保存成功历史
    返回 outcome
~~~

## 九、实验报告推荐结构

### 第 1 章：项目背景与需求分析

写项目定位、目标用户和问题背景：故障日志难读、硬件信息分散、多监控源不一致、AI 结论需要证据和安全控制。说明系统边界是桌面客户端 + 本地采集 + 外部 AI/语音服务，不是 Web 前后端。

### 第 2 章：系统总体设计与功能模块

建议拆为：

1. WPF + MVVM + Frame 导航。
2. 静态 Hardware Inventory。
3. HWiNFO/AIDA64/LHM Telemetry Provider 与统一模型。
4. Reconciliation 和固定 Source Priority。
5. Session 录制、统计和事件窗口。
6. Provider 配置与 Runtime Routing。
7. AI Diagnosis、Prompt、Parser、Grounding、Safety。
8. History、Markdown 报告和 GPT-SoVITS。

### 第 3 章：系统实现

建议重点选择：

1. 提示词模板使用：DiagnosisPromptBuilder、SessionAnalysisPromptBuilder。
2. 证据增强的 AI 应用：准确写成“本地 JSON 知识匹配 + 运行时证据注入 + Grounding”，不冒充向量 RAG。
3. 受约束的 AI 服务编排：Provider Routing、Parser、一次 repair、Safety；明确不是自主 Agent。

如果课程严格要求自主 Agent，第三项应改成“WPF 桌面客户端架构与外部 Provider 接入”，不要声称传统前后端。

### 第 4 章：测试设计与结果

介绍 xUnit、测试工程和 TestSupport；按 Provider/配置、Telemetry、Diagnosis、Session/Incident、History、ViewModel/View、Voice 分组；重点说明 StubOllamaServer、StubAiHttpHandler、TempDirectory、ProviderTestSupport、HistoryTestFactory、TestWav。给出真实结果：909 passed，0 failed，0 skipped。不要夸大为真实硬件全环境覆盖。

### 第 5 章：安装、配置与运行

按 Windows + .NET 8 WPF 环境、restore/build/test/run/publish、Provider/模型、Ollama/OpenAI Compatible、HWiNFO/AIDA64、GPT-SoVITS、数据目录和 DPAPI 凭据组织。外部工具版本、Visual Studio 版本和发布模式标记为需人工确认。

### 第 6 章：项目总结

可以总结以下真实亮点：

- 多数据源经设备 Reconciliation 和固定优先级选择，而非简单拼接或平均。
- Provider Profile、活动 Provider、请求快照和 Transport 分层，支持 Ollama 与 OpenAI Compatible。
- Prompt 明确区分 Fact、Inference、Unknown，并创建 source block。
- Grounding Validator 逐字符验证 Fact 引用。
- Parser、一次 repair 和 SafetyGuard 形成 AI 输出本地防线。
- Session 保留 canonical 数据、来源切换和统计；Windows 事件明确 correlation 不等于 causation。
- 本地历史、原子保存、坏文件隔离和临时目录测试体现桌面应用可靠性。
- 当前自动化测试 909 项全部通过。
