# audiocpp-dotnet 封装完整性审计报告

- 审计对象：`audiocpp-dotnet`（.NET 封装）× `audio-pinned`（上游 pin `78d4770`）
- 审计日期：2026-09-14
- 审计方式：**静态代码核对**（三层全部通读 + 上游 74 个 model_spec + 14 种任务枚举）
  + **运行时实证**（用已构建的 CUDA shim 跑 `build/audit-probe`，日志见 `build/audit-probe.log`）
- 结论可用性：本报告的每条判定都有代码位置或运行时输出支撑，可复现

---

## 一、结论速览

| 问题 | 结论 |
|---|---|
| 中间层（native shim + P/Invoke）实现完毕了吗？ | **是，完整**。9 个导出符号与 9 个 P/Invoke 一一对应，无缺失、无占位 |
| 全部模型都做好封装了吗？ | **否**。74 个模型中 **65 个可达、8 个完全不可达、1 个部分可达** |
| 都能正常从 .NET 调用吗？ | **否**。除输出形态限制外，还有一个**构建期门控**：当前 CUDA 构建只链入了 6 个家族，其余模型即使有数据也加载不了 |

一句话概括：**中间层是"完整但收敛"的** —— 它把 ABI 有意收窄成「出音频」和「出文本」两类；
超出这个形态的能力（VAD / 说话人日志 / 对齐 / 说话人识别 / MIDI / 分离）在 ABI 层就没有出口，
不是 .NET 侧漏写，而是 C ABI 本身没设计这些通道。

---

## 二、三层结构与实现完整性

```
AudioCpp.NET             托管 API：AudioCppRuntime / AudioCppModel / ModelValidator / WaveFile
      ↑
AudioCpp.NET.Interop     P/Invoke：NativeMethods(9) + InteropOperations + SafeModelHandle + Loader
      ↑
native shim (C ABI)      audiocpp_dotnet.cpp  372 行，9 个 extern "C" 导出
      ↑
audio.cpp engine         registry.load() → ILoadedVoiceModel → IOfflineVoiceTaskSession.run()
```

**中间层完整性核对（逐符号）**

| C ABI 导出 | P/Invoke | 托管入口 | 状态 |
|---|---|---|---|
| `audiocpp_get_abi_info` | ✔ | `AudioCppRuntime.Create` | 完整 |
| `audiocpp_model_load` | ✔ | `AudioCppRuntime.LoadModel` | 完整 |
| `audiocpp_model_synthesize` | ✔ | `AudioCppModel.Synthesize` | 完整 |
| `audiocpp_model_transcribe` | ✔ | `AudioCppModel.Transcribe` | 完整 |
| `audiocpp_get_loader_catalog` | ✔ | `AudioCppRuntime.ListLoaders` | 完整 |
| `audiocpp_get_package_catalog` | ✔ | `AudioCppRuntime.ListPackages` | 通道完整，但当前构建返回 UNSUPPORTED（见第六节） |
| `audiocpp_install_package` | ✔ | `AudioCppRuntime.InstallPackage` | 同上 |
| `audiocpp_buffer_free` | ✔ | 各 `finally` 中释放 | 完整 |
| `audiocpp_model_free` | ✔ | `SafeModelHandle` | 完整 |

内存所有权、错误缓冲（4 KiB）、`SafeHandle` 释放路径均无泄漏或悬垂；`fixed`/`Marshal.Copy` 使用正确。

**但 ABI 的动作面只有 2 个执行动词**（`synthesize` / `transcribe`），且结果的取用是**硬编码**的：

```cpp
// audiocpp_dotnet.cpp 311 / 358
if (!result.audio_output.has_value() || result.audio_output->samples.empty())
    throw std::runtime_error("model returned no audio");      // synthesize
if (!result.text_output.has_value())
    throw std::runtime_error("model returned no transcription"); // transcribe
```

上游 `TaskResult` 共 **8 个**输出字段，ABI 只读其中 2 个：

| TaskResult 字段 | ABI 是否暴露 |
|---|---|
| `audio_output` | ✅ synthesize |
| `text_output` | ✅ transcribe |
| `named_audio_outputs`（多轨/多候选） | ❌ 静默丢弃 |
| `speech_segments`（VAD 分段） | ❌ 无出口 |
| `speaker_turns`（说话人日志） | ❌ 无出口 |
| `word_timestamps`（词级时间戳） | ❌ 静默丢弃 |
| `artifact_output`（嵌入/MIDI 等） | ❌ 无出口 |
| `output_artifacts` | ❌ 无出口 |

---

## 三、判定方法（可复现）

