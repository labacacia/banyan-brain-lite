// ── tiny helpers ──────────────────────────────────────────────────────────
const $  = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));

async function api(path, opts = {}) {
  const r = await fetch(path, {
    headers: { "content-type": "application/json", ...(opts.headers || {}) },
    ...opts,
    body: opts.body && typeof opts.body !== "string" ? JSON.stringify(opts.body) : opts.body,
  });
  const text = await r.text();
  let data; try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!r.ok) throw Object.assign(new Error(r.statusText), { status: r.status, data });
  return data;
}

function setStatus(text, kind = "") {
  const el = $("#status");
  el.textContent = text;
  el.className = "status " + kind;
}

function tabSwitch(name) {
  $$(".tab").forEach(t => t.classList.toggle("active", t.dataset.tab === name));
  $$(".panel").forEach(p => p.classList.toggle("active", p.id === "tab-" + name));
  if (name === "agents") loadAgents();
  if (name === "about")  loadAbout();
}
$$(".tab").forEach(t => t.addEventListener("click", () => tabSwitch(t.dataset.tab)));

// ── Memory ────────────────────────────────────────────────────────────────
const writeForm = $("#write-form");
const writeContent = $("#write-content");
const writeNs = $("#write-namespace");
const writeAgent = $("#write-agent");
const writeResult = $("#write-result");

writeForm.addEventListener("submit", async e => {
  e.preventDefault();
  const content = writeContent.value.trim();
  if (!content) return;
  try {
    const r = await api("/api/memory", {
      method: "POST",
      body: { content, namespace: writeNs.value || null, agentNid: writeAgent.value || null },
    });
    writeResult.innerHTML = `Saved <code class="mono">${esc(r.memoryId)}</code>`;
    writeContent.value = "";
    if ($("#search-input").value) doSearch();
  } catch (err) {
    writeResult.innerHTML = `<span style="color:var(--bad)">Error: ${esc(err.message)}</span>`;
  }
});

const searchInput = $("#search-input");
const searchResults = $("#search-results");
const searchModeWrap = $("#search-mode");
let searchTimer = null;
let searchMode = "hybrid";

searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(doSearch, 200);
});

$$(".seg-btn", searchModeWrap).forEach(b => {
  b.addEventListener("click", () => {
    $$(".seg-btn", searchModeWrap).forEach(x => x.classList.remove("active"));
    b.classList.add("active");
    searchMode = b.dataset.mode;
    if (searchInput.value) doSearch();
  });
});

async function doSearch() {
  const q = searchInput.value.trim();
  if (!q) { searchResults.innerHTML = ""; return; }
  try {
    const r = await api(`/api/memory/search?q=${encodeURIComponent(q)}&k=20&mode=${searchMode}`);
    const hits = r.hits || r;  // tolerate either shape during dev
    if (hits.length === 0) {
      searchResults.innerHTML = `<div class="muted small">No matches.</div>`;
      return;
    }
    searchResults.innerHTML = hits.map((h, i) => {
      const badges = [];
      if (h.lexicalRank != null) badges.push(`<span class="badge lex">lex #${h.lexicalRank}</span>`);
      if (h.vectorRank  != null) badges.push(`<span class="badge vec">vec #${h.vectorRank}</span>`);
      const isFused = h.lexicalRank != null && h.vectorRank != null;
      const scoreCls = isFused ? "fused" : (h.lexicalRank != null ? "lex" : "vec");
      return `
        <div class="hit" data-id="${esc(h.memoryId)}">
          <div class="hit-content">${esc(h.content)}</div>
          <div class="hit-meta">
            <span class="muted">#${i + 1}</span>
            <span class="badge ${scoreCls}">${h.score.toFixed(4)}</span>
            ${badges.join("")}
            <span>${esc(h.namespace)}</span>
            <span class="mono">${esc(h.memoryId.slice(0,8))}</span>
          </div>
        </div>`;
    }).join("");
    $$(".hit", searchResults).forEach(el => {
      el.addEventListener("click", () => openMemory(el.dataset.id));
    });
  } catch (err) {
    searchResults.innerHTML = `<div style="color:var(--bad)">Search failed: ${esc(err.message)}</div>`;
  }
}

const memDetail = $("#memory-detail");
let openedMemoryId = null;

