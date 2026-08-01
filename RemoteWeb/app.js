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

  pinInput.value = pin;

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
    folderLine.textContent = `フォルダ: ${folderLabel}`;
    folderLine.title = st.folderPath || "";
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
