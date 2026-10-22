// open!bancho website: a small single-page app on top of the server's JSON API.
"use strict";

const $ = (s, el = document) => el.querySelector(s);
const app = $("#app");
const state = { token: null, me: null };
try { state.token = localStorage.getItem("token"); } catch { /* private mode: login lasts this tab */ }

const MODES = ["Estándar", "Taiko", "Catch", "Mania"];
const MOD_BITS = [[1, "NF"], [2, "EZ"], [4, "TD"], [8, "HD"], [16, "HR"], [32, "SD"], [64, "DT"], [128, "RX"], [256, "HT"], [512, "NC"],
  [1024, "FL"], [2048, "AT"], [4096, "SO"], [8192, "AP"], [16384, "PF"], [32768, "4K"], [65536, "5K"], [131072, "6K"], [262144, "7K"],
  [524288, "8K"], [1048576, "FI"], [2097152, "RD"], [4194304, "CN"], [16777216, "9K"], [536870912, "V2"], [1073741824, "MR"]];
const GRADE_COL = { SSH: "#9aa", SS: "#e0a000", SH: "#8899a6", S: "#18909a", A: "#4a9a1a", B: "#3b6fd0", C: "#9a3bd0", D: "#d0455b" };

// ---------- helpers ----------

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
const num = n => Number(n || 0).toLocaleString("es");
const pct = a => (Number(a || 0) * 100).toFixed(2) + "%";
const mods = m => MOD_BITS.filter(([b]) => (m & b) !== 0).map(([, s]) => s).join(",") || "";
function ago(d) {
  const s = (Date.now() - new Date(d).getTime()) / 1000;
  if (s < 60) return "hace un momento";
  const units = [[31536000, "año", "años"], [2592000, "mes", "meses"], [86400, "día", "días"], [3600, "hora", "horas"], [60, "minuto", "minutos"]];
  for (const [u, one, many] of units) if (s >= u) { const n = Math.floor(s / u); return `hace ${n} ${n === 1 ? one : many}`; }
  return "";
}
const date = d => new Date(d).toLocaleDateString("es", { year: "numeric", month: "long", day: "numeric" });
const flag = (code, name) => code ? `<span class="flag" title="${esc(name || code)}">${esc(code)}</span>` : "";
const userLink = (name) => `<a href="#/u/${encodeURIComponent(name)}">${esc(name)}</a>`;
function avatar(id, name, cls = "") {
  const initial = esc((name || "?").slice(0, 1).toUpperCase());
  // the server answers 404 when there's no avatar; the image then turns into the initial
  return `<img class="${cls}" src="/a/${id}?v=${avatarVersion}" alt="" onerror="this.outerHTML='<div class=&quot;${cls} av&quot;>${initial}</div>'">`;
}
let avatarVersion = Date.now();
function playtime(ms) {
  const m = Math.floor(ms / 60000), d = Math.floor(m / 1440), h = Math.floor(m % 1440 / 60);
  return d > 0 ? `${d}d ${h}h ${m % 60}m` : `${h}h ${m % 60}m`;
}

// mode icons: simple original shapes (circle, drum, fruit, keys)
const MODE_SVG = [
  '<svg viewBox="0 0 32 32"><circle cx="16" cy="16" r="12" fill="none" stroke="currentColor" stroke-width="3"/><circle cx="16" cy="16" r="5" fill="currentColor"/></svg>',
  '<svg viewBox="0 0 32 32"><circle cx="16" cy="16" r="12" fill="none" stroke="currentColor" stroke-width="3"/><path d="M16 4v24" stroke="currentColor" stroke-width="3"/><circle cx="16" cy="16" r="5" fill="none" stroke="currentColor" stroke-width="3"/></svg>',
  '<svg viewBox="0 0 32 32"><circle cx="16" cy="16" r="12" fill="none" stroke="currentColor" stroke-width="3"/><circle cx="12" cy="18" r="3.5" fill="currentColor"/><circle cx="20" cy="18" r="3.5" fill="currentColor"/><circle cx="16" cy="11" r="3.5" fill="currentColor"/></svg>',
  '<svg viewBox="0 0 32 32"><circle cx="16" cy="16" r="12" fill="none" stroke="currentColor" stroke-width="3"/><path d="M11 10v12M16 10v12M21 10v12" stroke="currentColor" stroke-width="3"/></svg>',
];
const modeTabs = (cur, href) => `<div class="modes">${MODE_SVG.map((s, i) =>
  `<a href="${href(i)}" class="${i === cur ? "on" : ""}" title="${MODES[i]}">${s}</a>`).join("")}</div>`;

