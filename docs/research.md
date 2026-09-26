# 数据源与工程决定

核对日期：2026-09-25。

## 2026-09-26 展开面板外部点击收起

- 移除标题栏向上箭头，以外部点击收起展开卡片；自由悬浮模式回到胶囊，任务栏模式隐藏卡片并保留任务栏胶囊。仅鼠标离开不收起。
- 参考 [EarTrumpet MouseHook 源码](https://github.com/File-New-Project/EarTrumpet/blob/master/EarTrumpet/Interop/Helpers/MouseHook.cs) 的委托保活、钩子生命周期和 `CallNextHookEx` 转发方式，以及 [Issue #897](https://github.com/File-New-Project/EarTrumpet/issues/897) 暴露的 Shell / 前台切换关闭边界；没有照搬其鼠标滚轮处理。QuotaPeek 保留 `NOACTIVATE`，通过 `WH_MOUSE_LL` 识别外部按键按下，不依赖从未获得过的焦点。
- 按 [Microsoft LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc) 的回调约束，只在回调中读取点击目标并投递 Dispatcher；布局、隐藏和设置持久化都在回调返回后执行，始终放行原点击。监听仅在面板展开且可见时启用，收起 / 隐藏 / 退出时解除；代次编号防止延迟的旧点击关闭重新打开的面板。
- 使用点击时的 `WindowFromPoint` 与 PID 识别应用自身窗口，涵盖设置、WPF 菜单和 Explorer 中的任务栏子窗口，防止同一次胶囊点击被关闭逻辑与切换逻辑处理两次。打开设置时暂缓外部收起，避免 WPF 隐藏 owner 时连带隐藏编辑窗口。
- 真实测试发现任务栏定时定位会把主面板提到自身右键菜单上方。定位改用 [SetWindowPos 的 SWP_NOZORDER](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)，保留初始化时的 TOPMOST 样式和弹出菜单顺序；回归检查等待一个完整任务栏定位周期后，验证设置菜单项仍然可被鼠标命中。

## 2026-09-26 供应商卡片分组

- 参考 [CodexBar 的 AccountMenuLayoutPlanner](https://github.com/steipete/CodexBar/blob/main/Sources/CodexBarCore/AccountMenuLayoutPlanner.swift)：展示层从独立快照生成主卡片和紧凑服务行。QuotaPeek 用 WPF 嵌套 ItemsControl 组织同一供应商的服务，钱包优先，组顺序沿用设置中首次出现的位置。
- [CodexBar Issue #949](https://github.com/steipete/CodexBar/issues/949) 提醒多账户布局需要保留独立身份，并验证滚动和刷新；[PR #3938](https://github.com/steipete/CodexBar/pull/3938) 将统一供应商入口与账户自己的用量、历史分开。本实现只做展示分组，原配置 ID、凭据、历史、告警及胶囊选择保持各数据源独立，不合计不同范围的金额。
- Hone 与 HoneWallet 属于同一供应商；服务地址按实际请求的规则归一化主机、默认端口、尾斜杠和 `/v1`，不同站点及租户路径分开。其他内置提供商按类型和地址分组，自定义 HTTP 按实际查询 URL，手动录入独立。每项服务仍显示自己的错误与更新时间，顶层汇总部分失败 / 过期 / 待连接状态；钱包已经在卡片内时，健康 API key 的未设限说明不再引导重复连接钱包。

## 2026-09-26 菜单外部点击关闭

- 参考 [wpf-notifyicon 的 ShowContextMenu 源码](https://github.com/hardcodet/wpf-notifyicon/blob/develop/src/NotifyIconWpf/TaskbarIcon.cs)、[Issue #37](https://github.com/hardcodet/wpf-notifyicon/issues/37) 和 [PR #119](https://github.com/hardcodet/wpf-notifyicon/pull/119)：从不激活的宿主弹出 WPF 菜单时，需要让菜单自己的 HWND 获得前台状态，并让菜单接收键盘焦点，才能正常处理外部点击和 Esc。
- QuotaPeek 的任务栏子窗口与悬浮窗口都保留 `NOACTIVATE`。在 `ContextMenu.Opened` 中取得已创建的菜单 HWND，仅激活菜单；按钮打开与右键打开共用该路径。使用 WPF 自带的关闭和鼠标捕获逻辑，不增加全局鼠标钩子或轮询。
- 本机旧 EXE 已复现：菜单打开后前台 HWND 仍是外部窗口，再次点击该窗口不能关闭菜单。回归脚本 `tests/context_menu_smoke.py` 使用实际鼠标输入，并验证外部窗口收到点击、菜单消失、菜单项仍可执行；可通过 `--pid` 复核正式实例。
- 构建时发现 Logo 已移动到 `docs/images/logo.png`，项目资源和图标生成脚本仍引用不存在的根目录路径。已将两处输入路径指向现有文件，保留应用内 `/Assets/logo.png` 资源名和原图片。

## 2026-09-26 任务栏嵌入

- [TrafficMonitor 的 TaskBarDlg](https://github.com/zhongyang219/TrafficMonitor/blob/master/TrafficMonitor/TaskBarDlg.cpp) 和 [PR #1106](https://github.com/zhongyang219/TrafficMonitor/pull/1106)：参考独立子窗口、Windows 11 直接挂到 `Shell_TrayWnd`、按任务栏真实矩形定位和定时恢复的思路。没有复制项目代码。
- [重叠问题 #1718](https://github.com/zhongyang219/TrafficMonitor/issues/1718)：Windows 11 的任务栏子窗口矩形不等于图标占用区域，不能把嵌入理解为系统为插件预留空间。QuotaPeek 在后台通过 UI Automation 读取按钮矩形，仅使用左半边空位；不调整 Explorer 的布局。没有足够空间时回退为悬浮胶囊，并继续检测恢复。
- [WPF HwndSourceParameters 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndSourceParameters.cs) 明确区分 `UsesPerPixelTransparency` 与旧的 `UsesPerPixelOpacity`；前者支持 Windows 8 及以上的透明子窗口。本项目使用独立 `HwndSource` 承载紧凑胶囊，主窗口继续承载展开卡片，任务栏子窗口销毁后可以单独重建。
- [SetParent 文档](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent) 提示窗口样式和跨进程 DPI 的限制。因此不把现有 WPF 主窗口重新设为 Explorer 的子窗口；通过创建参数直接指定子窗口父级，像素布局和 WPF 尺寸按实际 DPI 换算。
- `TaskbarDocked` 默认关闭以兼容旧配置；独立于原来的悬浮坐标、展开状态。入口放在胶囊右键菜单、托盘菜单和桌面偏好。嵌入模式单击切换上方卡片，菜单可切回自由悬浮；长账户名称让出空间给余额。
- 本机真实点击发现：跨进程任务栏子窗口收到 `WM_MOUSEACTIVATE` 前，前台窗口可能已被系统清空，单独返回 `MA_NOACTIVATE` 不够。用 [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) 的进程外前台事件记录原窗口，在点击时仅恢复空前台状态；如果已有新的活动窗口则不干预。停用嵌入或退出时注销事件，不向 Explorer 注入 DLL。

## Hone 优先

用户给出的站点是 [hone.vvvv.ee](https://hone.vvvv.ee/)。无认证的 `GET /api/status` 实测返回 New API、版本 `v1.0.0-rc.40`、`quota_display_type=USD`、`quota_per_unit=500000`。

`/v1/dashboard/billing/subscription` 和 `/v1/dashboard/billing/usage` 无 key 均返回 HTTP 401，说明认证路由存在；这不是账户余额验证。

最有价值的 GitHub 参考是 [QuantumNous/new-api 的 billing.go](https://github.com/QuantumNous/new-api/blob/main/controller/billing.go)。其兼容账单的字段名虽然含 USD，实际单位受站点展示设置影响；消费值额外乘 100，且消费不按传入日期过滤。因此实现时独立读取 Hone 的展示币种，除以 100，并在卡片标注“累计消费 / 站点账单”。站点可选择按账户或 token 统计，不能仅从普通 key 断言是账户余额。

上游使用 `100000000` 表示无限 token 限额。本实现将其显示为无限 / 未知余额，不显示成一亿美元。没有用户 key 时保留“待连接”，不放演示金额。

### 账户钱包与 key 账单分开

2026-09-25 实测已配置的普通 key：兼容 subscription 返回无限 key 限额，usage 返回当前 key 的累计消费；用同一 key 请求 `/api/user/self` 返回 401。已登录的 Hone 网页有独立的钱包余额和账户总消费，这些数值不能从无限 key 推算。

补充参考 [New API v1.0.0-rc.40 路由](https://github.com/QuantumNous/new-api/blob/v1.0.0-rc.40/router/api-router.go)、[账户认证](https://github.com/QuantumNous/new-api/blob/v1.0.0-rc.40/middleware/auth.go) 和 [账户控制器](https://github.com/QuantumNous/new-api/blob/v1.0.0-rc.40/controller/user.go)。采用独立的 **Hone 钱包** 数据源和独立账户访问令牌，通过只读 `GET /api/user/self` 读取 `data.quota`、`data.used_quota`。余额除以公开 `/api/status` 中的 `quota_per_unit`；CNY 再乘站点 `usd_exchange_rate`，USD 不乘汇率。不借用 API key 的限额、倍率或累计消费。

钱包数据源使用独立配置 ID、Windows 凭据项和历史流；在 Hone API 设置中可直接添加到列表首位。未连接时不填入网页截图金额或演示金额。401 / 403 提示账户访问令牌问题，并沿用失败保留旧余额的策略。

另核对了 [New API token usage 文档](https://github.com/QuantumNous/new-api-docs/blob/main/docs/api/token-usage.md)：它返回 token 原始 quota，并非天然货币。当前不混用这个接口和兼容账单接口。协议解析自行实现，未复制 New API 的 AGPL 源码。

## Codex 订阅额度

采用 [官方 Codex App Server 文档](https://learn.chatgpt.com/docs/app-server) 中的 stdio 初始化和 `account/rateLimits/read`。本机已通过实测读取；只调用初始化和额度查询，不运行 thread/turn，不处理登录 token，不调用额度重置或邮件接口。

读取 `rateLimitsByLimitId` 全部额度桶，缺失时兼容 `rateLimits`。窗口时长、重置 Unix 秒时间戳和百分比均由返回数据决定；没有的窗口不补零。扩展统一模型为 `RateWindow`，现金金额仍使用 `decimal`。账号 ID 与重置券 ID 不写入应用数据。

每次查询临时启动一个隐藏 app-server 子进程，完成后关闭；超时仅终止自己启动的子进程，不结束正在运行的 Codex。

## 其他参考

- [Microsoft WPF-Samples](https://github.com/microsoft/WPF-Samples)：原生 WPF 工程与分层思路；使用独立 Core、Services、Native 和 UI，没有引入整套外部 UI 框架。
- [hardcodet/wpf-notifyicon](https://github.com/hardcodet/wpf-notifyicon)：核对托盘交互。当前需求简单，最终用 WinForms NotifyIcon，无额外托盘包。
- [check_balance](https://github.com/hanmumuHL/check_balance)：作为平台覆盖的检索入口；内置字段优先核对官方协议，没有扫描其支持的其他软件密钥。
- [DeepSeek balance](https://api-docs.deepseek.com/api/get-user-balance/)：按币种选择 `total_balance`。
- [SiliconFlow OpenAPI](https://github.com/siliconflow/siliconcloud/blob/main/openapi.yaml)：余额用 `totalBalance`。
- [OpenRouter 当前 key](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-key)：限额、剩余和月消费分开，null 表示未设限，不能作为账户余额。
- [Microsoft 扩展窗口样式](https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles)：`TOOLWINDOW`、`NOACTIVATE` 与锁定时的 `TRANSPARENT`；WPF 透明窗口自身带 `LAYERED`。
- [SHQueryUserNotificationState](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shqueryusernotificationstate)：检测独占 / D3D 全屏 / 演示状态。
- [CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)：泛型凭据存储，释放原生缓冲区时清零。

## 持久化和网络边界

SQLite 保存规范化快照与阈值状态；UI 只拿规范化模型。失败保留 fetchedAt，并另记 attemptedAt。使用 HTTPS、禁用重定向与 Cookie、限制响应大小，不把 API 响应错误正文或请求头送进 UI / 日志，防止平台回显密钥。设置文件原子替换，损坏设置先备份。

自定义 HTTP 使用受限字段路径，支持点属性和数组索引。它不是完整 JSONPath 引擎，界面与文档均写明范围。

## 打包内存

参考 [.NET single-file 部署文档](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) 与 [dotnet/designs 的 single-file 设计](https://github.com/dotnet/designs/blob/main/accepted/2020/single-file/design.md)。对相同应用实测：压缩程序集直接从 bundle 加载时工作集约 309 MiB，改成 `IncludeAllContentForSelfExtract=true` 后约 166 MiB，私有内存由约 183 MiB 降到 97 MiB。因此保留压缩便携 EXE，但首次启动解压全部内容，由运行库从文件加载。短时数据不等于全天性能保证。

## 2026-09-26 胶囊滚轮切换

- [WPF HwndMouseInputProvider 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/InterOp/HwndMouseInputProvider.cs) 将原生 `WM_MOUSEWHEEL` 转为 WPF 鼠标输入。浮动胶囊外层与任务栏 UserControl 使用 `PreviewMouseWheel`，覆盖文字、圆点、内边距和菜单按钮；只有收起的浮动胶囊处理切换，展开卡片的 ScrollViewer 保留自己的滚动。
- [WPF Issue #5936](https://github.com/dotnet/wpf/issues/5936) 提醒不能把每一个小幅 Delta 都当成一步，否则触控板会跳得过快。依据 [WM_MOUSEWHEEL 文档](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel) 累计至 120 再切换，保留余量；反向、离开胶囊和展开 / 收起时清除不完整手势。
- 手动选择以数据源 ID 保存在本次运行中，独立于每次刷新重建的卡片对象；仍按设置中的已启用顺序循环。启动时沿用原来的低额度优先，所选源被停用或移除时安全回退。滚动仅更新胶囊显示，不重新请求额度、查询历史、保存配置或抢占焦点。

## 2026-09-26 胶囊拖动

- [WPF ButtonBase 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ButtonBase.cs)：按钮会处理鼠标按下并捕获鼠标。原胶囊的余额区域是展开按钮，只有右侧文字图标绑定拖动；直接拖余额不会移动窗口。改为外层 Preview 事件统一处理，展开按钮的普通单击仍保留，设置、刷新、收起按钮和滚动条排除在拖动区域之外。
- [Eto 的 WPF 窗口实现](https://github.com/picoe/Eto/blob/develop/src/Eto.Wpf/Forms/WpfWindow.cs)、[Issue #1903](https://github.com/picoe/Eto/issues/1903)、[PR #2108](https://github.com/picoe/Eto/pull/2108)：参考交互控件与窗口拖动分离、鼠标捕获需要可靠清理的经验。QuotaPeek 保留原有不激活窗口的像素定位方式；按下、拖动、松开及捕获丢失由同一外层元素处理，不引入框架依赖。
- 使用 Windows 的最小拖动距离并按窗口 DPI 换算到物理像素，区分单击抖动和真正拖动。最终交互按用户要求移除悬停展开及移开后自动收起：悬停保持原状，单击胶囊展开，按住移动则拖动且不触发展开。松开前排队的移动事件可能看到已释放的按钮状态，因此由 MouseUp / LostMouseCapture 完成手势清理，避免丢失轻微抖动后的单击。
- 首次 Loaded 中的宽度调整可能被 Show 的初始原生尺寸覆盖。首次 ContentRendered 再应用展开 / 收起尺寸，随后定位并保存；避免收起状态重启后回到展开宽度，也避免初始化中覆盖已保存的位置。

## 2026-09-26 临时 Logo

- 参考 [PowerToys 的 PowerLauncher.csproj](https://github.com/microsoft/PowerToys/blob/main/src/modules/launcher/PowerLauncher/PowerLauncher.csproj)，将同一 ICO 同时设为 `ApplicationIcon` 和 WPF `Resource`，覆盖 EXE、窗口与托盘；单文件发布后无需在程序旁放置图片。
- [WPF Window 源码](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs) 明确窗口图标优先使用 `Window.Icon`；这里显式引用内嵌资源。[WinForms Issue #8929](https://github.com/dotnet/winforms/issues/8929) 与 [PR #8983](https://github.com/dotnet/winforms/pull/8983) 说明 ICO 尺寸选择与原生图标提取的边界。托盘直接按系统小图标尺寸读取内嵌 ICO，并在退出时释放。
- README 和界面直接使用根目录 `logo.png` 原图，不添加背景色；`tools/update-logo.ps1` 仅按原比例生成 16–256 px 共九种尺寸的 ICO，保留透明背景。README 明确说明 Logo 仅作临时使用，并非本项目作者本人设计。
