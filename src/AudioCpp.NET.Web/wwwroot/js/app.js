/* ══ AudioCpp.NET workbench · front-end ══ */
"use strict";

const $ = (id) => document.getElementById(id);
let localModels = [];
let taskCatalog = [];
let loaderCatalog = [];
let buildInfo = null;

const taskDecks = { asr: "deckAsr", tts: "deckTts", stream: "deckStream", any: "deckAny" };
const taskTabs = { asr: "tabAsr", tts: "tabTts", stream: "tabStream", any: "tabAny" };

function showTask(task) {
  const active = taskDecks[task] ? task : "any";
  for (const [key, id] of Object.entries(taskDecks)) $(id).hidden = key !== active;
  for (const [key, id] of Object.entries(taskTabs)) $(id).checked = key === active;
}

function describeModel(entry) {
  $("modelCapabilities").textContent = entry
    ? `${entry.name} · ${entry.task} · Family: ${entry.family || "未知（可手动填写）"} · 支持语言（规格声明）: ${(entry.languages || []).join(", ") || "未知"} · 任务: ${(entry.tasks || []).join(", ")}。文件完整不代表 native 支持推理。`
    : "未匹配本地模型：Family 可手动填写，留空由运行时推断。";
}

for (const task of ["asr", "tts", "stream", "any"]) {
  const pathInput = $(task + "ModelPath");
  const familyInput = $(task + "Family");
  if (!pathInput || !familyInput) continue;
  pathInput.addEventListener("input", () => {
    const path = pathInput.value.trim().replaceAll("\\", "/").toLowerCase();
    const entry = localModels.find(m => m.path.replaceAll("\\", "/").toLowerCase() === path);
    familyInput.value = entry?.family || "";
    describeModel(entry);
  });
}

async function api(path, options) {
  let response;
  try {
    response = await fetch(path, options);
  } catch (err) {
    throw new Error(`无法连接服务：${err.message}`);
  }
  if (!response.ok) {
    let message = `HTTP ${response.status}`;
    try {
      const body = await response.json();
      if (body && body.error) message = body.error;
    } catch { /* keep status text */ }
    throw new Error(message);
  }
  return response.json();
}

/* ── studio log ── */
function log(level, message) {
  const line = document.createElement("div");
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  line.innerHTML = `<span class="t">${time}</span><span class="${level}"></span>`;
  line.lastChild.textContent = message;
  const box = $("log");
  box.prepend(line);
  while (box.children.length > 120) box.lastChild.remove();
}

function showError(message) {
  $("errorText").textContent = message;
  $("errorBanner").hidden = false;
  log("err", message);
}
$("errorClose").addEventListener("click", () => { $("errorBanner").hidden = true; });

/* ── config / uplink ── */
async function loadConfig() {
  const config = await api("/api/config");
  $("nativePath").value = config.nativePath ?? "";
  $("modelsDir").value = config.modelsDirectory ?? "";
  $("endpoint").value = config.huggingFaceEndpoint ?? "";
  $("endpointBadge").textContent = config.huggingFaceEndpoint ?? "";
  return config;
}

$("configForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = $("saveConfig");
  button.disabled = true;
  try {
    const config = await api("/api/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        nativePath: $("nativePath").value.trim(),
        modelsDirectory: $("modelsDir").value.trim(),
        huggingFaceEndpoint: $("endpoint").value.trim(),
      }),
    });
    $("endpointBadge").textContent = config.huggingFaceEndpoint;
    log("ok", `配置已应用 · models=${config.modelsDirectory}`);
    await Promise.all([loadPackages(), loadModels(), loadCatalog()]);
  } catch (err) {
    showError(err.message);
  } finally {
    button.disabled = false;
  }
});

/* ── ABI · task catalog · loaders ── */
const REQUIRED_CAPABILITIES = {
  synthesize: "utterance 生成",
  transcribe: "语音识别",
  model_manager: "模型管理",
  structured_results: "结构化结果",
  streaming: "流式会话",
  task_catalog: "任务目录",
  artifacts: "结构产物",
  exec_options: "执行选项",
};