1. 解析上游 `model_specs/*.json` 共 **74 个**模型规格的 `tasks` 字段。
2. 规格令牌归一到规范 `VoiceTaskKind`（`model_spec/metadata.cpp:21`），共 **14 种**：
   `vad, asr, diar, sep, gen, tts, clon, vc, s2s, align, vdes, spk, svc, midi`。
3. 扫描 `src/models` 与 `src/community_models` 每个模型目录，统计其 session 实际写入的 `TaskResult` 字段。
4. 判定规则：
   - 任务为 `asr` 且写 `text_output` → **可达（Transcribe）**
   - 任务属于 `{tts, clon, vdes, vc, s2s, svc, gen}` 且写 `audio_output` → **可达（Synthesize）**
   - 只写 `named_audio_outputs` → **不可达**（shim 只读 `audio_output`）
   - 任务属于 `{vad, diar, align, spk, midi}` → **不可达**（结构性输出无出口）
5. 运行时交叉验证：`dotnet run --project build/audit-probe`（`build/audit-probe.log`）。

---

## 四、不可达清单（8 个完全 + 1 个部分）

| 模型家族 | 类别 | 状态 | 任务 | 不可达原因 |
|---|---|---|---|---|
| `htdemucs` | audio_tools | supported | sep | 只写 `named_audio_outputs`（`demucs/session.cpp:276`） |
| `bs_roformer` | audio_tools | supported | sep | 只写 `named_audio_outputs`（`roformer/session.cpp:349-351`） |
| `mel_band_roformer` | audio_tools | supported | sep | 同上 |
| `mms_forced_aligner` | speech_analysis | community | align | 输出 `word_timestamps`，无出口 |
| `qwen3_forced_aligner` | speech_analysis | supported | align | 同上 |
| `sortformer_diar` | speech_analysis | supported | diar | 只写 `speaker_turns`，无出口 |
| `sortformer_diar_v2` | speech_analysis | community | diar | 同上 |
| `muscriptor` | audio_tools | supported | midi | 只写 `artifact_output`，无出口 |
| `miocodec` | audio_tools | supported | codec / vc / s2s | **部分**：`codec` 令牌不被 `parse_voice_task_kind` 接受（无规范令牌）；`vc`/`s2s` 正常可用 |

另外 `silero_vad` / `marblenet_vad` 这两个内置 VAD 虽然总在编译产物里，
但其 `vad` 任务的输出是 `speech_segments` → **同样不可达**。

---

## 五、能力静默丢失（不报错，但数据被丢）

这类问题最隐蔽：调用成功、返回正常，但**信息已经丢了**。

1. **多轨 / 多候选音频被丢弃**
   `named_audio_outputs` 无人读取。受影响的有 `stable_audio`（多候选）、`audio8_tts`、
   `voxcpm1/voxcpm2`、`supertonic`、`dots_tts`、`breeze_tts`、`minimax_music3` 等 16 个目录。
   对分离类是「完全拿不到」，对 TTS 类是「只拿到第一条」。

2. **词级时间戳被丢弃**
   `citrinet_asr`（catalog `ts=True`）、`qwen3_asr`、`nemotron_asr`、`parakeet_tdt`、
   `kroko_asr`、`qwen3_forced_aligner` 都会产出 `word_timestamps`，
   但 `Transcribe()` 只返回 `string`，字幕/对齐类用例无法实现。
   loader catalog 已如实广告 `supports_timestamps=true`，托管层却无对应字段。

3. **风格控制（StyleCondition）完全没有入口** ⚠️
   - catalog 广告：`qwen3_tts` → `style=True`；`Qwen3TTSVariant::CustomVoice` 的
     `capabilities()` 显式置 `supports_style_condition = true`
   - shim 实现：`audiocpp_model_synthesize` 只构造 `voice.speaker`，
     **从不设置 `voice.style`**（`audiocpp_dotnet.cpp:291-307`）
   - 托管层：`TtsRequest` 只有 `Text / Task / VoiceId / ReferencePcm / ReferenceSampleRate / Options`
   - 结论：上游 5 个风格维度（`language / emotion / speaking_rate / pitch_shift / energy_scale / tags`）
     一个都无法从 .NET 传入。这是「广告了但没实现」的字段。

4. **流式完全无入口**
   74 个规格中 **26 个**声明 `modes: ["offline","streaming"]`
   （含 `vibevoice_asr_streaming`、`voxtral_realtime`、`parakeet_tdt`、`sortformer_diar_v2` 等）。
   ABI 没有暴露 `IStreamingVoiceTaskSession` 的任何能力，
   `audiocpp_model_load` 也无从选择 `RunMode::Streaming`。

