/* ══ AudioCpp.NET workbench · front-end ══ */
"use strict";

const $ = (id) => document.getElementById(id);

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
    await Promise.all([loadPackages(), loadModels()]);
  } catch (err) {
    showError(err.message);
  } finally {
    button.disabled = false;
  }
});

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
      item.append(name, ggufs);
      item.addEventListener("click", () => {
        const target = $("tabTts").checked ? "ttsModelPath" : "asrModelPath";
        $(target).value = entry.path;
        list.querySelectorAll(".mdl").forEach((node) => node.classList.toggle("selected", node === item));
        log("info", `${entry.name} → ${target === "ttsModelPath" ? "TTS" : "ASR"} 模型路径`);
      });
      list.append(item);
    }
  } catch (err) {
    log("err", `扫描本地模型失败：${err.message}`);
  }
}

$("refreshModels").addEventListener("click", loadModels);

/* ── tabs ── */
function syncDecks() {
  const tts = $("tabTts").checked;
  $("deckAsr").hidden = tts;
  $("deckTts").hidden = !tts;
}
$("tabAsr").addEventListener("change", syncDecks);
$("tabTts").addEventListener("change", syncDecks);

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
}
wireDropzone("asrDrop", "asrFile", "asrFileName", "asrClear");
wireDropzone("ttsRefDrop", "ttsRefFile", "ttsRefName", "ttsRefClear");

/* ── inference runs ── */
function formExtras(form, optionsId, threadsId, familyId) {
  const options = $(optionsId).value.trim();
  if (options) form.append("options", options);
  const threads = $(threadsId).value.trim();
  if (threads) form.append("threads", threads);
  form.append("family", $(familyId).value.trim());
}

async function runDeck(button, buildForm, onDone) {
  button.disabled = true;
  button.classList.add("busy");
  try {
    const result = await api(onDone.endpoint, { method: "POST", body: buildForm() });
    log("ok", onDone.message(result));
    onDone.render(result);
  } catch (err) {
    showError(err.message);
  } finally {
    button.disabled = false;
    button.classList.remove("busy");
  }
}

$("asrRun").addEventListener("click", () => {
  const file = $("asrFile").files[0];
  if (!file) { showError("请先选择要识别的 WAV 文件。"); return; }
  runDeck($("asrRun"), () => {
    const form = new FormData();
    form.append("audio", file);
    form.append("modelPath", $("asrModelPath").value.trim());
    formExtras(form, "asrOptions", "asrThreads", "asrFamily");
    return form;
  }, {
    endpoint: "/api/asr",
    message: (result) => `ASR 完成（${result.samples} samples @ ${result.sampleRate} Hz）`,
    render: (result) => {
      $("asrOutWrap").hidden = false;
      $("asrOut").textContent = result.text || "（空文本：音频可能为静音）";
    },
  });
});

$("ttsRun").addEventListener("click", () => {
  const text = $("ttsText").value.trim();
  if (!text) { showError("请输入要合成的文本。"); return; }
  runDeck($("ttsRun"), () => {
    const form = new FormData();
    form.append("text", text);
    form.append("modelPath", $("ttsModelPath").value.trim());
    const reference = $("ttsRefFile").files[0];
    const referenceText = $("ttsRefText").value.trim();
    if (reference) form.append("voiceRef", reference);
    if (referenceText) form.append("referenceText", referenceText);
    formExtras(form, "ttsOptions", "ttsThreads", "ttsFamily");
    return form;
  }, {
    endpoint: "/api/tts",
    message: (result) => `TTS 完成：${result.fileName} · ${result.duration.toFixed(2)}s @ ${result.sampleRate} Hz`,
    render: (result) => {
      $("resultPanel").hidden = false;
      $("resAudio").src = result.audioUrl;
      $("resSpec").textContent = `${result.sampleRate} Hz · ${result.channels}ch · ${result.duration.toFixed(2)}s · ${result.samples} samples`;
      $("resDownload").href = result.audioUrl;
      $("resDownload").download = result.fileName;
      $("resultPanel").scrollIntoView({ behavior: "smooth", block: "nearest" });
    },
  });
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
  await loadPackages();
  await loadModels();
})();