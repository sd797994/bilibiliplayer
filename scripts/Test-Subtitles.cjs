const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const root = path.resolve(__dirname, '..');
const playerSource = fs.readFileSync(path.join(root, 'BiliBiliPlayer/Views/PlayerPage.xaml.cs'), 'utf8');
const html = playerSource.match(/private const string LocalPlayerHtml = """\s*([\s\S]*?)\s*""";/)[1];
const playerScript = html.match(/<script>([\s\S]*?)<\/script>/)[1];
const testIdentity = { bvid: 'BV1wr8S6WEX7', aid: 1, cid: 2 };

class Element {
  constructor(id = '') {
    this.id = id;
    this.children = [];
    this.style = {};
    this.attributes = {};
    this.events = new Map();
    this.hidden = false;
    this.value = '0';
    this.clientWidth = id === 'wrap' ? 1000 : 320;
    this.clientHeight = id === 'wrap' ? 800 : 600;
    this.classes = new Set();
    this.classList = {
      add: (...names) => names.forEach(name => this.classes.add(name)),
      remove: (...names) => names.forEach(name => this.classes.delete(name)),
      contains: name => this.classes.has(name),
      toggle: (name, force) => {
        const enabled = force === undefined ? !this.classes.has(name) : force;
        if (enabled) this.classes.add(name); else this.classes.delete(name);
        return enabled;
      }
    };
  }
  set className(value) { this.classes = new Set(String(value).split(/\s+/).filter(Boolean)); }
  get className() { return [...this.classes].join(' '); }
  set textContent(value) { this.text = String(value); this.children = []; }
  get textContent() { return (this.text || '') + this.children.map(child => child.textContent).join(''); }
  appendChild(child) { this.children.push(child); return child; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.text = ''; this.children = children; }
  addEventListener(type, handler, options) {
    const listeners = this.events.get(type) || [];
    listeners.push({ handler, once: !!options?.once });
    this.events.set(type, listeners);
  }
  removeEventListener(type, handler) {
    this.events.set(type, (this.events.get(type) || []).filter(item => item.handler !== handler));
  }
  emit(type, event = {}) {
    const listeners = [...(this.events.get(type) || [])];
    this.events.set(type, listeners.filter(item => !item.once));
    for (const item of listeners) item.handler({ target: this, stopPropagation() {}, preventDefault() {}, ...event });
  }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  removeAttribute(name) { delete this.attributes[name]; }
  getBoundingClientRect() {
    return { left: 0, top: 0, right: this.clientWidth, bottom: this.clientHeight, width: this.clientWidth, height: this.clientHeight };
  }
  querySelectorAll() { return []; }
  querySelector() { return null; }
  focus() {}
  pause() { this.paused = true; this.emit('pause'); }
  async play() { this.paused = false; this.emit('play'); }
  load() {}
}

function createPlayer(identity = testIdentity) {
  const ids = [...html.matchAll(/id="([^"]+)"/g)].map(match => match[1]);
  const elements = Object.fromEntries(ids.map(id => [id, new Element(id)]));
  const video = elements.video;
  Object.assign(video, { currentTime: 0, duration: 60, videoWidth: 1920, videoHeight: 1080, paused: true, ended: false, playbackRate: 1 });
  const messages = [];
  const document = new Element('document');
  Object.assign(document, {
    getElementById: id => elements[id],
    createElement: () => new Element(),
    createTextNode: text => Object.assign(new Element(), { text }),
    createDocumentFragment: () => new Element(),
    documentElement: { requestFullscreen: async () => {} },
    exitFullscreen: async () => {}
  });
  const context = {
    document, window: {}, HTMLElement: Element,
    chrome: { webview: { postMessage: message => messages.push(message) } },
    ResizeObserver: class { observe() {} disconnect() {} },
    requestAnimationFrame: () => 1, cancelAnimationFrame() {},
    setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {},
    performance: { now: () => 0 }
  };
  vm.runInNewContext(playerScript, context, { filename: 'LocalPlayerHtml.js' });
  context.window.biliLocalPlayer.load('https://example.invalid/video', [], 0, identity);
  video.emit('loadedmetadata');
  return { player: context.window.biliLocalPlayer, elements, video, messages, document };
}