/** Bio markup: escaped first, then **bold**, *italic*, [text](https://link), # headings and line breaks. */
function markup(text) {
  return esc(text)
    .replace(/^# (.+)$/gm, "<h4>$1</h4>")
    .replace(/\*\*(.+?)\*\*/g, "<b>$1</b>")
    .replace(/\*(.+?)\*/g, "<i>$1</i>")
    .replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>')
    .replace(/\n/g, "<br>");
}

async function api(path, opt = {}) {
  const headers = Object.assign({}, opt.headers);
  if (state.token) headers.Authorization = "Bearer " + state.token;
  let body = opt.body;
  if (opt.json !== undefined) { headers["Content-Type"] = "application/json"; body = JSON.stringify(opt.json); }
  const ctl = new AbortController(), timer = setTimeout(() => ctl.abort(), 15000);
  let r;
  try { r = await fetch(path, { method: opt.method || (body !== undefined ? "POST" : "GET"), headers, body, signal: ctl.signal }); }
  catch { throw new Error("El servidor no responde. ¿Está abierto local_bancho.bat?"); }
  finally { clearTimeout(timer); }
  const text = await r.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (!r.ok) {
    if (r.status === 401 && state.token && path === "/api/me") logout(false);
    throw new Error((data && data.error) || `Error ${r.status}`);
  }
  return data;
}

function setToken(t) {
  state.token = t;
  try { t ? localStorage.setItem("token", t) : localStorage.removeItem("token"); } catch { /* ignore */ }
}

// ---------- header and login ----------

async function loadMe() {
  if (!state.token) { state.me = null; drawMe(); return; }
  try { state.me = await api("/api/me"); } catch { state.me = null; }
  drawMe();
}

function drawMe() {
  const box = $("#me");
  if (!state.me) {
    box.innerHTML = '<button class="btn ghost" id="open-login">Iniciar sesión</button>';
    $("#open-login").onclick = () => openLogin(false);
    return;
  }
  box.innerHTML = `<a href="#" id="me-btn" title="${esc(state.me.name)}">${avatar(state.me.id, state.me.name)}</a>`;
  $("#me-btn").onclick = e => { e.preventDefault(); $("#menu").classList.toggle("open"); };
  $("#menu-profile").href = "#/u/" + encodeURIComponent(state.me.name);
}

document.addEventListener("click", e => { if (!e.target.closest("#me")) $("#menu").classList.remove("open"); });
$("#menu-logout").onclick = e => { e.preventDefault(); logout(true); };

async function logout(tell) {
  try { if (state.token) await fetch("/api/logout", { method: "POST", headers: { Authorization: "Bearer " + state.token } }); } catch { /* offline */ }
  setToken(null);
  state.me = null;
  drawMe();
  if (tell) route();
}

let registering = false;
function openLogin(reg) {
  registering = reg;
  $("#login-title").textContent = reg ? "Crear una cuenta" : "Iniciar sesión";
  $("#l-go").textContent = reg ? "Crear cuenta" : "Iniciar sesión";
  $("#l-switch").textContent = reg ? "¿Ya tienes cuenta? Inicia sesión" : "¿No tienes cuenta? Créala aquí";
  $("#l-err").textContent = "";
  $("#login").classList.add("open");
  $("#l-name").focus();
}
$("#l-switch").onclick = e => { e.preventDefault(); openLogin(!registering); };
$("#l-close").onclick = e => { e.preventDefault(); $("#login").classList.remove("open"); };
$("#login-form").onsubmit = async e => {
  e.preventDefault();
  $("#l-err").textContent = "";
  $("#l-go").disabled = true;
  $("#l-go").textContent = "Conectando...";
  try {
    const r = await api(registering ? "/api/register" : "/api/login", { json: { name: $("#l-name").value.trim(), password: $("#l-pass").value } });
    setToken(r.token);
    $("#l-pass").value = "";
    $("#login").classList.remove("open");
    await loadMe();
    route();
  } catch (err) { $("#l-err").textContent = err.message; }
  $("#l-go").disabled = false;
  $("#l-go").textContent = registering ? "Crear cuenta" : "Iniciar sesión";
};
$("#search").onsubmit = e => { e.preventDefault(); const q = $("#q").value.trim(); if (q) location.hash = "#/u/" + encodeURIComponent(q); };

// ---------- pages ----------

const scoreRow = (s, extra = "") => `
  <div class="score">
    <span class="badge" style="background:${GRADE_COL[s.rank] || "#666"}">${esc(s.rank.replace("H", ""))}</span>
    <div class="t"><b><a href="#/b/${esc(s.hash)}">${esc(s.title)}</a></b>
      <div><span class="v">${esc(s.version)}</span> <span class="muted small">${ago(s.date)}</span> ${s.mods ? `<span class="pill">${mods(s.mods)}</span>` : ""}</div></div>
    ${extra}
    <span class="acc">${pct(s.accuracy)}</span>
    <span class="pp">${s.pp > 0 ? Math.round(s.weighted ?? s.pp) + "pp" : "-"}</span>
  </div>`;

async function home() {
  const [ping, online, recent] = await Promise.all([api("/api/ping"), api("/api/online"), api("/api/recent")]);
  app.innerHTML = `
    <section class="hero">
      <h1>open!bancho</h1>
      <p class="muted">El servidor propio de open!stable: cuentas, récords, clasificaciones, chat y multijugador.</p>
      <p>Conecta el juego en <b>Opciones &gt; Mantenimiento &gt; Servidor personalizado</b> con <code>${esc(location.origin)}</code></p>
      ${state.me ? `<a class="btn" href="#/u/${encodeURIComponent(state.me.name)}">Ver mi perfil</a>` : '<button class="btn" id="hero-reg">Crear una cuenta</button>'}
      <div class="big-logo">open!</div>
    </section>
    <div class="stats3">
      <div class="stat"><b>${num(ping.users)}</b><span class="muted">jugadores registrados</span></div>
      <div class="stat"><b>${num(ping.online)}</b><span class="muted">conectados ahora</span></div>
      <div class="stat"><b>${num(ping.scores)}</b><span class="muted">puntuaciones enviadas</span></div>
    </div>
    <div class="card"><h2>Usuarios en línea</h2>${onlineCards(online)}</div>
    <div class="card"><h2>Partidas recientes</h2>
      ${recent.length ? recent.map(r => scoreRow(r.score, `<span class="small">${flag(r.country)} ${userLink(r.score.player)}</span>`)).join("") : '<div class="empty">Todavía nadie ha jugado.</div>'}
    </div>`;
  const reg = $("#hero-reg");
  if (reg) reg.onclick = () => openLogin(true);
}

function onlineCards(list) {
  if (!list.length) return '<div class="empty">No hay nadie conectado.</div>';
  return `<div class="users">${list.map(u => `
    <a class="ucard" href="#/u/${encodeURIComponent(u.name)}">
      ${avatar(u.id, u.name)}
      <div><b>${esc(u.name)}</b><div class="small">${flag(u.country, u.countryName)} ${esc(u.countryName)}</div><div class="st">${esc(u.status)}</div></div>
      ${u.rank ? `<span class="rk">#${num(u.rank)}</span>` : ""}
    </a>`).join("")}</div>`;
}

async function rankings(params) {
  const mode = +(params.get("mode") || 0), country = params.get("country") || "", page = +(params.get("page") || 0);
  const d = await api(`/api/rankings?mode=${mode}&country=${encodeURIComponent(country)}&page=${page}`);
  const href = (m, c = country, p = 0) => `#/rankings?mode=${m}&country=${encodeURIComponent(c)}&page=${p}`;
  app.innerHTML = `
    <div class="page-title"><span class="ico">#</span> clasificaciones <span class="spacer"></span>${modeTabs(mode, i => href(i))}</div>
    <div class="card">
      <div class="row"><label class="muted small">País
        <select id="country"><option value="">Todos</option>${d.countries.map(c => `<option value="${esc(c.code)}" ${c.code === country ? "selected" : ""}>${esc(c.name)} (${c.players})</option>`).join("")}</select></label>
        <span class="spacer"></span><span class="muted small">${num(d.total)} jugadores · ${MODES[mode]}</span></div>
      ${d.rows.length ? `<table><tr><th></th><th>Jugador</th><th class="num">Precisión</th><th class="num">Partidas</th><th class="num">Rendimiento</th><th class="num hide-sm">SS</th><th class="num hide-sm">S</th><th class="num hide-sm">A</th></tr>
        ${d.rows.map(r => `<tr><td class="rankno">#${country ? r.countryRank : r.rank}</td>
          <td>${flag(r.country, r.countryName)} ${userLink(r.name)}</td><td class="num">${pct(r.accuracy)}</td><td class="num">${num(r.plays)}</td>
          <td class="num"><b>${num(Math.round(r.pp))}pp</b></td><td class="num hide-sm">${r.ss + r.ssh}</td><td class="num hide-sm">${r.s + r.sh}</td><td class="num hide-sm">${r.a}</td></tr>`).join("")}</table>`
        : '<div class="empty">Nadie ha jugado este modo todavía.</div>'}
      <div class="row">${page > 0 ? `<a class="btn grey" href="${href(mode, country, page - 1)}">Anterior</a>` : ""}
        <span class="spacer"></span>${(page + 1) * 50 < d.total ? `<a class="btn grey" href="${href(mode, country, page + 1)}">Siguiente</a>` : ""}</div>
    </div>
    ${d.countries.length ? `<div class="card"><h2>Por país</h2><table><tr><th></th><th>País</th><th class="num">Jugadores</th><th class="num">pp totales</th></tr>
      ${d.countries.map((c, i) => `<tr><td class="rankno">#${i + 1}</td><td>${flag(c.code)} <a href="${href(mode, c.code)}">${esc(c.name)}</a></td><td class="num">${c.players}</td><td class="num">${num(Math.round(c.pp))}</td></tr>`).join("")}</table></div>` : ""}`;
  $("#country").onchange = e => { location.hash = href(mode, e.target.value); };
}

function rankGraph(history) {
  if (history.length < 2) return '<div class="muted small" style="margin:12px 0">El gráfico de rango aparece tras jugar en días distintos.</div>';
  const ranks = history.map(h => h.rank), lo = Math.min(...ranks), hi = Math.max(...ranks), w = 600, h = 90;
  const pts = history.map((p, i) => `${(i / (history.length - 1) * w).toFixed(1)},${(hi === lo ? h / 2 : (p.rank - lo) / (hi - lo) * (h - 10) + 5).toFixed(1)}`).join(" ");
  return `<svg class="graph" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none"><polyline points="${pts}" fill="none" stroke="#ffd966" stroke-width="3" vector-effect="non-scaling-stroke"/>
    <title>Rango: de #${hi} a #${lo}</title></svg>`;
}

async function profile(name, params) {
  const mode = +(params.get("mode") || 0);
  const p = await api(`/api/users/${encodeURIComponent(name)}?mode=${mode}`);
  const u = p.user, own = state.me && state.me.id === u.id;
  const levelPct = Math.floor((u.level % 1) * 100);
  const styles = { mouse: "ratón", keyboard: "teclado", tablet: "tableta", touch: "pantalla táctil" };
  const playstyle = p.playstyle ? p.playstyle.split(",").map(s => styles[s] || s).join(", ") : "";
  const groups = [...new Set(p.medals.map(m => m.group))];
  const got = p.medals.filter(m => m.achieved).length;
  const showList = (list, id, extra) => list.length
    ? list.slice(0, 5).map(s => scoreRow(s, extra ? extra(s) : "")).join("") + (list.length > 5
      ? `<div id="${id}-more" style="display:none">${list.slice(5).map(s => scoreRow(s, extra ? extra(s) : "")).join("")}</div>
         <div class="row" style="justify-content:center"><button class="btn grey" data-more="${id}">Mostrar más</button></div>` : "")
    : '<div class="muted small">Nada por aquí todavía.</div>';
  app.innerHTML = `
    <div class="page-title"><span class="ico">i</span> información del jugador <span class="spacer"></span>${modeTabs(mode, i => `#/u/${encodeURIComponent(u.name)}?mode=${i}`)}</div>
    <div class="cover" style="${p.hasCover ? `background-image:url('/c/${u.id}?v=${avatarVersion}')` : ""}"></div>
    <div class="phead">
      ${avatar(u.id, u.name, "pav")}
      <div style="padding-top:12px">
        <div class="pname">${esc(u.name)} <span class="dot ${p.online ? "on" : ""}" title="${p.online ? "En línea" : "Desconectado"}"></span></div>
        ${u.title ? `<div class="ptitle" style="color:${esc(u.color || "#ff66aa")}">${esc(u.title)}</div>` : ""}
        <div>${flag(u.country, u.countryName)} ${esc(u.countryName || "Sin país")}</div>
      </div>
      <span class="spacer"></span>
      ${own ? '<a class="btn" href="#/settings">Editar perfil</a>' : state.me ? `<a class="btn ghost" href="#/chat?pm=${u.id}">Enviar mensaje</a>` : ""}
    </div>
    <div class="pbody">
      <div>
        <div class="ranks">
          <div><div class="lbl">Clasificación global</div><div class="val">#${num(u.rank)}</div></div>
          <div><div class="lbl">Clasificación nacional</div><div class="val">${u.countryRank ? "#" + num(u.countryRank) : "-"}</div></div>
        </div>
        ${rankGraph(p.history)}
        <div class="mini">
          <div><div class="lbl">Medallas</div><div class="val">${got}</div></div>
          <div><div class="lbl">pp</div><div class="val">${num(Math.round(u.pp))}</div></div>
          <div><div class="lbl">Tiempo de juego total</div><div class="val">${playtime(p.playTimeMs)}</div></div>
          <div class="grades">${[["SSH", u.ssh], ["SS", u.ss], ["SH", u.sh], ["S", u.s], ["A", u.a]].map(([g, n]) =>
            `<div class="grade g-${g.toLowerCase()}"><span>${g.replace("H", "")}</span>${n}</div>`).join("")}</div>
        </div>
      </div>
      <div class="side">
        <div>Puntuación clasificada <b>${num(u.rankedScore)}</b></div>
        <div>Precisión <b>${pct(u.accuracy)}</b></div>
        <div>Número de jugadas <b>${num(u.plays)}</b></div>
        <div>Puntuación total <b>${num(u.totalScore)}</b></div>
        <div>Golpes totales <b>${num(p.totalHits)}</b></div>
        <div>Golpes por jugada <b>${num(p.hitsPerPlay)}</b></div>
        <div>Combo máximo <b>${num(u.maxCombo)}</b></div>
        <div>Victorias en multijugador <b>${num(p.multiWins)}</b></div>
      </div>
    </div>
    <div class="level"><div class="bar"><i style="width:${levelPct}%"></i></div><span class="small">${levelPct}%</span><div class="hex" data-l="${Math.floor(u.level)}"></div></div>
    <div class="pinfo">
      <span>Se unió el <b>${date(p.joined)}</b></span>
      <span>${p.online ? `<b>En línea</b>: ${esc(p.status)}` : `Última vez <b>${ago(p.lastSeen)}</b>`}</span>
      ${playstyle ? `<span>Juega con <b>${esc(playstyle)}</b></span>` : ""}
      ${p.location ? `<span>📍 ${esc(p.location)}</span>` : ""}
      ${p.occupation ? `<span>Ocupación: ${esc(p.occupation)}</span>` : ""}
      ${p.interests ? `<span>Le gusta: ${esc(p.interests)}</span>` : ""}
    </div>
    <div class="links">
      ${p.twitter ? `<a href="https://x.com/${encodeURIComponent(p.twitter)}" target="_blank" rel="noopener noreferrer">@${esc(p.twitter)}</a>` : ""}
      ${p.discord ? `<span>Discord: ${esc(p.discord)}</span>` : ""}
      ${p.website ? `<a href="${esc(p.website)}" target="_blank" rel="noopener noreferrer">${esc(p.website.replace(/^https?:\/\//, ""))}</a>` : ""}
    </div>
    <nav class="subnav"><a href="#me-sec">¡yo!</a><a href="#recent-sec">Reciente</a><a href="#ranks-sec">Rangos</a><a href="#medals-sec">Medallas</a></nav>
    <div class="card" id="me-sec" style="border-radius:0"><h2>¡yo!</h2>
      ${p.bio ? `<div class="bio">${markup(p.bio)}</div>` : `<div class="muted">${own ? 'Tu espacio personal está vacío. <a href="#/settings">Escribe algo sobre ti</a>.' : "Este jugador todavía no escribió nada."}</div>`}</div>
    <div class="card" id="recent-sec"><h2>Reciente</h2>${showList(p.recent, "recent")}</div>
    <div class="card" id="ranks-sec"><h2>Rangos</h2>
      <h3>Mejor rendimiento</h3>${showList(p.best, "best", s => `<span class="muted small hide-sm">${Math.round(s.pp)}pp · valorado ${s.weight}%</span>`)}
      <h3>Primeros lugares <span class="pill">${p.firsts.length}</span></h3>${showList(p.firsts, "firsts")}
    </div>
    <div class="card" id="medals-sec"><h2>Medallas</h2><span class="muted small"> ${got} de ${p.medals.length}</span>
      ${groups.map(g => `<h3>${esc(g)}</h3><div class="medals">${p.medals.filter(m => m.group === g).map(m => `
        <div class="medal ${m.achieved ? "" : "off"}" title="${esc(m.description)}"><div class="m">${esc(m.name.replace(/[¡!]/g, "").slice(0, 2).toUpperCase())}</div>${esc(m.name)}</div>`).join("")}</div>`).join("")}
    </div>`;
  app.querySelectorAll("[data-more]").forEach(b => b.onclick = () => { $("#" + b.dataset.more + "-more").style.display = ""; b.remove(); });
  app.querySelectorAll('.subnav a').forEach(a => a.onclick = e => { e.preventDefault(); $(a.getAttribute("href")).scrollIntoView({ behavior: "smooth" }); });
}

async function settings() {
  if (!state.me) { app.innerHTML = '<div class="card empty">Inicia sesión para editar tu perfil.</div>'; openLogin(false); return; }
  const [p, countries] = await Promise.all([api("/api/me/full"), api("/api/countries")]);
  const u = p.user, styles = (p.playstyle || "").split(",");
  app.innerHTML = `
    <div class="page-title"><span class="ico">✎</span> ajustes del perfil</div>
    <div class="card">
      <h2>Imágenes</h2>
      <div class="uprow">${avatar(u.id, u.name, "prev")}
        <div class="grid"><b>Foto de perfil</b><span class="muted small">PNG, JPG o GIF de hasta 2 MB. Se ve en el juego y en la web.</span>
          <div class="row"><input type="file" id="f-av" accept="image/png,image/jpeg,image/gif"><button class="btn grey" id="rm-av">Quitar</button></div></div></div>
      <div class="uprow" style="margin-top:16px"><div class="prev wide" style="background:${p.hasCover ? `url('/c/${u.id}?v=${avatarVersion}') center/cover` : "var(--bg4)"}"></div>
        <div class="grid"><b>Portada</b><span class="muted small">La imagen ancha de arriba del perfil. Hasta 6 MB.</span>
          <div class="row"><input type="file" id="f-cv" accept="image/png,image/jpeg,image/gif"><button class="btn grey" id="rm-cv">Quitar</button></div></div></div>
      <div id="img-msg" class="small"></div>
    </div>
    <form class="card form" id="pf">
      <h2>Información</h2>
      <label>País <select name="country"><option value="">Sin país</option>${countries.map(c => `<option value="${c.code}" ${c.code === u.country ? "selected" : ""}>${esc(c.name)}</option>`).join("")}</select></label>
      <label>Título bajo tu nombre <input type="text" name="title" maxlength="30" value="${esc(u.title)}" placeholder="Ej.: Jugador de mania"></label>
      <label>Color del título <input type="color" name="color" value="${esc(u.color || "#ff66aa")}"></label>
      <label>Ubicación <input type="text" name="location" maxlength="40" value="${esc(p.location)}"></label>
      <label>Intereses <input type="text" name="interests" maxlength="60" value="${esc(p.interests)}"></label>
      <label>Ocupación <input type="text" name="occupation" maxlength="40" value="${esc(p.occupation)}"></label>
      <label>Twitter / X (sin @) <input type="text" name="twitter" maxlength="16" value="${esc(p.twitter)}"></label>
      <label>Discord <input type="text" name="discord" maxlength="40" value="${esc(p.discord)}"></label>
      <label>Sitio web <input type="text" name="website" maxlength="200" value="${esc(p.website)}" placeholder="https://"></label>
      <div><span class="muted small">Juego con</span><div class="checks">${[["mouse", "Ratón"], ["keyboard", "Teclado"], ["tablet", "Tableta"], ["touch", "Pantalla táctil"]]
        .map(([k, l]) => `<label><input type="checkbox" name="ps" value="${k}" ${styles.includes(k) ? "checked" : ""}> ${l}</label>`).join("")}</div></div>
      <label>¡yo! (tu espacio personal). Admite **negrita**, *cursiva*, # títulos y [enlaces](https://...)
        <textarea name="bio" maxlength="4000">${esc(p.bio)}</textarea></label>
      <div class="row"><button class="btn">Guardar</button><span id="pf-msg" class="small"></span>
        <span class="spacer"></span><a href="#/u/${encodeURIComponent(u.name)}">Ver mi perfil</a></div>
    </form>
    <form class="card form" id="pw">
      <h2>Contraseña</h2>
      <label>Contraseña actual <input type="password" name="old" autocomplete="current-password" required></label>
      <label>Contraseña nueva <input type="password" name="new" minlength="4" autocomplete="new-password" required></label>
      <div class="row"><button class="btn blue">Cambiar contraseña</button><span id="pw-msg" class="small"></span></div>
      <span class="muted small">Al cambiarla se cierran todas tus sesiones (también la del juego).</span>
    </form>`;

  const upload = async (input, path) => {
    const f = input.files[0];
    if (!f) return;
    $("#img-msg").className = "small";
    $("#img-msg").textContent = "Subiendo...";
    try {
      await api(path, { body: f, headers: { "Content-Type": f.type || "application/octet-stream" } });
      avatarVersion = Date.now();
      await loadMe();
      settings();
    } catch (err) { $("#img-msg").className = "small err"; $("#img-msg").textContent = err.message; }
  };
  $("#f-av").onchange = e => upload(e.target, "/api/me/avatar");
  $("#f-cv").onchange = e => upload(e.target, "/api/me/cover");
  $("#rm-av").onclick = async () => { await api("/api/me/avatar", { method: "DELETE" }); avatarVersion = Date.now(); await loadMe(); settings(); };
  $("#rm-cv").onclick = async () => { await api("/api/me/cover", { method: "DELETE" }); avatarVersion = Date.now(); settings(); };

  $("#pf").onsubmit = async e => {
    e.preventDefault();
    const f = new FormData(e.target);
    const msg = $("#pf-msg");
    try {
      await api("/api/me/profile", { json: {
        country: f.get("country"), title: f.get("title"), color: f.get("color"), location: f.get("location"), interests: f.get("interests"),
        occupation: f.get("occupation"), twitter: f.get("twitter"), discord: f.get("discord"), website: f.get("website"),
        playstyle: f.getAll("ps").join(","), bio: f.get("bio"),
      } });
      msg.className = "small ok"; msg.textContent = "¡Guardado!";
    } catch (err) { msg.className = "small err"; msg.textContent = err.message; }
  };
  $("#pw").onsubmit = async e => {
    e.preventDefault();
    const f = new FormData(e.target);
    const msg = $("#pw-msg");
    try {
      await api("/api/me/password", { json: { old: f.get("old"), new: f.get("new") } });
      setToken(null); state.me = null; drawMe();
      msg.className = "small ok"; msg.textContent = "Contraseña cambiada. Vuelve a iniciar sesión.";
      openLogin(false);
    } catch (err) { msg.className = "small err"; msg.textContent = err.message; }
  };
}

async function maps(params) {
  const q = params.get("q") || "";
  const list = await api("/api/maps?q=" + encodeURIComponent(q));
  app.innerHTML = `
    <div class="page-title"><span class="ico">♪</span> mapas jugados en este servidor</div>
    <div class="card">
      <form class="row" id="mq"><input class="spacer" style="background:var(--bg);border:1px solid var(--line);border-radius:8px;padding:8px 12px" name="q" value="${esc(q)}" placeholder="Buscar por título o dificultad"><button class="btn">Buscar</button></form>
      ${list.length ? `<table><tr><th>Mapa</th><th class="hide-sm">Modo</th><th class="num">Estrellas</th><th class="num">Partidas</th><th class="hide-sm">Mejor</th><th class="hide-sm">Estado</th></tr>
        ${list.map(m => `<tr><td><a href="#/b/${esc(m.hash)}">${esc(m.title)}</a> <span class="muted small">[${esc(m.version)}]</span></td>
          <td class="hide-sm">${MODES[m.mode]}</td><td class="num">${m.stars.toFixed(2)}★</td><td class="num">${m.plays}</td>
          <td class="hide-sm">${userLink(m.top)}</td><td class="hide-sm"><span class="pill">${esc(m.status)}</span></td></tr>`).join("")}</table>`
        : '<div class="empty">No hay mapas todavía.</div>'}
    </div>`;
  $("#mq").onsubmit = e => { e.preventDefault(); location.hash = "#/maps?q=" + encodeURIComponent(new FormData(e.target).get("q")); };
}

async function beatmap(hash, params) {
  const m = await api("/api/maps/" + encodeURIComponent(hash));
  const mode = params.has("mode") ? +params.get("mode") : m.boards[0]?.mode ?? 0;
  const board = m.boards.find(b => b.mode === mode) || { scores: [] };
  app.innerHTML = `
    <div class="page-title"><span class="ico">♪</span> ${esc(m.title)} <span class="muted">[${esc(m.version)}]</span></div>
    <div class="card">
      <div class="row"><span class="pill">${esc(m.status)}</span><span>${m.stars.toFixed(2)}★</span><span class="muted">${m.plays} partidas</span>
        <span class="spacer"></span><div class="tabs" style="margin:0;border:0">${m.boards.map(b => `<a class="${b.mode === mode ? "on" : ""}" href="#/b/${esc(hash)}?mode=${b.mode}">${MODES[b.mode]}</a>`).join("")}</div></div>
      <table><tr><th></th><th>Rango</th><th>Jugador</th><th class="num">Puntuación</th><th class="num">Precisión</th><th class="num">Combo</th><th class="num hide-sm">Mods</th><th class="num">pp</th></tr>
        ${board.scores.map((s, i) => `<tr><td class="rankno">#${i + 1}</td><td><b style="color:${GRADE_COL[s.rank] || "#aaa"}">${esc(s.rank.replace("H", ""))}</b></td>
          <td>${userLink(s.player)}</td><td class="num">${num(s.points)}</td><td class="num">${pct(s.accuracy)}</td><td class="num">${num(s.combo)}x</td>
          <td class="num hide-sm">${mods(s.mods)}</td><td class="num">${Math.round(s.pp)}</td></tr>`).join("")}</table>
    </div>`;
}

let timer = null;
async function multi() {
  const draw = async () => {
    const rooms = await api("/api/rooms");
    if (!location.hash.startsWith("#/multi")) return;
    app.innerHTML = `
      <div class="page-title"><span class="ico">⚔</span> multijugador</div>
      <div class="card"><h2>Partidas abiertas</h2><p class="muted small">Únete desde el juego: Play &gt; Multi. La lista se actualiza sola.</p>
        ${rooms.length ? `<table><tr><th>Partida</th><th class="hide-sm">Anfitrión</th><th>Mapa</th><th class="num">Jugadores</th><th>Estado</th></tr>
          ${rooms.map(r => `<tr><td>${r.hasPassword ? "🔒 " : ""}<b>${esc(r.name)}</b><div class="muted small">${MODES[r.mode]}</div></td><td class="hide-sm">${userLink(r.host)}</td>
            <td>${r.title ? esc(r.title) + ` <span class="muted small">[${esc(r.version)}]</span>` : '<span class="muted">sin mapa</span>'}</td>
            <td class="num">${r.players}/${r.max}<div class="muted small">${r.names.map(esc).join(", ")}</div></td>
            <td><span class="pill">${r.state === "playing" ? "en curso" : "abierta"}</span></td></tr>`).join("")}</table>`
          : '<div class="empty">No hay partidas ahora. ¡Crea una desde el juego!</div>'}</div>`;
  };
  await draw();
  timer = setInterval(() => draw().catch(() => { }), 3000);
}

async function chat(params) {
  const channels = await api("/api/channels");
  let pms = state.me ? await api("/api/me/pms") : [];
  let current = "osu", since = 0;
  const pmWith = params.get("pm");
  if (pmWith && state.me) {
    const r = await api("/api/chat/pm/" + pmWith, { method: "POST" });
    if (!pms.some(p => p.key === r.key)) pms.push({ key: r.key, withId: r.withId, withName: r.withName });
    current = r.key;
  }
  app.innerHTML = `
    <div class="page-title"><span class="ico">✉</span> chat</div>
    <div class="chat">
      <div class="chans" id="chans"></div>
      <div class="msgs"><div class="log" id="log"></div>
        <form class="send" id="send"><input id="msg" maxlength="300" autocomplete="off" placeholder="${state.me ? "Escribe un mensaje... (!roll, !online, !help)" : "Inicia sesión para escribir"}" ${state.me ? "" : "disabled"}>
        <button class="btn" ${state.me ? "" : "disabled"}>Enviar</button></form></div>
    </div>`;
  const drawChans = () => {
    $("#chans").innerHTML = '<div class="h">Canales</div>' + channels.map(c => `<a href="#" data-k="${c.key}" class="${c.key === current ? "on" : ""}" title="${esc(c.topic)}">${esc(c.name)}</a>`).join("")
      + (pms.length ? '<div class="h">Mensajes privados</div>' + pms.map(p => `<a href="#" data-k="${p.key}" class="${p.key === current ? "on" : ""}">${esc(p.withName)}</a>`).join("") : "");
    $("#chans").querySelectorAll("a").forEach(a => a.onclick = e => { e.preventDefault(); current = a.dataset.k; since = 0; $("#log").innerHTML = ""; drawChans(); poll(); });
  };
  const poll = async () => {
    try {
      const key = current;
      const list = await api(`/api/chat/${encodeURIComponent(key)}?since=${since}`);
      if (key !== current || !$("#log")) return;
      const log = $("#log"), atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 30;
      for (const m of list) {
        since = Math.max(since, m.id);
        const d = document.createElement("div");
        const t = new Date(m.time).toLocaleTimeString("es", { hour: "2-digit", minute: "2-digit" });
        d.innerHTML = `<span class="tm">${t}</span>${m.userId ? userLink(m.user) : `<b class="bot">${esc(m.user)}</b>`}: ${esc(m.text)}`;
        log.appendChild(d);
      }
      if (!log.children.length) log.innerHTML = `<div class="muted">${esc(channels.find(c => c.key === key)?.topic || "Conversación privada")}. Todavía no hay mensajes.</div>`;
      if (atBottom || list.length) log.scrollTop = log.scrollHeight;
      if (state.me) {
        const fresh = await api("/api/me/pms");
        if (fresh.length !== pms.length) { pms = fresh; drawChans(); }
      }
    } catch { /* server restarting: try again on the next tick */ }
  };
  drawChans();
  await poll();
  timer = setInterval(poll, 2000);
  $("#send").onsubmit = async e => {
    e.preventDefault();
    const text = $("#msg").value.trim();
    if (!text) return;
    $("#msg").value = "";
    try { await api(`/api/chat/${encodeURIComponent(current)}`, { json: { text } }); poll(); }
    catch (err) { $("#log").insertAdjacentHTML("beforeend", `<div class="err">${esc(err.message)}</div>`); }
  };
}

// ---------- router ----------

async function route() {
  clearInterval(timer);
  timer = null;
  const hash = location.hash || "#/";
  const [path, query] = hash.slice(1).split("?");
  const params = new URLSearchParams(query || "");
  const parts = path.split("/").filter(Boolean);
  document.querySelectorAll("#nav a").forEach(a => a.classList.toggle("on", a.getAttribute("href") === "#/" + (parts[0] || "")));
  window.scrollTo(0, 0);
  try {
    switch (parts[0]) {
      case undefined: await home(); document.title = "open!bancho"; break;
      case "rankings": await rankings(params); document.title = "Clasificaciones · open!bancho"; break;
      case "u": await profile(decodeURIComponent(parts[1] || ""), params); document.title = decodeURIComponent(parts[1] || "") + " · open!bancho"; break;
      case "settings": await settings(); document.title = "Ajustes · open!bancho"; break;
      case "maps": await maps(params); document.title = "Mapas · open!bancho"; break;
      case "b": await beatmap(parts[1] || "", params); document.title = "Mapa · open!bancho"; break;
      case "multi": await multi(); document.title = "Multijugador · open!bancho"; break;
      case "chat": await chat(params); document.title = "Chat · open!bancho"; break;
      case "login": openLogin(false); await home(); break;
      default: app.innerHTML = '<div class="card empty">Esta página no existe.</div>';
    }
  } catch (err) {
    app.innerHTML = `<div class="card empty err">${esc(err.message)}</div>`;
  }
}

window.addEventListener("hashchange", route);
loadMe().then(route);
