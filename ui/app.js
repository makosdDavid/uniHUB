'use strict';

const TOKEN = new URLSearchParams(location.search).get('t') || '';
const SVGNS = 'http://www.w3.org/2000/svg';
const $ = id => document.getElementById(id);
const clone = o => JSON.parse(JSON.stringify(o));

function h(tag, props, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') e.className = v;
    else if (k === 'text') e.textContent = v;
    else if (k === 'value') e.value = v;
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), v);
    else if (v === true) e.setAttribute(k, '');
    else e.setAttribute(k, v);
  }
  for (const kid of kids) if (kid != null) e.append(kid);
  return e;
}
function svgEl(tag, attrs, text) {
  const e = document.createElementNS(SVGNS, tag);
  for (const [k, v] of Object.entries(attrs || {})) e.setAttribute(k, v);
  if (text != null) e.textContent = text;
  return e;
}

// ---------- inputs ----------
const T16 = '044F:B10A';
const DIRS = ['U', 'UR', 'R', 'DR', 'D', 'DL', 'L', 'UL'];
const DIR_HU = ['fel', 'jobbra fel', 'jobbra', 'jobbra le', 'le', 'balra le', 'balra', 'balra fel'];
const DIR_ARROW = ['▲', '↗', '▶', '↘', '▼', '↙', '◀', '↖'];
const MOD_DOMS = new Set(['ShiftLeft', 'ShiftRight', 'ControlLeft', 'ControlRight', 'AltLeft', 'AltRight', 'MetaLeft', 'MetaRight']);
const LAYOUT_GROUPS = new Set(['Betűk és számok', 'Írásjelek']);

function inputName(id) {
  let m = /^b(\d+)$/.exec(id);
  if (m) return m[1] + '. gomb';
  m = /^h(\d)(U|UR|R|DR|D|DL|L|UL)$/.exec(id);
  if (m) return (m[1] === '1' ? 'Kalap ' : m[1] + '. kalap ') + DIR_HU[DIRS.indexOf(m[2])];
  return id;
}
function inputOrder(id) {
  let m = /^b(\d+)$/.exec(id);
  if (m) return Number(m[1]);
  m = /^h(\d)(\w+)$/.exec(id);
  return m ? 1000 + Number(m[1]) * 10 + DIRS.indexOf(m[2]) : 9999;
}
const isDiagonal = id => /^h\d(UR|DR|DL|UL)$/.test(id);

// ---------- state ----------
let ST = null;               // last /api/state
let work = null;             // edited copy of the configuration
let orig = '';
let live = { paused: false, dryRun: false, active: { id: 'default', name: '' }, exe: null };
let devices = [];
let devicesRev = -1;
let pressed = {};
let recent = [];
let logSeq = 0;
let serverDown = false;
let selProfile = 'default';
let selDevice = null;
let selInput = null;
let pendingType = 'none';    // tab chosen on an input that has no binding yet
let focusAfter = null;       // id of a capture field to focus after the panel is rebuilt
const keyByCode = new Map();
const keyByDom = new Map();
let layout = null;

async function api(path, body) {
  const opt = { headers: { 'X-Token': TOKEN } };
  if (body !== undefined) { opt.method = 'POST'; opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  const r = await fetch(path, opt);
  const j = await r.json();
  if (j && j.ok === false) throw new Error(j.error || 'Ismeretlen hiba');
  return j;
}

let toastTimer = 0;
function toast(msg, bad) {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast' + (bad ? ' bad' : '');
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, bad ? 12000 : 6000);
}

// ---------- keys ----------
function keyLabel(code) {
  const k = keyByCode.get(code);
  if (!k) return 'kód ' + code;
  if (layout && LAYOUT_GROUPS.has(k.group)) {
    const ch = layout.get(k.dom);
    if (ch && ch.trim()) return k.dom === 'IntlBackslash' ? ch.toUpperCase() + ' (a bal Shift melletti)' : ch.toUpperCase();
  }
  return k.name;
}
const comboLabel = keys => keys.length ? keys.map(keyLabel).join(' + ') : '(nincs billentyű)';
// Records a key or a combination in a text field: modifiers held + one key, or modifiers alone.
function attachCapture(input, onCombo) {
  let mods = [], peak = [], done = false;
  const reset = () => { mods = []; peak = []; done = false; };
  input.addEventListener('keydown', e => {
    if (e.key === 'Tab' && !mods.length) return;   // keep Tab for moving between fields
    e.preventDefault();
    e.stopPropagation();
    if (e.repeat) return;
    const k = keyByDom.get(e.code);
    if (!k) { toast('Ezt a billentyűt nem tudom felvenni – válaszd ki a listából.', true); return; }
    if (MOD_DOMS.has(e.code)) {
      if (!mods.includes(k.code)) mods.push(k.code);
      if (!peak.includes(k.code)) peak.push(k.code);
      done = false;
      input.value = comboLabel(mods) + ' + …';
      return;
    }
    done = true;
    input.value = '';
    // a modifier reported only by its flag (no separate keydown seen) still counts, as its left key
    const flags = [[e.ctrlKey, 'Control'], [e.shiftKey, 'Shift'], [e.altKey, 'Alt'], [e.metaKey, 'Meta']];
    const extra = [];
    for (const [on, name] of flags) {
      if (!on || mods.some(c => keyByCode.get(c).dom.startsWith(name))) continue;
      const left = keyByDom.get(name + 'Left');
      if (left) extra.push(left.code);
    }
    onCombo([...extra, ...mods, k.code].slice(0, 6));
  });
  input.addEventListener('keyup', e => {
    e.preventDefault();
    const k = keyByDom.get(e.code);
    if (!k || !MOD_DOMS.has(e.code)) return;
    mods = mods.filter(c => c !== k.code);
    if (mods.length) return;
    const p = peak.slice(0, 6), commit = !done && p.length;
    reset();
    input.value = '';
    if (commit) onCombo(p);
  });
  input.addEventListener('blur', () => { reset(); input.value = ''; });
}

function keySelect() {
  const sel = h('select', { 'aria-label': 'Billentyű a listából' }, h('option', { value: '', text: '…vagy válassz a listából' }));
  const groups = new Map();
  for (const k of ST.keys) {
    if (!groups.has(k.group)) groups.set(k.group, h('optgroup', { label: k.group }));
    groups.get(k.group).append(h('option', { value: String(k.code), text: keyLabel(k.code) }));
  }
  for (const g of groups.values()) sel.append(g);
  return sel;
}

function comboEditor(keys, onChange, capId) {
  const chips = h('div', { class: 'chips' });
  if (!keys.length) chips.append(h('span', { class: 'muted small', text: 'Még nincs billentyű.' }));
  keys.forEach((c, i) => {
    if (i) chips.append(h('span', { class: 'plus', text: '+' }));
    chips.append(h('span', { class: 'kchip' }, keyLabel(c),
      h('button', { type: 'button', title: 'Eltávolítás', 'aria-label': keyLabel(c) + ' eltávolítása', text: '✕', onclick: () => onChange(keys.filter((_, j) => j !== i)) })));
  });
  const cap = h('input', { type: 'text', class: 'capture', id: capId, readonly: true, placeholder: 'Kattints ide, és nyomd le a billentyűt vagy a kombinációt' });
  attachCapture(cap, combo => { focusAfter = capId; onChange(combo); });
  const sel = keySelect();
  const add = h('button', {
    type: 'button', class: 'mini', text: 'Hozzáad', onclick: () => {
      const c = Number(sel.value);
      if (!c) return;
      if (keys.includes(c)) { toast('Ez a billentyű már benne van.', true); return; }
      if (keys.length >= 6) { toast('Legfeljebb 6 billentyű lehet egyszerre.', true); return; }
      onChange([...keys, c]);
    }
  });
  return h('div', { class: 'combo' }, chips, cap, h('div', { class: 'row' }, sel, add));
}

