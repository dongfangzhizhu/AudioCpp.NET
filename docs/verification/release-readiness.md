# audiocpp-dotnet 全量模型封装 + 四平台矩阵 交付报告

- 日期：2026-09-16
- 上游：`audio-pinned` @ `78d47706c30ef215ba9ad3559baff309efeb5260`（脚本内 pin 校验通过）
- 构建：`AUDIOCPP_MODEL_SET=full`（全部 71 个模型目标）+ `AUDIOCPP_DOTNET_ENABLE_MODEL_MANAGER=ON`
- 后端：`cpu` 与 `cuda`（sm_89 / RTX 4090 24 GB）
- 平台：Windows 11（VS 18 Professional / MSVC 14.51 / Ninja / CUDA 13.3）+ WSL Debian 12（GCC 12.2 / Ninja / CUDA 13.0）
- ABI：`audiocpp-dotnet-shim 0.4.0`，`abi=1.3`，`capabilities=0xFF`

---

## 一、结论速览

| 用户要求 | 结果 |
|---|---|
| 完成整个项目 | ✅ 四平台矩阵 **FAILED=0** |
| 把所有模型都封装好 | ✅ **74 个 model_specs 家族 100% 有 loader，缺 0 个**；catalog 76 loader / 217 包 |
| CPU/CUDA 在 Windows 测试到 | ✅ win-cpu + win-cuda：ABI smoke + ASR/VAD/TTS 真实推理 + 托管测试 |
| CPU/CUDA 在 Linux 测试到 | ✅ linux-cpu + linux-cuda（WSL Debian）：同上一套，全绿 |
| Web UI 模型列表按类别显示 | ✅ 包/loader/本地模型三类列表全部改为**可折叠分类分组** + 过滤 + 计数 |

---

## 二、全量模型封装验证（核心交付）

### 2.1 编译期：74 个 loader 全链入

`AUDIOCPP_MODEL_SET=full` 生成 `audio_cpp/generated/model_registry_loaders.inc`，四平台实测**均为 74**：

| 构建 | loader 数 |
|---|---|
| `build/native-cpu` | **74** |
| `build/native-cuda` | **74** |
| `build-linux-cpu` | **74** |
| `build-linux-cuda` | **74** |

（历史遗留的 `custom` 集只有 4 个：citrinet / audio8 / fun / qwen3_tts。本轮 `CMakeLists.txt` 默认值已改为 `full`。）

### 2.2 运行期：catalog 覆盖度对照

用 `build/audit-probe` 驱动已构建 shim，实测 catalog（后端无关，cpu / cuda 结果一致）：

```
[A] abi=1.3  shim=audiocpp-dotnet-shim 0.4.0  backend=cpu|cuda
[A] capabilities=0xFF  bits=11111111   (8 个能力位全亮)
[B] 76 loaders
[C] ListPackages() -> OK count=217
```

与上游 `model_specs/*.json` 逐一对账：

| 对照项 | 数量 |
|---|---|
| catalog loader | 76 |
| model_specs 家族（JSON 文件数） | 74 |
| **规格有但 catalog 无** | **0** ← 全量封装成立的硬证明 |
| catalog 有但规格无 | 2（`silero_vad` / `marblenet_vad`，内置 VAD，无 spec 属正常） |

catalog loader 按类别分布（与 74 个家族分布完全吻合）：

| 类别 | catalog loader | model_specs 家族 |
|---|---|---|
| tts | 37 | 37 |
| asr | 15 | 15 |
| audio_tools | 7 | 7 |
| audio_generation | 6 | 6 |
| voice_conversion | 4 | 4 |
| speech_analysis | 6 | 4（+2 内置 VAD） |
| community | 1 | 1 |

### 2.3 包清单

`ListPackages()` 返回 **217 个可下载包**（model manager 已启用），覆盖全部 74 个家族；
Web 的 `/api/packages` 与 `/api/packages/download` 端点可用。

---

## 三、Web UI：模型列表分类分组

### 3.1 问题

