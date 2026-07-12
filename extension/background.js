const host = "com.nexusmods.downloader";

chrome.runtime.onInstalled.addListener(() => chrome.storage.local.set({ interceptEnabled: true }));

function isNexusDownload(url) {
  try {
    const { protocol, hostname } = new URL(url);
    return protocol === "https:" && ["nexusmods.com", "nexus-cdn.com"].some(domain =>
      hostname === domain || hostname.endsWith(`.${domain}`));
  } catch {
    return false;
  }
}

chrome.downloads.onCreated.addListener(async download => {
  const { interceptEnabled } = await chrome.storage.local.get({ interceptEnabled: true });
  if (!interceptEnabled) return;
  if (!isNexusDownload(download.finalUrl)) return;

  try {
    const result = await chrome.runtime.sendNativeMessage(host, {
      type: "download",
      downloadUrl: download.finalUrl,
      referrer: download.referrer
    });
    if (result?.ok) await chrome.downloads.cancel(download.id);
    else console.error("N网下载器-NYPD:", result?.error ?? "queue failed");
  } catch (error) {
    console.error("N网下载器-NYPD:", error);
  }
});
