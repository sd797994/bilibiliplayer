# 哔哩桌面

一个面向 Windows 10 的 .NET MAUI 9 深色 B 站播放器 Demo。

## 已实现

- 登录后优先使用账号 Cookie 获取个性化推荐，失败时自动回退热门视频流
- 视频卡片包含封面、标题、UP 主、播放量和时长
- 首页任意滚动位置按住鼠标下拉刷新，滚动到底自动加载下一页
- WebView2 官方网页登录，登录 Cookie 保存在应用独立的 WebView2 数据目录
- 本地保存公开用户资料，用于启动后展示登录状态
- 点击视频后在应用内弹出 B 站官方外链播放器；手动点击播放时默认有声
- Windows 10 自定义深色标题栏
- 网络错误、登录验证失败和播放器加载失败提示

## 运行

需要 Windows 10 1809+、.NET 9 SDK、MAUI Windows 工作负载及 WebView2 Runtime。

```powershell
dotnet build BiliBiliPlayer.sln -f net9.0-windows10.0.19041.0
dotnet run --project BiliBiliPlayer/BiliBiliPlayer.csproj -f net9.0-windows10.0.19041.0
```

## 发布 Windows 版本

以下脚本会生成 `Release / win-x64` 发布目录，并在桌面创建或更新“哔哩桌面”快捷方式：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1
```

发布文件位于 `artifacts/publish/win-x64`。

个性化推荐依赖 B 站网页端接口和有效登录 Cookie，接口不可用时会自动使用公开热门内容。播放器使用 B 站官方外链播放器，没有接入第三方视频解析服务。

## 本地高清播放实验版

本副本把详情页从 B 站 iframe 播放器改成了“后台解析 + 应用内本地播放器”：

- 后台隐藏 WebView2 打开当前 BV 对应的视频页，复用登录页同一个 WebView2 profile/cookie。
- 在浏览器上下文中请求 `x/player/playurl`，只获取当前账号本来有权限播放的清晰度。
- 优先解析 DASH，分别拿视频轨和音频轨 URL。
- 前台 `PlayerWebView` 只渲染项目自带的本地 HTML5 播放器，不显示或跳转 B 站页面。
- 播放 CDN 请求会补 `Referer: https://www.bilibili.com/video/<BVID>/` 和 `Origin: https://www.bilibili.com`。
- 顶部清晰度可选 360P / 480P / 720P / 1080P；如果 B 站实际只返回更低画质，标题栏会显示“回退”。

当前解析实现优先针对 Windows/WebView2。没有绕过付费、会员或 DRM 权限；服务端不给当前账号的格式不会被强行解锁。

## 2026-08-20 v2 test fixes

- Force-mute the hidden WebView2 resolver so the Bilibili page used for playurl resolution can never output a second audio stream.
- Also pause/mute media elements inside the hidden resolver page as a secondary safeguard.
- When DASH provides a separate audio track, the local player's video element is force-muted and only the selected audio track is audible, preventing muxed-track echo.
- Replaced the native Windows quality Picker with a compact custom quality menu.
- Replaced native HTML5 controls with app-owned player controls and a cleaner buffering overlay.

## 2026-08-20 v3 resolver lifetime optimization

- 删除详情页中常驻的 `ResolverWebView`；XAML 只保留一个轻量 `ResolverHost` 容器。
- 需要解析时才动态创建 1×1 WebView2，拿到播放清单后立即 `Stop`、导航到 `about:blank`、移出视觉树并 `DisconnectHandler`。
- 一次 1080P playurl 解析会把响应中的 360P / 480P / 720P / 1080P DASH 视频轨缓存到内存；之后切画质直接换缓存 CDN URL，不再启动后台 B 站页面。
- 播放失败或点击“重试”时会丢弃缓存清单，再临时创建一次 resolver 获取新的签名 URL。
- 缓存只存在于当前 `PlayerPage` 内存中，关闭详情页后清空，不写入磁盘。

注意：前台 `PlayerWebView` 仍然是本地 HTML5 播放器，因此正常播放期间仍有一个 WebView2；本次优化移除的是后台 B 站解析 WebView2 的常驻实例。

## 2026-08-26 v1.1 运行期续播

- 退出视频详情页时记录当前播放秒数，再次打开同一个 BV 时自动从该位置继续播放。
- 播放进度只保存在应用进程内存中，不写入配置、数据库或磁盘；应用退出后自动清空。
- 视频播放结束、距离结尾不足 10 秒或停留在开头 3 秒内时清除该视频的续播位置。
- 恢复播放时重新解析有效的 B 站 CDN 地址，只复用播放秒数，不缓存过期地址。

