// Laptop Doctor page: the checkup report, the treatment plan (approve / decline / always), History with Undo, and
// settings. Every decision goes to the native helper (DoctorService), which records it before anything runs.

const $ = (id) => document.getElementById(id);
const { escape } = AIS;

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const THRESHOLDS = [
  ['coldDays', 'Compress files unused for', 60],
  ['cloudColdDays', 'Free cloud copies unopened for', 60],
  ['archiveDays', 'Archive listed folders unused for', 90],
  ['installerAgeDays', 'Suggest removing installers older than', 30],
  ['inactiveProjectDays', 'Suggest cleaning projects untouched for', 90],
];

let settings = null;
let current = null; // { id, data }
const open = new Set(); // expanded plan groups

function size(bytes) {
  const units = ['bytes', 'KB', 'MB', 'GB', 'TB'];
  let n = bytes || 0;
  let i = 0;
  while (n >= 1000 && i < units.length - 1) { n /= 1000; i += 1; }
  return i ? `${n.toFixed(n < 10 ? 1 : 0)} ${units[i]}` : `${n} bytes`;
}

const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

function when(id) {
  const d = new Date(id);
  return isNaN(d) ? id : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function showError(error) { $('error').textContent = error ? error.message || String(error) : ''; }

async function host(type, payload = {}, timeoutMs) {
  const reply = await AIS.host({ type, ...payload }, timeoutMs);
  showError(null);
  return reply;
}

// ---------------------------------------------------------------------------
// Report and plan

function templateSummary(d) {
  const t = d.outcome.tier1 || {};
  const s = [t.done > 0
    ? `Freed ${size(t.freedBytes)} automatically without deleting anything (${plural(t.done, 'item')}; each can be undone in History).`
    : 'Nothing needed doing automatically.'];
  if (d.malware) s.push(`${plural(d.malware, 'file')} flagged as malware: Windows Security has quarantined ${d.malware === 1 ? 'it' : 'them'}.`);
  const weak = Object.entries(d.posture || {}).filter(([, v]) => /^(off|disabled|missing)$/i.test(v)).map(([k]) => k).sort();
  if (weak.length) s.push(`Security settings that are off: ${weak.join(', ')}.`);
  if (d.startup) s.push(`${plural(d.startup, 'startup item')} worth a look.`);
  const left = openItems(d);
  s.push(left.length
    ? `${plural(left.length, 'suggestion')} (${size(left.reduce((a, i) => a + i.bytes, 0))}) wait for your OK below. Nothing is removed until you approve, and deleted items go to the Recycle Bin.`
    : 'There is nothing to decide.');
  if (d.skipped) s.push(`${plural(d.skipped, 'area')} ${d.skipped === 1 ? 'was' : 'were'} not scanned.`);
  return s.join(' ');
}

function openItems(d) {
  const decided = new Set(d.decided);
  return d.plan.groups.flatMap((g) => g.items).filter((i) => !decided.has(i.id));
}

async function loadCheckups(select) {
  const { checkups } = await host('doctor.checkups');
  $('checkup').innerHTML = checkups.map((c) =>
    `<option value="${escape(c.checkup)}">${escape(when(c.checkup))}${c.planItems ? ` · ${c.planItems} to decide` : ''}</option>`).join('');
  $('checkup').hidden = !checkups.length;
  const id = select && checkups.some((c) => c.checkup === select) ? select : checkups[0]?.checkup;
  if (id) $('checkup').value = id;
  await loadCheckup(id);
}

async function loadCheckup(id) {
  if (!id) {
    current = null;
    $('summary').textContent = settings?.enabled
      ? 'No checkup yet. The first one runs at the next scheduled time, or press "Check up now".'
      : 'Laptop Doctor is off. Turn it on in Settings, or press "Check up now" for a one-off checkup.';
    $('plan').innerHTML = '';
    $('approveAll').hidden = true;
    $('claude').hidden = true;
    return;
  }
  const data = await host('doctor.get', { checkup: id });
  current = { id, data };
  history.replaceState(null, '', `#${encodeURIComponent(id)}`);
  renderReport();
}

function renderReport() {
  const d = current.data;
  $('summary').innerHTML = d.summary ? AIS.markdown(d.summary) : `<p>${escape(templateSummary(d))}</p>`;
  $('claude').hidden = !settings?.claudeSummary || !!d.summary;
  const decided = new Set(d.decided);
  const ruleable = new Set(d.ruleable);
  $('approveAll').hidden = !openItems(d).length;
  $('plan').innerHTML = d.plan.groups.map((g, gi) => {
    const left = g.items.filter((i) => !decided.has(i.id));
    const done = !left.length;
    return `<div class="card ${done ? 'decided' : ''}" data-group="${gi}">
      <div class="group-head">
        <span class="label" data-toggle="${gi}">${open.has(g.category) ? '▾' : '▸'} ${escape(g.label)}</span>
        <span class="small muted">${size(g.totalBytes)}</span>
      </div>
      ${open.has(g.category) ? `<div class="items">${g.items.map((i) => `
        <label><input type="checkbox" data-item="${escape(i.id)}" ${decided.has(i.id) ? 'disabled' : 'checked'}>
          <span class="grow"><span class="path">${escape(i.processName || i.path)}</span>
          <span class="small muted"> · ${size(i.bytes)} · ${escape(i.reason)}${decided.has(i.id) ? ' · decided' : ''}</span></span></label>`).join('')}</div>` : ''}
      ${done ? '<div class="small muted">Decided.</div>' : `<div class="actions">
        <button class="btn primary" data-decide="approve" data-g="${gi}">Approve</button>
        <button class="btn" data-decide="decline" data-g="${gi}">Decline</button>
        ${ruleable.has(g.category) ? `<button class="btn" data-decide="always" data-g="${gi}" title="Approve this kind of item in later checkups too">Always do this</button>` : ''}
      </div>`}
    </div>`;
  }).join('') || '<p class="muted">Nothing to decide in this checkup.</p>';
}

function chosen(gi) {
  const g = current.data.plan.groups[gi];
  const decided = new Set(current.data.decided);
  if (!open.has(g.category)) return g.items.filter((i) => !decided.has(i.id)).map((i) => i.id);
  const box = document.querySelector(`[data-group="${gi}"]`);
  return [...box.querySelectorAll('input[data-item]:checked:not(:disabled)')].map((el) => el.dataset.item);
}

async function decide(decision, items) {
  if (!items.length) return;
  if (decision === 'always' && !confirm('Approve this kind of item automatically in later checkups too?')) return;
  const { result } = await host('doctor.decide', { checkup: current.id, decision, items }, 600000);
  const o = result.outcome;
  $('status').textContent = decision === 'decline'
    ? `Declined ${plural(result.decided, 'item')}.`
    : `Done: ${o?.done ?? 0}${o?.skipped ? `, skipped ${o.skipped} that changed since the checkup` : ''}${o?.failed ? `, ${o.failed} failed` : ''}. Undo is in History.`;
  await loadCheckup(current.id);
}

$('plan').addEventListener('click', (e) => {
  const toggle = e.target.closest('[data-toggle]');
  if (toggle) {
    const cat = current.data.plan.groups[Number(toggle.dataset.toggle)].category;
    if (open.has(cat)) open.delete(cat); else open.add(cat);
    renderReport();
    return;
  }
  const button = e.target.closest('[data-decide]');
  if (button) decide(button.dataset.decide, chosen(Number(button.dataset.g))).catch(showError);
});

$('approveAll').addEventListener('click', () => {
  const items = openItems(current.data);
  const bytes = items.reduce((a, i) => a + i.bytes, 0);
  if (!confirm(`Approve all ${plural(items.length, 'item')} (${size(bytes)})? Deleted items go to the Recycle Bin.`)) return;
  decide('approve', items.map((i) => i.id)).catch(showError);
});

$('claude').addEventListener('click', async () => {
  $('claude').disabled = true;
  try {
    const { summary } = await host('doctor.summary', { checkup: current.id }, 180000);
    current.data.summary = summary;
    renderReport();
  } catch (error) { showError(error); } finally { $('claude').disabled = false; }
});

$('checkup').addEventListener('change', () => loadCheckup($('checkup').value).catch(showError));

$('checkNow').addEventListener('click', async () => {
  $('checkNow').disabled = true;
  $('status').textContent = 'Checking up… this can take a few minutes.';
  try {
    const { result } = await host('doctor.checkNow', { wait: true }, 1800000);
    $('status').textContent = result ? `Checkup done: ${size(result.tier1.freedBytes)} freed automatically, ${plural(result.planItems, 'item')} to decide.` : 'A checkup is already running.';
    await loadCheckups(result?.checkup);
  } catch (error) { showError(error); $('status').textContent = ''; } finally { $('checkNow').disabled = false; }
});

// ---------------------------------------------------------------------------
// History

const VERBS = { compress: 'Compressed', evictCloud: 'Freed local copy of', archiveFolder: 'Zipped', moveToTrash: 'Moved to the Recycle Bin',
  quarantine: 'Quarantined', disableStartupItem: 'Disabled', quitProcess: 'Quit', emptyTrash: 'Emptied the Recycle Bin' };

async function loadHistory() {
  const { history } = await host('doctor.history');
  $('history').innerHTML = history.map((h) => `
    <div class="card">
      <div class="row"><h3 class="grow">${escape(when(h.checkup))}</h3>
        ${h.entries.some((e) => e.canUndo) ? `<button class="btn" data-undo-checkup="${escape(h.checkup)}">Undo this checkup</button>` : ''}</div>
      ${h.entries.map((e) => `<div class="entry">
        <span class="grow"><span class="small">${escape(VERBS[e.action] || e.action)}</span> <span class="path small">${escape(e.path)}</span>
        <span class="small muted">${e.freedBytes ? ` · ${size(e.freedBytes)}` : ''}${e.by?.startsWith('rule:') ? ' · by a rule' : ''}${e.undone ? ' · undone' : ''}${e.note ? ` · ${escape(e.note)}` : ''}</span></span>
        ${e.canUndo ? `<button class="btn" data-undo="${escape(e.id)}">Undo</button>` : ''}</div>`).join('')}
    </div>`).join('') || '<p class="muted">Nothing has been done yet.</p>';
}

$('history').addEventListener('click', async (e) => {
  const one = e.target.closest('[data-undo]');
  const all = e.target.closest('[data-undo-checkup]');
  if (!one && !all) return;
  try {
    const { result } = one ? await host('doctor.undo', { entry: one.dataset.undo }, 600000)
      : await host('doctor.undoCheckup', { checkup: all.dataset.undoCheckup }, 600000);
    $('status').textContent = `Undone: ${result.done}${result.failed ? `, ${result.failed} failed` : ''}.`;
    await loadHistory();
  } catch (error) { showError(error); }
});

// ---------------------------------------------------------------------------
// Settings

function renderSettings() {
  const s = settings;
  $('enabled').checked = s.enabled;
  $('weekday').value = s.weekday;
  $('hour').value = s.hour;
  for (const k of ['evictCloud', 'compress', 'archive', 'claudeSummary']) $(k).checked = s[k];
  $('archiveFolders').value = s.archiveFolders.join('\n');
  $('archiveBox').hidden = !s.archive;
  for (const [key, , fallback] of THRESHOLDS) $(`t-${key}`).value = s.thresholds[key] ?? fallback;
}

async function saveSettings(change) {
  ({ settings } = await host('doctor.settings.set', { settings: { ...settings, ...change } }));
  renderSettings();
}

function setup() {
  $('weekday').innerHTML = DAYS.map((d, i) => `<option value="${i + 1}">${d}</option>`).join('');
  $('hour').innerHTML = Array.from({ length: 24 }, (_, h) => `<option value="${h}">${String(h).padStart(2, '0')}:00</option>`).join('');
  $('thresholds').innerHTML = THRESHOLDS.map(([key, label]) =>
    `<label class="toggle">${label} <span><input type="number" min="7" max="3650" id="t-${key}"> days</span></label>`).join('');
  const save = (change) => saveSettings(change).catch(showError);
  $('enabled').addEventListener('change', () => {
    if ($('enabled').checked && !settings.enabled) { $('enabled').checked = false; $('intro').hidden = false; return; }
    save({ enabled: $('enabled').checked });
  });
  $('turnOn').addEventListener('click', () => { $('intro').hidden = true; save({ enabled: true }); });
  $('notNow').addEventListener('click', () => { $('intro').hidden = true; });
  $('weekday').addEventListener('change', () => save({ weekday: Number($('weekday').value) }));
  $('hour').addEventListener('change', () => save({ hour: Number($('hour').value) }));
  for (const k of ['evictCloud', 'compress', 'archive', 'claudeSummary']) $(k).addEventListener('change', () => save({ [k]: $(k).checked }));
  $('archiveFolders').addEventListener('change', () => save({ archiveFolders: $('archiveFolders').value.split('\n').map((l) => l.trim()).filter(Boolean) }));
  for (const [key, , fallback] of THRESHOLDS) {
    $(`t-${key}`).addEventListener('change', (e) => save({ thresholds: { ...settings.thresholds, [key]: Number(e.target.value) || fallback } }));
  }
  $('openFolder').addEventListener('click', () => host('doctor.open').catch(showError));

  document.querySelectorAll('.tab').forEach((tab) => tab.addEventListener('click', () => {
    document.querySelectorAll('.tab').forEach((t) => t.classList.toggle('active', t === tab));
    for (const id of ['report', 'history', 'settings']) $(id).hidden = id !== tab.dataset.tab;
    if (tab.dataset.tab === 'history') loadHistory().catch(showError);
  }));

  chrome.runtime.onMessage.addListener((message) => {
    if (message.type === 'doctor-checkup' && !message.error && !$('checkNow').disabled) loadCheckups(current?.id).catch(showError);
  });
}

async function start() {
  setup();
  try {
    const reply = await host('doctor.settings');
    settings = reply.settings;
    renderSettings();
    if (!reply.writable) $('checkNow').disabled = true;
    await loadCheckups(decodeURIComponent(location.hash.slice(1)) || null);
  } catch (error) { showError(error); }
}

start();