// ---------- configuration helpers ----------
const prof = () => work.profiles.find(p => p.id === selProfile) || work.profiles[0];
const findBinding = (p, dev, inp) => p.bindings.find(b => b.device === dev && b.input === inp);
const deviceInfo = key => devices.find(d => d.key === key);
const deviceName = key => (deviceInfo(key) || {}).name || work.deviceNames[key] || key;
const isDirty = () => work && JSON.stringify(work) !== orig;

function setBinding(b) {
  const p = prof();
  const i = p.bindings.findIndex(x => x.device === b.device && x.input === b.input);
  if (i >= 0) p.bindings[i] = b; else p.bindings.push(b);
  const d = deviceInfo(b.device);
  if (d && work.deviceNames[b.device] !== d.name) work.deviceNames[b.device] = d.name;
}
function removeBinding(dev, inp) {
  const p = prof();
  p.bindings = p.bindings.filter(b => !(b.device === dev && b.input === inp));
}
function exeOwner(exe) {
  return work.profiles.find(p => p.exes.some(e => e.toLowerCase() === exe.toLowerCase()));
}
function knownDeviceKeys() {
  const keys = new Set(devices.map(d => d.key));
  for (const k of Object.keys(work.deviceNames)) keys.add(k);
  for (const p of work.profiles) for (const b of p.bindings) keys.add(b.device);
  return [...keys];
}

// ---------- header ----------
function renderTop() {
  $('cfg-path').textContent = ST ? ST.configPath : '';
  const m = $('chip-mode');
  if (serverDown) { m.textContent = 'A USB Mapper nem válaszol'; m.className = 'chip bad'; }
  else if (live.paused) { m.textContent = 'Szünetel – nem küld billentyűt'; m.className = 'chip warn'; }
  else if (live.dryRun) { m.textContent = 'Teszt mód – nem küld billentyűt, csak naplóz'; m.className = 'chip warn'; }
  else { m.textContent = 'Működik'; m.className = 'chip'; }
  $('chip-active').textContent = 'Most aktív: ' + live.active.name + (live.exe ? ' · ' + live.exe : '') + (live.macros ? ' · fut ' + live.macros + ' makró' : '') + (recording ? ' · rögzítés' : '');
  const dirty = isDirty();
  $('chip-dirty').hidden = !dirty;
  $('btn-save').disabled = !dirty || serverDown;
  $('btn-discard').disabled = !dirty;
  $('btn-pause').textContent = live.paused ? 'Folytatás' : 'Szünet';
  $('btn-pause').disabled = serverDown;
}

// ---------- profiles ----------
function renderPills() {
  const box = $('profile-tabs');
  box.replaceChildren();
  for (const p of work.profiles) {
    box.append(h('button', {
      type: 'button', class: 'pill' + (p.id === selProfile ? ' on' : ''), title: p.id === live.active.id ? 'Most ez a profil aktív' : null,
      onclick: () => { selProfile = p.id; pendingType = 'none'; renderProfiles(); renderDevice(); renderPanel(); renderTable(); }
    }, p.id === live.active.id ? h('span', { class: 'dot', 'aria-label': 'aktív' }) : null, p.name || '(név nélkül)'));
  }
  box.append(h('button', {
    type: 'button', class: 'pill', text: '+ Új profil', onclick: () => {
      if (work.profiles.length >= 50) { toast('Legfeljebb 50 profil lehet.', true); return; }
      const p = { id: 'p-' + Date.now().toString(36), name: 'Új profil', exes: [], bindings: [] };
      work.profiles.push(p);
      selProfile = p.id;
      renderProfiles(); renderDevice(); renderPanel(); renderTable(); renderTop();
      const n = $('profile-name'); if (n) { n.focus(); n.select(); }
    }
  }));
}

function renderSugg() {
  const box = $('sugg');
  if (!box) return;
  box.replaceChildren();
  const p = prof();
  const list = recent.filter(e => !exeOwner(e)).slice(0, 10);
  if (!list.length) { box.append(h('span', { class: 'muted', text: 'Indítsd el a játékot, és itt megjelenik a programneve.' })); return; }
  box.append(h('span', { class: 'muted', text: 'Legutóbb előtérben:' }));
  for (const e of list) box.append(h('button', { type: 'button', text: '+ ' + e, onclick: () => { p.exes.push(e); renderProfiles(); renderTop(); } }));
}

function renderProfiles() {
  renderPills();
  const p = prof();
  const box = $('profile-edit');
  box.replaceChildren();
  const name = h('input', { type: 'text', id: 'profile-name', class: 'grow', value: p.name, maxlength: '60', autocomplete: 'off' });
  name.addEventListener('input', () => { p.name = name.value; renderPills(); renderTop(); renderTable(); });
  box.append(h('div', { class: 'row' }, h('label', { for: 'profile-name', text: 'Név' }), name));
  if (p.id === 'default') {
    box.append(h('p', { class: 'note', text: 'Az alap profil minden olyan programra érvényes, amelyhez nincs saját profil (asztal, böngésző stb.).' }));
    return;
  }
  const chips = h('div', { class: 'chips' });
  if (!p.exes.length) chips.append(h('span', { class: 'muted small', text: 'Nincs program megadva – ez a profil így sosem lesz aktív.' }));
  p.exes.forEach((e, i) => chips.append(h('span', { class: 'kchip' }, e,
    h('button', { type: 'button', title: 'Eltávolítás', 'aria-label': e + ' eltávolítása', text: '✕', onclick: () => { p.exes.splice(i, 1); renderProfiles(); renderTop(); } }))));
  box.append(h('div', { class: 'row' }, h('label', { text: 'Programok' }), chips));
  const exeIn = h('input', { type: 'text', id: 'exe-input', class: 'grow', placeholder: 'Programnév, pl. X4.exe', maxlength: '100', autocomplete: 'off' });
  const addExe = () => {
    let e = exeIn.value.trim();
    if (!e) return;
    if (!/\.exe$/i.test(e)) e += '.exe';
    if (/[\\/:*?"<>|]/.test(e)) { toast('A programnévben nem lehet elérési út vagy ilyen jel: \\ / : * ? " < > |', true); return; }
    const owner = exeOwner(e);
    if (owner) { toast('A(z) ' + e + ' már a(z) „' + owner.name + '” profilban van.', true); return; }
    if (p.exes.length >= 30) { toast('Legfeljebb 30 program adható meg.', true); return; }
    p.exes.push(e);
    renderProfiles(); renderTop();
    $('exe-input').focus();
  };
  exeIn.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); addExe(); } });
  box.append(h('div', { class: 'row' }, h('label', { for: 'exe-input', text: 'Hozzáadás' }), exeIn, h('button', { type: 'button', class: 'mini', text: 'Hozzáad', onclick: addExe })));
  box.append(h('div', { class: 'sugg', id: 'sugg' }));
  renderSugg();
  box.append(h('div', { class: 'row' },
    h('button', {
      type: 'button', class: 'mini', text: 'Másolat készítése', onclick: () => {
        if (work.profiles.length >= 50) { toast('Legfeljebb 50 profil lehet.', true); return; }
        const c = clone(p);
        c.id = 'p-' + Date.now().toString(36); c.name = (p.name + ' (másolat)').slice(0, 60); c.exes = [];
        work.profiles.push(c); selProfile = c.id;
        renderProfiles(); renderDevice(); renderPanel(); renderTable(); renderTop();
      }
    }),
    h('button', {
      type: 'button', class: 'mini', text: 'Profil törlése', onclick: () => {
        if (!confirm('Törlöd a(z) „' + p.name + '” profilt? (' + p.bindings.length + ' hozzárendelés)\nA mentésig visszavonható az Elvetés gombbal.')) return;
        work.profiles = work.profiles.filter(x => x !== p); selProfile = 'default';
        renderProfiles(); renderDevice(); renderPanel(); renderTable(); renderTop();
      }
    })));
}