function renderBuildFlags(build, flags) {
  const lines = [
    `shim        ${build.shimVersion}`,
    `abi         ${build.abiMajor}.${build.abiMinor}`,
    `audio.cpp   ${build.audioCppCommit}`,
    `backend     ${build.backend}`,
    `capabilities 0x${Number(build.capabilities).toString(16)}`,
  ];
  for (const [flag, label] of Object.entries(REQUIRED_CAPABILITIES)) {
    const on = (flags || []).includes(flag);
    lines.push(`  ${on ? "✓" : "✗"} ${flag.padEnd(19)} ${label}`);
  }
  $("buildFlags").textContent = lines.join("\n");
}

function renderTaskCatalog(tasks) {
  const lines = [];
  for (const task of tasks) {
    const aliases = (task.aliases || []).length ? `  别名: ${task.aliases.join(", ")}` : "";
    lines.push(`${task.task.padEnd(6)} 输入=${(task.input || "").padEnd(10)} 输出=${(task.typicalOutputs || []).join("|")}${aliases}`);
  }
  $("taskCatalog").textContent = lines.join("\n");
}

function renderLoaderCatalog(loaders) {
  const lines = [];
  for (const loader of loaders) {
    const tasks = (loader.tasks || []).map(task => {
      const modes = (task.modes || []).length ? ` (${task.modes.join("|")})` : "";
      const token = task.canonical && task.canonical !== task.task ? `${task.canonical}<${task.task}` : task.task;
      return `${token}${modes}`;
    });
    lines.push(`${loader.family}: ${tasks.join(", ")}`);
    const flags = [
      loader.supportsSpeakerReference ? "speaker-ref" : null,
      loader.supportsStyleCondition ? "style-condition" : null,
      loader.supportsTimestamps ? "timestamps" : null,
    ].filter(Boolean);
    if (flags.length || (loader.languages || []).length) {
      lines.push(`             ${flags.join(" · ")}${flags.length && (loader.languages || []).length ? " · " : ""}${(loader.languages || []).join(", ")}`);
    }
  }
  $("loaderCatalog").textContent = lines.join("\n") || "（无加载器）";
}

async function loadCatalog() {
  $("catalogCount").textContent = "loading…";
  try {
    const [build, tasks, loaders] = await Promise.all([
      api("/api/build"), api("/api/tasks"), api("/api/loaders"),
    ]);
    buildInfo = build;
    taskCatalog = tasks.tasks || [];
    loaderCatalog = loaders.loaders || [];
    renderBuildFlags(build, build.flags);
    renderTaskCatalog(taskCatalog);
    renderLoaderCatalog(loaderCatalog);
    $("catalogCount").textContent = `${taskCatalog.length} tasks · ${loaderCatalog.length} loaders`;
    populateTaskSelects();
  } catch (err) {
    $("catalogCount").textContent = "unavailable";
    $("buildFlags").textContent = `native runtime unavailable: ${err.message}`;
    log("err", `读取能力目录失败：${err.message}`);
  }
}

/** Tasks whose declared input shape contains the requested modality. */
function tokensForInput(modality) {
  return taskCatalog
    .filter(task => (task.input || "").split("+").includes(modality))
    .flatMap(task => task.tokens || [task.task]);
}

function fillSelect(select, tokens, autoLabel) {
  if (!select) return;
  const previous = select.value;
  select.replaceChildren();
  const auto = document.createElement("option");
  auto.value = "";
  auto.textContent = autoLabel;
  select.append(auto);
  for (const token of tokens) {
    const option = document.createElement("option");
    option.value = token;
    const canonical = taskCatalog.find(task => (task.tokens || []).includes(token));
    option.textContent = canonical && canonical.task !== token ? `${token} → ${canonical.task}` : token;
    select.append(option);
  }
  select.value = tokens.includes(previous) || previous === "" ? previous : "";
}

function populateTaskSelects() {
  const allTokens = taskCatalog.flatMap(task => task.tokens || [task.task]);
  fillSelect($("anyTask"), allTokens, "（自动推断）");
  fillSelect($("asrTask"), tokensForInput("audio"), "asr（默认）");
  fillSelect($("ttsTask"), tokensForInput("text"), "tts（默认）");

  const streaming = new Set();
  for (const loader of loaderCatalog) {
    for (const task of loader.tasks || []) {
      if ((task.modes || []).includes("streaming")) streaming.add(task.canonical || task.task);
    }
  }
  const datalist = $("streamTaskOptions");
  datalist.replaceChildren();
  for (const token of streaming) {
    const option = document.createElement("option");
    option.value = token;
    datalist.append(option);
  }
}

$("refreshCatalog").addEventListener("click", loadCatalog);

