const $ = (id) => document.getElementById(id);
let currentTab = null;
let liveStarted = null;

async function refresh() {
  let state;
  try {
    state = await AIS.message({ type: 'status' });
  } catch (error) {
    $('status').textContent = error.message;
    return;
  }
  const { helper, live, settings } = state;
  if (!helper.connected) {
    $('status').innerHTML = `<span class="dot orange"></span> ${AIS.escape(helper.error && /not found|forbidden/i.test(helper.error)
      ? 'Helper not installed — run “Install AI Stack”' : `Helper unavailable (${helper.error || 'starting…'})`)}`;
  } else {
    $('status').innerHTML = '<span class="dot green"></span> Connected';
    AIS.host({ type: 'auth' }, 20000)
      .then((auth) => {
        $('status').innerHTML = auth.loggedIn
          ? `<span class="dot green"></span> Claude: ${AIS.escape(auth.summary)}`
          : `<span class="dot orange"></span> ${AIS.escape(auth.summary)} — run <b>claude</b> in a terminal to log in`;
      })
      .catch(() => {});
  }

  $('live').hidden = !live;
  $('stop').hidden = !live;
  $('start').hidden = Boolean(live);
  if (live) {
    $('liveTitle').textContent = live.title;
    try {
      const reply = await AIS.host({ type: 'meeting.get', meetingId: live.meetingId }, 10000);
      liveStarted = reply.meeting ? +new Date(reply.meeting.startedAt) : Date.now();
    } catch { liveStarted = Date.now(); }
  }

  for (const key of ['writingEnabled', 'styleSuggestions']) $(key).checked = Boolean(settings[key]);
  $('notesMode').value = settings.notesMode;
  $('notesLanguage').value = settings.notesLanguage;
  const site = /^https?:/.test(currentTab?.url || '') ? new URL(currentTab.url).hostname : '';
  $('site').textContent = site || 'this site';
  $('siteOff').checked = (settings.disabledSites || []).includes(site);
  $('siteOff').disabled = !site;
}

$('notesLanguage').innerHTML = AIS.LANGUAGES.map(([id, name]) => `<option value="${id}">${name}</option>`).join('');

for (const key of ['writingEnabled', 'styleSuggestions']) {
  $(key).addEventListener('change', (e) => chrome.storage.local.set({ [key]: e.target.checked }));
}
$('notesMode').addEventListener('change', (e) => chrome.storage.local.set({ notesMode: e.target.value }));
$('notesLanguage').addEventListener('change', (e) => chrome.storage.local.set({ notesLanguage: e.target.value }));
$('siteOff').addEventListener('change', async (e) => {
  const site = new URL(currentTab.url).hostname;
  const { disabledSites = [] } = await chrome.storage.local.get('disabledSites');
  const next = e.target.checked ? [...new Set([...disabledSites, site])] : disabledSites.filter((s) => s !== site);
  chrome.storage.local.set({ disabledSites: next });
});

$('start').addEventListener('click', async () => {
  $('start').disabled = true;
  try {
    await AIS.message({ type: 'start', tabId: currentTab?.id });
    if (currentTab) await chrome.sidePanel.open({ tabId: currentTab.id }).catch(() => {});
  } catch (error) {
    $('status').textContent = error.message;
  }
  $('start').disabled = false;
  refresh();
});
$('stop').addEventListener('click', async () => {
  await AIS.message({ type: 'stop' }).catch(() => {});
  refresh();
});
$('notepad').addEventListener('click', async () => {
  // Opening from the popup counts as a user gesture, so the side panel can open directly.
  if (currentTab) await chrome.sidePanel.open({ tabId: currentTab.id }).catch(() => AIS.message({ type: 'openNotepad' }));
  window.close();
});
$('library').addEventListener('click', () => { AIS.message({ type: 'openNotes' }); window.close(); });
$('doctor').addEventListener('click', () => { AIS.message({ type: 'openDoctor' }); window.close(); });
$('test').addEventListener('click', async () => {
  $('test').disabled = true;
  $('testResult').textContent = 'Checking “I has a apple.”…';
  const started = Date.now();
  try {
    const reply = await AIS.message({ type: 'grammar', paragraphs: ['I has a apple.'], app: 'AI Stack test' });
    const issues = JSON.parse(reply.text.slice(reply.text.indexOf('{'), reply.text.lastIndexOf('}') + 1)).issues || [];
    $('testResult').innerHTML = `<span class="dot green"></span> Works — ${issues.length} issue(s) found in ${((Date.now() - started) / 1000).toFixed(1)}s`;
  } catch (error) {
    $('testResult').innerHTML = `<span class="dot orange"></span> ${AIS.escape(error.message)}`;
  }
  $('test').disabled = false;
});

setInterval(() => {
  if (liveStarted) $('liveTime').textContent = AIS.clock(Date.now() - liveStarted);
}, 1000);

chrome.runtime.onMessage.addListener((message) => {
  if (message.type === 'status-changed') refresh();
});

chrome.tabs.query({ active: true, currentWindow: true }).then(([tab]) => {
  currentTab = tab || null;
  refresh();
});