// ---------- device view ----------
const T16_BTN = [null, { t: 'rect', x: 62, y: 96, w: 62, h: 44 }, { x: 230, y: 160 }, { x: 182, y: 146 }, { x: 278, y: 146 },
  { x: 60, y: 400 }, { x: 105, y: 400 }, { x: 150, y: 400 }, { x: 150, y: 450 }, { x: 105, y: 450 }, { x: 60, y: 450 },
  { x: 400, y: 400 }, { x: 355, y: 400 }, { x: 310, y: 400 }, { x: 310, y: 450 }, { x: 355, y: 450 }, { x: 400, y: 450 }];
const T16_HAT = { U: [230, 48], UR: [253, 53], R: [258, 76], DR: [253, 99], D: [230, 104], DL: [207, 99], L: [202, 76], UL: [207, 53] };

function buildT16() {
  const svg = svgEl('svg', { viewBox: '0 0 460 520', role: 'img', 'aria-label': 'A T.16000M vázlata a gombszámokkal' });
  svg.append(svgEl('rect', { class: 'hw', x: 130, y: 20, width: 200, height: 170, rx: 46 }),
    svgEl('rect', { class: 'hw', x: 205, y: 212, width: 50, height: 118, rx: 14 }),
    svgEl('rect', { class: 'hw', x: 20, y: 330, width: 420, height: 170, rx: 34 }),
    svgEl('text', { class: 'cap out', x: 92, y: 160 }, 'elsütő'),
    svgEl('text', { class: 'cap out', x: 230, y: 205 }, 'a bot feje'),
    svgEl('text', { class: 'cap', x: 105, y: 356 }, 'talp, 5–10'),
    svgEl('text', { class: 'cap', x: 355, y: 356 }, 'talp, 11–16'));
  for (let n = 1; n <= 16; n++) {
    const s = T16_BTN[n];
    const g = svgEl('g', { class: 'ctl', 'data-input': 'b' + n, 'data-base': 'ctl' });
    if (s.t === 'rect') g.append(svgEl('rect', { class: 'shape', x: s.x, y: s.y, width: s.w, height: s.h, rx: 10 }), svgEl('text', { x: s.x + s.w / 2, y: s.y + s.h / 2 }, '1'));
    else g.append(svgEl('circle', { class: 'shape', cx: s.x, cy: s.y, r: 18 }), svgEl('text', { x: s.x, y: s.y }, String(n)));
    svg.append(g);
  }
  DIRS.forEach((d, i) => {
    const [x, y] = T16_HAT[d];
    const g = svgEl('g', { class: 'ctl', 'data-input': 'h1' + d, 'data-base': 'ctl' });
    g.append(svgEl('circle', { class: 'shape', cx: x, cy: y, r: i % 2 ? 8 : 13 }), svgEl('text', { class: 'small', x, y }, DIR_ARROW[i]));
    svg.append(g);
  });
  return svg;
}

function buildGeneric(nButtons, nHats) {
  const wrap = h('div', { class: 'gen' });
  if (nButtons) {
    const grid = h('div', { class: 'btn-grid' });
    for (let i = 1; i <= nButtons; i++) grid.append(h('button', { type: 'button', class: 'inp', 'data-input': 'b' + i, 'data-base': 'inp', text: String(i) }));
    wrap.append(h('div', {}, h('h3', { text: 'Gombok' }), grid));
  }
  if (nHats) {
    const hats = h('div', { class: 'hats' });
    const order = ['UL', 'U', 'UR', 'L', null, 'R', 'DL', 'D', 'DR'];
    for (let k = 1; k <= nHats; k++) {
      const g = h('div', { class: 'hat' });
      for (const d of order) {
        if (!d) { g.append(h('span', { class: 'mid', text: nHats > 1 ? k + '.' : 'kalap' })); continue; }
        g.append(h('button', { type: 'button', class: 'inp', 'data-input': 'h' + k + d, 'data-base': 'inp', 'aria-label': inputName('h' + k + d), text: DIR_ARROW[DIRS.indexOf(d)] }));
      }
      hats.append(g);
    }
    wrap.append(h('div', {}, h('h3', { text: nHats > 1 ? 'Kalapok' : 'Kalap' }), hats));
  }
  if (!nButtons && !nHats) wrap.append(h('p', { class: 'muted', text: 'Ezen az eszközön nincs gomb vagy kalap.' }));
  return wrap;
}

function renderDeviceSelect() {
  const sel = $('device-select');
  sel.replaceChildren();
  const keys = knownDeviceKeys();
  if (!selDevice || !keys.includes(selDevice)) selDevice = keys.includes(T16) && deviceInfo(T16) ? T16 : (devices[0] ? devices[0].key : keys[0] || null);
  if (!keys.length) { sel.append(h('option', { value: '', text: 'Nincs játékvezérlő csatlakoztatva' })); sel.disabled = true; return; }
  sel.disabled = false;
  for (const k of keys) {
    const on = !!deviceInfo(k);
    sel.append(h('option', { value: k, text: deviceName(k) + ' (' + k + ')' + (on ? '' : ' – nincs csatlakoztatva') }));
  }
  sel.value = selDevice;
}

function renderDevice() {
  const view = $('device-view');
  view.replaceChildren();
  const note = $('device-note');
  if (!selDevice) { note.textContent = 'Csatlakoztass egy botot, gamepadet, pedált vagy gombpanelt.'; return; }
  const d = deviceInfo(selDevice);
  if (selDevice === T16) view.append(buildT16());
  else {
    let nb = d ? d.buttons : 0, nh = d ? d.hats : 0;
    if (!d) for (const p of work.profiles) for (const b of p.bindings) {
      if (b.device !== selDevice) continue;
      const m = /^b(\d+)$/.exec(b.input), hm = /^h(\d)/.exec(b.input);
      if (m) nb = Math.max(nb, Number(m[1]));
      if (hm) nh = Math.max(nh, Number(hm[1]));
    }
    view.append(buildGeneric(nb, nh));
  }
  const parts = [];
  if (d) parts.push(d.buttons + ' gomb, ' + d.hats + ' kalap.');
  else parts.push('Most nincs csatlakoztatva; a hozzárendelései megmaradnak.');
  if (selDevice === T16) parts.push('Vázlat, nem méretarányos, a bot jobbkezes állása szerint. A számok a Windows gombszámai.');
  parts.push('Kattints egy bemenetre, vagy nyomd meg a vezérlőn. Narancs keret: van hozzárendelése; zöld: épp nyomva.');
  note.textContent = parts.join(' ');
  applyLive();
}

function applyLive() {
  const p = prof();
  const on = new Set(pressed[selDevice] || []);
  for (const el of $('device-view').querySelectorAll('[data-input]')) {
    const id = el.getAttribute('data-input');
    const b = findBinding(p, selDevice, id);
    const cls = [el.getAttribute('data-base')];
    if (b) cls.push('bound');
    if (id === selInput) cls.push('sel');
    if (on.has(id)) cls.push('on');
    el.setAttribute('class', cls.join(' '));
    const title = inputName(id) + (b ? ': ' + actionLabel(b) : '');
    let t = el.querySelector(':scope > title');
    if (el instanceof SVGElement) {
      if (!t) { t = svgEl('title'); el.prepend(t); }
      t.textContent = title;
    } else el.title = title;
  }
}

// ---------- binding panel ----------
// ---------- vocabulary (labels follow G HUB's own Hungarian strings where it has one) ----------
const BIND_TYPES = [
  ['keys', 'Billentyű vagy kombináció'], ['macro', 'Makró'], ['text', 'Szöveg és emojik'], ['mouse', 'Egér'],
  ['media', 'Média'], ['system', 'Rendszer'], ['app', 'Alkalmazás indítása'], ['web', 'Weboldal megnyitása'],
  ['open', 'Fájl vagy mappa megnyitása'], ['audio', 'Audió'], ['sound', 'Hangminta'], ['pad', 'Virtuális gamepad (Xbox)'],
];
const MOUSE = [['left', 'Elsődleges kattintás (bal)'], ['right', 'Másodlagos kattintás (jobb)'], ['middle', 'Középső kattintás'],
  ['x1', 'Hátul (4. gomb)'], ['x2', 'Előre (5. gomb)'], ['double_left', 'Dupla kattintás (bal)'], ['double_right', 'Dupla kattintás (jobb)'],
  ['double_middle', 'Dupla kattintás (középső)'], ['wheel_up', 'Görgetés felfelé'], ['wheel_down', 'Görgetés lefelé'],
  ['wheel_left', 'Görgetés balra'], ['wheel_right', 'Görgetés jobbra']];