function testPlayer() {
  const { player, elements, video, messages } = createPlayer();
  const snapshot = { ...testIdentity, tracks: [
    { id: 'zh', lan: 'ai-zh', name: '中文（自动生成）', ai: true, def: true, cues: [
      { f: 1, t: 2, c: '第一句' }, { f: 2, t: 5, c: '持续字幕' }, { f: 3, t: 4, c: '重叠字幕' }
    ] },
    { id: 'en', lan: 'en', name: 'English', cues: [{ f: 1, t: 5, c: '<b>Literal subtitle</b>' }] }
  ] };
  const at = time => { video.currentTime = time; video.emit('timeupdate'); };
  const visible = () => elements.subtitles.classList.contains('visible');
  player.setSubtitles(snapshot, true, '', 1, 8);
  assert.equal(visible(), false);
  at(1);
  assert.equal(elements.subtitleText.textContent, '第一句');
  assert.equal(visible(), true);
  at(2);
  assert.equal(elements.subtitleText.textContent, '持续字幕');
  at(3.5);
  assert.equal(elements.subtitleText.textContent, '持续字幕\n重叠字幕');
  at(4.5);
  assert.equal(elements.subtitleText.textContent, '持续字幕');
  at(5);
  assert.equal(visible(), false);
  at(1.2);
  video.emit('seeking');
  video.emit('pause');
  assert.equal(elements.subtitleText.textContent, '第一句');

  elements.subtitleToggle.emit('click');
  assert.equal(elements.subtitleMenu.classList.contains('open'), true);
  assert.match(elements.subtitleTracks.children[1].textContent, /中文AI/);
  elements.subtitleTracks.children[0].emit('click');
  assert.equal(visible(), false);
  assert.equal(JSON.parse(messages.at(-1).split('LOCAL_PLAYER_SUBTITLE:')[1]).enabled, false);
  elements.subtitleTracks.children[2].emit('click');
  assert.equal(elements.subtitleText.textContent, '<b>Literal subtitle</b>');
  assert.equal(JSON.parse(messages.at(-1).split('LOCAL_PLAYER_SUBTITLE:')[1]).language, 'en');

  player.setSubtitles(snapshot, true, 'en', 1.25, 12);
  assert.match(elements.subtitleToggle.title, /English/);
  assert.equal(elements.subtitleSizeValue.textContent, '125%');
  elements.subtitleSize.value = '150';
  elements.subtitleSize.emit('input');
  assert.equal(JSON.parse(messages.at(-1).split('LOCAL_PLAYER_SUBTITLE:')[1]).fontScale, 1.5);
  elements.subtitleReset.emit('click');
  assert.equal(elements.subtitleSizeValue.textContent, '100%');
  assert.equal(elements.subtitleBottomValue.textContent, '8%');
  assert.equal(Number.parseFloat(elements.subtitles.style.bottom), 163.75);

  player.load('https://example.invalid/video', [], 3.5, testIdentity);
  assert.equal(visible(), false);
  video.emit('loadedmetadata');
  assert.equal(video.currentTime, 3.5);
  assert.equal(elements.subtitleText.textContent, '<b>Literal subtitle</b>');
  video.playbackRate = 2;
  video.emit('ratechange');
  at(4);
  assert.equal(visible(), true);
  video.ended = true;
  video.emit('ended');
  assert.equal(visible(), false);
  video.ended = false;

  // A late result cannot overwrite valid captions, including another part of the same BV.
  assert.equal(player.setSubtitles({ ...snapshot, cid: 3 }, true, '', 1, 8), false);
  at(1.2);
  assert.equal(elements.subtitleText.textContent, '<b>Literal subtitle</b>');
  assert.equal(player.setSubtitles({ ...snapshot, bvid: 'BV_OTHER' }, true, '', 1, 8), false);
  assert.equal(player.setSubtitles({ ...snapshot, aid: 9 }, true, '', 1, 8), false);
  assert.equal(player.setSubtitles({ tracks: snapshot.tracks }, true, '', 1, 8), false);

  player.setSubtitles({ ...testIdentity, tracks: [], login: true }, true, '', 1, 8);
  elements.subtitleToggle.emit('click');
  assert.equal(elements.hint.textContent, '登录后可用字幕');
  assert.equal(elements.subtitleMenu.classList.contains('open'), false);
  player.setSubtitles({ ...testIdentity, tracks: [], error: '字幕加载失败，请重新打开视频重试' }, true, '', 1, 8);
  elements.subtitleToggle.emit('click');
  assert.match(elements.hint.textContent, /字幕加载失败/);

  player.setSubtitles(snapshot, true, '', 1, 8);
  const nextVideo = { bvid: 'BV_NEXT', aid: 3, cid: 4 };
  player.load('https://example.invalid/next', [], 1.2, nextVideo);
  video.emit('loadedmetadata');
  assert.equal(elements.subtitleText.textContent, '');
  assert.equal(visible(), false);
  assert.equal(player.setSubtitles(snapshot, true, '', 1, 8), false);
  elements.subtitleToggle.emit('click');
  assert.match(elements.hint.textContent, /不匹配/);
  assert.equal(player.setSubtitles({ ...snapshot, ...nextVideo }, true, '', 1, 8), true);
  assert.equal(elements.subtitleText.textContent, '第一句');
  player.dispose();
  assert.equal(elements.subtitleText.textContent, '');
  assert.equal(player.setSubtitles({ ...snapshot, ...nextVideo }, true, '', 1, 8), false);
  console.log('PASS player: timing, seeking, settings, quality switch, login/errors, video/part identity, stale responses and disposal');
}

