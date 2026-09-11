# AudioCpp.NET：.NET 10 封装架构与完整实施计划

## 1. 文档状态

- 状态：已批准并进入开发
- 项目目录：`D:\SouceCode\python2net\audio\audiocpp-dotnet`
- 参考项目：`D:\SouceCode\python2net\audio\audiocpp-go`
- 上游源码：`D:\SouceCode\python2net\audio\audio.cpp`
- 固定上游提交：`78d47706c30ef215ba9ad3559baff309efeb5260`
- 上游远端：`https://github.com/0xShug0/audio.cpp`
- 目标框架：`.NET 10 (net10.0)`
- 初始 shim ABI：`1.0`

固定版本以 `eng/upstream.lock.json` 为唯一机器可读事实来源。任何上游升级都必须先生成差异报告、完成评审和测试，再显式更新锁文件；禁止在构建中隐式跟踪 `main`。

## 2. 调研结论

`audio.cpp` 是 C++17/CMake 工程，核心运行时产物是静态库 `engine_runtime`，没有稳定的官方 C API 或适合 P/Invoke 的共享库。其内部运行时已经抽象了 model、task session、离线和流式执行，但这些接口使用 C++ 类、STL、RTTI 和异常，不能直接跨越 .NET ABI。

`audiocpp-go` 验证了正确的基础路线：用很小的 `extern "C"` shim 静态链接上游及依赖，隐藏所有第三方符号，语言层动态加载并复制 native-owned PCM。然而它目前只导出五个 TTS 相关函数，不能表达 ASR、VAD、diarization、alignment、source separation 以及 streaming。

因此本项目复用 Go 版的构建、隔离和发布原则，但采用可扩展的 `runtime → model → session` 三层句柄以及显式 ABI/capability 协商。

## 3. 目标与非目标

### 3.1 目标

1. 提供安全、符合 .NET 习惯且可用于 NativeAOT 演进的 .NET 10 API。
2. 原生边界只使用 C ABI、POD、UTF-8、指针和长度，不暴露任何 C++ ABI。
3. 第一阶段稳定支持离线 TTS、voice design 和 voice clone。
4. 后续分批支持通用离线任务、结构化结果和流式任务。
5. 以 NuGet RID/runtime 包交付自包含原生库。
6. 将上游 commit、shim ABI、托管版本、构建选项和 artifact hash 作为兼容单元。
7. 上游升级必须根据差异分类决定动作，不做盲目自动升级。

### 3.2 首版非目标

- 不承诺覆盖所有上游模型和任务。
- 不把模型文件打入 NuGet。
- 不直接包装模型私有 C++ 类。
- 不使用 C++/CLI、SWIG 或直接调用 mangled C++ symbols。
- 不在首版提供 native buffer 零复制生命周期。
- 不在没有真实 GPU runner 时宣称 GPU E2E 已验证。
- `CancellationToken` 在上游没有中断钩子时只取消托管等待，不虚假承诺立即中止计算。

## 4. 总体架构

```text
Application
    │ public .NET API
    ▼
AudioCpp.NET
    │ runtime/model/session, typed requests/results
    ▼
AudioCpp.NET.Interop
    │ LibraryImport + NativeLibrary + SafeHandle + ABI validation
    ▼
audiocpp_dotnet_native
    │ versioned extern "C" ABI; exception and ownership boundary
    ▼
audio.cpp engine_runtime + ggml + model implementations
```

### 4.1 原则

- C++ 异常必须在 shim 内全部捕获。
- native 分配的内存只能由 native 配套函数释放。
- 托管调用者不持有上游 STL 对象或裸模型指针。
- 已发布 C 函数签名不修改；新增能力只追加符号或升级 ABI major。
- 高频 PCM 使用 `float* + count`，结构化元数据使用版本化 JSON；不得用 Base64 传递大块 PCM。
- 首版结果复制到 managed memory 后立即释放 native buffer，以正确性和生命周期安全为先。

## 5. ABI v1

### 5.1 版本协商

原生库提供 `audiocpp_get_abi_info`，返回：

- `struct_size`
- `abi_major` / `abi_minor`
- shim version
- 固定的 audio.cpp 完整 commit
- 编译 backend
- capability 位掩码

托管层必须在创建任何模型前验证 ABI major。ABI minor 采用向后兼容规则：托管层只使用自己已知且 capability 声明存在的函数。

### 5.2 句柄