const MOUSE_BTN = [['left', 'bal'], ['right', 'jobb'], ['middle', 'középső'], ['x1', 'hátul (4.)'], ['x2', 'előre (5.)']];
const WHEEL = [['up', 'felfelé'], ['down', 'lefelé'], ['left', 'balra'], ['right', 'jobbra']];
const MEDIA = [['play_pause', 'Lejátszás/Szünet'], ['stop', 'Leállítás'], ['next', 'Következő elem'], ['prev', 'Előző elem'],
  ['vol_up', 'Hangerő fel'], ['vol_down', 'Hangerő le'], ['mute', 'Némítás']];
const SYSTEM = [
  ['Szerkesztés', [['copy', 'Másolás'], ['cut', 'Kivágás'], ['paste', 'Beillesztés'], ['undo', 'Visszavonás'], ['redo', 'Újra'], ['select_all', 'Mindent kijelöl']]],
  ['Ablakok', [['close', 'Ablak bezárása (Alt+F4)'], ['minimize', 'Kis méret'], ['maximize', 'Teljes méret / vissza'], ['fullscreen', 'Teljes képernyő (F11)'],
    ['snap_left', 'Ablak balra igazítása'], ['snap_right', 'Ablak jobbra igazítása'], ['switch_apps', 'Váltás az alkalmazások között (Alt+Tab)'],
    ['shift_switch_apps', 'Váltás visszafelé'], ['task_view', 'Feladatnézet'], ['desktop', 'Asztal megjelenítése'],
    ['desktop_next', 'Következő virtuális asztal'], ['desktop_prev', 'Előző virtuális asztal']]],
  ['Nézet', [['zoom_in', 'Nagyítás'], ['zoom_out', 'Kicsinyítés'], ['scroll_left', 'Vízszintes görgetés balra'], ['scroll_right', 'Vízszintes görgetés jobbra'],
    ['brightness_up', 'Fényerő fel'], ['brightness_down', 'Fényerő le']]],
  ['Indítás', [['file_client', 'Fájlkezelő'], ['internet_client', 'Böngésző'], ['mail_client', 'Levelezés'], ['media_client', 'Médialejátszó'], ['calculator', 'Számológép']]],
  ['Egyéb', [['screen_capture', 'Képernyőkivágás'], ['lock', 'Képernyő zárolása'], ['app_menu', 'Helyi menü']]],
];
const SYSTEM_FLAT = SYSTEM.flatMap(g => g[1]);
const AUDIO = [['mute_speakers', 'Audiokimenet némítása'], ['mute_mic', 'Audiobemenet némítása'], ['mic_up', 'Audiobemenet növelése'],
  ['mic_down', 'Audiobemenet csökkentése'], ['out_cycle', 'Audiokimeneti ciklus (következő eszköz)'], ['in_cycle', 'Audiobemenet ciklus (következő eszköz)']];
const PLAY = [['once', 'Egyszer lejátssza'], ['hold', 'Szól, amíg nyomod'], ['toggle', 'Be/ki kapcsolja (ismétlődik)']];
const PAD = [['A', 'A'], ['B', 'B'], ['X', 'X'], ['Y', 'Y'], ['LB', 'Bal váll (LB)'], ['RB', 'Jobb váll (RB)'], ['LT', 'Bal ravasz (LT)'], ['RT', 'Jobb ravasz (RT)'],
  ['BACK', 'Back'], ['START', 'Start'], ['GUIDE', 'Xbox gomb'], ['LS', 'Bal kar lenyomása'], ['RS', 'Jobb kar lenyomása'],
  ['UP', 'D-pad fel'], ['DOWN', 'D-pad le'], ['LEFT', 'D-pad balra'], ['RIGHT', 'D-pad jobbra'],
  ['LS_UP', 'Bal kar fel'], ['LS_DOWN', 'Bal kar le'], ['LS_LEFT', 'Bal kar balra'], ['LS_RIGHT', 'Bal kar jobbra'],
  ['RS_UP', 'Jobb kar fel'], ['RS_DOWN', 'Jobb kar le'], ['RS_LEFT', 'Jobb kar balra'], ['RS_RIGHT', 'Jobb kar jobbra']];
const PAD_AXES = [['LX', 'Bal kar X'], ['LY', 'Bal kar Y'], ['RX', 'Jobb kar X'], ['RY', 'Jobb kar Y'], ['LT', 'Bal ravasz'], ['RT', 'Jobb ravasz']];
const MODES = [
  ['once', 'Nincs ismétlés', 'A makró egyszer fut le a makróbillentyű lenyomását követően.'],
  ['repeat', 'Ismétlés lenyomva tartás során', 'A makró folyamatosan fut, amíg a billentyűt lenyomva tartod.'],
  ['toggle', 'Kapcsoló', 'A makró folyamatosan fut, amíg ismét lenyomva ki nem kapcsolod.'],
  ['sequence', 'Szekvencia', 'Nyomd meg a makró lejátszásához. Nyomd meg, és tartsd lenyomva a „lenyomva tartás során” szekció megismétléséhez.'],
];
const STEP_TYPES = [['tap', 'Billentyűleütés'], ['key', 'Billentyű le/fel'], ['delay', 'Késleltetés'], ['text', 'Szöveg és emojik'],
  ['click', 'Kattintás'], ['mbtn', 'Egérgomb le/fel'], ['wheel', 'Görgetés'], ['move', 'Egérmozgás'], ['media', 'Média'], ['system', 'Rendszer'],
  ['app', 'Alkalmazás indítása'], ['web', 'Weboldal'], ['open', 'Fájl vagy mappa'], ['audio', 'Audió'], ['sound', 'Hangminta'],
  ['pad', 'Gamepad-gomb le/fel'], ['padaxis', 'Gamepad-tengely']];
const lbl = (list, v) => { const x = list.find(e => e[0] === v); return x ? x[1] : v; };

function actionLabel(b) {
  switch (b.type) {
    case 'keys': return comboLabel(b.keys);
    case 'macro': { const m = work.macros.find(x => x.id === b.macro); return 'Makró: ' + (m ? m.name : '(törölt makró)'); }
    case 'text': return 'Szöveg: „' + (b.text.length > 40 ? b.text.slice(0, 37) + '…' : b.text) + '”';
    case 'mouse': return lbl(MOUSE, b.action) + (b.repeat === 'hold' ? ', ismétel, amíg nyomod' : '');
    case 'media': return 'Média: ' + lbl(MEDIA, b.action);
    case 'system': return 'Rendszer: ' + lbl(SYSTEM_FLAT, b.action);
    case 'app': return 'Alkalmazás: ' + (b.path || '(nincs megadva)');
    case 'web': return 'Weboldal: ' + b.url;
    case 'open': return 'Megnyitás: ' + (b.path || '(nincs megadva)');
    case 'audio': return 'Audió: ' + lbl(AUDIO, b.action);
    case 'sound': return 'Hangminta: ' + (b.path || '(nincs megadva)') + ' – ' + lbl(PLAY, b.play);
    case 'pad': return 'Gamepad: ' + lbl(PAD, b.control);
  }
  return b.type;
}