/* ── packages / build info ── */
async function loadPackages() {
  const led = $("buildLed");
  led.className = "led busy";
  $("buildInfo").textContent = "loading catalog…";
  try {
    const data = await api("/api/packages");
    const build = data.build ?? {};
    $("buildInfo").textContent = `shim ${build.shimVersion} · audio.cpp ${String(build.audioCppCommit).slice(0, 8)} · ${build.backend}`;
    led.className = "led on";
    const packages = data.packages ?? [];
    $("packageCount").textContent = `${packages.length} packages`;
    const list = $("packageList");
    list.replaceChildren();
    for (const pkg of packages) {
      const item = document.createElement("li");
      item.className = "pkg";
      const dot = document.createElement("span");
      dot.className = `led ${pkg.installed ? "on" : "off"}`;
      const id = document.createElement("span");
      id.className = "id";
      id.textContent = pkg.id;
      const state = document.createElement("span");
      state.className = `pkg-state ${pkg.installed ? "ok" : ""}`;
      state.textContent = pkg.installed ? "READY" : "AVAILABLE";
      const install = document.createElement("button");
      install.className = "btn btn-mini";
      install.type = "button";
      install.textContent = pkg.installed ? "REINSTALL" : "INSTALL";
      install.addEventListener("click", () => downloadPackage(pkg.id, install));
      item.append(dot, id, state, install);
      list.append(item);
    }
    if (!data.modelManager && data.message) log("amber", data.message);
    log("info", `目录已加载：${packages.length} 个模型包`);
  } catch (err) {
    led.className = "led err";
    $("buildInfo").textContent = "native runtime unavailable";
    log("err", `加载模型目录失败：${err.message}`);
  }
}

async function downloadPackage(packageId, button) {
  button.disabled = true;
  $("buildLed").className = "led busy";
  log("amber", `开始下载 ${packageId} …（下载期间请勿运行推理）`);
  try {
    const result = await api("/api/packages/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ packageId, overwrite: button.textContent === "REINSTALL" }),
    });
    log("ok", result.message || `${packageId} 完成`);
    if (result.progress) log("info", result.progress);
    await Promise.all([loadPackages(), loadModels()]);
  } catch (err) {
    $("buildLed").className = "led err";
    showError(`下载 ${packageId} 失败：${err.message}`);
  } finally {
    button.disabled = false;
  }
}

$("refreshPackages").addEventListener("click", loadPackages);

/* ── detected local models ── */
async function loadModels() {
  try {
    const models = await api("/api/models");
    localModels = models;
    models.sort((a, b) => (a.task || "unknown").localeCompare(b.task || "unknown") || a.name.localeCompare(b.name));
    $("modelCount").textContent = `${models.length} folders`;
    const list = $("modelList");
    list.replaceChildren();
    for (const entry of models) {
      const item = document.createElement("li");
      item.className = "mdl";
      item.title = entry.path;
      const name = document.createElement("span");
      name.className = "name";
      name.textContent = entry.name;
      const ggufs = document.createElement("span");
      ggufs.className = "ggufs";
      ggufs.textContent = (entry.models ?? []).length ? entry.models.join(" · ") : "no gguf";
      const badge = document.createElement("span");
      badge.className = entry.manifest === false ? "mdl-badge raw" : (entry.complete ? "mdl-badge ok" : "mdl-badge bad");
      badge.textContent = entry.manifest === false ? "unmanaged" : (entry.complete ? "✓ complete" : "✗ incomplete");
      badge.title = entry.issues || "package manifest verification";
      const meta = document.createElement("span");
      meta.className = "mdl-meta";
      const taskLabels = { asr: "语音识别", tts: "语音合成", vad: "语音活动检测", music: "歌词/音乐生成", unknown: "未识别任务" };
      const canonical = (entry.canonicalTasks || []).join(", ");
      meta.textContent = `${taskLabels[entry.task] || entry.task || taskLabels.unknown} · ${(entry.family || "待推断")} · ${(entry.languages || []).join(", ") || "语言未知"}${canonical ? ` · 可跑任务 ${canonical}` : ""}`;
      const verify = document.createElement("button");
      verify.className = "btn btn-mini";
      verify.type = "button";
      verify.textContent = "VERIFY";
      verify.addEventListener("click", (event) => { event.stopPropagation(); verifyModel(entry.path, verify); });
      item.append(name, meta, ggufs, badge, verify);
      item.addEventListener("click", () => {
        // Keep the generic console pointed at whatever was clicked; the dedicated
        // decks may not have an input for this model's task.
        $("anyModelPath").value = entry.path;
        $("anyFamily").value = entry.family || "";
        const dedicated = ["asr", "tts"].includes(entry.task) ? entry.task : null;
        showTask(dedicated ?? "any");
        describeModel(entry);
        list.querySelectorAll(".mdl").forEach(node => node.classList.toggle("selected", node === item));
        if (!dedicated) {
          log("info", `${entry.name} → 通用任务控制台（${entry.task || "未识别任务"}）`);
          return;
        }
        const target = dedicated + "ModelPath";
        $(target).value = entry.path;
        $(dedicated + "Family").value = entry.family || "";
        log("info", `${entry.name} → ${dedicated === "tts" ? "TTS" : "ASR"} 模型路径`);
      });
      item.tabIndex = 0;
      item.addEventListener("keydown", event => {
        if (event.target === item && ["Enter", " "].includes(event.key)) { event.preventDefault(); item.click(); }
      });
      list.append(item);
    }
  } catch (err) {
    log("err", `扫描本地模型失败：${err.message}`);
  }
}

