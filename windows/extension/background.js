// AI Stack service worker: bridge to the native helper, grammar requests, call detection.

const HOST = 'com.aistack.helper';

const DEFAULTS = {
  writingEnabled: true,
  styleSuggestions: true,
  disabledSites: [],
  writingModel: 'haiku',
  notesMode: 'auto', // auto | ask | manual
  notesLanguage: 'auto', // whisper language code or auto
  notesModel: 'opus',
};

const GRAMMAR_PROMPT = `You are the proofreading engine of a writing assistant like Grammarly. The user is typing in another app; you get their text as numbered paragraphs and return the spans to underline.

Two kinds of issues:
- "error" (red underline): spelling mistakes, typos, grammar errors (agreement, tense, articles, prepositions, conjugation), wrong/missing/duplicated words, clearly wrong punctuation or capitalisation.
- "style" (blue underline): awkward, unclear, wordy, redundant or unnatural phrasing that a skilled editor would rewrite. Propose a clearly better alternative with the same meaning and tone. Look actively for these: filler ("basically", "in order to", "at this point in time"), clumsy sentence structure, passive constructions that hurt clarity, repetition.

Rules:
- Keep each paragraph's language (English, French, ...) in fixes. Respect the writer's variant (British vs American) and register (casual chat vs formal email).
- Never flag names, brands, URLs, emails, code, hashtags, emoji or deliberate informal style.
- "text" must be copied EXACTLY from the paragraph (same characters, case, spacing). Errors: only the wrong word(s); duplicated words: include both copies (e.g. "to to" -> "to"). Style: the phrase or full sentence to rewrite.
- "fix" replaces "text" and must read correctly in place: check capitalisation and the words around the span. It must differ from "text".
- "before": up to 4 words immediately preceding "text" in the same paragraph, copied exactly ("" only if "text" starts the paragraph).
- "why": 2-8 words, in English.
- Real errors only; spans must not overlap; at most 2 style issues per paragraph.

Reply with ONLY minified JSON (no prose, no code fences):
{"issues":[{"p":<paragraph id number>,"type":"error"|"style","text":"...","before":"...","fix":"...","why":"..."}]}
Nothing to report: {"issues":[]}`;

const MEETING_SERVICES = [
  [/meet\.google\.com/, 'Google Meet'],
  [/teams\.(microsoft|live)\.com|teams\.cloud\.microsoft/, 'Microsoft Teams'],
  [/zoom\.us/, 'Zoom'],
  [/whereby\.com/, 'Whereby'],
  [/webex\.com/, 'Webex'],
  [/app\.slack\.com/, 'Slack Huddle'],
  [/discord\.com/, 'Discord'],
  [/meet\.jit\.si/, 'Jitsi'],
  [/around\.co/, 'Around'],
  [/gather\.town/, 'Gather'],
  [/riverside\.fm/, 'Riverside'],
  [/chime\.aws/, 'Chime'],
];

let settings = { ...DEFAULTS };
const settingsReady = chrome.storage.local.get(Object.keys(DEFAULTS)).then((stored) => {
  settings = { ...DEFAULTS, ...stored };
});
chrome.storage.onChanged.addListener((changes) => {
  for (const [key, { newValue }] of Object.entries(changes)) {
    if (key in DEFAULTS) settings[key] = newValue ?? DEFAULTS[key];
  }
});

// ---------------------------------------------------------------------------
// Native helper connection

let port = null;
let nextId = 1;
const pending = new Map();
let helper = { connected: false, error: null, info: null };

function helperError(raw) {
  if (/not found|not registered|forbidden/i.test(raw || '')) {
    return 'The AI Stack helper is not installed on this computer. Run "Install AI Stack" first.';
  }
  return `The AI Stack helper stopped (${raw || 'unknown reason'}). It restarts automatically.`;
}

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST);
  } catch (error) {
    helper = { connected: false, error: error.message, info: null };
    return;
  }
  helper = { connected: true, error: null, info: helper.info };
  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError?.message || 'disconnected';
    port = null;
    helper = { connected: false, error: reason, info: null };
    for (const request of pending.values()) request.reject(new Error(helperError(reason)));
    pending.clear();
    broadcast({ type: 'status-changed' });
    setTimeout(connect, 5000);
  });
  call('ping', {}, 15000)
    .then((info) => {
      helper.info = info;
      if (info.live && !live) {
        live = { meetingId: info.live.id, tabId: null, auto: false, title: info.live.title, inactiveSince: null };
        updateBadge();
      }
      broadcast({ type: 'status-changed' });
    })
    .catch(() => {});
}