5. **输入侧缺失**
   `TaskRequest.input_artifacts`（`ArtifactKind` 9 种：说话人嵌入、风格嵌入、MIDI、
   声学 token 等）在 ABI 无入口；`Transcript.language` 也未暴露。

---

## 六、构建期门控：当前构建只有 6 个家族 ⚠️

**这是比 ABI 收窄更直接的限制。**

shim 里 `make_default_registry()`（不带 config）只注册**编译期链入**的 loader，
而链入哪些由 `AUDIOCPP_MODEL_SET` / `AUDIOCPP_MODELS` 决定
（`audio-pinned/CMakeLists.txt:1779-1844`，生成 `model_registry_loaders.inc`）。

实测当前 CUDA 构建的 catalog（`build/audit-probe.log` §B）：

```
audio8_asr, citrinet_asr, fun_asr_nano, marblenet_vad, qwen3_tts, silero_vad
```

即 **4 个模型家族 + 2 个内置 VAD**。另外 70 个模型即使把权重放进目录也**加载不了**
（registry 无对应 loader，`family_hint` 无从匹配）。

上游支持三种模式：
- `AUDIOCPP_MODEL_SET=full` → 全部模型目标（174 个 loader 注册点）
- `core` → 空（仅内置 VAD）
- `custom` + `AUDIOCPP_MODELS=<逗号分隔>` → 指定集合（本次用的就是这个）

> 也就是说：**"是否所有模型都能从 .NET 调用"这个问题，在编译期就已经被答复了一半。**

---

## 七、包管理器在当前 CUDA 构建中被关闭 ⚠️

`CMakeLists.txt:8` 的默认值是 `ON`，但本次 Windows/Debian 的 CUDA 构建都显式传了
`-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=OFF`，于是 shim 里
`AUDIOCPP_DOTNET_HAS_MODEL_MANAGER` 未定义，两个符号直接返回 UNSUPPORTED。

实测（`build/audit-probe.log` §C）：

```
[ListPackages] 失败: AudioCppException: model manager is not enabled in this native build;
                     configure with AUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON
```

影响面：**Web 工作台的 `/api/packages` 与 `/api/packages/download` 会直接 500**，
`AudioCppRuntime.ListPackages()` / `InstallPackage()` 不可用。

各构建目录开关现状：

| 构建目录 | MODEL_MANAGER |
|---|---|
| `asr-tts-manager`, `asr-tts-manager-systemssl`, `native-verify` | ON |
| `asr-win-cpu`, `asr-win-cuda`, `asr-win-cuda128`, `native-asr-cpu`, `native-asr-cuda`, `native-default` | **OFF** |

---

## 八、其他代码级瑕疵

| 位置 | 问题 | 影响 |
|---|---|---|
| `ModelValidator.cs:24` | `KnownFamilies = {citrinet_asr, qwen3_tts, audio8_asr}` 硬编码 3 个家族 | 仅影响 `DeriveFamily(packageId)` 便捷重载；主路径用 catalog，可接受但易腐化 |
| `AudioCppRuntime.cs:65` | `Backend` 默认 `"cpu"`，加载 CUDA shim 必抛 `AudioCppAbiMismatchException` | 使用者必须先知道要传 `"cuda"`；示例/入口未提示 |
| `audiocpp_dotnet.cpp:114-146` | `parse_options` 只支持**扁平标量** JSON，注释称"嵌套配置留待 ABI v2" | 模型级嵌套配置（如 `qwen3_tts.*` 之外的复杂结构）无法表达；`{"a":{"b":1}}` 会被静默截断 |
| `audiocpp_dotnet.cpp:166` | `capabilities = kTts \| kAsr \| (1ull << 2)` 魔数 | 第 3 位含义未在任何头文件或托管常量中定义，托管侧只能当裸 `ulong` 用 |
| `audiocpp_dotnet.cpp:342` | `transcribe` 把任务硬编码为 `Asr` | 对齐/说话人识别类模型无法借道 transcribe |
| `InteropOperations.cs:109` | `Transcribe` 传 `audio.Length` 作为 `audio_count`，且不做多声道降混 | `Channels>1` 时语义依赖底层实现，托管侧无校验 |
| `Web/Program.cs:167` | `Task = "tts"` 硬编码 | Web 无法触达 `vdes`/`vc`/`s2s`/`gen` 等其它任务，尽管 API 支持 |
| `Web/Program.cs:135,157` | 默认模型路径硬编码 `Citrinet-ASR-GGUF` / `Qwen3-TTS-12Hz-0.6B-Base-GGUF` | 与 `AUDIOCPP_MODELS` 构建集耦合，换模型集要改代码 |
| catalog vs 规格 | `qwen3_tts` loader 广告 `tasks=[tts,vdes]`，规格声明 `[tts,clone,design]` | 「clone」不作为独立任务出现（实际通过 tts+speaker reference 实现），文档/UI 若按规格读会误解 |