async function openMemory(id) {
  try {
    const m = await api(`/api/memory/${id}`);
    const trace = await api(`/api/memory/${id}/trace`);
    openedMemoryId = id;
    $("#md-id").textContent = id;
    $("#md-content").textContent = m.content;
    $("#md-edit-content").value = m.content;
    $("#md-edit-pane").hidden = true;
    $("#md-trace").innerHTML = trace.map(e => {
      const cls = e.typeName === "Tombstone" ? "status-pill revoked"
                : e.typeName === "Update"    ? "status-pill expired"
                :                              "status-pill active";
      return `
        <tr>
          <td class="muted small">${new Date(e.occurredAt).toLocaleString()}</td>
          <td><span class="${cls}">${esc(e.typeName)}</span></td>
          <td>${esc(e.content || "—")}</td>
          <td class="muted small">${esc(e.metadata ? JSON.stringify(e.metadata) : "—")}</td>
        </tr>`;
    }).join("");
    memDetail.hidden = false;
    memDetail.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (err) {
    alert("Could not load memory: " + err.message);
  }
}

$("#md-close").addEventListener("click", () => { memDetail.hidden = true; openedMemoryId = null; });

$("#md-edit").addEventListener("click", () => {
  $("#md-edit-pane").hidden = false;
  $("#md-edit-content").focus();
});
$("#md-edit-cancel").addEventListener("click", () => { $("#md-edit-pane").hidden = true; });

$("#md-edit-save").addEventListener("click", async () => {
  if (!openedMemoryId) return;
  const content = $("#md-edit-content").value.trim();
  if (!content) return;
  try {
    await api(`/api/memory/${openedMemoryId}`, { method: "PUT", body: { content } });
    await openMemory(openedMemoryId);
    if (searchInput.value) doSearch();
  } catch (err) { alert("Update failed: " + err.message); }
});

$("#md-forget").addEventListener("click", async () => {
  if (!openedMemoryId) return;
  const reason = prompt("Forget reason:", "user-requested");
  if (reason == null) return;
  try {
    await api(`/api/memory/${openedMemoryId}?reason=${encodeURIComponent(reason)}`, { method: "DELETE" });
    memDetail.hidden = true;
    openedMemoryId = null;
    if (searchInput.value) doSearch();
  } catch (err) { alert("Forget failed: " + err.message); }
});

$("#seed-btn").addEventListener("click", async () => {
  const samples = [
    "User prefers concise summaries with bullet points.",
    "Project deadline for the migration is March 15th, 2026.",
    "The build pipeline broke last quarter when we tried to merge auth and identity into one service.",
    "Banyan stores memories as immutable events; the latest snapshot lives in memories_current.",
    "BM25 search uses the Okapi formula on the content column tokenised by unicode61.",
    "Agents authenticate via Ed25519 NID certificates issued by the embedded NPS-NIP CA.",
    "Operators log in via OIDC Device Code flow when there's no local browser available.",
    "The dual-track identity model: humans get JWTs, agents get X.509-style NIDs.",
  ];
  for (const s of samples) {
    await api("/api/memory", { method: "POST", body: { content: s } });
  }
  writeResult.innerHTML = `<span style="color:var(--good)">Seeded ${samples.length} sample memories.</span>`;
});

// ── Agents ────────────────────────────────────────────────────────────────
const issueForm = $("#issue-form");
const issueResult = $("#issue-result");

issueForm.addEventListener("submit", async e => {
  e.preventDefault();
  const id   = $("#issue-id").value.trim();
  const caps = $("#issue-caps").value.split(",").map(s => s.trim()).filter(Boolean);
  if (!id) return;
  try {
    const r = await api("/api/agents", { method: "POST", body: { id, capabilities: caps } });
    issueResult.innerHTML = `
      <div class="alert success">
        <strong>Issued ${esc(r.nid)}</strong>
        <div class="muted small">serial ${esc(r.serial)} · expires ${new Date(r.expiresAt).toLocaleString()}</div>
        <div style="margin-top:10px">Private key (save now — won't be shown again):</div>
        <div class="copyable">
          <code>${esc(r.privateKeyBase64)}</code>
          <button class="ghost" data-copy="${esc(r.privateKeyBase64)}">Copy</button>
        </div>
      </div>`;
    issueForm.reset();
    loadAgents();
  } catch (err) {
    issueResult.innerHTML = `<div class="alert">Error: ${esc(err.data?.error || err.message)}</div>`;
  }
});

document.addEventListener("click", async e => {
  if (e.target?.dataset?.copy != null) {
    await navigator.clipboard.writeText(e.target.dataset.copy);
    const orig = e.target.textContent;
    e.target.textContent = "Copied!";
    setTimeout(() => { e.target.textContent = orig; }, 1200);
  }
});

async function loadAgents() {
  try {
    const rows = await api("/api/agents");
    $("#agents-count").textContent = `(${rows.length})`;
    $("#agents-tbody").innerHTML = rows.map(r => `
      <tr>
        <td class="mono small">${esc(r.nid)}</td>
        <td class="mono small">${esc(r.serial)}</td>
        <td>${esc(r.entityType)}</td>
        <td class="muted small">${new Date(r.issuedAt).toLocaleString()}</td>
        <td><span class="status-pill ${esc(r.status)}">${esc(r.status)}${r.revokeReason ? ": " + esc(r.revokeReason) : ""}</span></td>
        <td>${r.status === "active" ? `<button class="ghost danger" data-revoke="${esc(r.nid)}">revoke</button>` : ""}</td>
      </tr>`).join("");
    $$("[data-revoke]").forEach(b => b.addEventListener("click", () => revoke(b.dataset.revoke)));
  } catch (err) {
    $("#agents-tbody").innerHTML = `<tr><td colspan="6" style="color:var(--bad)">${esc(err.data?.error || err.message)}</td></tr>`;
  }
}

async function revoke(nid) {
  const reason = prompt(`Revoke ${nid}? Reason:`, "operator-initiated");
  if (reason == null) return;
  try { await api(`/api/agents/${encodeURIComponent(nid)}/revoke`, { method: "POST", body: { reason } }); loadAgents(); }
  catch (err) { alert("Revoke failed: " + err.message); }
}

// ── About ─────────────────────────────────────────────────────────────────
async function loadAbout() {
  const dl = $("#about-dl"); dl.innerHTML = "";
  try {
    const ca = await api("/api/ca");
    const modeLabel = ca.mode === "external" ? " <span class='badge'>external</span>" : " <span class='badge'>embedded</span>";
    const issued  = ca.issuedCount  != null ? ca.issuedCount  : "—";
    const revoked = ca.revokedCount != null ? ca.revokedCount : "—";
    dl.innerHTML = `
      <dt>CA mode</dt><dd>${ca.mode}${modeLabel}</dd>
      <dt>CA NID</dt><dd class="mono small">${esc(ca.caNid)}</dd>
      <dt>CA pub key</dt><dd class="mono small">${esc(ca.caPubKey)}</dd>
      <dt>Issued</dt><dd>${issued}</dd>
      <dt>Revoked</dt><dd>${revoked}</dd>`;
  } catch (err) {
    dl.innerHTML = `<dt>CA</dt><dd class="muted">${esc(err.data?.error || err.message)}</dd>`;
  }

  const me = $("#me-dl");
  try {
    const m = await api("/api/auth/me");
    if (!m.loggedIn) { me.innerHTML = `<dt>status</dt><dd class="muted">not logged in</dd>`; return; }
    me.innerHTML = `
      <dt>username</dt><dd>${esc(m.username || "—")}</dd>
      <dt>roles</dt><dd>${esc((m.roles || []).join(", ") || "—")}</dd>
      <dt>expires</dt><dd>${m.expiresAt ? new Date(m.expiresAt).toLocaleString() : "—"}</dd>`;
  } catch (err) {
    me.innerHTML = `<dt>status</dt><dd class="muted">${esc(err.message)}</dd>`;
  }
}

// ── boot ──────────────────────────────────────────────────────────────────
(async function boot() {
  try {
    const h = await api("/api/health");
    setStatus(`v${h.version}`, "ok");
  } catch (err) {
    setStatus("offline", "err");
  }
})();

// ── particle background ───────────────────────────────────────────────────
// Lightweight neon-network: ~120 nodes drift, link to neighbours within range,
// pulse toward cursor. Pure 2D canvas, no deps.
(function particles() {
  const cv = document.getElementById("bg-particles");
  if (!cv) return;
  const ctx = cv.getContext("2d");

  const COUNT = window.matchMedia("(max-width: 700px)").matches ? 60 : 120;
  const LINK_DIST = 130;
  const MOUSE_PULL = 90;
  const PALETTE = [
    [76, 194, 255],   // electric blue
    [0, 212, 255],    // cyan
    [255, 77, 141],   // hot pink
    [255, 0, 128],    // deep pink
  ];

  let w = 0, h = 0, dpr = Math.min(window.devicePixelRatio || 1, 2);
  let mx = -9999, my = -9999;
  const nodes = [];

  function resize() {
    w = window.innerWidth; h = window.innerHeight;
    cv.width  = w * dpr; cv.height = h * dpr;
    cv.style.width = w + "px"; cv.style.height = h + "px";
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  resize();
  window.addEventListener("resize", resize);
  window.addEventListener("mousemove", e => { mx = e.clientX; my = e.clientY; });
  window.addEventListener("mouseleave", () => { mx = -9999; my = -9999; });

  for (let i = 0; i < COUNT; i++) {
    nodes.push({
      x: Math.random() * w,
      y: Math.random() * h,
      vx: (Math.random() - 0.5) * 0.3,
      vy: (Math.random() - 0.5) * 0.3,
      r: 0.7 + Math.random() * 1.6,
      c: PALETTE[Math.floor(Math.random() * PALETTE.length)],
    });
  }

  function tick() {
    ctx.clearRect(0, 0, w, h);

    // links first (under nodes)
    for (let i = 0; i < nodes.length; i++) {
      const a = nodes[i];
      for (let j = i + 1; j < nodes.length; j++) {
        const b = nodes[j];
        const dx = a.x - b.x, dy = a.y - b.y;
        const d = Math.hypot(dx, dy);
        if (d > LINK_DIST) continue;
        const alpha = (1 - d / LINK_DIST) * 0.18;
        // blend the two endpoint colours
        const cr = (a.c[0] + b.c[0]) / 2 | 0;
        const cg = (a.c[1] + b.c[1]) / 2 | 0;
        const cb = (a.c[2] + b.c[2]) / 2 | 0;
        ctx.strokeStyle = `rgba(${cr},${cg},${cb},${alpha})`;
        ctx.lineWidth = 0.8;
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
      }
    }

    // nodes
    for (const n of nodes) {
      // cursor pull
      const dx = mx - n.x, dy = my - n.y;
      const d = Math.hypot(dx, dy);
      if (d < MOUSE_PULL && d > 0.001) {
        const f = (1 - d / MOUSE_PULL) * 0.04;
        n.vx += (dx / d) * f;
        n.vy += (dy / d) * f;
      }

      n.x += n.vx; n.y += n.vy;

      // wrap edges
      if (n.x < -10) n.x = w + 10; else if (n.x > w + 10) n.x = -10;
      if (n.y < -10) n.y = h + 10; else if (n.y > h + 10) n.y = -10;

      // gentle damping so cursor pull doesn't run away
      n.vx *= 0.985; n.vy *= 0.985;
      // base drift floor
      const speed = Math.hypot(n.vx, n.vy);
      if (speed < 0.1) {
        n.vx += (Math.random() - 0.5) * 0.08;
        n.vy += (Math.random() - 0.5) * 0.08;
      }

      // glow + dot
      const [r, g, b] = n.c;
      ctx.fillStyle = `rgba(${r},${g},${b},0.95)`;
      ctx.shadowColor = `rgba(${r},${g},${b},0.7)`;
      ctx.shadowBlur = 12;
      ctx.beginPath();
      ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.shadowBlur = 0;

    requestAnimationFrame(tick);
  }
  tick();
})();

// ── Auth status pill (top-right header) ─────────────────────────────────────
// Shows the signed-in username when the browser carries a valid `banyan_session`
// cookie, or a Sign-in link when it doesn't. Stays out of the way when the
// server is running without OLS identity wired (zero-config Lite demo).
(async () => {
  const pill   = document.getElementById('auth-pill');
  const userEl = document.getElementById('auth-user');
  const signin = document.getElementById('auth-signin');
  const logout = document.getElementById('auth-logout');
  if (!pill || !signin) return;

  try {
    const r = await fetch('/api/auth/me', { credentials: 'same-origin' });
    if (!r.ok) return;                          // identity not wired — leave header bare
    const me = await r.json();
    if (me.loggedIn) {
      const role = (me.roles || []).find(x => /admin/i.test(x));
      userEl.textContent = me.username + (role ? ' · admin' : '');
      pill.hidden = false;
    } else {
      signin.hidden = false;
    }
  } catch { /* server doesn't expose /api/auth/me — stay quiet */ }

  if (logout) logout.addEventListener('click', async () => {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    location.reload();
  });
})();