$("refreshModels").addEventListener("click", loadModels);

/* ── per-model package verification ── */
async function verifyModel(path, button) {
  button.disabled = true;
  try {
    const report = await api("/api/verify", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
    });
    if (report.complete) {
      log("ok", `校验通过：${report.packageId || report.path} · ${report.checkedFiles} 文件 / ${report.checkedBytes} B`);
    } else {
      log("err", `校验失败：${report.packageId || report.path}`);
      for (const issue of report.issues ?? []) {
        log("err", `  ${issue.kind}: ${issue.path}${issue.detail ? `（${issue.detail}）` : ""}`);
      }
    }
    await loadModels();
  } catch (err) {
    const message = err && err.message ? err.message : String(err);
    showError(`校验失败：${message}`);
  } finally {
    button.disabled = false;
  }
}

/* ── tabs ── */
function syncDecks() {
  const task = Object.keys(taskTabs).find(key => $(taskTabs[key]).checked) || "asr";
  showTask(task);
  const pathInput = $(task + "ModelPath");
  if (pathInput) describeModel(localModels.find(m => m.path === pathInput.value));
}
for (const tab of Object.values(taskTabs)) $(tab).addEventListener("change", syncDecks);

/* ── dropzones ── */
function wireDropzone(zoneId, inputId, nameId, clearId) {
  const zone = $(zoneId), input = $(inputId), name = $(nameId), clear = $(clearId);
  const paint = () => {
    const has = input.files.length > 0;
    zone.classList.toggle("has-file", has);
    name.hidden = !has;
    clear.hidden = !has;
    if (has) name.textContent = input.files[0].name;
  };
  zone.addEventListener("click", (event) => {
    if (event.target === clear || clear.contains(event.target)) return;
    input.click();
  });
  input.addEventListener("change", paint);
  clear.addEventListener("click", (event) => {
    event.stopPropagation();
    input.value = "";
    paint();
  });
  for (const type of ["dragenter", "dragover"]) {
    zone.addEventListener(type, (event) => { event.preventDefault(); zone.classList.add("drag"); });
  }
  for (const type of ["dragleave", "drop"]) {
    zone.addEventListener(type, (event) => { event.preventDefault(); zone.classList.remove("drag"); });
  }
  zone.addEventListener("drop", (event) => {
    const file = event.dataTransfer?.files?.[0];
    if (file) { input.files = event.dataTransfer.files; paint(); }
  });
  return paint;
}
const repaint = {
  asr: wireDropzone("asrDrop", "asrFile", "asrFileName", "asrClear"),
  tts: wireDropzone("ttsRefDrop", "ttsRefFile", "ttsRefName", "ttsRefClear"),
  stream: wireDropzone("streamDrop", "streamFile", "streamFileName", "streamClear"),
  any: wireDropzone("anyDrop", "anyFile", "anyFileName", "anyClear"),
  anyRef: wireDropzone("anyRefDrop", "anyRefFile", "anyRefName", "anyRefClear"),
};