| 句柄 | 所有权 | 职责 |
|---|---|---|
| `audiocpp_runtime` | `SafeRuntimeHandle` | 运行时、注册表和构建信息 |
| `audiocpp_model` | `SafeModelHandle` | 已加载模型和 backend 配置 |
| `audiocpp_session` | `SafeSessionHandle` | task、mode、prepare/run/stream 状态 |

首批实现 runtime/model 及 TTS 便利调用；session 的公开通用执行在离线任务阶段完成。ABI 从一开始保留三层结构，避免 TTS API 固化未来设计。

### 5.3 错误和内存

- 函数返回稳定的粗粒度状态码；详细信息写入 caller-provided UTF-8 buffer。
- 所有输入都检查 null、空值、长度和数值边界。
- PCM 输出由 `malloc` 创建，只能通过 `audiocpp_buffer_free` 释放。
- 销毁函数接受 null；托管层通过 `SafeHandle` 保证正常路径最多释放一次。

## 6. 托管 API

首批类型：

- `AudioCppRuntime`
- `AudioCppModel`
- `AudioCppRuntimeOptions`
- `AudioCppModelOptions`
- `TtsRequest`
- `AudioBuffer`
- `AudioCppException` 及 ABI/load/inference 子类型
- `AudioCppBuildInfo`

并发约束：单个 model 首版串行调用，防止 synthesis 与 dispose 竞争。后续根据上游模型能力引入多 session 和受控 session pool，不默认承诺模型线程安全。

## 7. 原生构建和分发

### 7.1 构建

外层 CMake 在 `add_subdirectory(audio.cpp)` 前设置：

- `CMAKE_POSITION_INDEPENDENT_CODE=ON`
- hidden visibility
- `AUDIOCPP_DEPLOYMENT_BUILD=ON`
- `ENGINE_ENABLE_CPU_ALL_VARIANTS=OFF`
- CPU/CUDA/Vulkan/HIP/Metal backend 选项
- 通用 CPU 包使用 `ENGINE_ENABLE_NATIVE_CPU=OFF`

`engine_runtime` 静态链接进一个共享库，Linux 使用 version script，macOS 使用 exported symbols list，Windows 使用 `.def`，并以 fail-closed 测试验证只导出允许的 `audiocpp_*`。

### 7.2 NuGet 规划

- `AudioCpp.NET`：托管 API
- `AudioCpp.NET.Runtime.win-x64-cpu`
- `AudioCpp.NET.Runtime.linux-x64-cpu`
- `AudioCpp.NET.Runtime.linux-arm64-cpu`
- `AudioCpp.NET.Runtime.osx-arm64-metal`
- GPU runtime packages 后续独立交付

默认 CPU artifact 必须采用 portable ISA；本机 `native` 优化构建不能作为通用 NuGet 二进制。

## 8. 实施阶段

### 阶段 0：基线和治理（当前批次）

- [x] 独立 Git 仓库
- [x] 完整计划与 ADR
- [x] 固定上游完整 SHA
- [x] 上游差异分类脚本
- [x] .NET 10 solution 骨架
- [ ] 完成首个提交

验收：锁文件与 Git 中的基线一致；候选更新不会自动修改 pin。

### 阶段 1：ABI/TTS 基础（当前开发起点）

- [x] ABI v1 头文件设计
- [x] runtime/model/TTS shim 初始实现
- [x] `LibraryImport`、`SafeHandle`、ABI 校验和公共 API 初始实现
- [x] 无模型单元测试
- [ ] Windows/Linux/macOS 原生 CI
- [ ] 真实 qwen3_tts E2E

验收：managed build/test 通过；native shim 可针对固定上游编译；符号白名单通过；有模型时完成 `.NET → shim → audio.cpp → PCM`。

### 阶段 2：发布基础

- runtime NuGet 按 RID/backend 拆分。
- 包内容、动态依赖、符号、license、NOTICE、checksum、SBOM 检查。
- CPU E2E 为正式发布强制门禁；无 GPU runner 的 GPU 包必须标记 build-only/preview。

### 阶段 3：通用离线任务

优先顺序：ASR → VAD → diarization → alignment → source separation → voice conversion → audio generation。

- 通用 request/result schema 有独立 schema version。
- 大块音频走二进制 buffer；文本、segments、turns、words、metadata 走 JSON。
- 常用任务提供强类型 facade；模型特有参数保留 options/raw JSON escape hatch。
- 每新增任务必须有 native test、managed mapper test、真实模型 E2E 和与同 commit 上游 CLI/server 的 golden parity。

### 阶段 4：流式

