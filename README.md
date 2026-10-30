# Usage Lite

Windows 自用额度查看器。首版只保留 Codex / Claude 额度、托盘菜单和原生面板。

## 使用

打开 `release\UsageLite.exe`。左键点击蓝色托盘图标打开面板，右键显示菜单。

- 默认托盘数字显示 Codex 剩余额度；优先 5 小时窗口，缺少该窗口时使用每周窗口。
- 右键可切换 Codex / Claude，以及优先显示的额度窗口。菜单限制在当前屏幕工作区内，避开任务栏。
- 每 1 分钟重新读取本地记录；“刷新本地记录”不会向服务端请求额度。
- 面板采用无边框圆角窗口和圆角刷新按钮，拖动顶部标题可移动；右上角关闭按钮或 Esc 仅隐藏面板，完全退出请用托盘菜单“退出”。
- 未知或已经到重置时间的额度显示 `—`。不推算重置时间，不把缺失数据当作 100%。
- Claude Desktop 缓存超过两小时不作为当前额度；在 Claude 桌面端查看用量后可更新本地缓存。

## 数据与设置

- Codex：`CODEX_HOME` 或 `%USERPROFILE%\.codex` 下的 `sessions`、`archived_sessions` 和 `logs_2.sqlite`，数据库以只读方式连接。
- Claude Desktop：普通安装版和 Microsoft Store 版的 `plan-usage-history.json`、同一组织的本地 Chromium HTTP 缓存。
- 已存在的 Claude Code `usage-status.json` 可作兼容数据源。不安装或修改 hooks。
- 不读取登录凭据、不解密 Cookie、不发网络请求。只写自己的 `%LOCALAPPDATA%\UsageLite\settings.json` 和轮转日志 `app.log`。
- 源码、构建产物与原 usage 独立。没有开机启动设置，没有自动升级，没有后台观察任务。

## 实现

C# / .NET 10 Windows Forms。没有 Python、pythonnet、pywebview、WebView2、UI Automation 或目录监听。所有窗口、菜单、图标创建与销毁都在 UI 主线程；文件读取在单一后台任务中执行，避免并发刷新。

SQLite 通过 Microsoft.Data.Sqlite 与 SQLitePCLRaw 的 Windows 系统 SQLite provider 读取；缓存解压使用托管实现 ZstdSharp.Port。Windows 10 或更新的 64 位系统。

## 构建与测试

需要 .NET 10 SDK。发布产物自带运行时，使用者无需安装 SDK。

```powershell
.\build.ps1 -Test -Publish
```

`build.ps1` 会优先使用 PATH 中的 dotnet；本项目所在工作区还可使用 `..\build\usage-lite-tools\dotnet\dotnet.exe`。

测试使用独立临时目录与合成缓存，覆盖窗口分类、SQLite 只读、缓存解压、组织切换、错误/过期数据及文件共享；不会向真实 Claude / Codex 数据写入测试文件。结果保存在 `artifacts\test-results.json`。

可生成本机额度快照用于本地诊断：

```powershell
.\release\UsageLite.exe --snapshot --output .\artifacts\snapshot.json
```

## 来源与许可

AGPL-3.0-only，见 LICENSE。Claude Desktop 历史/Chromium 缓存解析格式与部分边界处理、测试用合成缓存参考并改写自 [aqua5230/usage](https://github.com/aqua5230/usage)（Copyright 2026 lollapalooza 及贡献者）。本应用是独立 C# 实现；不加载或执行原项目 Python 代码。

第三方软件许可随发布目录附带：Microsoft.Data.Sqlite / SQLitePCLRaw / ZstdSharp.Port，以及 .NET 运行时自带的许可证和第三方声明。
