const enabled = document.querySelector("#enabled");
const dot = document.querySelector("#dot");
const connection = document.querySelector("#connection");

async function refresh() {
  const stored = await chrome.storage.local.get({ interceptEnabled: true });
  enabled.checked = stored.interceptEnabled;
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
refresh();