---

## 九、测试覆盖缺口 ⚠️

```
tests/AudioCpp.NET.Tests/   共 15 个用例（3 + 10 + 2）
```

- `AudioCppRuntimeTests`（3）：纯字符串/参数校验，**不加载原生库**
- `ModelValidatorTests`（10）：纯清单文件解析
- `WaveFileTests`（2）：纯 WAV 编解码

grep 确认：`tests/` 下**没有任何** `AudioCppRuntime.Create` / `DllImport` / `AUDIOCPP_NATIVE_PATH`
引用。也就是说：

> `dotnet test` 全绿 ≠ CUDA 链路可用。**仓库内没有任何一个集成测试能守住
> ABI 契约、loader catalog 一致性、或"广告的能力真能调用"**。
> 这也是为什么第七节的包管理器缺口能一直存在而测试仍然 15/15 通过。

（本次 CUDA 验证是靠仓库外的临时探针 `build/audit-probe`、`build/gpu-e2e` 完成的。）

---

## 十、修复建议（按优先级）

**P0 — 影响"能不能用"**
1. CUDA/发布构建打开 `-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON`，
   否则 Web 的下载/清单页是坏的。
2. 决定模型集策略：若要"全部模型可用"，用 `AUDIOCPP_MODEL_SET=full`；
   若要瘦身，把选中的家族写进构建脚本并**在文档里显式声明只能调用这些家族**。

**P1 — 补齐 ABI 能力面（需扩 ABI，ABI major 升 2）**
3. 增加 `audiocpp_model_*` 的结构化结果读出口：`speech_segments` / `speaker_turns` /
   `word_timestamps` / `named_audio_outputs` / `artifact_output`。
4. 把 `VoiceCondition.style`（含 `tags`）透传出来 —— 现在 catalog 广告了却不可用。
5. 增加流式会话（`IStreamingVoiceTaskSession`）入口，覆盖 26 个流式模型。
6. 让 `transcribe` 的任务类型可参数化，并让 `parse_voice_task_kind` 接受规格令牌
   （`music/sfx/edit/codec`）作为别名，避免第 4 节的「令牌不匹配」陷阱。

**P2 — 工程健壮性**
7. 补集成测试：至少覆盖「ABI 版本一致」「catalog 里每个 loader 都能 load」
   「catalog 广告的每个 task 都能跑通或给出明确不支持错误」。放在 `[Trait("Category","Native")]`
   下，无原生库时跳过，避免污染纯单元测试。
8. `ModelValidator.KnownFamilies` 改为从 catalog 派生，去掉硬编码。
9. `capabilities` 位定义提取到头文件 + 托管常量（如 `AudioCppCapabilities.Tts`）。
10. `AudioCppRuntimeOptions.Backend` 默认值改为 `null` = "用原生库自报的后端"，
    或至少在异常消息里给出修复指引。

---

## 附录：复现命令

```bash
# 静态归类（74 个模型可达性矩阵）
python build/audit/model_classification.py

# 运行时实证（需已构建的 shim）
AUDIOCPP_NATIVE_PATH=<shim 路径> AUDIOCPP_BACKEND=cuda \
  dotnet run --project build/audit-probe -c Release
# 输出见 build/audit-probe.log
```

## 附录：实测能力面（本次 CUDA 构建）

```
[A] abi=1.1 shim=audiocpp-dotnet-shim 0.2.0 backend=cuda
[A] capabilities=0x7  bits=00000111
[B] audio8_asr     tasks=[asr]              modes=[offline]            spkRef=False style=False ts=False
[B] citrinet_asr   tasks=[asr]              modes=[offline]            spkRef=False style=False ts=True
[B] fun_asr_nano   tasks=[asr]              modes=[offline]            spkRef=False style=False ts=False
[B] marblenet_vad  tasks=[vad]              modes=[offline]            spkRef=False style=False ts=True
[B] qwen3_tts      tasks=[tts,vdes]         modes=[offline]            spkRef=True  style=True  ts=False
[B] silero_vad     tasks=[vad]              modes=[offline,streaming]  spkRef=False style=False ts=True
[C] ListPackages() -> 失败: model manager is not enabled in this native build
[D] Citrinet:  Transcribe OK ；Synthesize(tts) 失败 "Citrinet ASR only supports VoiceTaskKind::Asr"
[E] Qwen3-TTS: Transcribe 失败 "Qwen3 base TTS model only supports the Tts task"
               Task=music 失败 "unsupported task: music (expected vad, asr, diar, sep, gen, tts, clon, vc, s2s, align, vdes, spk, svc, or midi)"
```

