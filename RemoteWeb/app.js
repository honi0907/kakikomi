(() => {
  const auth = document.getElementById("auth");
  const app = document.getElementById("app");
  const pinInput = document.getElementById("pin");
  const authError = document.getElementById("authError");
  const connectBtn = document.getElementById("connectBtn");
  const disconnectBtn = document.getElementById("disconnectBtn");
  const playBtn = document.getElementById("playBtn");
  const backBtn = document.getElementById("backBtn");
  const fwdBtn = document.getElementById("fwdBtn");
  const clearBtn = document.getElementById("clearBtn");
  const netaList = document.getElementById("netaList");
  const statusLine = document.getElementById("statusLine");
  const folderLine = document.getElementById("folderLine");
  const reloadNetasBtn = document.getElementById("reloadNetasBtn");
  const netaLoopBtn = document.getElementById("netaLoopBtn");
  const restartAppBtn = document.getElementById("restartAppBtn");
  const tabNetas = document.getElementById("tabNetas");
  const tabSaves = document.getElementById("tabSaves");
  const panelNetas = document.getElementById("panelNetas");
  const panelSaves = document.getElementById("panelSaves");
  const reloadSavesBtn = document.getElementById("reloadSavesBtn");
  const saveFolderLine = document.getElementById("saveFolderLine");
  const saveList = document.getElementById("saveList");
  const saveListEmpty = document.getElementById("saveListEmpty");
  const saveViewer = document.getElementById("saveViewer");
  const saveViewerImg = document.getElementById("saveViewerImg");
  const saveViewerTitle = document.getElementById("saveViewerTitle");
  const saveViewerClose = document.getElementById("saveViewerClose");
  const timeText = document.getElementById("timeText");
  const seekBar = document.getElementById("seekBar");
  const preview = document.getElementById("preview");
  const previewPh = document.getElementById("previewPlaceholder");

  let ws = null;
  let pin = localStorage.getItem("kakikomiRemotePin") || "";
  let previewUrl = null;
  let seeking = false;
  let lastPreviewSent = 0;
  let latestStatus = null;
  let reloading = false;
  let loopOn = false;
  let savesLoading = false;

  pinInput.value = pin;

  function apiHeaders() {
    const headers = {};
    if (pin) headers["X-Kakikomi-Pin"] = pin;
    return headers;
  }

  function pinQuery() {
    return pin ? `?pin=${encodeURIComponent(pin)}` : "";
  }

  function saveImageUrl(name) {
    return `/api/saves/${encodeURIComponent(name)}${pinQuery()}`;
  }

  function formatBytes(n) {
    if (!Number.isFinite(n) || n < 0) return "";
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  }

  function formatSaveTime(iso) {
    try {
      const d = new Date(iso);
      return d.toLocaleString("ja-JP", {
        month: "numeric",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      });
    } catch {
      return "";
    }
  }

  function setSidebarTab(which) {
    const saves = which === "saves";
    tabNetas.classList.toggle("active", !saves);
    tabSaves.classList.toggle("active", saves);
    tabNetas.setAttribute("aria-selected", saves ? "false" : "true");
    tabSaves.setAttribute("aria-selected", saves ? "true" : "false");
    panelNetas.classList.toggle("hidden", saves);
    panelSaves.classList.toggle("hidden", !saves);
    if (saves) loadSaves();
  }

  async function loadSaves() {
    if (savesLoading) return;
    savesLoading = true;
    reloadSavesBtn.disabled = true;
    reloadSavesBtn.textContent = "読み込み中…";
    saveList.innerHTML = "";
    saveListEmpty.classList.add("hidden");
    try {
      const res = await fetch("/api/saves", { headers: apiHeaders() });
      const data = await res.json();
      if (!data.ok) {
        saveListEmpty.textContent = data.error || "読み込みに失敗しました";
        saveListEmpty.classList.remove("hidden");
        return;
      }
      saveFolderLine.textContent = `保存: ${data.folderPath || "—"}`;
      saveFolderLine.title = data.folderPath || "";
      const files = data.files || [];
      if (files.length === 0) {
        saveListEmpty.textContent = "保存された PNG はありません";
        saveListEmpty.classList.remove("hidden");
        return;
      }
      files.forEach((f) => {
        const b = document.createElement("button");
        b.type = "button";
        b.className = "save-item";
        const name = document.createElement("span");
        name.className = "save-item-name";
        name.textContent = f.name;
        const meta = document.createElement("span");
        meta.className = "save-item-meta";
        meta.textContent = `${formatSaveTime(f.modifiedUtc)} · ${formatBytes(f.sizeBytes)}`;
        b.appendChild(name);
        b.appendChild(meta);
        b.onclick = () => openSaveViewer(f.name);
        saveList.appendChild(b);
      });
    } catch {
      saveListEmpty.textContent = "読み込みに失敗しました";
      saveListEmpty.classList.remove("hidden");
    } finally {
      savesLoading = false;
      reloadSavesBtn.disabled = false;
      reloadSavesBtn.textContent = "保存一覧を更新";
    }
  }

  function openSaveViewer(name) {
    saveViewerTitle.textContent = name;
    saveViewerImg.src = saveImageUrl(name);
    saveViewer.classList.remove("hidden");
    saveViewer.setAttribute("aria-hidden", "false");
  }

  function closeSaveViewer() {
    saveViewer.classList.add("hidden");
    saveViewer.setAttribute("aria-hidden", "true");
    saveViewerImg.removeAttribute("src");
    saveViewerTitle.textContent = "";
  }

  function wsUrl() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    const q = pin ? `?pin=${encodeURIComponent(pin)}` : "";
    return `${proto}//${location.host}/ws${q}`;
  }

  function send(cmd, extra = {}) {
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ cmd, ...extra }));
  }

  /** 本家 FormatClock と同じ 0.1 秒表示 */
  function fmt(sec) {
    if (!Number.isFinite(sec) || sec < 0) sec = 0;
    sec = Math.round(sec * 10) / 10;
    let tenths = Math.round((sec - Math.floor(sec)) * 10);
    if (tenths >= 10) {
      sec = Math.floor(sec) + 1;
      tenths = 0;
    }
    const total = Math.floor(sec);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = (total % 60).toString().padStart(2, "0");
    if (h >= 1)
      return `${h}:${m.toString().padStart(2, "0")}:${s}.${tenths}`;
    return `${m}:${s}.${tenths}`;
  }

  function applyStatus(st) {
    if (!st || !st.ok) return;
    latestStatus = st;

    const folderLabel = st.folderName || st.folderPath || "未設定";
    folderLine.textContent = `ネタ: ${folderLabel}`;
    folderLine.title = st.folderPath || "";
    if (st.saveFolderPath) {
      saveFolderLine.textContent = `保存: ${st.saveFolderPath}`;
      saveFolderLine.title = st.saveFolderPath;
    }
    reloadNetasBtn.disabled = !st.folderPath || reloading;

    loopOn = !!st.netaLoop;
    netaLoopBtn.classList.toggle("active", loopOn);
    netaLoopBtn.textContent = loopOn ? "ループ停止" : "ネタをループ";

    const loopLabel = loopOn ? " · ループ中" : "";
    const base = `${st.displayName || "未選択"} · ${st.playing ? "再生中" : "停止"} · ${st.rate}x${loopLabel}`;
    statusLine.textContent = st.statusText ? `${base}\n${st.statusText}` : base;
    playBtn.classList.toggle("playing", !!st.playing);

    document.querySelectorAll("button.rate").forEach((btn) => {
      const r = Number(btn.dataset.rate);
      btn.classList.toggle("active", Math.abs(r - Number(st.rate)) < 0.01);
    });

    const dur = Number(st.durationSec) || 0;
    const pos = Number(st.positionSec) || 0;
    timeText.textContent = `${fmt(pos)} / ${fmt(dur)}`;

    if (!seeking) {
      seekBar.max = Math.max(0, dur);
      seekBar.value = String(Math.min(pos, dur));
      seekBar.disabled = !st.hasTimeline || dur <= 0;
    }

    const selectedPath = (st.path || "").toLowerCase();
    netaList.innerHTML = "";
    (st.netas || []).forEach((n) => {
      const b = document.createElement("button");
      b.className = "neta";
      if (n.missing) b.classList.add("missing");
      if (selectedPath && n.path && n.path.toLowerCase() === selectedPath)
        b.classList.add("selected");
      b.textContent = n.name || n.path;
      b.disabled = !!n.missing;
      b.onclick = () => send("selectNeta", { path: n.path });
      netaList.appendChild(b);
    });
  }

  function connect() {
    authError.textContent = "";
    pin = pinInput.value || "";
    localStorage.setItem("kakikomiRemotePin", pin);

    if (ws) {
      try { ws.close(); } catch {}
      ws = null;
    }

    ws = new WebSocket(wsUrl());
    ws.binaryType = "arraybuffer";

    ws.onopen = () => {
      auth.classList.add("hidden");
      app.classList.remove("hidden");
      send("refresh");
      if (!panelSaves.classList.contains("hidden")) loadSaves();
    };

    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") {
        try { applyStatus(JSON.parse(ev.data)); } catch {}
        return;
      }

      const blob = new Blob([ev.data], { type: "image/jpeg" });
      const url = URL.createObjectURL(blob);
      if (previewUrl) URL.revokeObjectURL(previewUrl);
      previewUrl = url;
      preview.src = url;
      previewPh.classList.add("hidden");
    };

    ws.onclose = () => {
      auth.classList.remove("hidden");
      app.classList.add("hidden");
      authError.textContent = "切断されました。再接続してください。";
    };

    ws.onerror = () => {
      authError.textContent = "接続に失敗しました。PIN・ポート・ファイアウォールを確認してください。";
    };
  }

  function onSeekInput() {
    seeking = true;
    const seconds = Number(seekBar.value);
    const dur = Number(seekBar.max) || 0;
    timeText.textContent = `${fmt(seconds)} / ${fmt(dur)}`;
    const now = performance.now();
    // 本家スライダーに近い細かさ（約 60fps 上限でプレビュー送信）
    if (now - lastPreviewSent > 16) {
      lastPreviewSent = now;
      send("seekPreview", { seconds });
    }
  }

  function onSeekCommit() {
    const seconds = Number(seekBar.value);
    send("seek", { seconds });
    seeking = false;
  }

  connectBtn.onclick = connect;
  disconnectBtn.onclick = () => { if (ws) ws.close(); };
  playBtn.onclick = () => send("playPause");
  backBtn.onclick = () => send("skipBack");
  fwdBtn.onclick = () => send("skipForward");
  clearBtn.onclick = () => send("clearInk");
  reloadNetasBtn.onclick = () => {
    if (reloading) return;
    reloading = true;
    reloadNetasBtn.disabled = true;
    reloadNetasBtn.textContent = "更新中…";
    send("reloadNetas");
    setTimeout(() => {
      reloading = false;
      reloadNetasBtn.textContent = "一覧を更新";
      if (latestStatus)
        reloadNetasBtn.disabled = !latestStatus.folderPath;
    }, 1200);
  };

  netaLoopBtn.onclick = () => {
    if (loopOn) {
      send("netaLoop", { enabled: false });
    } else {
      send("netaLoop", { enabled: true });
    }
  };

  tabNetas.onclick = () => setSidebarTab("netas");
  tabSaves.onclick = () => setSidebarTab("saves");
  reloadSavesBtn.onclick = () => loadSaves();
  saveViewerClose.onclick = closeSaveViewer;
  saveViewer.onclick = (e) => {
    if (e.target === saveViewer) closeSaveViewer();
  };

  restartAppBtn.onclick = () => {
    if (!confirm(
      "Kakikomi を強制再起動します。\n" +
      "通信は切断されます。よろしいですか？"
    )) {
      return;
    }
    send("restartApp");
    authError.textContent = "再起動を送信しました…";
    auth.classList.remove("hidden");
    app.classList.add("hidden");
    if (ws) {
      try { ws.close(); } catch {}
      ws = null;
    }
  };

  document.querySelectorAll("button.rate").forEach((btn) => {
    btn.onclick = () => send("rate", { rate: Number(btn.dataset.rate) });
  });

  seekBar.addEventListener("pointerdown", () => { seeking = true; });
  seekBar.addEventListener("input", onSeekInput);
  seekBar.addEventListener("change", onSeekCommit);
  seekBar.addEventListener("pointerup", onSeekCommit);
  seekBar.addEventListener("touchend", onSeekCommit);

  pinInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") connect();
  });
})();
