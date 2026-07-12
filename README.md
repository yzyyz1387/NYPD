# Nexus Mods Downloader

Windows download manager: a Chromium extension observes a Nexus Mods browser download, obtains its final temporary CDN URL, then hands it to one local desktop window. The page shows queued, active, paused, cancelled, completed, and failed downloads with live transfer speed.

It does not use an API key, read browser cookies, proxy files, scrape Nexus, or use parallel connections to bypass account speed limits.

## Run it

1. Optional: choose the destination folder with `setx NEXUSMODS_DOWNLOAD_DIR "D:\Mods"`.

2. In Edge, open `edge://extensions` (or Chrome's `chrome://extensions`), enable Developer mode, choose **Load unpacked**, and select `extension`.

3. Install the native host for both Edge and Chrome:

   ```powershell
   .\native\Install-NativeHost.ps1
   ```

4. Reload the extension, restart Edge/Chrome, and start a **Manual download** on Nexus Mods. The browser begins the download just long enough to reveal the final CDN URL; the extension queues it in the local downloader, then cancels the browser's copy. Successful downloads go to `Downloads\Nexus Mods` by default.

To open the manager without starting a download:

```powershell
.\native\publish\NexusModsDownloader.exe --show
```

During development, use `dotnet run --project .\native -- --show` instead.

## Checks

```powershell
dotnet run --project .\native -- --self-check
dotnet build .\native
```

## Scope

This version accepts only signed HTTPS URLs from Nexus-owned domains. Large files use up to four Range segments only when the CDN supports them; a server-side account or total-speed limit still applies. The visible queue is kept only while the app is open; a captured URL can expire, so restart the manual download in Edge to obtain a fresh one.
