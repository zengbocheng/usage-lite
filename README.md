# Usage Lite

Windows 自用额度查看器。首版只保留 Codex / Claude 额度、托盘菜单和原生面板。

## 使用

打开 `release\UsageLite.exe`。左键点击蓝色托盘图标打开面板，右键显示菜单。

- 默认托盘数字显示 Codex 剩余额度；优先 5 小时窗口，缺少该窗口时使用每周窗口。
- 右键可切换 Codex / Claude，以及优先显示的额度窗口。菜单限制在当前屏幕工作区内，避开任务栏。
- 默认开启任务栏额度标签，显示透明的 `Codex: 71%` 文字，10pt 常规字体；与托盘共用额度来源和窗口设置。左键打开/隐藏面板，右键打开同一菜单。取消勾选“任务栏显示额度标签”可关闭，重启后保留手动选择。
- 标签目前适配 Windows 10 的横向原生任务栏，会临时收窄应用图标区域来预留空间并避开天气组件；关闭标签或正常退出时恢复。全屏和自动隐藏时隐藏标签，Explorer 恢复后自动重新定位。竖向、空间不足或不支持的任务栏仅显示托盘图标。
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

C# / .NET 10 Windows Forms。没有 Python、pythonnet、pywebview、WebView2 或目录监听。任务栏定位通过 Win32 窗口几何信息完成，不使用 UI Automation 客户端或辅助功能轮询线程；WinForms 可按 Windows 请求加载自身控件的系统辅助功能提供程序。所有窗口、菜单、图标创建与销毁都在 UI 主线程；文件读取在单一后台任务中执行，避免并发刷新。

SQLite 通过 Microsoft.Data.Sqlite 与 SQLitePCLRaw 的 Windows 系统 SQLite provider 读取；缓存解压使用托管实现 ZstdSharp.Port。Windows 10 或更新的 64 位系统。

## 构建与测试

需要 .NET 10 SDK。发布产物自带运行时，使用者无需安装 SDK。

```powershell
.\build.ps1 -Test -Publish
```

`build.ps1` 会优先使用 PATH 中的 dotnet；本项目所在工作区还可使用 `..\build\usage-lite-tools\dotnet\dotnet.exe`。

测试使用独立临时目录与合成缓存，覆盖窗口分类、SQLite 只读、缓存解压、组织切换、错误/过期数据及文件共享；不会向真实 Claude / Codex 数据写入测试文件。结果保存在 `artifacts\test-results.json`。

面板布局回归测试：`release\UsageLite.exe --panel-smoke-test --output artifacts\panel-layout\results.json`。使用合成额度，检查窗口放大、隐藏后切换 DPI（100%–225%）及重新打开时的卡片比例，生成预览图；不会改动系统缩放或当前托盘实例。

可生成本机额度快照用于本地诊断：

```powershell
.\release\UsageLite.exe --snapshot --output .\artifacts\snapshot.json
```

## 来源与许可

AGPL-3.0-only，见 LICENSE。Claude Desktop 历史/Chromium 缓存解析格式与部分边界处理、测试用合成缓存参考并改写自 [aqua5230/usage](https://github.com/aqua5230/usage)（Copyright 2026 lollapalooza 及贡献者）。本应用是独立 C# 实现；不加载或执行原项目 Python 代码。

第三方软件许可随发布目录附带：Microsoft.Data.Sqlite / SQLitePCLRaw / ZstdSharp.Port，以及 .NET 运行时自带的许可证和第三方声明。
