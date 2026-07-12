const host = "com.nexusmods.downloader";
const defaultInterceptMinMb = 5;

chrome.runtime.onInstalled.addListener(async () => {
  const stored = await chrome.storage.local.get(["interceptEnabled", "interceptMinMb"]);
  await chrome.storage.local.set({
    interceptEnabled: stored.interceptEnabled ?? true,
    interceptMinMb: stored.interceptMinMb ?? defaultInterceptMinMb
  });
});

function isNexusDownload(url) {
  try {
    const { protocol, hostname } = new URL(url);
    return protocol === "https:" && ["nexusmods.com", "nexus-cdn.com"].some(domain =>
      hostname === domain || hostname.endsWith(`.${domain}`));
  } catch {
    return false;
  }
}

function knownDownloadSize(download) {
  const size = download.fileSize > 0 ? download.fileSize : download.totalBytes;
  return Number.isFinite(size) && size > 0 ? size : null;
}

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitKnownDownloadSize(download) {
  let size = knownDownloadSize(download);
  for (let attempt = 0; size === null && attempt < 5; attempt++) {
    await delay(200);
    const [current] = await chrome.downloads.search({ id: download.id });
    if (!current) return null;
    size = knownDownloadSize(current);
  }
  return size;
}

chrome.downloads.onCreated.addListener(async download => {
  const { interceptEnabled, interceptMinMb } = await chrome.storage.local.get({ interceptEnabled: true, interceptMinMb: defaultInterceptMinMb });
  if (!interceptEnabled) return;
  if (!isNexusDownload(download.finalUrl)) return;
  const size = await waitKnownDownloadSize(download);
  const parsedMinMb = Number(interceptMinMb);
  const minMb = Number.isFinite(parsedMinMb) && parsedMinMb >= 0 ? parsedMinMb : defaultInterceptMinMb;
  const minBytes = minMb * 1024 * 1024;
  if (size !== null && size < minBytes) return;
  const [current] = await chrome.downloads.search({ id: download.id });
  const filename = (current?.filename || download.filename || "").split(/[\\/]/).pop();

  try {
    const result = await chrome.runtime.sendNativeMessage(host, {
      type: "download",
      downloadUrl: download.finalUrl,
      referrer: download.referrer,
      filename
    });
    if (result?.ok) await chrome.downloads.cancel(download.id);
    else console.error("N网下载器-NYPD:", result?.error ?? "queue failed");
  } catch (error) {
    console.error("N网下载器-NYPD:", error);
  }
});