function testWatchHistoryNotification() {
  const { player, video, messages } = createPlayer();
  const watchMessages = () => messages.filter(message => message.startsWith('LOCAL_PLAYER_WATCHED:'));

  video.currentTime = 0.9;
  video.emit('timeupdate');
  assert.equal(watchMessages().length, 0, 'Paused or sub-second media must not enter account history');

  video.play();
  video.currentTime = 1.1;
  video.emit('timeupdate');
  assert.equal(watchMessages().length, 1, 'First real playback second should report history once');
  assert.deepEqual(JSON.parse(watchMessages()[0].slice('LOCAL_PLAYER_WATCHED:'.length)), {
    ...testIdentity, time: 1.1
  });

  video.currentTime = 10;
  video.emit('timeupdate');
  player.load('https://example.invalid/reload', [], 20, testIdentity);
  video.emit('loadedmetadata');
  video.play();
  video.emit('timeupdate');
  assert.equal(watchMessages().length, 1, 'Quality reloads must not create periodic heartbeats');
  player.dispose();
  console.log('PASS watch history trigger: actual playback, one-second threshold, identity and one-shot behavior');
}

function testParts() {
  const { player, elements, video, messages, document } = createPlayer();
  const first = { ...testIdentity, page: 1 };
  const second = { ...testIdentity, cid: 3, page: 2 };
  const parts = [
    { cid: first.cid, page: 1, part: '配音', duration: 1558 },
    { cid: second.cid, page: 2, part: '<原声>', duration: 1606 }
  ];
  assert.equal(elements.partToggle.disabled, true);
  assert.equal(player.setParts({ ...first, parts: parts.slice(0, 1) }), true);
  elements.partToggle.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), false);
  assert.equal(elements.partToggle.title, '此视频只有 1 P');

  assert.equal(player.setParts({ ...first, parts }), true);
  assert.equal(elements.partToggle.disabled, false);
  assert.equal(elements.partList.children[0].attributes['aria-checked'], 'true');
  assert.equal(elements.partList.children[1].textContent, 'P2<原声>26:46');
  elements.partToggle.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), true);
  elements.wrap.emit('mouseleave');
  assert.equal(elements.wrap.classList.contains('controls-hover'), true);
  elements.surface.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), false);
  assert.equal(video.paused, true, 'Dismissing the menu must not toggle playback');
  elements.partToggle.emit('click');
  document.emit('keydown', { key: 'Escape' });
  assert.equal(elements.partMenu.classList.contains('open'), false);

  player.setSubtitles({ ...first, tracks: [{ id: 'zh', lan: 'zh', cues: [{ f: 0, t: 10, c: 'P1字幕' }] }] }, true, '', 1, 8);
  elements.subtitleToggle.emit('click');
  elements.partToggle.emit('click');
  assert.equal(elements.subtitleMenu.classList.contains('open'), false);
  elements.commentToggle.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), false);
  elements.partToggle.emit('click');
  assert.equal(elements.commentPanel.attributes['aria-hidden'], 'true');
  elements.subtitleToggle.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), false);

  const partMessages = () => messages.filter(message => message.startsWith('LOCAL_PLAYER_PART:'));
  elements.partList.children[0].emit('click');
  assert.equal(partMessages().length, 0, 'Selecting the current P is a no-op');
  video.currentTime = 120;
  elements.partList.children[1].emit('click');
  assert.equal(elements.partToggle.disabled, true);
  assert.deepEqual(JSON.parse(partMessages()[0].slice('LOCAL_PLAYER_PART:'.length)), { bvid: first.bvid, cid: 3, page: 2 });
  elements.partList.children[1].emit('click');
  assert.equal(partMessages().length, 1, 'Do not dispatch duplicate switches');

  player.suspend();
  video.emit('canplay');
  assert.equal(video.paused, true, 'Old pending autoplay must be cancelled');
  player.load('https://example.invalid/p2', [], 0, second);
  assert.equal(player.state().duration, 0, 'Do not save old media duration for a loading P');
  assert.equal(elements.subtitleText.textContent, '');
  player.setParts({ ...second, parts });
  player.setPartsBusy(false);
  video.emit('loadedmetadata');
  assert.equal(video.currentTime, 0, 'P2 must not inherit P1 time');
  assert.equal(player.state().cid, 3);
  assert.equal(player.state().page, 2);
  assert.equal(elements.partToggle.disabled, false);
  assert.equal(elements.partList.children[1].attributes['aria-checked'], 'true');
  assert.equal(player.setParts({ ...first, parts }), false, 'Reject stale part snapshots');
  assert.equal(player.setParts({ ...second, bvid: 'OTHER', parts }), false);
  assert.equal(player.setSubtitles({ ...first, tracks: [] }, true, '', 1, 8), false);

  // Quality changes keep the current P; superseded loads cannot seek or autoplay later.
  player.load('https://example.invalid/p2-hd', [], 24, second);
  player.load('https://example.invalid/p2-sd', [], 14, second);
  assert.equal(video.events.get('loadedmetadata').length, 1);
  video.emit('loadedmetadata');
  assert.equal(video.currentTime, 14);
  assert.equal(elements.partList.children[1].attributes['aria-checked'], 'true');
  player.setPartsBusy(true);
  player.setPartsBusy(false);
  elements.partToggle.emit('click');
  assert.equal(elements.partMenu.classList.contains('open'), true, 'A failed/retried switch must unlock the menu');
  player.setDanmakuVisible(false, false);
  assert.equal(elements.danmaku.classList.contains('off'), true, 'Top-level danmaku control remains supported');
  player.dispose();
  assert.equal(elements.partList.children.length, 0);
  assert.equal(elements.partToggle.disabled, true);
  assert.equal(player.setParts({ ...second, parts }), false);
  console.log('PASS parts: single-P disabled, menu, names/durations, selection, duplicate clicks, identity, progress isolation, pending load cleanup and disposal');
}

