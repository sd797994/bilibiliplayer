# Bilibili 微信分享 Chrome 插件

这是从 BiliBiliPlayer 播放页“分享”功能中拆出的独立 Chrome Manifest V3 插件。

## 功能

- 打开插件时自动识别当前 `bilibili.com/video/BV...` 视频详情页。
- 也可以手动粘贴 B站视频链接或 BV号。
- 通过 B站公开视频详情接口读取标题、封面、UP主、播放量和时长。
- 从 `http://127.0.0.1:30002/share/recent_contacts` 读取最近联系人。
- 临时下载视频封面并取得 Chrome 下载记录中的本机绝对路径。
- 通过 `http://127.0.0.1:30002/send_xml_with_local_thumb` 发送微信小卡片。
- 卡片成功后，可通过 `http://127.0.0.1:30001/SendTextMsg` 再发送最多 500 字推荐语。
- 发送结束后删除临时封面文件和对应下载记录。

## 与播放器现有协议的对应关系

卡片请求保持原字段：

```json
{
  "wxid": "目标 wxid",
  "title": "视频标题",
  "description": "UP主：...\n播放：...",
  "url": "https://www.bilibili.com/video/BV.../",
  "thumb_path": "Chrome 下载得到的本机绝对路径",
  "upload_wxid": "filehelper",
  "timeout_seconds": 30
}
```

推荐语请求保持原字段：

```json
{
  "wxidorgid": "目标 wxid",
  "msg": "推荐语"
}
```

## 本地安装

1. 确认微信和现有本地 Hook v5 服务已启动，三个接口可访问。
2. 打开 Chrome 的 `chrome://extensions/`。
3. 开启“开发者模式”。
4. 点击“加载已解压的扩展程序”。
5. 选择本仓库的 `chrome-extension` 目录。
6. 打开任意 B站视频详情页，点击浏览器工具栏中的插件图标。

## 注意事项

- 该插件依赖 Chrome 的 `downloads` 权限，因为现有卡片 Hook 要求 `thumb_path` 是本机文件路径，而不是图片 URL。
- Chrome 的 `DownloadItem.filename` 提供绝对本地路径，因此无需修改现有 `send_xml_with_local_thumb` 服务。
- 新版 Chrome 对访问本地/回环网络有额外安全控制。如果浏览器首次弹出“本地网络/回环网络”访问授权，需要允许此插件访问本地服务。
- 插件没有保存微信联系人、推荐语或视频信息；这些数据仅在当前弹窗/发送流程中使用。
- 当前实现只依赖公开的视频详情数据，不需要读取 B站登录 Cookie。

## 已知边界

此仓库内可以验证插件协议、权限声明和静态代码，但无法从远程 CI/ChatGPT 环境连接你电脑上的 `127.0.0.1:30001/30002` 做真实微信发送。因此首次本机加载后，建议先用“文件传输助手”或测试联系人验证一次卡片封面路径是否被 Hook 正常读取。