/* ── shared result rendering ── */
function segmentLines(segments) {
  return (segments || [])
    .map(segment => `[${segment.startSample}–${segment.endSample}] ${(segment.confidence ?? 0).toFixed(2)} ${segment.text || ""}`)
    .join("\n");
}
function wordLines(words) {
  return (words || [])
    .map(word => `[${word.startSample}–${word.endSample}] ${(word.word || "")} p=${(word.confidence ?? 0).toFixed(2)}`)
    .join("\n");
}
function turnLines(turns) {
  return (turns || [])
    .map(turn => `[${turn.startSample}–${turn.endSample}] ${turn.speakerId || "?"} ${(turn.confidence ?? 0).toFixed(2)} ${turn.text || ""}`)
    .join("\n");
}
function artifactLines(artifacts, primary) {
  const all = [...(primary ? [primary] : []), ...(artifacts || [])];
  return all
    .map(artifact => `${artifact.id || "(anonymous)"} kind=${artifact.kind} bytes=${artifact.bytes}` +
      (artifact.payloadTruncated ? " （payload 过大，未回传）" : "") +
      (artifact.meta && Object.keys(artifact.meta).length ? ` meta=${JSON.stringify(artifact.meta)}` : ""))
    .join("\n");
}
function audioClipLines(clips) {
  return (clips || [])
    .map(clip => `${clip.id || "(primary)"} · ${clip.sampleRate} Hz · ${clip.channels}ch · ${clip.samples} samples · ${(clip.duration ?? 0).toFixed(2)}s`)
    .join("\n");
}

function audioClipLines(clips) {
  return (clips || [])
    .map(clip => `${clip.id || "(primary)"} · ${clip.sampleRate} Hz · ${clip.channels}ch · ${clip.samples} samples · ${(clip.duration ?? 0).toFixed(2)}s`)
    .join("\n");
}

/** Renders every audio channel into `container` as a playable, downloadable track. */
function renderAudioTracks(container, primary, named) {
  container.replaceChildren();
  const tracks = [];
  if (primary) tracks.push({ ...primary, label: "primary" });
  for (const clip of named || []) tracks.push({ ...clip, label: clip.id || "track" });
  if (tracks.length === 0) {
    container.textContent = "（该任务没有音频输出）";
    return;
  }
  for (const track of tracks) {
    const box = document.createElement("div");
    box.className = "track";
    const label = document.createElement("div");
    label.className = "track-label mono";
    label.textContent = `${track.label} · ${track.sampleRate} Hz · ${track.channels}ch · ${track.samples} samples · ${(track.duration ?? 0).toFixed(2)}s`;
    const audio = document.createElement("audio");
    audio.controls = true;
    audio.preload = "metadata";
    audio.src = track.url;
    const link = document.createElement("a");
    link.className = "btn btn-mini";
    link.href = track.url;
    link.download = track.fileName || "";
    link.textContent = "⤓ download wav";
    box.append(label, audio, link);
    container.append(box);
  }
}

function prettyJson(value) {
  try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; }
}

/** Renders the shared RunResult shape returned by /api/run, /api/asr and /api/tts. */
function renderRunResult(result, ids) {
  $(ids.wrap).hidden = false;
  if (ids.summary) {
    $(ids.summary).textContent =
      `task        ${result.task || "(未指定)"}\n` +
      `schema      ${result.schemaVersion ?? "-"}\n` +
      `text        ${result.textLanguage ? `[${result.textLanguage}] ` : ""}${result.text ? `${result.text.length} 字符` : "（无）"}\n` +
      `audio       ${result.audio ? result.audio.fileName : "（无）"}\n` +
      `named audio ${(result.namedAudio || []).length}\n` +
      `segments    ${(result.segments || []).length}\n` +
      `words       ${(result.words || []).length}\n` +
      `turns       ${(result.turns || []).length}\n` +
      `artifacts   ${(result.artifacts || []).length}${result.artifact ? " + primary" : ""}`;
  }
  if (ids.text) $(ids.text).textContent = result.text || "（空文本）";
  if (ids.audio) renderAudioTracks($(ids.audio), result.audio, result.namedAudio);
  if (ids.segments) $(ids.segments).textContent = segmentLines(result.segments) || "（无语音分段）";
  if (ids.words) $(ids.words).textContent = wordLines(result.words) || "（无词级时间戳）";
  if (ids.turns) $(ids.turns).textContent = turnLines(result.turns) || "（无说话人分段）";
  if (ids.artifacts) $(ids.artifacts).textContent = artifactLines(result.artifacts, result.artifact) || "（无结构产物）";
  if (ids.raw && result.rawJson) $(ids.raw).textContent = prettyJson(result.rawJson);
}