function testPartResolver() {
  const source = fs.readFileSync(path.join(root, 'BiliBiliPlayer/Services/BiliPlaybackResolver.cs'), 'utf8');
  const method = source.slice(source.indexOf('private static async Task<VideoContext> ReadVideoContextAsync'));
  const script = method.match(/\$\$"""\s*([\s\S]*?)\s*"""/)[1];
  const data = { ...testIdentity, cid: 2, pages: [
    { cid: 2, page: 1, part: '配音', duration: 1558 },
    { cid: 3, page: 2, part: '原声', duration: 1606 }
  ] };
  const resolve = (videoData, pageNumber) => JSON.parse(vm.runInNewContext(
    script.replaceAll('{{pageNumber}}', String(pageNumber)),
    { window: { __INITIAL_STATE__: { videoData } }, document: { querySelectorAll: () => [] } }
  ));
  const second = resolve(data, 2);
  assert.equal(second.cid, 3, 'P2 must be selected from pages even when videoData.cid is P1');
  assert.equal(second.pageNumber, 2);
  assert.equal(second.parts.length, 2);
  assert.equal(second.parts.reduce((sum, part) => sum + part.duration, 0), 3164);
  assert.equal(resolve(data, 1).cid, 2);
  assert.equal(resolve(data, 99).pageNumber, 1, 'A removed remembered page falls back to the first P');
  const single = resolve({ ...testIdentity, title: '正片', duration: 60 }, 1);
  assert.equal(single.parts.length, 1);
  assert.equal(single.parts[0].cid, testIdentity.cid);

  const fallbackMethod = source.slice(source.indexOf('private static async Task<string?> TryReadPagePlayInfoAsync'));
  const fallbackScript = fallbackMethod.match(/\$\$"""\s*([\s\S]*?)\s*"""/)[1];
  const fallback = (info, count = 2) => JSON.parse(vm.runInNewContext(
    fallbackScript.replaceAll('{{context.Parts.Count}}', String(count)).replaceAll('{{context.Cid}}', '3'),
    { window: { __playinfo__: info } }
  ));
  assert.equal(fallback({ data: { cid: 2 } }), null);
  assert.equal(fallback({ data: { last_play_cid: 3 } }), null, 'Watch history is not proof of media ownership');
  assert.equal(fallback({ data: { cid: 3 } }).data.cid, 3);
  assert.equal(fallback({ data: { quality: 80 } }, 1).data.quality, 80);
  console.log('PASS part resolver: page/cid mapping, durations, single-P fallback and stale play-info rejection');
}

