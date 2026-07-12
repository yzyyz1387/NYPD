const enabled = document.querySelector("#enabled");
const minMb = document.querySelector("#minMb");
const dot = document.querySelector("#dot");
const connection = document.querySelector("#connection");

async function refresh() {
  const stored = await chrome.storage.local.get({ interceptEnabled: true, interceptMinMb: 5 });
  enabled.checked = stored.interceptEnabled;
  minMb.value = stored.interceptMinMb;
  try {
    const reply = await chrome.runtime.sendNativeMessage("com.nexusmods.downloader", { type: "ping" });
    if (!reply?.ok) throw new Error();
    dot.className = "online";
    connection.textContent = "桌面下载器已连接";
  } catch {
    dot.className = "offline";
    connection.textContent = "未检测到桌面下载器";
  }
}

enabled.addEventListener("change", () => chrome.storage.local.set({ interceptEnabled: enabled.checked }));
minMb.addEventListener("change", () => {
  const value = Math.max(0, Number(minMb.value) || 0);
  minMb.value = value;
  chrome.storage.local.set({ interceptMinMb: value });
});
refresh();