// ---------- small form builders ----------
function sel(options, value, onChange, attrs) {
  const s = h('select', attrs || {});
  for (const o of options) {
    if (Array.isArray(o[1])) { const g = h('optgroup', { label: o[0] }); for (const x of o[1]) g.append(h('option', { value: x[0], text: x[1] })); s.append(g); }
    else s.append(h('option', { value: o[0], text: o[1] }));
  }
  s.value = value;
  s.addEventListener('change', () => onChange(s.value));
  return s;
}
function num(value, min, max, onChange, attrs) {
  const i = h('input', Object.assign({ type: 'number', min: String(min), max: String(max), value: String(value) }, attrs || {}));
  i.addEventListener('change', () => {
    const v = Math.round(Number(i.value));
    if (!(v >= min && v <= max)) { toast('Az érték ' + min + ' és ' + max + ' között lehet.', true); i.value = value; return; }
    onChange(v);
  });
  return i;
}
function txt(value, placeholder, onChange, attrs) {
  const i = h('input', Object.assign({ type: 'text', value: value || '', placeholder: placeholder || '', autocomplete: 'off', spellcheck: 'false' }, attrs || {}));
  i.addEventListener('change', () => onChange(i.value));
  return i;
}
function area(value, placeholder, onChange) {
  const a = h('textarea', { rows: '3', placeholder, maxlength: '4000' });
  a.value = value || '';
  a.addEventListener('change', () => onChange(a.value));
  return a;
}
const field = (label, ...kids) => h('label', { class: 'field' }, h('span', { text: label }), ...kids);

// ---------- binding panel ----------
function afterEdit() { renderTop(); applyLive(); renderPanel(); renderTable(); renderMacros(); }

function defaultBinding(type) {
  const base = { device: selDevice, input: selInput, type };
  switch (type) {
    case 'keys': return null;   // created once a key is chosen
    case 'macro': return work.macros.length ? Object.assign(base, { macro: work.macros[0].id }) : null;
    case 'text': return Object.assign(base, { text: '' });
    case 'mouse': return Object.assign(base, { action: 'left', repeat: 'once' });
    case 'media': return Object.assign(base, { action: 'play_pause' });
    case 'system': return Object.assign(base, { action: 'copy' });
    case 'app': return Object.assign(base, { path: '', args: '', dir: '' });
    case 'web': return Object.assign(base, { url: 'https://' });
    case 'open': return Object.assign(base, { path: '' });
    case 'audio': return Object.assign(base, { action: 'mute_mic' });
    case 'sound': return Object.assign(base, { path: '', play: 'once' });
    case 'pad': return Object.assign(base, { control: 'A' });
  }
  return null;
}

function renderPanel() {
  const pane = $('pane');
  pane.replaceChildren();
  const typeBox = $('type-box');
  typeBox.replaceChildren();
  if (!selDevice || !selInput) {
    $('sel-name').textContent = 'Válassz bemenetet';
    $('sel-sub').textContent = 'Kattints egy gombra a rajzon, vagy nyomd meg a vezérlőn.';
    return;
  }
  const p = prof();
  const b = findBinding(p, selDevice, selInput);
  const type = b ? b.type : pendingType;
  $('sel-name').textContent = inputName(selInput);
  $('sel-sub').textContent = deviceName(selDevice) + ' (' + selDevice + ') · profil: ' + p.name;
  typeBox.append(field('Hozzárendelés', sel([['none', 'Nincs']].concat(BIND_TYPES), type, t => {
    if (b && !confirm('A bemenet jelenlegi hozzárendelése (' + actionLabel(b) + ') lecserélődik. Folytatod?')) { renderPanel(); return; }
    removeBinding(selDevice, selInput);
    pendingType = t;
    const nb = defaultBinding(t);
    if (nb) setBinding(nb);
    if (t === 'keys') focusAfter = 'cap-main';
    afterEdit();
  }, { id: 'type-select' })));

  const set = patch => { setBinding(Object.assign({}, b, patch)); afterEdit(); };
  switch (type) {
    case 'none':
      pane.append(h('p', { class: 'note', text: 'Ebben a profilban ezen a bemeneten nincs hozzárendelés.' }));
      if (isDiagonal(selInput)) pane.append(h('p', { class: 'note', text: 'Kötés nélkül ez az átló a két szomszédos irány hozzárendelését nyomja egyszerre (mint a W+D).' }));
      break;
    case 'keys':
      pane.append(comboEditor(b ? b.keys : [], keys => {
        if (!keys.length) removeBinding(selDevice, selInput);
        else setBinding({ device: selDevice, input: selInput, type: 'keys', keys });
        afterEdit();
      }, 'cap-main'));
      pane.append(h('p', { class: 'note', text: 'Amíg a gombot nyomva tartod, a billentyűk is lenyomva maradnak. Kombinációnál sorban nyomja le, és fordított sorrendben engedi fel.' }));
      break;
    case 'macro':
      if (!work.macros.length) {
        pane.append(h('p', { class: 'note', text: 'Még nincs makró. Hozz létre egyet, és ez a bemenet azt fogja indítani.' }),
          h('button', { type: 'button', class: 'mini', text: '+ Új makró ehhez a bemenethez', onclick: () => { const m = newMacro(); setBinding({ device: selDevice, input: selInput, type: 'macro', macro: m.id }); afterEdit(); openMacro(m.id); } }));
        break;
      }
      pane.append(h('div', { class: 'row' },
        sel(work.macros.map(m => [m.id, m.name + ' – ' + lbl(MODES, m.mode)]), b ? b.macro : '', v => set({ macro: v })),
        h('button', { type: 'button', class: 'mini', text: 'Szerkesztés', onclick: () => openMacro(b.macro) }),
        h('button', { type: 'button', class: 'mini', text: '+ Új makró', onclick: () => { const m = newMacro(); set({ macro: m.id }); openMacro(m.id); } })));
      break;
    case 'text':
      pane.append(area(b.text, 'Ide írj be szöveget', v => set({ text: v })),
        h('p', { class: 'note', text: 'Karakterenként, Unicode-dal írja be (ékezetek, emojik is), mint a G HUB „Szöveg és emojik” funkciója. Egyes játékok a Unicode-bevitelt nem fogadják; oda billentyűleütéses makró kell.' }));
      break;
    case 'mouse': {
      pane.append(field('Művelet', sel(MOUSE, b.action, v => set({ action: v, repeat: v.startsWith('wheel_') ? b.repeat : 'once' }))));
      if (b.action.startsWith('wheel_')) pane.append(field('Nyomva tartva', sel([['once', 'Egy kattanás nyomásonként'], ['hold', 'Ismétel, amíg nyomod']], b.repeat, v => set({ repeat: v }))));
      else if (!b.action.startsWith('double_')) pane.append(h('p', { class: 'note', text: 'Az egérgomb addig marad lenyomva, amíg a vezérlő gombját nyomod (húzás is működik).' }));
      break;
    }
    case 'media': pane.append(field('Művelet', sel(MEDIA, b.action, v => set({ action: v })))); break;
    case 'system':
      pane.append(field('Művelet', sel(SYSTEM, b.action, v => set({ action: v }))),
        h('p', { class: 'note', text: 'A billentyűparancsok billentyűzetkiosztástól függetlenül működnek (magyar billentyűzeten is a Ctrl+Z a visszavonás).' }));
      break;
    case 'app':
      pane.append(field('Program (teljes útvonal)', txt(b.path, 'C:\\Program Files\\…\\program.exe', v => set({ path: v.trim() }))),
        field('Paraméterek', txt(b.args, 'nem kötelező', v => set({ args: v }))),
        field('Munkakönyvtár', txt(b.dir, 'nem kötelező; alapból a program mappája', v => set({ dir: v.trim() }))));
      break;
    case 'web': pane.append(field('Cím', txt(b.url, 'https://…', v => set({ url: v.trim() })))); break;
    case 'open': pane.append(field('Fájl vagy mappa (teljes útvonal)', txt(b.path, 'C:\\Users\\…', v => set({ path: v.trim() })))); break;
    case 'audio': pane.append(field('Művelet', sel(AUDIO, b.action, v => set({ action: v })))); break;
    case 'sound':
      pane.append(field('Hangfájl (.wav, .mp3, .wma)', txt(b.path, 'C:\\…\\hang.wav', v => set({ path: v.trim() }))),
        field('Lejátszás', sel(PLAY, b.play, v => set({ play: v }))));
      break;
    case 'pad':
      pane.append(field('Vezérlő', sel(PAD, b.control, v => set({ control: v }))),
        h('p', { class: 'note', text: 'A ViGEm virtuális Xbox 360 vezérlőjén nyomja, amíg a gombot nyomod. A játék ezt külön vezérlőként látja.' }));
      break;
  }
  if (focusAfter) { const f = $(focusAfter); focusAfter = null; if (f) f.focus(); }
}