## 2026-08-26 v1.2 动态推荐刷新

- 首页首次加载、任意滚动位置的手势刷新和滚动加载更多分别推进推荐刷新序号，不再重复请求同一批内容。
- 刷新时主动过滤当前列表中已经出现的视频；存在少量推荐重叠时自动继续获取下一批。
- 未登录或登录推荐失效时优先使用 B 站匿名动态推荐，动态接口不可用时才按新页码回退热门内容。
- 刷新与自动加载更多冲突时等待当前加载结束，不再静默丢弃刷新操作。

## 2026-08-26 v1.3 评论触底分页

- 视频详情页的热门、最新评论分别维护分页游标，滚动接近底部时自动加载下一页。
- 新评论增量插入列表，不重绘视频、不重置播放进度，切换画质时保留已经加载的评论。
- 评论分页使用独立 HTTP 请求，解析工作不占用界面线程；请求失败只在评论底部提示并支持点击重试。
- 页面关闭会取消未完成的评论请求，前端加载状态与 C# 加载锁共同避免连续触底产生重复请求。
- 评论游标接口不可用时自动回退传统页码接口，不影响当前视频播放。

## 2026-08-27 v1.4 网页字幕

- 在临时解析 WebView2 中复用 B 站登录会话，读取当前 `aid/cid` 的字幕清单与时间轴正文；字幕 CDN 请求不附带 Cookie，避免跨域凭据限制。
- 字幕正文仅保存在当前详情页内存中，获取完成后仍会立即销毁临时解析 WebView2，不增加常驻后台播放器。
- 底部控制栏新增“字幕”菜单，支持关闭、多语言选择和 AI 标记；默认开启官网默认轨道，并记住语言、开关、字号和底部位置偏好。
- 字幕以视频当前时间为准，支持拖动、暂停、倍速、画质切换、续播与全屏；自动适配画面比例，并避开控制栏和打开的侧面菜单。
- 未登录且接口要求登录时提示“登录后可用字幕”；无字幕或抓取失败不会影响视频播放。不对烧录字幕进行 OCR，也不额外生成 AI 字幕。
- 无网络测试可运行 `node scripts/Test-Subtitles.cjs`，覆盖字幕解析、同步、菜单、偏好、登录限制及资源清理。

## 2026-08-28 多 P 选集

- 底部重复的“弹”按钮替换为“选集”，顶部弹幕开关保留；只有一 P 的视频将选集按钮置灰并禁用。
- 多 P 菜单显示每 P 的名称、时长和当前选中项，长列表可滚动；点击其他 P 会按对应 `cid` 重新解析播放源，同时切换弹幕和字幕。
- 各 P 的播放进度独立保存在运行期内存中，重新打开视频恢复上次观看的 P；切换画质不改变当前 P，切换 P 时保留清晰度偏好。
- 不自动连播下一 P。卡片继续显示整条投稿的总时长，播放器显示当前 P 的媒体时长。
- 回归检查：`node scripts/Test-Subtitles.cjs`（字幕及选集菜单/解析）和 `dotnet run --project scripts/tests/PlaybackProgress.Tests/PlaybackProgress.Tests.csproj`（按 P 保存/恢复进度）。

## 2026-08-28 v1.4.2 详情页启动修复

- 首次进入详情页时，跳过尚未初始化的播放器脚本调用，避免 MAUI WebView 的未完成任务阻塞后续视频解析。
- 播放器脚本调用增加就绪检查、5 秒超时和页面关闭取消处理；切换 P 时仍会暂停旧音视频并保存进度。
- `dotnet run --project scripts/tests/PlaybackProgress.Tests/PlaybackProgress.Tests.csproj` 同时覆盖未就绪、脚本无响应和取消等启动边界。
- Windows 原生集成检查：`powershell -NoProfile -File scripts/Test-NativePlayback.ps1`。该脚本编译并启动 Debug 版，使用实际 MAUI 页面、WebView2 和公开视频，检查单 P、多 P、切换 P2、画质切换和重新打开续播；通过解码帧和播放时间判断实播，不使用模拟媒体。
- 原生检查需要网络及现有 WebView2 环境，测试播放器保持静音；结果写入 `artifacts/native-playback-*.jsonl`，测试结束自动退出。测试入口不编译进 Release 版，不记录 Cookie 或 CDN 签名地址。