async function runDeck(button, endpoint, buildForm, message, render) {
  button.disabled = true;
  button.classList.add("busy");
  try {
    const result = await api(endpoint, { method: "POST", body: buildForm() });
    log("ok", message(result));
    render(result);
    return result;
  } catch (err) {
    showError(err.message);
    return null;
  } finally {
    button.disabled = false;
    button.classList.remove("busy");
  }
}

/** Appends options / threads / family, the three fields every deck shares. */
function formExtras(form, optionsId, threadsId, familyId) {
  const options = $(optionsId)?.value.trim();
  if (options) form.append("options", options);
  const threads = $(threadsId)?.value.trim();
  if (threads) form.append("threads", threads);
  const family = $(familyId)?.value.trim();
  if (family) form.append("family", family);
}

/** Appends the StyleCondition form fields; a blank field simply omits the key. */
function formStyle(form, prefix) {
  const fields = {
    styleLanguage: prefix + "StyleLanguage",
    emotion: prefix + "Emotion",
    speakingRate: prefix + "SpeakingRate",
    pitchShift: prefix + "PitchShift",
    energyScale: prefix + "EnergyScale",
  };
  for (const [key, id] of Object.entries(fields)) {
    const value = $(id)?.value.trim();
    if (value) form.append(key, value);
  }
  const tags = $(prefix + "StyleTags")?.value.trim();
  if (tags) form.append("styleTags", tags);
}

/* ── ASR deck (audio-input tasks) ── */
$("asrRun").addEventListener("click", () => {
  const file = $("asrFile").files[0];
  if (!file) { showError("请先选择要识别的 WAV 文件。"); return; }
  runDeck($("asrRun"), "/api/asr", () => {
    const form = new FormData();
    form.append("audio", file);
    form.append("modelPath", $("asrModelPath").value.trim());
    const task = $("asrTask").value.trim();
    if (task) form.append("task", task);
    const language = $("asrTextLanguage").value.trim();
    if (language) form.append("textLanguage", language);
    formExtras(form, "asrOptions", "asrThreads", "asrFamily");
    return form;
  }, (result) => `ASR 完成（task=${result.task} · ${result.samples} samples @ ${result.sampleRate} Hz）`, (result) => {
    $("asrOutWrap").hidden = false;
    $("asrOut").textContent = result.text || "（空文本：音频可能为静音）";
    const structured = ["segments", "words", "turns", "artifacts"].some(key =>
      (result[key] || []).length > 0) || result.artifact != null;
    $("asrStructured").hidden = !structured;
    $("asrSegments").textContent = segmentLines(result.segments) || "（无语音分段）";
    $("asrWords").textContent = wordLines(result.words) || "（无词级时间戳）";
    $("asrTurns").textContent = turnLines(result.turns) || "（无说话人分段）";
    $("asrArtifactListPre") ?? null;
    $("asrArtifacts").textContent = artifactLines(result.artifacts, result.artifact) || "（无结构产物）";
  });
});

/* ── TTS deck (text-input tasks) ── */
$("ttsRun").addEventListener("click", () => {
  const text = $("ttsText").value.trim();
  if (!text) { showError("请输入要合成的文本。"); return; }
  runDeck($("ttsRun"), "/api/tts", () => {
    const form = new FormData();
    form.append("text", text);
    form.append("modelPath", $("ttsModelPath").value.trim());
    const task = $("ttsTask").value.trim();
    if (task) form.append("task", task);
    const language = $("ttsTextLanguage").value.trim();
    if (language) form.append("textLanguage", language);
    const voiceId = $("ttsVoiceId").value.trim();
    if (voiceId) form.append("voiceId", voiceId);
    const reference = $("ttsRefFile").files[0];
    const referenceText = $("ttsRefText").value.trim();
    if (reference) form.append("voiceRef", reference);
    if (referenceText) form.append("referenceText", referenceText);
    const artifacts = $("ttsArtifacts").value.trim();
    if (artifacts) form.append("artifacts", artifacts);
    formStyle(form, "tts");
    formExtras(form, "ttsOptions", "ttsThreads", "ttsFamily");
    return form;
  }, (result) => `TTS 完成：${result.fileName} · ${result.duration.toFixed(2)}s @ ${result.sampleRate} Hz`, (result) => {
    $("resultPanel").hidden = false;
    $("resAudio").src = result.audioUrl;
    $("resSpec").textContent = `${result.sampleRate} Hz · ${result.channels}ch · ${result.duration.toFixed(2)}s · ${result.samples} samples`;
    $("resDownload").href = result.audioUrl;
    $("resDownload").download = result.fileName;
    $("ttsOutWrap").hidden = false;
    $("ttsNamedAudio").textContent = (result.namedAudio || [])
      .map(clip => `${clip.id || "(unnamed)"} · ${clip.sampleRate} Hz · ${clip.channels}ch · ${clip.samples} samples · ${clip.url}`)
      .join("\n") || "（无命名音轨）";
    $("ttsArtifactList").textContent = artifactLines(result.artifacts, null) || "（无结构产物）";
    $("resultPanel").scrollIntoView({ behavior: "smooth", block: "nearest" });
  });
});