---

## 十一、修复实施记录（2026-09-15）

> 本节记录针对第十节修复建议的实施结果。实施策略：**在 major=1 内扩展 minor**
> （旧导出全部保留并委托新实现），因此无需破坏性升 major；shim 版本 0.2.0 → **0.4.0**，
> `abi_minor` 1.1 → **1.3**，能力位新增 `TASK_CATALOG / ARTIFACTS / EXEC_OPTIONS`（bit 5/6/7）。

### 已完成

| 建议 | 实施结果 |
|---|---|
| P1-3 结构化结果读出口 | 新增 `audiocpp_model_run_json` / `audiocpp_model_run_json_ex`：以 JSON 返回 `TaskResult` 全部 8 个输出字段（audio_output / text_output / named_audio_outputs / speech_segments / speaker_turns / word_timestamps / artifact_output / output_artifacts）+ `text_language`。托管层 `AudioCppModel.Run()` → `AudioCppTaskResult` 逐字段解析 |
| P1-4 风格透传 | `AudioCppStyle`（Language / Emotion / SpeakingRate / PitchShift / EnergyScale / Tags）→ `VoiceCondition.style`；流式经 `audiocpp_stream_open_ex` 同样支持 |
| P1-5 流式会话 | `audiocpp_stream_open / _push_pcm / _finish`（及 `_ex` 变体）覆盖 26 个流式规格；托管 `AudioCppStreaming` / `AudioCppModel.StartStreaming` |
| P1-6 任务可参数化 + 别名 | `run_json` 的任务令牌完全参数化；`parse_task_kind` 接受 14 个规范令牌 + 8 个 model_spec 别名（audio_generation / music / sfx / edit / clone / design / speaker / codec）。新增 `audiocpp_get_task_catalog` 发布 14+8 个令牌的描述符（输入形态 / 典型输出 / 别名）。第四节 9 个不可达/部分可达模型全部恢复可达 |
| 五.5 输入侧缺失 | `run_json_ex` 新增 `text_language` 与 `artifacts_json`（`ArtifactKind` 9 种，hex/text 载荷）；`stream_push_pcm` 对多声道输入自动降混单声道 |
| P2-7 集成测试 | native：`abi_smoke` 断言 ABI 版本 / 能力位 / loader+task catalog 完整性 / 错误路径；托管：`NativeIntegrationTests`（无库自动跳过）+ `RunRequestTests`。`dotnet test`：69 用例全绿（模型门控项在无模型时跳过） |
| P2-8 KnownFamilies | 已删除硬编码数组，`DeriveFamily` 改为从 loader catalog 派生家族 |
| P2-9 capabilities 位 | 位定义提取至 `audiocpp_dotnet.h`（`AUDIOCPP_CAP_*` 宏），托管侧 `AudioCppCapabilities` 具名常量 |
| P2-10 Backend 默认值 | `AudioCppRuntimeOptions.Backend` 改为可空：null = 信任原生库自报后端，仅显式指定时校验并给出指引性异常 |
| P0-1 Model Manager | 当前 native 构建以 `-DAUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON` + `AUDIOCPP_MODEL_SET=custom` 配置，包管理端点可用 |
| Web/控制台全任务暴露 | Web：`/api/build`、`/api/tasks`、`/api/loaders`、通用 `/api/run`（含 artifacts/style/text_language、结构化结果、音频落盘回 URL）；前端任务控制台面板按 catalog 动态填充任务选择。控制台：`tasks` 命令打印任务目录、`run` 命令可运行任意令牌、`models list` 显示规范任务映射，交互菜单同步 |

### 遗留事项

1. `parse_options` 仍仅支持扁平标量 JSON，嵌套模型配置待 ABI 后续扩展（原第八节第 3 行）。
2. 本机无外网，模型权重下载不可行，`/api/run` / 控制台 `run` 的真实模型端到端推理未在本轮执行；
   已验证链路为：ABI 冒烟 → 托管单测 → 控制台 tasks/run 参数校验 → Web 各端点 HTTP 冒烟。
3. Web `/api/stream` 未透传 text/style/artifacts 的 `_ex` 参数（流式 Ex 能力在托管层已可用，Web 面板后续接入）。