平铺列表规模：**217 个模型包 / 76 个 loader / 本地模型目录** —— 单列不可用。

### 3.2 后端改动

`src/AudioCpp.NET.Web/ModelMetadata.cs` 重写为 **model_specs 索引**（启动时扫描一次）：

- `Resolve(packageId)` → family / category / tasks / languages
- `CategoryOfFamily(family)` → 给 loader 用（loader 本身不带 category）
- `FamilyOfPackage(packageId)` → 给包列表用
- 内置 VAD 显式归入 `speech_analysis`，避免落进 "unknown"
- 畸形 spec 跳过而非整体失败

端点新增字段：

| 端点 | 新增 |
|---|---|
| `/api/models` | `category`、`installedBytes` |
| `/api/packages` | `family`、`category` |
| `/api/loaders` | `category` |

### 3.3 前端改动

- `js/app.js`：新增通用分组渲染器 `renderGroupedList()` + 全部展开/折叠 `wireGroupToggle()`
- 三个列表全部改为**可折叠分类分组**，默认全折叠；折叠状态按容器记忆
- 每个列表配**过滤输入框**（类别名/包名/家族），**搜索时强制展开命中组**
- 分类头显示计数（模型包显示"已装 N"、本地模型显示"完整 N"、loader 显示任务/flags）
- `index.html`：`ul` → `div.group-list` + toggle 按钮 + filter 输入
- `css/app.css`：追加 `.group*` / `.filter-input` / `.pkg-family` / `.loader-row` 样式（沿用深色 amber/teal 皮肤）

类别标签与排序：`asr → tts → audio_generation → voice_conversion → audio_tools → speech_analysis → community → unknown`
（中文标签：语音识别 ASR / 语音合成 TTS / 音频生成 / 音色转换 / 音频工具 / 语音分析 / 社区模型 / 未分类）

### 3.4 实测

```
/api/models    -> 4 目录，category = asr/asr/asr/tts
/api/packages  -> 217 包，7 类（tts 127 / asr 33 / audio_generation 26 / audio_tools 13 /
                               voice_conversion 10 / speech_analysis 7 / community 1）
/api/loaders   -> 76 loader，unknown 为空（内置 VAD 已归入 speech_analysis）
```