/* ── streaming deck ── */
function streamPolicyLine(policy, family, task) {
  const seconds = policy.preferredChunkSeconds ? ` (${policy.preferredChunkSeconds}s)` : "";
  return `${family} · ${task} · ${policy.input}/${policy.output} · preferred chunk ${policy.preferredChunkSamples} samples${seconds}`;
}

function renderStreamEvents(result) {
  const lines = [];
  for (const event of result.events || []) {
    const at = `@${event.offset}`;
    if (event.partialText) lines.push(`${at}  partial  ${event.partialText}`);
    for (const activity of event.voiceActivity || []) {
      const span = activity.segment ? ` seg=[${activity.segment.startSample}–${activity.segment.endSample}]` : "";
      lines.push(`${at}  voice    ${activity.kind} @ ${activity.sample} p=${(activity.probability ?? 0).toFixed(2)}${span}`);
    }
    for (const turn of event.speakerTurns || []) {
      lines.push(`${at}  speaker  ${turn.speakerId} [${turn.startSample}–${turn.endSample}] p=${(turn.confidence ?? 0).toFixed(2)}`);
    }
    for (const word of event.wordTimestamps || []) {
      lines.push(`${at}  word     ${word.word} [${word.startSample}–${word.endSample}]`);
    }
    for (const artifact of event.artifacts || []) {
      lines.push(`${at}  artifact ${artifact.id} kind=${artifact.kind} bytes=${artifact.bytes}`);
    }
    if (event.isFinal) lines.push(`${at}  final`);
  }
  return lines.join("\n");
}

$("streamProbe").addEventListener("click", async () => {
  const modelPath = $("streamModelPath").value.trim();
  if (!modelPath) { showError("请先填写流式模型目录（Model path）。"); return; }
  const button = $("streamProbe");
  button.disabled = true;
  try {
    const query = new URLSearchParams({
      modelPath,
      family: $("streamFamily").value.trim(),
      task: $("streamTask").value.trim() || "vad",
    });
    const policy = await api(`/api/stream/policy?${query}`);
    $("streamOutWrap").hidden = false;
    $("streamPolicy").textContent = streamPolicyLine(policy, policy.family, policy.task);
    log("ok", `stream policy ${policy.family} ${policy.input}/${policy.output} · chunk=${policy.preferredChunkSamples}`);
  } catch (err) {
    showError(err.message);
  } finally {
    button.disabled = false;
  }
});

function buildStreamForm() {
  const form = new FormData();
  form.append("audio", $("streamFile").files[0]);
  form.append("modelPath", $("streamModelPath").value.trim());
  form.append("task", $("streamTask").value.trim() || "vad");
  const chunkMs = $("streamChunkMs").value.trim();
  if (chunkMs) form.append("chunkMs", chunkMs);
  const text = $("streamText").value.trim();
  if (text) form.append("text", text);
  const language = $("streamTextLanguage").value.trim();
  if (language) form.append("textLanguage", language);
  formExtras(form, "streamOptions", "streamThreads", "streamFamily");
  return form;
}

function renderStreamResult(result) {
  $("streamOutWrap").hidden = false;
  $("streamPolicy").textContent = `${streamPolicyLine(result.policy, result.family, result.task)}\n` +
    `chunk ${result.chunkSamples} samples · ${result.chunks} chunks · ${result.samples} samples @ ${result.sampleRate} Hz · ${result.channels}ch` +
    (result.paddedTailSamples ? ` · tail padded ${result.paddedTailSamples} samples` : "");
  $("streamEvents").textContent = renderStreamEvents(result) || "（无流式事件）";
  $("streamSegments").textContent = segmentLines(result.segments) || "（未检测到语音段）";
}

