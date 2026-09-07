let currentVideo = null;
let selectedContact = null;
let contacts = [];

const els = {
  input: document.querySelector('#videoUrlInput'),
  load: document.querySelector('#loadButton'),
  reload: document.querySelector('#reloadButton'),
  card: document.querySelector('#videoCard'),
  cover: document.querySelector('#coverImage'),
  duration: document.querySelector('#durationBadge'),
  title: document.querySelector('#videoTitle'),
  meta: document.querySelector('#videoMeta'),
  contacts: document.querySelector('#contacts'),
  contactsStatus: document.querySelector('#contactsStatus'),
  recommendation: document.querySelector('#recommendation'),
  status: document.querySelector('#status'),
  send: document.querySelector('#sendButton')
};

els.load.addEventListener('click', () => loadVideoFromInput());
els.reload.addEventListener('click', () => initialize(true));
els.send.addEventListener('click', () => sendShare());
els.input.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') loadVideoFromInput();
});

initialize(false);

async function initialize(forceContacts) {
  setStatus('正在读取当前页面…');
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (tab?.url) els.input.value = tab.url;
    const bvid = extractBvid(tab?.url || '');
    if (bvid) await loadVideo(bvid);
    else {
      currentVideo = null;
      els.card.classList.add('hidden');
      setStatus('当前页不是可识别的 B站视频详情页，也可以粘贴视频链接或 BV号。');
    }
  } catch (error) {
    setStatus(error.message || String(error), 'error');
  }
  if (forceContacts || contacts.length === 0) await loadContacts();
}

async function loadVideoFromInput() {
  const bvid = extractBvid(els.input.value.trim());
  if (!bvid) {
    setStatus('无法识别这个 B站视频链接或 BV号。', 'error');
    return;
  }
  await loadVideo(bvid);
}

async function loadVideo(bvid) {
  setBusy(true);
  setStatus('正在读取视频信息…');
  try {
    currentVideo = await rpc({ type: 'get-video', bvid });
    els.cover.src = currentVideo.coverUrl;
    els.duration.textContent = currentVideo.durationText;
    els.title.textContent = currentVideo.title;
    els.meta.textContent = `UP主：${currentVideo.ownerName} · 播放：${currentVideo.viewCountText}`;
    els.card.classList.remove('hidden');
    els.input.value = currentVideo.url;
    setStatus('视频信息已就绪。');
  } catch (error) {
    currentVideo = null;
    els.card.classList.add('hidden');
    setStatus(error.message || String(error), 'error');
  } finally {
    setBusy(false);
    updateSendState();
  }
}

async function loadContacts() {
  els.contactsStatus.textContent = '读取中…';
  try {
    contacts = await rpc({ type: 'get-contacts' });
    selectedContact = selectedContact
      ? contacts.find((item) => item.wxid === selectedContact.wxid) || null
      : null;
    renderContacts();
    els.contactsStatus.textContent = contacts.length ? `${contacts.length} 个` : '暂无';
  } catch (error) {
    contacts = [];
    selectedContact = null;
    renderContacts();
    els.contactsStatus.textContent = '读取失败';
    setStatus(error.message || String(error), 'error');
  }
  updateSendState();
}

function renderContacts() {
  els.contacts.replaceChildren();
  if (contacts.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = '暂无可选联系人';
    els.contacts.appendChild(empty);
    return;
  }

  for (const contact of contacts) {
    const button = document.createElement('button');
    button.className = `contact${selectedContact?.wxid === contact.wxid ? ' selected' : ''}`;
    button.type = 'button';

    const avatar = contact.avatarUrl
      ? document.createElement('img')
      : document.createElement('div');
    avatar.className = contact.avatarUrl ? 'avatar' : 'avatar avatar-fallback';
    if (contact.avatarUrl) avatar.src = contact.avatarUrl;
    else avatar.textContent = contact.displayName.slice(0, 1) || '微';

    const name = document.createElement('span');
    name.className = 'contact-name';
    name.textContent = contact.displayName;

    const kind = document.createElement('span');
    kind.className = 'contact-kind';
    kind.textContent = contact.isGroup ? '群聊' : '联系人';

    button.append(avatar, name, kind);
    button.addEventListener('click', () => {
      selectedContact = contact;
      renderContacts();
      setStatus(`将发送给：${contact.displayName}（${contact.isGroup ? '群聊' : '联系人'}）`);
      updateSendState();
    });
    els.contacts.appendChild(button);
  }
}

async function sendShare() {
  if (!currentVideo || !selectedContact) return;
  setBusy(true);
  els.send.textContent = '发送中…';
  setStatus(`正在生成卡片并发送给 ${selectedContact.displayName}…`);
  try {
    const result = await rpc({
      type: 'send-share',
      video: currentVideo,
      contact: selectedContact,
      recommendation: els.recommendation.value
    });
    els.recommendation.value = '';
    setStatus(result.message || '已发送。', 'ok');
  } catch (error) {
    setStatus(error.message || String(error), 'error');
  } finally {
    els.send.textContent = '发送给所选联系人';
    setBusy(false);
    updateSendState();
  }
}

function updateSendState() {
  els.send.disabled = !currentVideo || !selectedContact || els.send.dataset.busy === '1';
}

function setBusy(value) {
  els.send.dataset.busy = value ? '1' : '0';
  els.load.disabled = value;
  els.reload.disabled = value;
  updateSendState();
}

function setStatus(message, kind = '') {
  els.status.textContent = message;
  els.status.className = `status${kind ? ` ${kind}` : ''}`;
}

function extractBvid(value) {
  const text = String(value || '').trim();
  const direct = text.match(/^(BV[0-9A-Za-z]+)$/i);
  if (direct) return normalizeBv(direct[1]);
  const fromUrl = text.match(/bilibili\.com\/video\/(BV[0-9A-Za-z]+)/i);
  if (fromUrl) return normalizeBv(fromUrl[1]);
  return null;
}

function normalizeBv(value) {
  return `BV${value.slice(2)}`;
}

async function rpc(message) {
  const response = await chrome.runtime.sendMessage(message);
  if (!response?.ok) throw new Error(response?.error || '插件后台请求失败。');
  return response.data;
}