async function executeResolver(fetchImpl, identity) {
  const source = fs.readFileSync(path.join(root, 'BiliBiliPlayer/Services/BiliPlaybackResolver.cs'), 'utf8');
  const method = source.slice(source.indexOf('private static async Task<SubtitleSnapshot> FetchSubtitlesInPageAsync'));
  const script = method.match(/var script = \$\$"""\s*([\s\S]*?)\s*""";/)[1]
    .replace('{{prefixJson}}', '"TEST:"')
    .replaceAll('{{bvidJson}}', JSON.stringify(identity.bvid))
    .replaceAll('{{aid}}', String(identity.aid)).replaceAll('{{cid}}', String(identity.cid));
  let post;
  const result = new Promise(resolve => { post = resolve; });
  vm.runInNewContext(script, {
    URL, URLSearchParams, AbortController, setTimeout, clearTimeout,
    fetch: fetchImpl,
    chrome: { webview: { postMessage: message => post(JSON.parse(message.slice(5))) } }
  }, { filename: 'FetchSubtitlesInPageAsync.js' });
  return await result;
}

async function runResolver(dm, bodies = {}, fallback = null, identity = testIdentity) {
  const calls = [];
  const snapshot = await executeResolver(async (url, options) => {
      calls.push({ url, options });
      const apiPath = new URL(url).pathname;
      const data = apiPath === '/x/v2/dm/view' ? dm
        : apiPath === '/x/player/wbi/v2' ? fallback : bodies[url];
      if (!data) throw new Error('Missing test response');
      if (data instanceof Error) throw data;
      return { ok: true, status: 200, text: async () => typeof data === 'string' ? data : JSON.stringify(data) };
  }, identity);
  assert.ok(calls.every(call => new URL(call.url).pathname !== '/x/player/v2'), 'Never fall back to the legacy subtitle API');
  return { snapshot, calls };
}