$("streamRun").addEventListener("click", () => {
  if (!$("streamFile").files[0]) { showError("请先选择要流式处理的 WAV 文件。"); return; }
  runDeck($("streamRun"), "/api/stream", buildStreamForm,
    (result) => `流式完成：${result.chunks} 块 / ${result.contentEventCount} 事件（共 ${result.eventCount} 次轮询）/ ${result.segments.length} 语音段` +
      (result.paddedTailSamples ? ` · 尾部补零 ${result.paddedTailSamples} samples` : ""),
    renderStreamResult);
});

/* ── generic task console (any task) ── */
const ANY_RESULT_IDS = {
  wrap: "anyOutWrap", summary: "anySummary", text: "anyTextOut", audio: "anyAudio",
  segments: "anySegments", words: "anyWords", turns: "anyTurns",
  artifacts: "anyArtifactList", raw: "anyRaw",
};

function buildAnyForm(requireAudio) {
  const form = new FormData();
  const modelPath = $("anyModelPath").value.trim();
  if (!modelPath) throw new Error("请填写 Model path（模型目录或 manifest）。");
  form.append("modelPath", modelPath);
  const task = $("anyTask").value.trim();
  if (task) form.append("task", task);
  const text = $("anyText").value.trim();
  if (text) form.append("text", text);
  const language = $("anyTextLanguage").value.trim();
  if (language) form.append("textLanguage", language);
  const voiceId = $("anyVoiceId").value.trim();
  if (voiceId) form.append("voiceId", voiceId);
  const audio = $("anyFile").files[0];
  if (audio) form.append("audio", audio);
  else if (requireAudio) throw new Error("流式运行需要选择 Audio input 文件。");
  const reference = $("anyRefFile").files[0];
  if (reference) form.append("voiceRef", reference);
  const referenceText = $("anyRefText").value.trim();
  if (referenceText) form.append("referenceText", referenceText);
  const artifacts = $("anyArtifacts").value.trim();
  if (artifacts) form.append("artifacts", artifacts);
  formStyle(form, "any");
  formExtras(form, "anyOptions", "anyThreads", "anyFamily");
  if (!audio && !text) throw new Error("请至少提供 Text 或 Audio input 之一。");
  return form;
}

function resetAnyForm() {
  for (const id of ["anyText", "anyTextLanguage", "anyVoiceId", "anyRefText", "anyStyleLanguage",
                    "anyEmotion", "anySpeakingRate", "anyPitchShift", "anyEnergyScale",
                    "anyStyleTags", "anyOptions", "anyArtifacts", "anyThreads", "anyChunkMs"]) {
    if ($(id)) $(id).value = "";
  }
  $("anyTask").value = "";
  $("anyFile").value = "";
  $("anyRefFile").value = "";
  repaint.any();
  repaint.anyRef();
  $("anyOutWrap").hidden = true;
  log("info", "通用任务表单已重置");
}

$("anyClearForm").addEventListener("click", resetAnyForm);

$("anyRun").addEventListener("click", () => {
  let form;
  try { form = buildAnyForm(false); } catch (err) { showError(err.message); return; }
  runDeck($("anyRun"), "/api/run", () => form, (result) =>
    `任务完成：task=${result.task} · schema=${result.schemaVersion} · 音频 ${result.audio ? 1 : 0}+${(result.namedAudio || []).length} · 分段 ${(result.segments || []).length} · 产物 ${(result.artifacts || []).length}`,
    (result) => renderRunResult(result, ANY_RESULT_IDS));
});

$("anyRunStream").addEventListener("click", () => {
  let form;
  try { form = buildAnyForm(true); } catch (err) { showError(err.message); return; }
  const chunkMs = $("anyChunkMs").value.trim();
  if (chunkMs) form.append("chunkMs", chunkMs);
  runDeck($("anyRunStream"), "/api/stream", () => form,
    (result) => `流式任务完成：${result.task} · ${result.chunks} 块 / ${result.contentEventCount} 事件 / ${result.segments.length} 语音段`,
    renderStreamResult);
});

/* ── boot ── */
(async () => {
  syncDecks();
  try {
    await loadConfig();
    log("info", "workbench 已连接");
  } catch (err) {
    log("err", `读取配置失败：${err.message}`);
  }
  await Promise.all([loadPackages(), loadModels(), loadCatalog()]);
})();