// ---------- macro library ----------
let selMacro = null;
let recTarget = null;      // { id, list } where the next recording goes
let recording = false;
let recCount = 0;

function newMacro() {
  const m = { id: 'm-' + Date.now().toString(36), name: 'Új makró', mode: 'once', interruptible: false, useDefaultDelay: true, defaultDelay: 50, simple: [], press: [], held: [], release: [] };
  work.macros.push(m);
  return m;
}
function openMacro(id) {
  selMacro = id;
  renderMacros();
  const c = $('macro-card'); if (c) c.scrollIntoView({ behavior: 'smooth', block: 'start' });
}
function macroUsers(id) {
  const out = [];
  for (const p of work.profiles) for (const b of p.bindings) if (b.type === 'macro' && b.macro === id) out.push(p.name + ': ' + deviceName(b.device) + ' · ' + inputName(b.input));
  return out;
}

function stepSummary(s) {
  switch (s.t) {
    case 'key': return keyLabel(s.code) + (s.down ? ' le' : ' fel');
    case 'tap': return comboLabel(s.keys);
    case 'delay': return s.ms + ' ms';
    case 'text': return '„' + s.text + '”';
    case 'mbtn': return lbl(MOUSE_BTN, s.button) + ' egérgomb ' + (s.down ? 'le' : 'fel');
    case 'click': return (s.double ? 'dupla kattintás ' : 'kattintás ') + lbl(MOUSE_BTN, s.button);
    case 'wheel': return 'görgetés ' + lbl(WHEEL, s.dir) + ' ×' + s.ticks;
    case 'move': return 'egér ' + s.dx + ', ' + s.dy;
    case 'media': return lbl(MEDIA, s.action);
    case 'system': return lbl(SYSTEM_FLAT, s.action);
    case 'audio': return lbl(AUDIO, s.action);
    case 'app': return s.path;
    case 'web': return s.url;
    case 'open': case 'sound': return s.path;
    case 'pad': return lbl(PAD, s.control) + (s.down ? ' le' : ' fel');
    case 'padaxis': return lbl(PAD_AXES, s.axis) + ' = ' + s.value;
  }
  return s.t;
}

function defaultStep(t) {
  switch (t) {
    case 'tap': return { t, keys: [] };
    case 'key': return { t, code: 30, down: true };
    case 'delay': return { t, ms: 100 };
    case 'text': return { t, text: '' };
    case 'click': return { t, button: 'left', double: false };
    case 'mbtn': return { t, button: 'left', down: true };
    case 'wheel': return { t, dir: 'down', ticks: 1 };
    case 'move': return { t, dx: 0, dy: 0 };
    case 'media': return { t, action: 'play_pause' };
    case 'system': return { t, action: 'copy' };
    case 'app': return { t, path: '', args: '', dir: '' };
    case 'web': return { t, url: 'https://' };
    case 'open': return { t, path: '' };
    case 'audio': return { t, action: 'mute_mic' };
    case 'sound': return { t, path: '' };
    case 'pad': return { t, control: 'A', down: true };
    case 'padaxis': return { t, axis: 'LX', value: 0 };
  }
}

function stepEditor(s, change, capId) {
  const up = (k, v) => change(Object.assign({}, s, { [k]: v }));
  const downSel = () => sel([['1', 'le'], ['0', 'fel']], s.down ? '1' : '0', v => up('down', v === '1'));
  switch (s.t) {
    case 'tap': return comboEditor(s.keys, keys => change(Object.assign({}, s, { keys })), capId);
    case 'key': {
      const opts = ST.keys.map(k => [String(k.code), keyLabel(k.code)]);
      return h('div', { class: 'row' }, sel(opts, String(s.code), v => up('code', Number(v))), downSel());
    }
    case 'delay': return h('div', { class: 'row' }, num(s.ms, 0, 3600000, v => up('ms', v)), h('span', { class: 'muted', text: 'ms' }));
    case 'text': return area(s.text, 'Ide írj be szöveget', v => up('text', v));
    case 'click': return h('div', { class: 'row' }, sel(MOUSE_BTN, s.button, v => up('button', v)),
      h('label', { class: 'check' }, (() => { const c = h('input', { type: 'checkbox', checked: s.double }); c.addEventListener('change', () => up('double', c.checked)); return c; })(), ' dupla'));
    case 'mbtn': return h('div', { class: 'row' }, sel(MOUSE_BTN, s.button, v => up('button', v)), downSel());
    case 'wheel': return h('div', { class: 'row' }, sel(WHEEL, s.dir, v => up('dir', v)), num(s.ticks, 1, 100, v => up('ticks', v)), h('span', { class: 'muted', text: 'kattanás' }));
    case 'move': return h('div', { class: 'row' }, h('span', { class: 'muted', text: 'x' }), num(s.dx, -10000, 10000, v => up('dx', v)), h('span', { class: 'muted', text: 'y' }), num(s.dy, -10000, 10000, v => up('dy', v)));
    case 'media': return sel(MEDIA, s.action, v => up('action', v));
    case 'system': return sel(SYSTEM, s.action, v => up('action', v));
    case 'audio': return sel(AUDIO, s.action, v => up('action', v));
    case 'app': return h('div', { class: 'combo' }, txt(s.path, 'C:\\…\\program.exe', v => up('path', v.trim())), txt(s.args, 'paraméterek (nem kötelező)', v => up('args', v)), txt(s.dir, 'munkakönyvtár (nem kötelező)', v => up('dir', v.trim())));
    case 'web': return txt(s.url, 'https://…', v => up('url', v.trim()));
    case 'open': return txt(s.path, 'C:\\…', v => up('path', v.trim()));
    case 'sound': return txt(s.path, 'C:\\…\\hang.wav', v => up('path', v.trim()));
    case 'pad': return h('div', { class: 'row' }, sel(PAD, s.control, v => up('control', v)), downSel());
    case 'padaxis': return h('div', { class: 'row' }, sel(PAD_AXES, s.axis, v => up('axis', v)),
      s.axis === 'LT' || s.axis === 'RT' ? num(s.value, 0, 255, v => up('value', v)) : num(s.value, -32768, 32767, v => up('value', v)));
  }
  return h('span', { text: stepSummary(s) });
}

const STEP_PAGE = 150;
const showAll = new Set();

