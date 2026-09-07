const ENDPOINTS = {
  contacts: 'http://127.0.0.1:30002/share/recent_contacts',
  card: 'http://127.0.0.1:30002/send_xml_with_local_thumb',
  text: 'http://127.0.0.1:30001/SendTextMsg'
};

const MAX_COVER_BYTES = 20 * 1024 * 1024;

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  handleMessage(message)
    .then((data) => sendResponse({ ok: true, data }))
    .catch((error) => sendResponse({ ok: false, error: error?.message || String(error) }));
  return true;
});

async function handleMessage(message) {
  switch (message?.type) {
    case 'get-video':
      return getVideo(message.bvid);
    case 'get-contacts':
      return getContacts();
    case 'send-share':
      return sendShare(message.video, message.contact, message.recommendation || '');
    default:
      throw new Error('未知插件请求。');
  }
}

async function getVideo(bvid) {
  if (!/^BV[0-9A-Za-z]+$/.test(bvid || '')) {
    throw new Error('无法识别 BVID。');
  }
  const response = await fetch(`https://api.bilibili.com/x/web-interface/view?bvid=${encodeURIComponent(bvid)}`);
  if (!response.ok) throw new Error(`B站视频接口返回 HTTP ${response.status}。`);
  const body = await response.json();
  if (body.code !== 0 || !body.data) throw new Error(body.message || `B站接口错误（${body.code}）。`);
  const data = body.data;
  return {
    bvid: data.bvid || bvid,
    title: data.title || bvid,
    coverUrl: normalizeHttps(data.pic || ''),
    ownerName: data.owner?.name || '未知UP主',
    viewCountText: formatCount(data.stat?.view || 0),
    durationText: formatDuration(data.duration || 0),
    url: `https://www.bilibili.com/video/${data.bvid || bvid}/`
  };
}

async function getContacts() {
  let response;
  try {
    response = await fetch(ENDPOINTS.contacts);
  } catch {
    throw new Error('无法连接微信联系人服务，请确认微信及本地 Hook v5 已启动。');
  }
  const body = await readJson(response, '微信联系人服务返回了无法识别的数据。');
  if (!response.ok || body.ret !== 0) {
    throw new Error(body.msg || `联系人服务返回 HTTP ${response.status}，ret=${body.ret ?? -1}。`);
  }
  return Array.isArray(body.data)
    ? body.data
        .filter((item) => item?.wxid && item?.display_name)
        .map((item) => ({
          wxid: String(item.wxid),
          displayName: String(item.display_name),
          avatarUrl: item.avatar_url || '',
          isGroup: Boolean(item.is_group),
          lastTimestamp: Number(item.last_timestamp || 0)
        }))
    : [];
}

async function sendShare(video, contact, recommendation) {
  if (!video?.bvid || !contact?.wxid) throw new Error('视频或联系人信息不完整。');
  let downloadId = null;
  try {
    const downloaded = await downloadCover(video.coverUrl, video.bvid);
    downloadId = downloaded.id;
    const payload = {
      wxid: contact.wxid,
      title: (video.title || video.bvid).trim(),
      description: `UP主：${video.ownerName || '未知UP主'}\n播放：${video.viewCountText || '0'}`,
      url: video.url || `https://www.bilibili.com/video/${video.bvid}/`,
      thumb_path: downloaded.filename,
      upload_wxid: 'filehelper',
      timeout_seconds: 30
    };

    const card = await postJson(ENDPOINTS.card, payload, '无法连接微信卡片服务，请确认微信及本地 Hook v5 已启动。');
    assertCardResult(card.response, card.body);

    const text = String(recommendation || '').trim();
    if (!text) return { message: `卡片已发送给 ${contact.displayName}。` };

    const textResult = await postJson(
      ENDPOINTS.text,
      { wxidorgid: contact.wxid, msg: text },
      '卡片已发送，但无法连接微信文本消息服务。'
    );
    if (!textResult.response.ok || textResult.body.ret !== 0) {
      const detail = textResult.body.msg || textResult.body.retmsg || `HTTP ${textResult.response.status}，ret=${textResult.body.ret ?? -1}`;
      throw new Error(`卡片已发送，但推荐语发送失败：${detail}`);
    }
    return { message: `卡片和推荐语已发送给 ${contact.displayName}。` };
  } finally {
    if (downloadId !== null) {
      try { await chrome.downloads.removeFile(downloadId); } catch {}
      try { await chrome.downloads.erase({ id: downloadId }); } catch {}
    }
  }
}