async function testResolver() {
  const goodUrl = 'https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/12' + 'a'.repeat(32);
  const wrongUrl = 'https://aisubtitle.hdslb.com/bfs/ai_subtitle/prod/99' + 'a'.repeat(32);
  const subtitle = url => ({ code: 0, data: { subtitle: { subtitles: [
    { id_str: '1', lan: 'ai-zh', subtitle_url: url }
  ] } } });
  const oneCue = { body: [{ from: 1, to: 2, content: '正确字幕' }] };
  const empty = { code: 0, data: { subtitle: { subtitles: [] } } };
  const { snapshot, calls } = await runResolver({ code: 0, data: { subtitle: {
    lan: 'ai-zh', subtitles: [
      { id_str: '90071992547409930', lan: 'ai-zh', lan_doc: '中文（自动翻译）', type: 1, subtitle_url: goodUrl.replace('https:', '') },
      { id_str: 'failed', lan: 'en', subtitle_url: '//aisubtitle.hdslb.com/missing.json' }
    ]
  } } }, { [goodUrl]: { body: [
    { from: 3, to: 4, content: '第二句' },
    { from: 1, to: 2, content: '第一句' },
    { from: 'invalid', to: 3, content: '无效' },
    { from: 5, to: 4, content: '修复结束时间' }
  ] } });
  assert.equal(snapshot.tracks.length, 1);
  assert.equal(snapshot.tracks[0].id, '90071992547409930');
  assert.equal(snapshot.tracks[0].ai, true);
  assert.equal(snapshot.tracks[0].def, true);
  assert.equal(snapshot.tracks[0].cues[0].c, '第一句');
  assert.equal(snapshot.tracks[0].cues[2].t, 7);
  assert.equal(snapshot.error, '');
  assert.equal(snapshot.bvid, testIdentity.bvid);
  assert.equal(snapshot.aid, testIdentity.aid);
  assert.equal(snapshot.cid, testIdentity.cid);
  assert.equal(new URL(calls[0].url).pathname, '/x/v2/dm/view');
  assert.equal(new URL(calls[0].url).searchParams.get('oid'), '2');
  assert.equal(calls[0].options.credentials, 'include');
  assert.equal(calls[1].options.credentials, 'omit');
  assert.equal(calls[0].options.cache, 'no-store');

  const login = await runResolver(empty, {}, { code: 0, data: { ...testIdentity, need_login_subtitle: true, subtitle: { subtitles: [] } } });
  assert.equal(login.snapshot.login, true);
  const none = await runResolver(empty, {}, { code: 0, data: { ...testIdentity, subtitle: { subtitles: [] } } });
  assert.equal(none.snapshot.login, false);
  assert.equal(none.snapshot.error, '');

  // Reproduces the reported bug: matching metadata IDs but a different AI subtitle file.
  const wrong = await runResolver(subtitle(wrongUrl), { [wrongUrl]: oneCue });
  assert.equal(wrong.snapshot.tracks.length, 0);
  assert.match(wrong.snapshot.error, /不匹配/);
  assert.equal(wrong.calls.length, 1, 'Do not even download a mismatched subtitle file');
  for (const url of ['https://evil.invalid/subtitle', 'https://hdslb.com.evil.invalid/subtitle', 'javascript:alert(1)']) {
    const bad = await runResolver(subtitle(url));
    assert.equal(bad.snapshot.tracks.length, 0);
    assert.equal(bad.calls.length, 1);
  }
  for (const field of ['bvid', 'aid', 'cid']) {
    const wrongMetadata = subtitle(goodUrl);
    wrongMetadata.data[field] = 'wrong';
    const bad = await runResolver(wrongMetadata, { [goodUrl]: oneCue });
    assert.match(bad.snapshot.error, /不匹配/);
    assert.equal(bad.calls.length, 1);
    const fallbackData = { ...subtitle(goodUrl), data: { ...subtitle(goodUrl).data, ...testIdentity, [field]: 'wrong' } };
    const fallbackBad = await runResolver(empty, { [goodUrl]: oneCue }, fallbackData);
    assert.match(fallbackBad.snapshot.error, /不匹配/);
    assert.equal(fallbackBad.calls.length, 2);
  }
  const unbound = await runResolver(empty, { [goodUrl]: oneCue }, subtitle(goodUrl));
  assert.equal(unbound.snapshot.tracks.length, 0, 'Fallback requires all three IDs');
  const fallbackData = subtitle(goodUrl);
  Object.assign(fallbackData.data, testIdentity);
  const fallback = await runResolver(new Error('DM unavailable'), { [goodUrl]: oneCue }, fallbackData);
  assert.equal(fallback.snapshot.tracks[0].cues[0].c, '正确字幕');
  const fallbackWrongFile = subtitle(wrongUrl);
  Object.assign(fallbackWrongFile.data, testIdentity);
  const rejectedFallback = await runResolver(empty, { [wrongUrl]: oneCue }, fallbackWrongFile);
  assert.match(rejectedFallback.snapshot.error, /不匹配/);

  // Opaque community CC filenames remain supported through the cid-scoped configuration.
  const ccUrl = 'https://subtitle.hdslb.com/bfs/subtitle/opaque.json';
  const cc = await runResolver(subtitle(ccUrl.replace('https:', 'http:')), { [ccUrl]: '\uFEFF' + JSON.stringify(oneCue) });
  assert.equal(cc.snapshot.tracks.length, 1);
  const malformed = await runResolver(subtitle(goodUrl), { [goodUrl]: '<html>error</html>' });
  assert.match(malformed.snapshot.error, /加载失败/);
  const timeout = await runResolver(subtitle(goodUrl), { [goodUrl]: new Error('AbortError') });
  assert.match(timeout.snapshot.error, /加载失败/);
  const failure = await runResolver({ code: -403, message: 'Denied' }, {});
  assert.equal(failure.snapshot.tracks.length, 0);
  assert.match(failure.snapshot.error, /加载失败/);
  console.log('PASS resolver: cid-scoped API, credentials, IDs, AI file ownership, CC tracks, fallback, malformed data, errors and login');
}