function stepList(m, key, title) {
  const steps = m[key];
  const box = h('div', { class: 'steps-box' });
  box.append(h('div', { class: 'steps-head' }, h('h3', { text: title + ' (' + steps.length + ' lépés)' }),
    steps.length ? h('button', { type: 'button', class: 'mini', text: 'Lista ürítése', onclick: () => { if (confirm('Törlöd ennek a résznek mind a ' + steps.length + ' lépését?')) { m[key] = []; afterEdit(); } } }) : null));
  const commit = ns => { m[key] = ns; afterEdit(); };
  const ol = h('ol', { class: 'steps' });
  const limit = showAll.has(m.id + key) ? steps.length : Math.min(steps.length, STEP_PAGE);
  for (let i = 0; i < limit; i++) {
    const s = steps[i];
    const ed = stepEditor(s, ns => { const c = steps.slice(); c[i] = ns; commit(c); }, 'cap-' + m.id + '-' + key + '-' + i);
    const move = d => { const c = steps.slice(); const [x] = c.splice(i, 1); c.splice(i + d, 0, x); commit(c); };
    ol.append(h('li', { class: 'step' + (s.t === 'delay' && m.useDefaultDelay ? ' dim' : '') },
      h('span', { class: 'num', text: (i + 1) + '.' }),
      h('div', {}, h('div', { class: 'step-type', text: lbl(STEP_TYPES, s.t) + (s.t === 'delay' && m.useDefaultDelay ? ' (szokásos késleltetésnél nem használt)' : '') }), ed),
      h('span', { class: 'ctrls' },
        h('button', { type: 'button', class: 'icon', title: 'Feljebb', 'aria-label': 'Feljebb', text: '↑', disabled: i === 0, onclick: () => move(-1) }),
        h('button', { type: 'button', class: 'icon', title: 'Lejjebb', 'aria-label': 'Lejjebb', text: '↓', disabled: i === steps.length - 1, onclick: () => move(1) }),
        h('button', { type: 'button', class: 'icon', title: 'Lépés törlése', 'aria-label': 'Lépés törlése', text: '✕', onclick: () => { const c = steps.slice(); c.splice(i, 1); commit(c); } }))));
  }
  if (steps.length) box.append(ol);
  if (limit < steps.length) box.append(h('button', { type: 'button', class: 'mini', text: 'Mind a ' + steps.length + ' lépés mutatása', onclick: () => { showAll.add(m.id + key); renderMacros(); } }));
  let addType = 'tap';
  box.append(h('div', { class: 'row add-row' },
    sel(STEP_TYPES, addType, v => { addType = v; }, { 'aria-label': 'Új lépés típusa' }),
    h('button', { type: 'button', class: 'mini', text: '+ Lépés hozzáadása', onclick: () => { if (addType === 'tap') focusAfter = 'cap-' + m.id + '-' + key + '-' + steps.length; commit(steps.concat([defaultStep(addType)])); } }),
    recording
      ? (recTarget && recTarget.id === m.id && recTarget.list === key
        ? h('button', { type: 'button', class: 'mini rec on', text: '■ Rögzítés leállítása (' + recCount + ')', onclick: stopRecording })
        : h('span', { class: 'muted small', text: 'Másik rész rögzítése fut.' }))
      : h('button', { type: 'button', class: 'mini rec', text: '● Rögzítés ide', onclick: () => startRecording(m.id, key) })));
  return box;
}

function renderMacros() {
  const list = $('macro-list');
  const ed = $('macro-edit');
  if (!list || !work) return;
  list.replaceChildren();
  if (selMacro && !work.macros.some(m => m.id === selMacro)) selMacro = null;
  for (const m of work.macros) {
    list.append(h('button', { type: 'button', class: 'pill' + (m.id === selMacro ? ' on' : ''), text: m.name, title: lbl(MODES, m.mode), onclick: () => { selMacro = m.id; renderMacros(); } }));
  }
  list.append(h('button', { type: 'button', class: 'pill', text: '+ Új makró', onclick: () => { if (work.macros.length >= 500) { toast('Legfeljebb 500 makró lehet.', true); return; } const m = newMacro(); selMacro = m.id; renderTop(); renderMacros(); const n = $('macro-name'); if (n) { n.focus(); n.select(); } } }));
  ed.replaceChildren();
  const m = work.macros.find(x => x.id === selMacro);
  if (!m) { ed.append(h('p', { class: 'note', text: work.macros.length ? 'Válassz makrót a szerkesztéshez.' : 'Még nincs makró.' })); return; }

  const name = h('input', { type: 'text', id: 'macro-name', value: m.name, maxlength: '80', autocomplete: 'off' });
  name.addEventListener('change', () => { m.name = name.value; renderTop(); renderMacros(); renderPanel(); renderTable(); });
  ed.append(field('Név', name));

  const modes = h('div', { class: 'modes', role: 'radiogroup', 'aria-label': 'Makró típusa' });
  for (const [v, t, help] of MODES) {
    const r = h('input', { type: 'radio', name: 'macro-mode', value: v, checked: m.mode === v });
    r.addEventListener('change', () => { m.mode = v; afterEdit(); });
    modes.append(h('label', { class: 'mode' + (m.mode === v ? ' on' : '') }, r, h('span', {}, h('b', { text: t }), h('small', { text: help }))));
  }
  ed.append(h('h3', { text: 'Típus' }), modes);

  const opts = h('div', { class: 'params' });
  if (m.mode !== 'once') {
    const c = h('input', { type: 'checkbox', checked: m.interruptible });
    c.addEventListener('change', () => { m.interruptible = c.checked; afterEdit(); });
    opts.append(h('label', { class: 'check', title: 'G HUB: interruptible. Alapból nem: felengedéskor (kapcsolónál újabb nyomáskor) a megkezdett kör végigfut.' }, c,
      m.mode === 'toggle' ? ' Kikapcsoláskor azonnal megáll (a kör közepén is)' : ' Felengedéskor azonnal megáll (a kör közepén is)'));
  }
  const dd = h('input', { type: 'checkbox', checked: m.useDefaultDelay });
  dd.addEventListener('change', () => { m.useDefaultDelay = dd.checked; afterEdit(); });
  opts.append(h('label', { class: 'check' }, dd, ' Szokásos késleltetések használata'));
  if (m.useDefaultDelay) opts.append(h('label', { class: 'check' }, num(m.defaultDelay, 0, 60000, v => { m.defaultDelay = v; afterEdit(); }), ' ms a műveletek között'));
  ed.append(opts, h('p', { class: 'note', text: m.useDefaultDelay ? 'Jelenleg egy szokásos késleltetést használsz a műveletek között; a rögzített várakozásokat kihagyja.' : 'Jelenleg rögzített beviteli késleltetéseket használsz (a Késleltetés lépések szerint).' }));

  const recOpts = h('div', { class: 'params' }, h('span', { class: 'muted small', text: 'Rögzítéskor felveszi:' }));
  for (const [k, t] of [['keyboard', 'billentyűzet'], ['mouse', 'egérgombok és görgő'], ['move', 'egérmozgás'], ['pads', 'játékvezérlők (→ virtuális Xbox)']]) {
    const c = h('input', { type: 'checkbox', checked: recOptions[k], disabled: recording });
    c.addEventListener('change', () => { recOptions[k] = c.checked; });
    recOpts.append(h('label', { class: 'check' }, c, ' ' + t));
  }
  ed.append(recOpts);

  if (m.mode === 'sequence') {
    ed.append(stepList(m, 'press', 'Lenyomáskor'), stepList(m, 'held', 'Lenyomva tartás során'), stepList(m, 'release', 'Felengedéskor'));
  } else ed.append(stepList(m, 'simple', 'Lépések'));

  const users = macroUsers(m.id);
  ed.append(h('p', { class: 'note', text: users.length ? 'Használja: ' + users.join('; ') : 'Még egyik bemenet sem használja.' }));
  ed.append(h('div', { class: 'row' },
    h('button', { type: 'button', class: 'mini', text: 'Másolat készítése', onclick: () => { const c = clone(m); c.id = 'm-' + Date.now().toString(36); c.name = (m.name + ' (másolat)').slice(0, 80); work.macros.push(c); selMacro = c.id; afterEdit(); } }),
    h('button', { type: 'button', class: 'mini', text: 'Makró törlése', onclick: () => {
      if (!confirm('Törlöd a(z) „' + m.name + '” makrót?' + (users.length ? '\nAz ezt használó ' + users.length + ' hozzárendelés is törlődik.' : '') + '\nA mentésig visszavonható az Elvetés gombbal.')) return;
      work.macros = work.macros.filter(x => x !== m);
      for (const p of work.profiles) p.bindings = p.bindings.filter(b => !(b.type === 'macro' && b.macro === m.id));
      selMacro = null; afterEdit();
    } })));
  if (focusAfter) { const f = $(focusAfter); focusAfter = null; if (f) f.focus(); }
}

// ---------- recording ----------
const recOptions = { keyboard: true, mouse: true, move: true, pads: true };