async function downloadCover(coverUrl, bvid) {
  const url = normalizeHttps(coverUrl || '');
  if (!/^https?:\/\//i.test(url)) throw new Error('当前视频没有有效的封面地址。');

  const response = await fetch(url, {
    headers: {
      'Accept': 'image/*',
      'Referer': `https://www.bilibili.com/video/${bvid}/`
    }
  });
  if (!response.ok) throw new Error(`下载 B 站封面失败：HTTP ${response.status}。`);
  const length = Number(response.headers.get('content-length') || 0);
  if (length > MAX_COVER_BYTES) throw new Error('封面文件过大。');
  const buffer = await response.arrayBuffer();
  if (!buffer.byteLength || buffer.byteLength > MAX_COVER_BYTES) throw new Error('封面文件大小无效。');

  const type = response.headers.get('content-type') || 'image/jpeg';
  const ext = imageExtension(type);
  const dataUrl = `data:${type};base64,${arrayBufferToBase64(buffer)}`;
  const id = await chrome.downloads.download({
    url: dataUrl,
    filename: `bilibili-wechat-share/${bvid}-${Date.now()}${ext}`,
    conflictAction: 'uniquify',
    saveAs: false
  });
  const item = await waitForDownload(id);
  if (!item.filename) throw new Error('Chrome 未返回封面的本地路径。');
  return { id, filename: item.filename };
}

async function waitForDownload(id) {
  for (let i = 0; i < 120; i += 1) {
    const items = await chrome.downloads.search({ id });
    const item = items[0];
    if (!item) throw new Error('找不到临时封面下载任务。');
    if (item.state === 'complete') return item;
    if (item.state === 'interrupted') throw new Error(`封面下载失败：${item.error || '下载被中断'}。`);
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('封面下载超时。');
}

async function postJson(url, payload, connectError) {
  let response;
  try {
    response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
  } catch {
    throw new Error(connectError);
  }
  const body = await readJson(response, '本地服务返回了无法识别的数据。');
  return { response, body };
}

function assertCardResult(response, body) {
  const ret = Number.isInteger(body.ret) ? body.ret : -1;
  if (response.ok && ret === 0 && privateSendSucceeded(body)) return;
  if (ret === -6) throw new Error('微信封面上传超时，请确认 Hook v5 状态正常后重试。');
  const detail = body.msg || body.retmsg || `HTTP ${response.status}，ret=${ret}`;
  throw new Error(`微信分享失败：${detail}${body.msg || body.retmsg ? `（ret=${ret}）` : ''}`);
}

function privateSendSucceeded(body) {
  if (typeof body.private_body !== 'string') return true;
  if (!body.private_body.trim()) return false;
  try {
    const nested = JSON.parse(body.private_body);
    return nested.ret === 0 && (nested.data?.ret ?? 0) === 0;
  } catch {
    return false;
  }
}

async function readJson(response, message) {
  try { return await response.json(); } catch { throw new Error(message); }
}

function normalizeHttps(value) {
  if (value.startsWith('//')) return `https:${value}`;
  if (value.startsWith('http://')) return `https://${value.slice(7)}`;
  return value;
}

function formatCount(value) {
  const count = Number(value || 0);
  if (count >= 100000000) return `${(count / 100000000).toFixed(1).replace(/\.0$/, '')}亿`;
  if (count >= 10000) return `${(count / 10000).toFixed(1).replace(/\.0$/, '')}万`;
  return count.toLocaleString('zh-CN');
}

function formatDuration(seconds) {
  const total = Math.max(0, Number(seconds || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = Math.floor(total % 60);
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${m}:${String(s).padStart(2, '0')}`;
}

function imageExtension(type) {
  const lower = String(type).toLowerCase();
  if (lower.includes('png')) return '.png';
  if (lower.includes('webp')) return '.webp';
  if (lower.includes('gif')) return '.gif';
  return '.jpg';
}

function arrayBufferToBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  const chunk = 0x8000;
  let binary = '';
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode(...bytes.subarray(i, Math.min(i + chunk, bytes.length)));
  }
  return btoa(binary);
}
