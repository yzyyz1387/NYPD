# N网下载器-NYPD

NYPD - Nexus YZ Patrol Downloader 是一个面向 Nexus Mods 手动下载链接的 Windows 下载管理器。

浏览器插件捕捉到 N 网下载请求后，会把最终下载链接交给本机下载器，随后取消浏览器自己的下载任务。下载器支持下载队列、暂停/继续、取消、历史记录、日志、代理配置、分片下载和浏览器插件安装提示。

## 使用

1. 运行本机下载器。
2. 在下载页或设置页点击“安装浏览器插件”。
3. 按弹窗提示打开 Edge/Chrome 扩展页面，开启开发人员模式，选择释放出的插件目录。
4. 在 Nexus Mods 点击 Manual download，下载请求会自动进入本机下载器。

也可以在下载页输入下载链接后按回车，或点击下载图标手动加入队列。

## 开发检查

```powershell
dotnet run --project .\native -- --self-check
dotnet build .\native
```

## 发布更新

创建 GitHub Release，例如 `v1.0.5.1`。工作流会构建发布包、同步到 Gitee Release，并生成 `latest.json` 供软件检查更新使用。
