// YieldDataLogger live-ticks page - connects to /hubs/ticks via SignalR,
// subscribes per symbol, renders a scrolling log of incoming ticks.
(() => {
  'use strict';

  // --- state ---
  const MAX_LOG_ROWS = 500;
  const state = {
    connection: null,
    subscriptions: new Set(),   // symbols (uppercase)
    lastPriceBySymbol: new Map(),
    totalTicks: 0,
    ticksThisSecond: 0,
    lastSecond: Math.floor(Date.now() / 1000),
  };

  // --- dom refs ---
  const els = {
    connState:   document.getElementById('conn-state'),
    symInput:    document.getElementById('sym-input'),
    symOptions:  document.getElementById('sym-options'),
    btnSub:      document.getElementById('btn-subscribe'),
    btnUnsub:    document.getElementById('btn-unsubscribe'),
    btnClear:    document.getElementById('btn-clear-log'),
    subs:        document.getElementById('subs'),
    rate:        document.getElementById('rate'),
    count:       document.getElementById('count'),
    logBody:     document.getElementById('log-body'),
  };

  // --- helpers ---
  function setConnState(kind) {
    els.connState.className = 'pill pill-state ' + kind;
    els.connState.textContent = kind;
  }

  function escape(s) {
    return String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[c]));
  }

  function formatTime(tsUnix) {
    const d = new Date(tsUnix * 1000);
    const hh = String(d.getHours()).padStart(2, '0');
    const mm = String(d.getMinutes()).padStart(2, '0');
    const ss = String(d.getSeconds()).padStart(2, '0');
    const ms = String(d.getMilliseconds()).padStart(3, '0');
    return `${hh}:${mm}:${ss}.${ms}`;
  }

  function formatPrice(p) {
    if (!Number.isFinite(p)) return String(p);
    const abs = Math.abs(p);
    const decimals = abs >= 1000 ? 2 : abs >= 100 ? 3 : abs >= 10 ? 4 : 5;
    return p.toFixed(decimals);
  }

  // --- catalog autocomplete ---
  async function loadCatalog() {
    try {
      const res = await fetch('/api/instruments');
      if (!res.ok) return;
      const items = await res.json();
      els.symOptions.innerHTML = items
        .map(i => i.canonicalSymbol)
        .sort()
        .map(s => `<option value="${escape(s)}">`)
        .join('');
    } catch { /* non-fatal */ }
  }

  // --- subscriptions UI ---
  function renderSubs() {
    const syms = [...state.subscriptions].sort();
    if (syms.length === 0) {
      els.subs.innerHTML = `<span style="color:var(--text-dim);font-size:12px">No subscriptions yet.</span>`;
      return;
    }
    els.subs.innerHTML = syms.map(s => `
      <span class="sub-chip">${escape(s)} <button type="button" data-sym="${escape(s)}" title="Unsubscribe">&times;</button></span>
    `).join('');
    els.subs.querySelectorAll('button[data-sym]').forEach(btn => {
      btn.addEventListener('click', () => removeSubscription(btn.dataset.sym));
    });
  }

  async function addSubscription() {
    const raw = els.symInput.value.trim().toUpperCase();
    if (!raw) return;
    if (state.subscriptions.has(raw)) {
      els.symInput.value = '';
      return;
    }
    try {
      await state.connection.invoke('Subscribe', [raw]);
      state.subscriptions.add(raw);
      renderSubs();
      els.symInput.value = '';
      els.symInput.focus();
    } catch (e) {
      alert('Subscribe failed: ' + e.message);
    }
  }

  async function removeSubscription(sym) {
    if (!state.subscriptions.has(sym)) return;
    try {
      await state.connection.invoke('Unsubscribe', [sym]);
    } catch { /* keep UI in sync even if invoke failed */ }
    state.subscriptions.delete(sym);
    renderSubs();
  }

  async function unsubscribeAll() {
    if (state.subscriptions.size === 0) return;
    const syms = [...state.subscriptions];
    try { await state.connection.invoke('Unsubscribe', syms); } catch { /* */ }
    state.subscriptions.clear();
    renderSubs();
  }

  // --- tick log ---
  function clearLog() {
    els.logBody.innerHTML = `<tr class="log-empty"><td colspan="4">Waiting for ticks…</td></tr>`;
    state.totalTicks = 0;
    els.count.textContent = '0 total';
  }

  function onTick(payload) {
    state.totalTicks++;
    state.ticksThisSecond++;

    // drop empty-state row if present
    const empty = els.logBody.querySelector('tr.log-empty');
    if (empty) empty.remove();

    const prev = state.lastPriceBySymbol.get(payload.symbol);
    state.lastPriceBySymbol.set(payload.symbol, payload.price);

    let flashClass = '';
    if (prev != null) {
      if (payload.price > prev)      flashClass = 'flash-up';
      else if (payload.price < prev) flashClass = 'flash-down';
    }

    const row = document.createElement('tr');
    if (flashClass) row.className = flashClass;
    row.innerHTML = `
      <td class="col-time">${formatTime(payload.tsUnix)}</td>
      <td class="col-symbol">${escape(payload.symbol)}</td>
      <td class="col-price">${formatPrice(payload.price)}</td>
      <td class="col-source">${escape(payload.source || '')}</td>
    `;
    els.logBody.insertBefore(row, els.logBody.firstChild);

    // cap log rows
    while (els.logBody.childElementCount > MAX_LOG_ROWS) {
      els.logBody.lastElementChild.remove();
    }

    els.count.textContent = `${state.totalTicks.toLocaleString()} total`;
  }

  function tickRateTimer() {
    setInterval(() => {
      const nowSec = Math.floor(Date.now() / 1000);
      if (nowSec !== state.lastSecond) {
        els.rate.textContent = `${state.ticksThisSecond} /s`;
        state.ticksThisSecond = 0;
        state.lastSecond = nowSec;
      }
    }, 250);
  }

  // --- connection ---
  async function connect() {
    setConnState('connecting');
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/ticks')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    conn.on('Tick', onTick);
    conn.onreconnecting(() => setConnState('connecting'));
    conn.onreconnected(async () => {
      setConnState('connected');
      // re-apply subscriptions after reconnect
      if (state.subscriptions.size > 0) {
        try { await conn.invoke('Subscribe', [...state.subscriptions]); } catch { /* */ }
      }
    });
    conn.onclose(() => setConnState('disconnected'));

    state.connection = conn;
    try {
      await conn.start();
      setConnState('connected');
    } catch (e) {
      setConnState('disconnected');
      console.error('SignalR start failed', e);
    }
  }

  // --- boot ---
  function init() {
    renderSubs();
    clearLog();
    loadCatalog();
    tickRateTimer();

    els.btnSub.addEventListener('click', addSubscription);
    els.btnUnsub.addEventListener('click', unsubscribeAll);
    els.btnClear.addEventListener('click', clearLog);
    els.symInput.addEventListener('keydown', (e) => { if (e.key === 'Enter') addSubscription(); });

    connect();
  }
  init();
})();