function call(type, payload = {}, timeoutMs = 180000) {
  connect();
  if (!port) return Promise.reject(new Error(helperError(helper.error)));
  const id = nextId++;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error('The AI Stack helper did not answer in time.'));
    }, timeoutMs);
    pending.set(id, {
      resolve: (value) => { clearTimeout(timer); resolve(value); },
      reject: (error) => { clearTimeout(timer); reject(error); },
    });
    try {
      port.postMessage({ id, type, ...payload });
    } catch (error) {
      pending.delete(id);
      clearTimeout(timer);
      reject(error);
    }
  });
}

function onHostMessage(message) {
  if (message.id != null && pending.has(message.id)) {
    const request = pending.get(message.id);
    pending.delete(message.id);
    if (message.ok) request.resolve(message);
    else request.reject(new Error(message.error || 'Helper error'));
    return;
  }
  if (message.event === 'segment') broadcast({ type: 'segment', meetingId: message.meetingId, segment: message.segment });
  if (message.event === 'meeting') onMeetingEvent(message.meeting);
  if (message.event === 'doctor-checkup') onDoctorCheckup(message);
}

// ---------------------------------------------------------------------------
// Laptop Doctor: the helper runs the weekly checkup; at most one notification a day, only when there's a plan.

function onDoctorCheckup(message) {
  broadcast({ type: 'doctor-checkup', ...message });
  if (!message.notify) return;
  const gb = (message.planBytes / 1e9).toFixed(1);
  chrome.notifications.create(`doctor:${message.checkup}`, {
    type: 'basic',
    iconUrl: 'icons/icon128.png',
    title: 'Laptop Doctor checkup',
    message: `${message.planItems} suggestion${message.planItems === 1 ? '' : 's'} (${gb} GB) wait for your OK. Nothing is removed until you approve.`,
  });
}

function openDoctor(checkup) {
  const base = chrome.runtime.getURL('doctor.html');
  const url = checkup ? `${base}#${encodeURIComponent(checkup)}` : base;
  chrome.tabs.query({ url: `${base}*` }).then((tabs) => {
    if (tabs.length) {
      chrome.tabs.update(tabs[0].id, { active: true, url });
      chrome.windows.update(tabs[0].windowId, { focused: true });
    } else {
      chrome.tabs.create({ url });
    }
  });
}

function broadcast(message) {
  chrome.runtime.sendMessage(message).catch(() => {});
}

// ---------------------------------------------------------------------------
// Meetings

let live = null; // { meetingId, tabId, auto, title, inactiveSince }
const micFrames = new Map(); // "tabId:frameId" -> { active, since, seen }
const suppressedTabs = new Set();
const askedTabs = new Set();

function serviceFor(url) {
  for (const [pattern, name] of MEETING_SERVICES) if (pattern.test(url || '')) return name;
  return 'Browser';
}

function cleanTitle(title) {
  if (!title) return null;
  let t = title.replace(/^\(\d+\)\s*/, '').trim();
  t = t.replace(/^Meet\s*[-–—]\s*/, '').replace(/\s*\|\s*Microsoft Teams$/, '').replace(/\s*[-|]\s*Zoom$/, '').trim();
  if (/^[a-z]{3,4}-[a-z]{4}-[a-z]{3}$/.test(t)) return null;
  if (['Meet', 'Google Meet', 'Microsoft Teams', 'Teams', 'Zoom', 'Meeting', 'Zoom Meeting', 'Calls'].includes(t)) return null;
  return t || null;
}

function tabMicState(tabId) {
  let active = false;
  let since = Infinity;
  for (const [key, state] of micFrames) {
    if (!key.startsWith(`${tabId}:`)) continue;
    if (state.active && Date.now() - state.seen < 15000) {
      active = true;
      since = Math.min(since, state.since);
    }
  }
  return { active, since };
}