- 使用 `push_audio`、`finish_input`、`poll_event` 的 pull 模式。
- .NET 暴露 `IAsyncEnumerable<AudioCppStreamEvent>`。
- 不从 native 线程直接回调 managed delegate。
- 验证背压、取消、dispose race、多 session 和长期内存稳定性。

### 阶段 5：扩展后端与生产强化

- CUDA、Vulkan、HIP/ROCm E2E runner。
- 包签名、Windows Authenticode、provenance/SBOM。
- NativeAOT、trim、single-file publish 验证。
- 性能基线、内存基线和长期 soak tests。

## 9. 上游升级决策流程

### 9.1 固定流程

```text
发现候选 commit
  → 获取到本地 audio.cpp 仓库
  → Compare-Upstream.ps1 生成报告
  → 按差异分类评估
  → 在分支中适配 shim / build / tests
  → 全矩阵验证
  → 人工审批
  → Update-UpstreamLock.ps1 显式更新 lock
  → preview 发布
  → 正式发布
```

### 9.2 差异到动作的映射

| 差异 | 风险 | 必须执行的操作 |
|---|---|---|
| `include/engine/framework/runtime`、`core` 或 `src/framework/runtime` | 高 | 阻止自动升级；审查语义、适配 shim、跑 ABI/native/managed/所有受影响 E2E |
| 根/子目录 `CMakeLists.txt`、构建脚本、workflow | 高 | 重建全部 RID/backend，检查 toolchain、动态依赖、导出符号和 portable CPU |
| `external`、`.gitmodules` | 高 | 解析子模块版本，检查许可证、SBOM、符号冲突和运行时依赖 |
| `model_specs`、model/community model | 中到高 | 更新 capability/model matrix，运行受影响模型 golden tests，评估二进制大小 |
| `tests` | 中 | 将上游新增/改变的行为映射为 wrapper 回归测试 |
| 仅 docs | 低 | 仍需 native build + managed tests 后才可更新 pin |

### 9.3 版本规则

| 变化 | shim ABI | NuGet SemVer |
|---|---|---|
| 上游内部适配但公开语义不变 | 不变 | patch |
| 新增兼容能力/函数 | ABI minor +1 | minor |
| 现有函数/结构/语义不兼容 | ABI major +1 | major |
| 新 runtime RID/backend | 不变或 minor | minor |
| C# 公共 API 破坏性改变 | 视情况 | major |

维护 `stable` 与 `next/preview` 两条逻辑线。上游 main 的候选构建不能直接成为 stable 包。

## 10. 测试策略

### PR 快速门禁

- `dotnet format --verify-no-changes`
- `dotnet build -warnaserror`
- 无模型 unit tests
- Windows/Linux CPU native build
- C ABI smoke test
- 导出符号白名单
- NuGet pack 内容检查

### Nightly

- 检测上游候选差异
- 小型公开模型 CPU TTS/ASR smoke
- ASan/UBSan/LSan（适用平台）
- golden parity 与内存趋势

### Release

- 全 RID/backend build
- CPU E2E 强制通过
- GPU 构建和真实 runner 验证状态明确
- hash、license、NOTICE、SBOM、签名和 provenance
- release notes 列出 NuGet 版本、ABI、上游 SHA、能力变化与已知限制

## 11. 主要风险

1. **内部 C++ API 变化：** 通过精确 pin、集中 adapter、差异阻断和完整回归控制。
2. **API 爆炸：** 使用通用 task/result 底座，稳定能力才提供强类型 facade。
3. **内存与 CRT：** 原生分配原生释放，托管复制，全部句柄使用 `SafeHandle`。
4. **CPU 指令不兼容：** 通用包关闭 host-native ISA。
5. **GPU 依赖复杂：** 每个 backend 独立 package；不静默降级；真实 runner 前不夸大支持等级。
6. **流式线程/取消：** pull 模式、受控背压、明确 cooperative cancellation。
7. **模型测试成本：** PR 无模型，nightly/release 使用 hash 固定的外部模型缓存。
8. **供应链和许可：** 每个 artifact 带 license/NOTICE、SBOM、上游 SHA 和 checksum。

## 12. 完成定义

一个阶段只有在代码、测试、文档、构建和发布证据同时存在时才算完成。尤其是：

- “可以编译”不等于“模型语义兼容”；
- “有导出函数”不等于“所有模型支持该能力”；
- “支持取消 token”不等于“native 推理可立即被中断”；
- “GPU 构建成功”不等于“GPU 推理已经 E2E 验证”。

所有上述限制必须在 API 文档和发布说明中保持可见。
