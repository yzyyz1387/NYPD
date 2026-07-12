$extensionId = 'deppclipjakbhemmkjblhkgepkglggbj'
$publish = Join-Path $PSScriptRoot 'publish'
dotnet publish (Join-Path $PSScriptRoot 'NexusModsDownloader.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$hostManifest = (Get-Content -Raw (Join-Path $PSScriptRoot 'native-host.template.json')).Replace('__EXTENSION_ID__', $extensionId).Replace('__EXECUTABLE_PATH__', (Join-Path $publish 'NYPD.exe').Replace('\', '\\'))
$manifestPath = Join-Path $PSScriptRoot 'native-host.json'
Set-Content -Path $manifestPath -Value $hostManifest -NoNewline
@(
  'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.nexusmods.downloader',
  'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.nexusmods.downloader'
) | ForEach-Object {
  New-Item -Path $_ -Force | Out-Null
  Set-ItemProperty -Path $_ -Name '(Default)' -Value $manifestPath
}
Write-Host "Installed Native Messaging host for Chrome and Edge. Reload the extension, then start a Nexus Mods manual download."