async function startMeeting(tabId, auto) {
  if (live) return live;
  await settingsReady;
  let tab = null;
  if (tabId != null) tab = await chrome.tabs.get(tabId).catch(() => null);
  const service = tab ? serviceFor(tab.url) : null;
  const title = tab ? cleanTitle(tab.title) : null;
  const time = new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  const reply = await call('meeting.start', {
    title: title || `${service || 'Meeting'}${service ? ' call' : ''} · ${time}`,
    titleIsAuto: !title,
    app: service || '',
    url: tab?.url || '',
    language: settings.notesLanguage,
    model: settings.notesModel,
  });
  live = { meetingId: reply.meeting.id, tabId: tab?.id ?? null, auto, title: reply.meeting.title, inactiveSince: null };
  updateBadge();
  if (live.tabId != null) chrome.tabs.sendMessage(live.tabId, { type: 'recording', on: true }).catch(() => {});
  chrome.notifications.create('recording', {
    type: 'basic',
    iconUrl: 'icons/icon128.png',
    title: 'AI Stack is taking notes',
    message: live.title,
    buttons: [{ title: 'Open notepad' }, { title: 'Stop' }],
    priority: 0,
  });
  broadcast({ type: 'status-changed' });
  return live;
}

async function stopMeeting(byUser) {
  if (!live) return;
  const current = live;
  live = null;
  if (byUser && current.tabId != null && tabMicState(current.tabId).active) suppressedTabs.add(current.tabId);
  if (current.tabId != null) chrome.tabs.sendMessage(current.tabId, { type: 'recording', on: false }).catch(() => {});
  chrome.notifications.clear('recording');
  updateBadge('…');
  broadcast({ type: 'status-changed' });
  try {
    await call('meeting.stop', {}, 30000);
  } catch (error) {
    console.warn('stop failed', error);
  }
}

function onMeetingEvent(meeting) {
  broadcast({ type: 'meeting-changed', meeting });
  if (meeting.status === 'processing' && !live) updateBadge('…');
  if (meeting.status === 'ready') {
    updateBadge();
    chrome.notifications.create(`ready:${meeting.id}`, {
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title: 'Meeting notes ready',
      message: meeting.title,
      priority: 1,
    });
  }
  if (meeting.status === 'failed') {
    updateBadge();
    chrome.notifications.create(`ready:${meeting.id}`, {
      type: 'basic',
      iconUrl: 'icons/icon128.png',
      title: "Couldn't write meeting notes",
      message: meeting.error || meeting.title,
    });
  }
}

function updateBadge(text) {
  if (live) {
    chrome.action.setBadgeText({ text: 'REC' });
    chrome.action.setBadgeBackgroundColor({ color: '#e5484d' });
  } else {
    chrome.action.setBadgeText({ text: text || '' });
    chrome.action.setBadgeBackgroundColor({ color: '#6e6e73' });
  }
}

async function evaluateCalls() {
  await settingsReady;
  const now = Date.now();
  if (live) {
    if (!live.auto || live.tabId == null) return;
    const { active } = tabMicState(live.tabId);
    if (active) {
      live.inactiveSince = null;
    } else {
      live.inactiveSince ??= now;
      if (now - live.inactiveSince > 20000) stopMeeting(false);
    }
    return;
  }
  const tabs = new Set([...micFrames.keys()].map((key) => Number(key.split(':')[0])));
  for (const tabId of tabs) {
    const { active, since } = tabMicState(tabId);
    if (!active) {
      suppressedTabs.delete(tabId);
      askedTabs.delete(tabId);
      continue;
    }
    if (suppressedTabs.has(tabId) || now - since < 3000) continue;
    if (settings.notesMode === 'auto') {
      startMeeting(tabId, true).catch((error) => console.warn('start failed', error));
    } else if (settings.notesMode === 'ask' && !askedTabs.has(tabId)) {
      askedTabs.add(tabId);
      const tab = await chrome.tabs.get(tabId).catch(() => null);
      chrome.notifications.create(`ask:${tabId}`, {
        type: 'basic',
        iconUrl: 'icons/icon128.png',
        title: `Call detected — ${serviceFor(tab?.url)}`,
        message: cleanTitle(tab?.title) || 'Take notes for this call?',
        buttons: [{ title: 'Take notes' }, { title: 'Not now' }],
        requireInteraction: true,
      });
    }
    break;
  }
}

setInterval(evaluateCalls, 2000);

chrome.tabs.onRemoved.addListener((tabId) => {
  for (const key of [...micFrames.keys()]) if (key.startsWith(`${tabId}:`)) micFrames.delete(key);
  if (live?.tabId === tabId && live.auto) live.inactiveSince = Date.now() - 17000; // stop ~3 s after closing the call tab
});

chrome.tabs.onUpdated.addListener((tabId, change) => {
  if (!change.url) return;
  for (const key of [...micFrames.keys()]) if (key.startsWith(`${tabId}:`)) micFrames.delete(key);
});