async function startRecording(id, list) {
  try {
    await api('/api/record/start', recOptions);
    recording = true; recCount = 0; recTarget = { id, list };
    renderMacros();
    toast('Rögzítés fut. Billentyűzet, egér és vezérlő is mehet; a leállító kattintás nem kerül bele.');
  } catch (e) { toast(e.message, true); }
}

async function stopRecording() {
  try {
    const r = await api('/api/record/stop', {});
    recording = false;
    const m = recTarget && work.macros.find(x => x.id === recTarget.id);
    if (m && r.steps.length) {
      m[recTarget.list] = m[recTarget.list].concat(r.steps);
      if (r.steps.some(s => s.t === 'delay')) m.useDefaultDelay = false;   // recorded timing is what was asked for
    }
    toast(r.steps.length ? r.steps.length + ' lépés rögzítve. Mentés után érvényes.' : 'Nem rögzült semmi.');
    recTarget = null;
    afterEdit();
  } catch (e) { toast(e.message, true); }
}

// ---------- table ----------
function renderTable() {
  const p = prof();
  $('all-title').textContent = 'A(z) „' + (p.name || '') + '” profil hozzárendelései';
  const tb = $('all-rows');
  tb.replaceChildren();
  const rows = [...p.bindings].sort((a, b) => a.device === b.device ? inputOrder(a.input) - inputOrder(b.input) : deviceName(a.device).localeCompare(deviceName(b.device), 'hu'));
  if (!rows.length) { tb.append(h('tr', {}, h('td', { colspan: '4', class: 'muted', text: 'Ebben a profilban még nincs hozzárendelés.' }))); return; }
  for (const b of rows) {
    const tr = h('tr', { class: 'clickable' + (b.device === selDevice && b.input === selInput ? ' cur' : '') },
      h('td', { text: deviceName(b.device) }),
      h('td', { class: 'k', text: inputName(b.input) }),
      h('td', { text: actionLabel(b) }),
      h('td', {}, h('button', { type: 'button', class: 'mini', text: 'Törlés', onclick: ev => { ev.stopPropagation(); removeBinding(b.device, b.input); afterEdit(); } })));
    tr.addEventListener('click', () => {
      const devChanged = b.device !== selDevice;
      selDevice = b.device; selInput = b.input; pendingType = 'none';
      if (devChanged) { renderDeviceSelect(); renderDevice(); } else applyLive();
      renderPanel(); renderTable();
      $('panel').scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    });
    tb.append(tr);
  }
}

// ---------- log ----------
function addLog(entries) {
  const ol = $('log');
  for (const e of entries) {
    const cls = /^HIBA|VÉGZETES/.test(e.text) ? 'err' : /teszt mód, nem küldve/.test(e.text) ? 'dry' : null;
    ol.prepend(h('li', { class: cls }, h('span', { class: 't', text: e.time }), e.text.trim()));
    logSeq = Math.max(logSeq, e.seq);
  }
  while (ol.children.length > 300) ol.lastElementChild.remove();
}

// ---------- live updates ----------
const isTyping = () => { const a = document.activeElement; return a && (a.tagName === 'SELECT' || a.tagName === 'TEXTAREA' || (a.tagName === 'INPUT' && (a.type === 'text' || a.type === 'number'))); };

function handlePressed(next) {
  let jump = null;
  for (const [dev, list] of Object.entries(next)) {
    const before = new Set(pressed[dev] || []);
    for (const i of list) if (!before.has(i)) jump = { dev, i };
  }
  pressed = next;
  if (jump && !recording && $('auto-select').checked && !isTyping() && (jump.dev !== selDevice || jump.i !== selInput)) {
    const devChanged = jump.dev !== selDevice;
    selDevice = jump.dev; selInput = jump.i; pendingType = 'none';
    if (devChanged) { renderDeviceSelect(); renderDevice(); }
    renderPanel(); renderTable();
  }
  applyLive();
}

async function poll() {
  let delay = 150;
  try {
    const r = await api('/api/live?since=' + logSeq);
    serverDown = false;
    const activeChanged = r.active.id !== live.active.id;
    live = { paused: r.paused, dryRun: r.dryRun, active: r.active, exe: r.exe, macros: r.macrosRunning };
    if (r.recording !== recording || (recording && r.recordCount !== recCount)) {
      if (!r.recording) recTarget = null;
      recording = r.recording; recCount = r.recordCount;
      const b = document.querySelector('.rec.on');
      if (b && recording) b.textContent = '■ Rögzítés leállítása (' + recCount + ')'; else renderMacros();
    }
    if (r.devicesRev !== devicesRev) {
      const s = await api('/api/state');
      devices = s.devices; devicesRev = s.devicesRev;
      renderDeviceSelect(); renderDevice();
    }
    handlePressed(r.pressed);
    addLog(r.log);
    if (JSON.stringify(r.recentExes) !== JSON.stringify(recent)) { recent = r.recentExes; renderSugg(); }
    if (activeChanged) renderPills();
  } catch (e) {
    serverDown = true;
    delay = 1500;
  }
  renderTop();
  setTimeout(poll, delay);
}

// ---------- load / save ----------
async function loadState(keepSelection) {
  ST = await api('/api/state');
  keyByCode.clear(); keyByDom.clear();
  for (const k of ST.keys) { keyByCode.set(k.code, k); keyByDom.set(k.dom, k); }
  devices = ST.devices; devicesRev = ST.devicesRev;
  work = clone(ST.config); orig = JSON.stringify(ST.config);
  live = { paused: ST.paused, dryRun: ST.dryRun, active: ST.active, exe: ST.exe };
  recent = ST.recentExes;
  if (!work.profiles.some(p => p.id === selProfile)) selProfile = 'default';
  if (!keepSelection) { selInput = null; }
  pendingType = 'none';
  if (!logSeq) addLog(ST.log);
  if (!work.macros) work.macros = [];
  renderTop(); renderProfiles(); renderDeviceSelect(); renderDevice(); renderPanel(); renderTable(); renderMacros();
}

async function save() {
  try {
    const r = await api('/api/save', { baseRev: ST.rev, config: work });
    await loadState(true);
    toast('Elmentve, azonnal érvényes.' + (r.backup ? ' Az előző állapot: backups\\' + r.backup : ''));
  } catch (e) { toast(e.message, true); }
}

function wire() {
  $('btn-save').addEventListener('click', save);
  $('btn-discard').addEventListener('click', () => { if (confirm('Elveted a nem mentett változásokat?')) loadState(true).catch(e => toast(e.message, true)); });
  $('btn-pause').addEventListener('click', async () => {
    try { const r = await api('/api/pause', { paused: !live.paused }); live.paused = r.paused; renderTop(); } catch (e) { toast(e.message, true); }
  });
  $('btn-quit').addEventListener('click', async () => {
    if (isDirty() && !confirm('Vannak nem mentett változások. Biztosan kilépsz?')) return;
    try { await api('/api/quit', {}); } catch (e) { /* already gone */ }
    work = null;
    document.body.replaceChildren(h('main', {}, h('section', { class: 'card' }, h('h2', { text: 'A USB Mapper leállt' }),
      h('p', { class: 'note', text: 'Ezt a lapot bezárhatod. Újraindítás: az asztali USB Mapper ikonnal.' }))));
  });
  $('device-select').addEventListener('change', e => { selDevice = e.target.value; selInput = null; pendingType = 'none'; renderDevice(); renderPanel(); renderTable(); });
  $('device-view').addEventListener('click', e => {
    const t = e.target.closest('[data-input]');
    if (!t) return;
    selInput = t.getAttribute('data-input'); pendingType = 'none';
    applyLive(); renderPanel(); renderTable();
  });
  window.addEventListener('beforeunload', e => { if (isDirty()) { e.preventDefault(); e.returnValue = ''; } });
}

async function start() {
  try { if (navigator.keyboard && navigator.keyboard.getLayoutMap) layout = await navigator.keyboard.getLayoutMap(); } catch (e) { layout = null; }
  wire();
  try { await loadState(false); } catch (e) { toast('Nem sikerült betölteni: ' + e.message, true); return; }
  poll();
}
start();