async function testLive() {
  const bvid = 'BV1wr8S6WEX7';
  const headers = { 'User-Agent': 'Mozilla/5.0', Referer: `https://www.bilibili.com/video/${bvid}/`, Origin: 'https://www.bilibili.com' };
  const view = await fetch(`https://api.bilibili.com/x/web-interface/view?bvid=${bvid}`, { headers, signal: AbortSignal.timeout(10000) }).then(response => response.json());
  assert.equal(view.code, 0, 'Live video metadata must succeed');
  assert.equal(view.data.bvid, bvid);
  const identity = { bvid, aid: view.data.aid, cid: view.data.cid || view.data.pages[0].cid };
  const snapshot = await executeResolver((url, options) => fetch(url, { ...options, headers }), identity);
  assert.equal(snapshot.error, '', snapshot.error);
  const track = snapshot.tracks.find(item => item.lan === 'ai-zh') || snapshot.tracks[0];
  assert.ok(track?.cues.length > 0, 'Must retrieve real timed captions');

  // Feed the actual network result through the actual player script, not a transcript fixture.
  const { player, video, elements } = createPlayer(identity);
  video.duration = view.data.duration;
  assert.equal(player.setSubtitles(snapshot, true, track.lan, 1, 8), true);
  const at = time => { video.currentTime = time; video.emit('timeupdate'); return elements.subtitleText.textContent; };
  assert.equal(at(7), '');
  assert.match(at(13), /我们知道如何制造火箭/);
  assert.match(at(18), /也知道如何制造火箭所需的一切/);
  assert.doesNotMatch(track.cues.map(cue => cue.c).join('\n'), /人均5万日元|京都都来个三天两夜|京都.*三天两夜/);
  console.log(`PASS LIVE ${bvid} aid=${identity.aid} cid=${identity.cid}: ${track.cues.length} cues; 7s empty, 13s rocket, 18s screenshot match`);
}

(async () => {
  testPlayer();
  testWatchHistoryNotification();
  testParts();
  testPartResolver();
  await testResolver();
  if (process.argv.includes('--live')) await testLive();
})().catch(error => { console.error(error); process.exitCode = 1; });
