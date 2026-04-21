// YieldDataLogger admin UI - vanilla ES. Hits the same /api/instruments endpoints
// the REST clients use; on Phase 3c the bearer token gets injected here and all requests
// require admin role server-side.
(() => {
  'use strict';

  // --- state ---
  const state = {
    instruments: [],
    filter: '',
    editingSymbol: null, // canonicalSymbol being edited (updates form title)
  };

  // --- dom refs ---
  const els = {
    tbody:       document.getElementById('instruments-tbody'),
    count:       document.getElementById('catalog-count'),
    filter:      document.getElementById('filter-input'),
    listStatus:  document.getElementById('list-status'),
    formStatus:  document.getElementById('form-status'),
    formTitle:   document.getElementById('form-title'),
    form:        document.getElementById('instrument-form'),
    btnSave:     document.getElementById('btn-save'),
    btnReset:    document.getElementById('btn-reset'),
    fCanonical:  document.getElementById('f-canonical'),
    fPid:        document.getElementById('f-pid'),
    fCnbc:       document.getElementById('f-cnbc'),
    fCategory:   document.getElementById('f-category'),
    categoryOpts:document.getElementById('category-options'),
  };

  // --- api helpers ---
  const api = {
    list:   ()        => fetchJson('/api/instruments'),
    upsert: (dto)     => fetchJson('/api/instruments', { method: 'POST', body: JSON.stringify(dto) }),
    remove: (symbol)  => fetchJson(`/api/instruments/${encodeURIComponent(symbol)}`, { method: 'DELETE' }),
  };

  async function fetchJson(url, init = {}) {
    const res = await fetch(url, {
      ...init,
      headers: { 'Content-Type': 'application/json', ...(init.headers ?? {}) },
    });
    if (!res.ok) {
      const text = await res.text().catch(() => '');
      const err = new Error(`${res.status} ${res.statusText}${text ? ' - ' + text : ''}`);
      err.status = res.status;
      err.body = text;
      throw err;
    }
    if (res.status === 204) return null;
    return res.json();
  }

  // --- rendering ---
  function render() {
    const items = filterItems(state.instruments, state.filter);
    els.count.textContent = `${state.instruments.length} instrument${state.instruments.length === 1 ? '' : 's'}`;

    if (items.length === 0) {
      els.tbody.innerHTML = `<tr><td colspan="5" style="padding:24px;text-align:center;color:var(--text-muted)">
        ${state.instruments.length === 0 ? 'Catalog is empty.' : 'No matches.'}
      </td></tr>`;
      return;
    }

    // group by category
    const groups = new Map();
    for (const i of items) {
      const key = (i.category || '(uncategorised)').toLowerCase();
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(i);
    }

    const rows = [];
    const sortedGroups = [...groups.keys()].sort();
    for (const key of sortedGroups) {
      rows.push(renderCategoryRow(key, groups.get(key).length));
      for (const i of groups.get(key).sort((a, b) => a.canonicalSymbol.localeCompare(b.canonicalSymbol))) {
        rows.push(renderRow(i));
      }
    }
    els.tbody.innerHTML = rows.join('');

    // bind row actions
    els.tbody.querySelectorAll('button[data-action="edit"]').forEach(btn => {
      btn.addEventListener('click', () => onEdit(btn.dataset.symbol));
    });
    els.tbody.querySelectorAll('button[data-action="remove"]').forEach(btn => {
      btn.addEventListener('click', () => onRemove(btn.dataset.symbol));
    });
  }

  function renderCategoryRow(name, count) {
    return `<tr class="category-row"><td colspan="5">${escape(name)} &nbsp; <span style="color:var(--text-dim)">${count}</span></td></tr>`;
  }

  function renderRow(i) {
    const pid  = i.investingPid  != null ? String(i.investingPid)            : `<span class="empty-cell">-</span>`;
    const cnbc = i.cnbcSymbol    != null ? escape(i.cnbcSymbol)              : `<span class="empty-cell">-</span>`;
    const cat  = i.category      != null ? escape(i.category)                : `<span class="empty-cell">-</span>`;
    return `<tr>
      <td class="col-symbol">${escape(i.canonicalSymbol)}</td>
      <td class="col-pid">${pid}</td>
      <td class="col-cnbc">${cnbc}</td>
      <td class="col-category">${cat}</td>
      <td class="col-actions">
        <button class="row-btn row-btn-primary" data-action="edit"   data-symbol="${escape(i.canonicalSymbol)}">Edit</button>
        <button class="row-btn row-btn-danger"  data-action="remove" data-symbol="${escape(i.canonicalSymbol)}">Remove</button>
      </td>
    </tr>`;
  }

  function renderCategoryOptions() {
    const cats = new Set(state.instruments.map(i => i.category).filter(Boolean));
    els.categoryOpts.innerHTML = [...cats].sort().map(c => `<option value="${escape(c)}">`).join('');
  }

  function filterItems(items, q) {
    const needle = q.trim().toLowerCase();
    if (!needle) return items;
    return items.filter(i =>
      (i.canonicalSymbol && i.canonicalSymbol.toLowerCase().includes(needle)) ||
      (i.cnbcSymbol      && i.cnbcSymbol.toLowerCase().includes(needle)) ||
      (i.category        && i.category.toLowerCase().includes(needle)) ||
      (i.investingPid != null && String(i.investingPid).includes(needle))
    );
  }

  function escape(s) {
    return String(s).replace(/[&<>"']/g, c => ({
      '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;',
    }[c]));
  }

  // --- state helpers ---
  function setStatus(el, msg, kind) {
    el.textContent = msg || '';
    el.className = 'form-status' + (kind ? ' ' + kind : '');
    if (msg && kind === 'ok') setTimeout(() => { if (el.textContent === msg) { el.textContent = ''; el.className = 'form-status'; } }, 3000);
  }

  function setEditing(symbol) {
    state.editingSymbol = symbol;
    els.formTitle.textContent = symbol ? `Edit ${symbol}` : 'Add instrument';
    els.btnSave.textContent = symbol ? 'Update' : 'Save';
  }

  function resetForm() {
    els.form.reset();
    setEditing(null);
    setStatus(els.formStatus, '');
    els.fCanonical.focus();
  }

  // --- event handlers ---
  async function reload() {
    try {
      const items = await api.list();
      state.instruments = items;
      renderCategoryOptions();
      render();
    } catch (e) {
      setStatus(els.listStatus, `Failed to load instruments: ${e.message}`, 'error');
    }
  }

  async function onSubmit(ev) {
    ev.preventDefault();
    const dto = {
      canonicalSymbol: els.fCanonical.value.trim(),
      investingPid:    els.fPid.value ? Number(els.fPid.value) : null,
      cnbcSymbol:      els.fCnbc.value.trim() || null,
      category:        els.fCategory.value.trim() || null,
    };
    if (!dto.canonicalSymbol) {
      setStatus(els.formStatus, 'Canonical symbol is required.', 'error');
      return;
    }
    if (dto.investingPid == null && !dto.cnbcSymbol) {
      setStatus(els.formStatus, 'Provide at least an investing PID or a CNBC symbol.', 'error');
      return;
    }

    els.btnSave.disabled = true;
    try {
      await api.upsert(dto);
      setStatus(els.formStatus, `Saved ${dto.canonicalSymbol}.`, 'ok');
      resetForm();
      await reload();
    } catch (e) {
      setStatus(els.formStatus, e.message, 'error');
    } finally {
      els.btnSave.disabled = false;
    }
  }

  function onEdit(symbol) {
    const i = state.instruments.find(x => x.canonicalSymbol === symbol);
    if (!i) return;
    setEditing(i.canonicalSymbol);
    els.fCanonical.value = i.canonicalSymbol;
    els.fPid.value       = i.investingPid ?? '';
    els.fCnbc.value      = i.cnbcSymbol ?? '';
    els.fCategory.value  = i.category ?? '';
    els.form.scrollIntoView({ behavior: 'smooth', block: 'start' });
    els.fCanonical.focus();
  }

  async function onRemove(symbol) {
    if (!confirm(`Remove ${symbol} from the catalog?\n\nThe collector will stop receiving ticks for it on next run/reload. Existing history in storage is kept.`)) return;
    try {
      await api.remove(symbol);
      setStatus(els.listStatus, `Removed ${symbol}.`, 'ok');
      await reload();
    } catch (e) {
      setStatus(els.listStatus, e.message, 'error');
    }
  }

  function onFilter(ev) {
    state.filter = ev.target.value;
    render();
  }

  // --- boot ---
  function init() {
    els.form.addEventListener('submit', onSubmit);
    els.btnReset.addEventListener('click', resetForm);
    els.filter.addEventListener('input', onFilter);
    reload();
  }
  init();
})();