async function openNotepad(tabId) {
  try {
    if (tabId != null) {
      await chrome.sidePanel.open({ tabId });
      return;
    }
  } catch (error) {
    // Needs a user gesture in some contexts; fall back to a small window.
  }
  chrome.windows.create({ url: chrome.runtime.getURL('sidepanel.html?window=1'), type: 'popup', width: 420, height: 760 });
}

function openNotes(meetingId) {
  const url = chrome.runtime.getURL(`notes.html${meetingId ? `#${meetingId}` : ''}`);
  chrome.tabs.query({ url: chrome.runtime.getURL('notes.html') + '*' }).then((tabs) => {
    if (tabs.length) {
      chrome.tabs.update(tabs[0].id, { active: true, url });
      chrome.windows.update(tabs[0].windowId, { focused: true });
    } else {
      chrome.tabs.create({ url });
    }
  });
}

chrome.notifications.onButtonClicked.addListener((id, index) => {
  if (id === 'recording') {
    if (index === 0) openNotepad(live?.tabId);
    else stopMeeting(true);
  } else if (id.startsWith('ask:')) {
    const tabId = Number(id.split(':')[1]);
    if (index === 0) startMeeting(tabId, true).catch(() => {});
    else suppressedTabs.add(tabId);
    chrome.notifications.clear(id);
  }
});

chrome.notifications.onClicked.addListener((id) => {
  if (id.startsWith('ready:')) openNotes(id.slice(6));
  else if (id.startsWith('doctor:')) openDoctor(id.slice(7));
  else if (id === 'recording') openNotepad(live?.tabId);
  chrome.notifications.clear(id);
});

// ---------------------------------------------------------------------------
// Messages from content scripts and extension pages

function grammarPrompt(paragraphs, app) {
  let prompt = `App: ${app}\n`;
  paragraphs.forEach((p, i) => { prompt += `<p id="${i + 1}">${p}</p>\n`; });
  return prompt;
}

function grammarConfig() {
  return { model: settings.writingModel, system: GRAMMAR_PROMPT, effort: 'low', noThinking: true, keepWarm: true };
}

let lastPrewarm = 0;

async function handle(message, sender) {
  await settingsReady;
  switch (message.type) {
    case 'grammar': {
      const reply = await call('claude', { ...grammarConfig(), prompt: grammarPrompt(message.paragraphs, message.app), timeoutSec: 60 }, 70000);
      return { text: reply.text };
    }
    case 'prewarm':
      if (Date.now() - lastPrewarm > 30000) {
        lastPrewarm = Date.now();
        call('prewarm', grammarConfig(), 10000).catch(() => {});
      }
      return {};
    case 'mic': {
      const tabId = sender.tab?.id;
      if (tabId == null) return {};
      const key = `${tabId}:${sender.frameId ?? 0}`;
      const previous = micFrames.get(key);
      micFrames.set(key, {
        active: message.active,
        since: message.active ? (previous?.active ? previous.since : Date.now()) : Infinity,
        seen: Date.now(),
      });
      return { recording: live?.tabId === tabId };
    }
    case 'status':
      return { helper, live, settings, version: chrome.runtime.getManifest().version };
    case 'start':
      return { live: await startMeeting(message.tabId ?? null, false) };
    case 'stop':
      await stopMeeting(true);
      return {};
    case 'openNotepad':
      await openNotepad(message.tabId ?? sender.tab?.id ?? null);
      return {};
    case 'openNotes':
      openNotes(message.meetingId);
      return {};
    case 'openDoctor':
      openDoctor(message.checkup);
      return {};
    case 'host': {
      // Raw helper requests (doctor decisions, settings) come only from our own pages, never from content scripts.
      if (!sender.url?.startsWith(chrome.runtime.getURL(''))) throw new Error('host requests are for extension pages only');
      const { type, ...payload } = message.request;
      return await call(type, payload, message.timeoutMs || 300000);
    }
    default:
      throw new Error(`Unknown message ${message.type}`);
  }
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message.type !== 'string' || ['segment', 'status-changed', 'meeting-changed', 'doctor-checkup'].includes(message.type)) return false;
  handle(message, sender)
    .then((result) => sendResponse({ ok: true, ...result }))
    .catch((error) => sendResponse({ ok: false, error: error.message }));
  return true;
});

// Keep the helper connected: Chrome keeps this worker alive while the native port is open.
chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
chrome.alarms.create('keepalive', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(connect);
connect();