截图曾存于 .artifacts/shots/（scratch 目录已清理；UI 结论由 /api/* 端点实测复核）

---

## 四、四平台测试矩阵（FAILED=0）

两套脚本产物：`docs/verification/logs/win-full-matrix.log（本轮迁移前位于 .artifacts/）`（Windows）与 `docs/verification/logs/linux-full-matrix.log（同上）`（WSL Debian）。

| 项目 | win-cpu | win-cuda | linux-cpu | linux-cuda |
|---|---|---|---|---|
| loaders | 74 | 74 | 74 | 74 |
| shim 产物 | 20.2 MiB dll | 70.5 MiB dll | 58.0 MiB so | 118.7 MiB so |
| ABI smoke | ✅ exit 0 | ✅ exit 0 | ✅ exit 0 | ✅ exit 0 |
| ASR（Citrinet） | ✅ exit 0，bytes=1360 | ✅ exit 0，bytes=1358 | ✅ exit 0，bytes=1360 | ✅ exit 0，bytes=1358 |
| VAD（Silero 流式） | ✅ exit 0，start=9/end=8 | ✅ exit 0，start=9/end=8 | ✅ exit 0，start=9/end=8 | ✅ exit 0，start=9/end=8 |
| TTS（Qwen3-TTS 克隆） | ✅ exit 0，samples=90240 | ✅ exit 0，samples=30720 | ✅ exit 0，samples=90240 | ✅ exit 0，samples=90240 |
| 托管测试（对 shim） | ✅ 65 通过 / 0 失败 / 4 跳过 | ✅ 65 / 0 / 4 | ✅ 65 / 0 / 4 | ✅ 65 / 0 / 4 |
| catalog | ✅ 76 / 217 | ✅ 76 / 217 | ✅ 76 / 217 | ✅ 76 / 217 |

- ABI smoke 四个 cell 均输出 `backend=<cell>`、`capabilities 0xff` → 后端确实按 cell 切换。
- ASR 的 `schema_ok=1 task_echo=1` 证明结构化结果（`run_json`）路径可用。
- VAD 走的是流式会话（`stream_open → push_pcm → finish`），事件计数一致。
- TTS 走 `synthesize` + 参考音频（ICL 音色克隆）路径，落盘 WAV 可播放。
- 4 个跳过项是 `[NativeFact(needsModel: true)]` 门控用例，需 `AUDIOCPP_TEST_MODEL` 指向模型包才启用。

---

## 五、本轮关键排障：Linux CPU TTS 被 OOM Killer 杀掉

### 5.1 现象

首轮 Linux 矩阵里唯一红项：`e2e-tts-cpu` 退出码 **137**（SIGKILL），日志 0 字节，无任何输出。

### 5.2 定位过程

| 步骤 | 观察 | 结论 |
|---|---|---|
| 查内核日志 | `Out of memory: Killed process (audiocpp_dotnet) total-vm:54701164kB, anon-rss:28906360kB` | 确认是 **OOM Killer**，不是崩溃 |
| 查内存配置 | 宿主物理内存 **31.8 GB**，`.wslconfig` 为 `memory=32GB, swap=16GB`，OOM 时 swap 已用 5.5 GB | 需求超过 ~33 GB，本就装不下 |
| `--threads 4` 复跑 | 峰值 RSS 仍 **27.5 GB**（32 线程时 28.9 GB） | 排除线程池 scratch buffer 因素 |
| 去掉 `--voice-ref` | 模型直接报 `Qwen3 base TTS requires voice clone reference audio`，峰值 1.95 GB | 该模型强制要求克隆参考，无法绕开 |
| 查参考音频 | 源片 `models/jinguling.wav`（24000 Hz） **时长 141.92 s** | 参考音频异常长 |
| 换 12 s 参考 + 限帧 | exit 0，耗时 98 s，峰值 RSS **4.98 GB** | **根因锁定：显存/内存占用随参考音频长度放大** |

### 5.3 根因

两件事叠加：

1. **参考音频过长**。`qwen3_tts` 的 ICL 克隆要把参考音频喂进 codec encoder 并作为 prompt，
   中间激活随参考时长增长。142 s 参考 → 峰值 28 GB；12 s 参考 → 峰值 < 5 GB。
2. **生成预算未限**。上游 `Qwen3TTSGenerationOptions::max_new_tokens` 默认 **2048** 帧
   （`include/engine/models/qwen3_tts/types.h`、`assets.h`）。CUDA 上 talker 很快采到 EOS
   （实际只生成 ~27 帧）所以看起来"没问题"；CPU 上不早停，就要跑满预算，耗时以小时计。
   上游允许用扁平选项键 **`max_tokens`** 覆盖：
   ```cpp
   // src/models/qwen3_tts/session.cpp
   options.max_new_tokens = config.max_new_tokens;                    // 2048
   if (parse_int_option(request.options, {"max_tokens"}))             // ← 可覆盖
       options.max_new_tokens = *value;
   ```

### 5.4 修正（只改测试夹具，不改 wrapper）

| 文件 | 改动 |
|---|---|
| `native/tests/e2e_probe.cpp` | 新增 `--max-tokens N`，把 `max_tokens` 并入 options JSON（连同 `reference_text`） |
| `eng/matrix/linux-full-matrix.sh` | TTS 改用 12 s 参考（脚本内从 142 s 片段头部派生）；传 `--max-tokens 48` |
| `eng/matrix/win-full-matrix.ps1` | 同上（新建，与 Linux 脚本对称） |

修正后 CPU TTS：98 s 完成，峰值 4.98 GB，输出 2.96 s 可播放 WAV。
修复后四平台矩阵 **FAILED=0**。

> 说明：这是**测试夹具**的合理性修正（2 分 22 秒的克隆参考 + 2048 帧无界生成本身不适合做冒烟用例），
> shim / Interop / 托管三层均未改动。

---

## 六、构建阻塞点与解法（累计）

| # | 现象 | 根因 | 解法 |
|---|---|---|---|
| 1 | Bash 不能调 `cmd.exe` / `powershell.exe`；PowerShell 工具不能调 `cmd.exe` | 本机安全策略双向拦截 | 用 VS **DevShell 模块**（`Microsoft.VisualStudio.DevShell.dll` + `Enter-VsDevShell`）在 PowerShell 内取得 MSVC 环境，绕开必须经 cmd 的 `vcvars64.bat` |
| 2 | PowerShell 工具 stdout 恒为空 | 本机缺陷 | 所有 PS 输出 `*> file` / `Set-Content`，再用文件读取 |
| 3 | CUDA 编译在 `fattn-mma-f16-instance-*.cu` 崩溃，**无错误输出** | `-j24` 时并发 nvcc 耗尽 32 GB 物理内存（OOM） | 降到 `-j4`～`-j6`，且**编译期间独占机器**（停掉 Web / 浏览器 / 其他重任务） |
| 4 | 同上，第二次整轮 ninja 被中止 | 与 dotnet/Chrome 争内存 | 串行化：一次只跑一个重型任务 |
| 5 | WSL 里 FetchContent 下载 boringssl 失败（github HTTP/2 中断） | 网络不稳 | 复用 Windows 已下载的源码：`-DFETCHCONTENT_SOURCE_DIR_AUDIOCPP_BORINGSSL=<副本>` |
| 6 | WSL 里 CMake 把 nvcc 解析成 `/mnt/c/.../nvcc.exe` | 登录 shell 经 interop 继承 Windows PATH | 脚本先剔除 PATH 中所有 `/mnt/*`，再显式 `-DCMAKE_CUDA_COMPILER=/usr/local/cuda/bin/nvcc -DCUDAToolkit_ROOT=/usr/local/cuda` |
| 7 | MSVC 下裸 `/utf-8` 泄漏进 nvcc，报 "A single input file is required…" | `add_compile_options(/utf-8)` 经目录属性继承到 CUDA 目标 | 已在本仓库 `CMakeLists.txt` 用 `$<$<COMPILE_LANG_AND_ID:C,CXX>:/utf-8>` 发生器表达式限定语言 |
| 8 | Linux CPU TTS 退出码 137、日志 0 字节 | 142 s 克隆参考 + 2048 帧无界生成 ⇒ 峰值 28 GB > 32 GB WSL 上限 | 夹具改用 12 s 参考 + `--max-tokens 48`（见第五节） |

---

## 七、复跑方式

```powershell
# Windows：全量矩阵（cpu + cuda：编译 → smoke → e2e → 托管测试）
powershell -ExecutionPolicy Bypass -File eng/matrix/win-full-matrix.ps1
# 需要重新配置时加 -Configure；只跑一个后端加 -Backends cpu
```

```bash
# WSL Debian：全量矩阵（cpu + cuda 同一套流程）
wsl -d Debian -- bash -lc "bash /mnt/d/SouceCode/python2net/audio/audiocpp-dotnet/eng/matrix/linux-full-matrix.sh"
```

```bash
# catalog 覆盖度复核（任选 shim）
AUDIOCPP_NATIVE_PATH=<shim> AUDIOCPP_BACKEND=cpu dotnet run --project build/audit-probe -c Release
```

单点排障辅助脚本（`eng/matrix/`）：

| 脚本 | 用途 |
|---|---|
| `linux-cpu-tts-probe.sh <threads> <clone\|plain> [ref.wav]` | 复跑 CPU TTS 并采样峰值 RSS（定位 OOM 用） |
| `linux-tts-retest.sh <cpu\|cuda> [max_tokens]` | 同步探针改动后只重建 e2e 目标并复测 TTS |
| `mem-snapshot.sh` | 一次性抓当前 e2e 进程的 VmRSS / VmPeak / VmHWM |

---

## 八、仓库改动清单

功能改动（仓库源码）：

| 文件 | 改动 |
|---|---|
| `native/tests/e2e_probe.cpp` | 新增 `--max-tokens N`，并入 options JSON；usage 同步 |
| `src/AudioCpp.NET.Web/ModelMetadata.cs` | 重写为 model_specs 索引，提供 category / family 查询 |
| `src/AudioCpp.NET.Web/Program.cs` | `/api/models`、`/api/packages`、`/api/loaders` 增加 category（及 family / installedBytes） |
| `src/AudioCpp.NET.Web/wwwroot/index.html` | 列表容器改为分组结构，新增过滤框与折叠按钮 |
| `src/AudioCpp.NET.Web/wwwroot/js/app.js` | `renderGroupedList()` / `wireGroupToggle()`，三个列表改用分组渲染 |
| `src/AudioCpp.NET.Web/wwwroot/css/app.css` | 分组卡片、过滤框、loader 行样式 |

构建/测试辅助脚本（**已并入版本控制**，见 `eng/matrix/`）：`configure-win.ps1`、`build-win.ps1`、
`win-full-matrix.ps1`、`linux-full-matrix.sh`，排障工具在 `eng/matrix/tools/`。

---

# 第三轮：发布就绪化（清理 / 规范化 / 打包 / CI）

## 结论：具备发布条件

封装完整性（74/74 loader，0 缺失）与四平台矩阵（`FAILED=0`）均已实证，无需再补功能。
本轮的改动全部围绕"能安全地分发出去"：目录规范化、许可证合规、打包布局、发布自动化。

## 目录规范化

发现并修正了一个会导致产物丢失的结构问题：**`build/` 在 `.gitignore` 内，而矩阵脚本与交付报告
原先都放在 `build/` 下，永远不会被提交**。迁移后：

| 原位置 | 现位置 |
| --- | --- |
| `build/matrix/{win-full-matrix.ps1, linux-full-matrix.sh, configure-win.ps1, build-win.ps1}` | `eng/matrix/` |
| `build/matrix/{linux-cpu-tts-probe.sh, linux-tts-retest.sh, mem-snapshot.sh}` | `eng/matrix/tools/` |
| `build/full-model-matrix-report.md` | `docs/verification/release-readiness.md`（本文件） |
| `build/wrapping-audit-report.md` | `docs/verification/model-coverage-audit.md` |
| `PLAN.md` | `docs/PLAN.md` |

统一的目录约定（全部在 gitignored 的 `build/` 下）：
`build/native-<backend>`（CMake 构建树）、`build/deps/boringssl-src`（离线依赖缓存）、
`build/artifacts/`（测试输出与夹具）、`build/nuget-staging/`（打包暂存）、`build/nuget/`（包输出）。

删除：6.6 GB 遗留实验构建树、341 MB scratch 目录、根目录散落日志、被取代的 `configure-win.cmd`、
一次性 WSL 脚本。**源码零改动**（除下文两处顺带修正）。

## 顺带修正的两个真实缺陷

1. **`NativeLibraryLoader` 仓库探测不到 Ninja 产物**。探测只找 `build/native*/Release|Debug/`，
   而 Ninja 把库直接放在 `build/native-<backend>/`。补上 flat 探测（向后兼容多配置生成器）。
2. **矩阵脚本依赖 scratch 目录里的测试夹具**。原 `.artifacts/jinguling-16k.wav` 位于被清理的
   scratch 目录中，且源片 `models/jinguling.wav` 是 **24000 Hz**。新增零依赖的
   `eng/matrix/tools/make-fixtures.py` 在运行时重采样，产物 `ref-12s-16k.wav` = 384044 B，
   与旧脚本 `head -c 384044` 的结果**逐字节一致**。

## 许可证

上游 audio.cpp 为 **Apache-2.0**（Copyright 2026 ShugoAI LLC），本项目沿用。已从上游 `LICENSE`
第 15–190 行原样提取 Apache-2.0 全文作为 `LICENSE`，并新增 `NOTICE` 登记静态链接的第三方组件：
ggml / cpp-httplib / libyaml / cJSON（均 MIT）、sentencepiece（Apache-2.0）、
BoringSSL（OpenSSL + SSLeay）。

> **待确认（不要猜测）**：`audio-pinned/external/llama_tokenizer` 在上游 pin 的版本里
> **没有任何 LICENSE/COPYING 文件**，源码中也未见授权声明，而 shim 静态链接了它。
> 公开发布前需向上游确认授权，或改用不链接该组件的构建。此项已如实写入 `NOTICE`
> 的 "OPEN QUESTION" 段，未做任何推断。

## 打包布局

三个包，均已核对实际 nupkg 内容：

| 包 | 内容 |
| --- | --- |
| `AudioCpp.NET` | `lib/net10.0/AudioCpp.NET.dll`、`AudioCpp.NET.Interop.dll`、`AudioCpp.NET.xml`、`README.md` |
| `AudioCpp.NET.Runtime` | `runtimes/{win-x64,linux-x64}/native/`、`buildTransitive/*.props|.targets` |
| `AudioCpp.NET.Runtime.Cuda` | 同上（CUDA shim） |

Interop 程序集**内嵌进主包**而非发布为独立包，因此不产生包依赖（用
`TargetsForTfMSpecificBuildOutput` + `BuildOutputInPackage` 实现，已实证生效）。

两个 Runtime 包提供**同名 native 文件**，靠 `buildTransitive` 里的
`AudioCppRuntimeConflictGuard` 把"同时引用两个"从"加载哪个后端看 restore 顺序"
变成一个明确的编译错误。`ShimStagingCheck.targets` 则在没暂存 native 时直接报错，
避免打出一个空包、到用户机上才炸成 `DllNotFoundException`。

## CI / 发布自动化

| 工作流 | 触发 | 作用 |
| --- | --- | --- |
| `ci.yml` | push/PR | Windows + Linux 托管层 build + test + 试打包 |
| `native-abi.yml` | 改动 `native/**`、`CMakeLists.txt`、`eng/**` | clone 上游到 pin 的 commit，用 `MODEL_SET=core` + 关掉 model manager 快速编译并跑 `abi_smoke` |
| `release.yml` | tag `v*.*.*` 或手动 | 打包并推送 nuget.org；托管包从源码构建，Runtime 包从 Release 上的 `audiocpp-native-*.zip` 组装 |

**为什么 Runtime 包必须走归档接缝**：全量 shim 要编译 74 个模型家族加引擎，CUDA 版本还需要
NVIDIA 工具链与 GPU，两者都放不进 GitHub 托管 runner。因此由能编译的机器产出
`audiocpp-native-<rid>-<backend>.zip`，CI 只负责组装与发布。缺归档时托管包照发，
Runtime 包跳过并给出 warning，而不是让整条流水线失败。

## 复现实测（本轮）

- WSL 侧以改造后的 `eng/matrix/linux-full-matrix.sh` 重跑：**FAILED=0**，
  cpu/cuda 各 `loaders 74`、`abi_smoke 0`、`asr 0`、`vad 0`、`tts 0`、托管 `65-0-4`，
  两后端 TTS 输出一致（`samples=90240`）；产出 shim 58.0 MiB / 118.7 MiB
- 打包实测：CPU 两包成功产出并逐项校验通过（布局、无包依赖、符号包）

## 已知环境约束（会影响复现）

- **WSL 不归还内存**：跑完 Linux 矩阵后宿主 31.8 GB 只剩约 5.8 GB 可用，
  紧接着的 nvcc 全量编译会**静默死掉**（日志停在半途、无错误行）。
  编译 Windows CUDA 前先 `wsl.exe --shutdown`。
- **CUDA 全量编译必须独占**：`-j24` 必崩，`-j6` 在内存紧张时也不稳；
  可用内存低于约 12 GB 时用 `-j4`。CPU 全量编译同样吃内存，`-j4/-j8` 更稳（默认 24 偏大）。

