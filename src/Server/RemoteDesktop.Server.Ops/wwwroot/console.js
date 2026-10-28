// The console's whole client: sign-in, the overview's numbers, and node registration. Every decision that matters —
// what is fresh, what a counter means, who may register a node — is made on the server; this file formats and draws.

const state = {
  challenge: null,
  user: null,
  page: "overview",
  overview: null,
  nodes: [],
  sessions: [],
  alerts: [],
  rules: [],
  actions: [],
  users: [],
  devices: [],
  deviceCursor: null,
  timer: null,
};

const el = {
  loginView: document.getElementById("loginView"),
  appView: document.getElementById("appView"),
  loginForm: document.getElementById("loginForm"),
  passwordStep: document.getElementById("passwordStep"),
  totpStep: document.getElementById("totpStep"),
  totpHint: document.getElementById("totpHint"),
  username: document.getElementById("username"),
  password: document.getElementById("password"),
  totpCode: document.getElementById("totpCode"),
  error: document.getElementById("loginError"),
  loginButton: document.getElementById("loginButton"),
  totpButton: document.getElementById("totpButton"),
  backButton: document.getElementById("backButton"),
  userName: document.getElementById("userName"),
  userRole: document.getElementById("userRole"),
  avatar: document.getElementById("avatar"),
  logout: document.getElementById("logoutButton"),
  theme: document.getElementById("themeToggle"),
  clock: document.getElementById("clock"),
  consoleVersion: document.getElementById("consoleVersion"),
  pageTitle: document.getElementById("pageTitle"),
  pages: {
    overview: document.getElementById("page-overview"),
    sessions: document.getElementById("page-sessions"),
    alerts: document.getElementById("page-alerts"),
    actions: document.getElementById("page-actions"),
    audit: document.getElementById("page-audit"),
    devices: document.getElementById("page-devices"),
    nodes: document.getElementById("page-nodes"),
    users: document.getElementById("page-users"),
  },
  kpiDevices: document.getElementById("kpiDevices"),
  kpiSessions: document.getElementById("kpiSessions"),
  kpiBandwidth: document.getElementById("kpiBandwidth"),
  kpiAlerts: document.getElementById("kpiAlerts"),
  overviewStamp: document.getElementById("overviewStamp"),
  overviewNodes: document.getElementById("overviewNodes"),
  nodeCount: document.getElementById("nodeCount"),
  traffic: document.getElementById("trafficChart"),
  trafficEmpty: document.getElementById("trafficEmpty"),
  trafficWindow: document.getElementById("trafficWindow"),
  scrapeHint: document.getElementById("scrapeHint"),
  nodeTbody: document.getElementById("nodeTbody"),
  nodesEmpty: document.getElementById("nodesEmpty"),
  nodesHint: document.getElementById("nodesHint"),
  nodeFormCard: document.getElementById("nodeFormCard"),
  nodeForm: document.getElementById("nodeForm"),
  nodeName: document.getElementById("nodeName"),
  nodeRole: document.getElementById("nodeRole"),
  nodeUrl: document.getElementById("nodeUrl"),
  nodeRegion: document.getElementById("nodeRegion"),
  nodeSubmit: document.getElementById("nodeSubmit"),
  nodeError: document.getElementById("nodeError"),
  nodeDetailCard: document.getElementById("nodeDetailCard"),
  nodeDetailTitle: document.getElementById("nodeDetailTitle"),
  nodeDetailClose: document.getElementById("nodeDetailClose"),
  nodeDetailInfo: document.getElementById("nodeDetailInfo"),
  nodeMetricTbody: document.getElementById("nodeMetricTbody"),
  nodeMetricsEmpty: document.getElementById("nodeMetricsEmpty"),
  sessionTbody: document.getElementById("sessionTbody"),
  sessionsEmpty: document.getElementById("sessionsEmpty"),
  sessionsHint: document.getElementById("sessionsHint"),
  sessionDetailCard: document.getElementById("sessionDetailCard"),
  sessionDetailTitle: document.getElementById("sessionDetailTitle"),
  sessionDetailClose: document.getElementById("sessionDetailClose"),
  sessionEvents: document.getElementById("sessionEvents"),
  sessionDisconnect: document.getElementById("sessionDisconnect"),
  actionTbody: document.getElementById("actionTbody"),
  actionsEmpty: document.getElementById("actionsEmpty"),
  actionsHint: document.getElementById("actionsHint"),
  deviceNode: document.getElementById("deviceNode"),
  deviceSearch: document.getElementById("deviceSearch"),
  devicesHint: document.getElementById("devicesHint"),
  deviceTbody: document.getElementById("deviceTbody"),
  devicesEmpty: document.getElementById("devicesEmpty"),
  devicesMore: document.getElementById("devicesMore"),
  linkMix: document.getElementById("linkMix"),
  platformMix: document.getElementById("platformMix"),
  mixEmpty: document.getElementById("mixEmpty"),
  navAudit: document.getElementById("navAudit"),
  navUsers: document.getElementById("navUsers"),
  userTbody: document.getElementById("userTbody"),
  usersEmpty: document.getElementById("usersEmpty"),
  usersHint: document.getElementById("usersHint"),
  usersNotice: document.getElementById("usersNotice"),
  userFormCard: document.getElementById("userFormCard"),
  userForm: document.getElementById("userForm"),
  newUserName: document.getElementById("newUserName"),
  userPassword: document.getElementById("userPassword"),
  newUserRole: document.getElementById("newUserRole"),
  userSubmit: document.getElementById("userSubmit"),
  userError: document.getElementById("userError"),
  alertsHint: document.getElementById("alertsHint"),
  alertTbody: document.getElementById("alertTbody"),
  alertsEmpty: document.getElementById("alertsEmpty"),
  rulesHint: document.getElementById("rulesHint"),
  ruleTbody: document.getElementById("ruleTbody"),
  ruleForm: document.getElementById("ruleForm"),
  ruleName: document.getElementById("ruleName"),
  ruleKind: document.getElementById("ruleKind"),
  ruleMetric: document.getElementById("ruleMetric"),
  ruleMetricField: document.getElementById("ruleMetricField"),
  ruleDenominator: document.getElementById("ruleDenominator"),
  ruleDenominatorField: document.getElementById("ruleDenominatorField"),
  ruleComparator: document.getElementById("ruleComparator"),
  ruleThreshold: document.getElementById("ruleThreshold"),
  ruleFor: document.getElementById("ruleFor"),
  ruleSeverity: document.getElementById("ruleSeverity"),
  ruleSubmit: document.getElementById("ruleSubmit"),
  ruleError: document.getElementById("ruleError"),
  auditTbody: document.getElementById("auditTbody"),
  auditEmpty: document.getElementById("auditEmpty"),
  verifyAudit: document.getElementById("verifyAudit"),
  auditVerifyResult: document.getElementById("auditVerifyResult"),
};

const pageTitles = {
  overview: "总览",
  sessions: "会话监控",
  alerts: "告警中心",
  actions: "管理动作",
  audit: "审计日志",
  devices: "设备管理",
  nodes: "节点管理",
  users: "账号管理",
};

/* ---------------- theme ---------------- */

function applyTheme(theme) {
  document.body.classList.toggle("light", theme === "light");
  localStorage.setItem("ops-theme", theme);
  if (state.overview) {
    drawTraffic(state.overview.traffic);
  }
}

applyTheme(localStorage.getItem("ops-theme") === "light" ? "light" : "dark");
el.theme.addEventListener("click", () => {
  applyTheme(document.body.classList.contains("light") ? "dark" : "light");
});

/* ---------------- clock ---------------- */

function pad(value) {
  return String(value).padStart(2, "0");
}

function clockText(date) {
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

function tick() {
  const now = new Date();
  el.clock.textContent =
    `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${clockText(now)}`;
}

tick();
setInterval(tick, 1000);

/* ---------------- http ---------------- */

function csrfToken() {
  const entry = document.cookie.split("; ").find((part) => part.startsWith("ops_csrf="));
  return entry ? decodeURIComponent(entry.slice("ops_csrf=".length)) : "";
}

async function api(path, { method = "GET", body } = {}) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  if (method !== "GET" && method !== "HEAD") {
    headers["X-Ops-CSRF"] = csrfToken();
  }

  const response = await fetch(path, {
    method,
    headers,
    credentials: "same-origin",
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  // A 401 from the sign-in endpoints means "wrong credentials", not "your session ended"; letting it call
  // signedOut() would replace the real error message with "已退出".
  if (response.status === 401 && !path.startsWith("/api/auth/")) {
    signedOut();
    return { status: 401, ok: false, payload: null };
  }

  let payload = null;
  if (response.status !== 204) {
    payload = await response.json().catch(() => null);
  }
  return { status: response.status, ok: response.ok, payload };
}

/* ---------------- views ---------------- */

function setError(message) {
  el.error.textContent = message || "";
  el.error.hidden = !message;
}

function showLogin(message) {
  stopPolling();
  el.appView.hidden = true;
  el.loginView.hidden = false;
  el.passwordStep.hidden = false;
  el.totpStep.hidden = true;
  state.challenge = null;
  el.password.value = "";
  el.totpCode.value = "";
  setError(message);
  el.username.focus();
}

function showTotp(challenge, hint) {
  state.challenge = challenge;
  el.passwordStep.hidden = true;
  el.totpStep.hidden = false;
  el.totpHint.textContent = hint || "输入验证器上的 6 位动态码。";
  setError(null);
  el.totpCode.value = "";
  el.totpCode.focus();
}

function showApp(user) {
  state.user = user;
  el.loginView.hidden = true;
  el.appView.hidden = false;
  el.userName.textContent = user.name;
  el.userRole.textContent = user.role;
  el.avatar.textContent = user.name.slice(0, 1).toUpperCase();
  el.nodeFormCard.hidden = user.role !== "admin";
  el.ruleForm.hidden = user.role !== "admin";
  el.navAudit.hidden = user.role !== "admin";
  el.navUsers.hidden = user.role !== "admin";
  el.userFormCard.hidden = user.role !== "admin";

  switchPage("overview");
  startPolling();
}

function switchPage(page) {
  state.page = page;
  el.pageTitle.textContent = pageTitles[page] || page;
  for (const [name, section] of Object.entries(el.pages)) {
    section.hidden = name !== page;
  }
  document.querySelectorAll(".nav-item[data-page]").forEach((item) => {
    item.classList.toggle("active", item.dataset.page === page);
  });

  if (page === "nodes") {
    loadNodes();
  }
  if (page === "sessions") {
    loadSessions();
  }
  if (page === "alerts") {
    void loadRules();
    void loadAlerts();
    void loadMetrics();
  }
  if (page === "audit") {
    void loadAudit();
  }
  if (page === "actions") {
    void loadActions();
  }
  if (page === "users") {
    void loadUsers();
  }
  if (page === "devices") {
    void (async () => {
      const count = await loadDeviceNodes();
      if (count > 0) {
        await loadDevices(true);
      } else {
        el.devicesHint.textContent = "";
        renderDevices();
      }
    })();
  }
}

document.querySelectorAll(".nav-item[data-page]").forEach((item) => {
  item.addEventListener("click", () => switchPage(item.dataset.page));
});

/* ---------------- sign-in ---------------- */

function describeSignIn(status, payload) {
  if (status === 429) {
    const seconds = payload && payload.retryAfterSeconds ? payload.retryAfterSeconds : 0;
    return seconds > 0 ? `尝试次数过多，请 ${Math.ceil(seconds / 60)} 分钟后再试。` : "尝试次数过多，请稍后再试。";
  }
  if (status === 403) {
    return "该账号已被禁用，请联系另一位管理员。";
  }
  return "账号、口令或动态码不正确。";
}

el.loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  setError(null);

  if (state.challenge === null) {
    el.loginButton.disabled = true;
    try {
      const { status, payload } = await api("/api/auth/login", {
        method: "POST",
        body: { username: el.username.value, password: el.password.value },
      });

      if (status === 200 && payload.status === "totp-required") {
        showTotp(payload.challenge, "口令正确。输入验证器上的 6 位动态码。");
      } else if (status === 200 && payload.status === "signed-in") {
        showApp(payload.user);
      } else {
        setError(describeSignIn(status, payload));
      }
    } catch {
      setError("无法连接服务器。");
    } finally {
      el.loginButton.disabled = false;
    }
    return;
  }

  el.totpButton.disabled = true;
  try {
    const { status, payload } = await api("/api/auth/totp", {
      method: "POST",
      body: { challenge: state.challenge, code: el.totpCode.value.trim() },
    });

    if (status === 200 && payload.status === "signed-in") {
      showApp(payload.user);
    } else {
      setError(describeSignIn(status, payload));
      el.totpCode.select();
    }
  } catch {
    setError("无法连接服务器。");
  } finally {
    el.totpButton.disabled = false;
  }
});

el.backButton.addEventListener("click", () => showLogin(null));

el.logout.addEventListener("click", async () => {
  try {
    await api("/api/auth/logout", { method: "POST" });
  } finally {
    signedOut();
  }
});

function signedOut() {
  state.user = null;
  state.overview = null;
  showLogin("已退出。");
}

/* ---------------- formatting ---------------- */

function formatCount(value) {
  return typeof value === "number" ? Math.round(value).toLocaleString("zh-CN") : null;
}

function formatBytesPerSecond(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return null;
  }
  const units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
  let scaled = value;
  let unit = 0;
  while (scaled >= 1024 && unit < units.length - 1) {
    scaled /= 1024;
    unit++;
  }
  const digits = scaled >= 100 || unit === 0 ? 0 : scaled >= 10 ? 1 : 2;
  return `${scaled.toFixed(digits)} ${units[unit]}`;
}

function formatRelative(timestamp) {
  if (!timestamp) {
    return "从未";
  }
  const seconds = Math.max(0, Math.round((Date.now() - timestamp) / 1000));
  if (seconds < 60) {
    return `${seconds} 秒前`;
  }
  if (seconds < 3600) {
    return `${Math.round(seconds / 60)} 分钟前`;
  }
  return `${Math.round(seconds / 3600)} 小时前`;
}

function setKpi(node, text) {
  node.textContent = text ?? "—";
  node.classList.toggle("has-value", text !== null && text !== undefined);
}

/* ---------------- overview ---------------- */

async function loadOverview() {
  if (state.page !== "overview" || document.hidden || !state.user) {
    return;
  }

  const { status, payload } = await api("/api/overview");
  if (status !== 200 || !payload) {
    return;
  }

  state.overview = payload;
  renderOverview(payload);
}

function renderOverview(overview) {
  const kpis = overview.kpis;

  setKpi(el.kpiDevices, formatCount(kpis.devicesOnline));
  setKpi(el.kpiSessions, formatCount(kpis.relaySessions));
  setKpi(el.kpiBandwidth, formatBytesPerSecond(kpis.egressBytesPerSecond));
  setKpi(el.kpiAlerts, typeof kpis.alertsToday === "number" ? formatCount(kpis.alertsToday) : null);

  el.overviewStamp.textContent = overview.traffic.length === 0
    ? "等待第一次抓取"
    : `更新于 ${clockText(new Date(overview.generatedAt))}`;
  el.trafficWindow.textContent = `近 ${overview.windowMinutes} 分钟`;
  el.scrapeHint.textContent = "每 10 秒抓取一次";

  drawTraffic(overview.traffic);
  renderOverviewNodes(overview.nodes);
  renderMix(overview);
}

/* ---------------- link and platform split ---------------- */

const platformNames = { windows: "Windows", macos: "macOS", linux: "Linux" };

/** One labelled bar: a name, a proportional track and the count that produced it. */
function mixRow(label, count, total, tone) {
  const row = document.createElement("div");
  row.className = "mix-row";

  const name = document.createElement("span");
  name.className = "mix-label";
  name.textContent = label;

  const track = document.createElement("span");
  track.className = "mix-track";
  const fill = document.createElement("i");
  fill.className = `mix-fill ${tone || ""}`.trim();
  // CSSOM rather than a style attribute: the page's CSP refuses the latter, and this is the one value that has to be
  // computed.
  fill.style.width = total > 0 ? `${Math.round((count / total) * 100)}%` : "0";
  track.append(fill);

  const value = document.createElement("span");
  value.className = "mix-value";
  value.textContent = total > 0 ? `${count} · ${Math.round((count / total) * 100)}%` : "—";

  row.append(name, track, value);
  return row;
}

function renderMix(overview) {
  const direct = overview.directSessions ?? 0;
  const relayed = overview.relayedSessions ?? 0;
  const links = direct + relayed;
  const platforms = overview.platforms || [];
  const platformTotal = platforms.reduce((sum, entry) => sum + entry.sessions, 0);

  el.linkMix.replaceChildren();
  el.platformMix.replaceChildren();

  if (links > 0) {
    el.linkMix.append(mixRow("直连 P2P", direct, links));
    el.linkMix.append(mixRow("中继", relayed, links, "warn"));
  }

  // A relay-only window is worth drawing even when no host said which platform it runs.
  for (const entry of platforms) {
    el.platformMix.append(mixRow(platformNames[entry.platform] || entry.platform, entry.sessions, platformTotal, "accent2"));
  }

  el.mixEmpty.hidden = links > 0 || platforms.length > 0;
}

function renderOverviewNodes(nodes) {
  el.nodeCount.textContent = `${nodes.length} 个节点`;
  el.overviewNodes.replaceChildren();

  if (nodes.length === 0) {
    const empty = document.createElement("p");
    empty.className = "empty";
    empty.textContent = "还没有登记节点。到「节点管理」添加第一个。";
    el.overviewNodes.append(empty);
    return;
  }

  for (const node of nodes) {
    const row = document.createElement("div");
    row.className = "node-row";

    const dot = document.createElement("span");
    dot.className = `dot ${node.lastScrapeOk === true ? "ok" : node.lastScrapeOk === false ? "bad" : "unknown"}`;
    dot.title = node.lastScrapeOk === true ? "最近一次抓取成功" : node.lastScrapeOk === false ? "最近一次抓取失败" : "尚未抓取";

    const name = document.createElement("span");
    name.className = "node-name";
    name.textContent = node.name;

    const role = document.createElement("span");
    role.className = "node-role";
    role.textContent = node.role;

    const when = document.createElement("span");
    when.className = "node-when";
    when.textContent = node.enabled ? formatRelative(node.lastScrapeAt) : "已停用";

    row.append(dot, name, role, when);
    row.classList.toggle("disabled", !node.enabled);
    el.overviewNodes.append(row);
  }
}

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function drawTraffic(points) {
  const canvas = el.traffic;
  const rect = canvas.getBoundingClientRect();
  const width = Math.max(320, rect.width);
  const height = 200;
  const dpr = window.devicePixelRatio || 1;

  canvas.width = Math.round(width * dpr);
  canvas.height = Math.round(height * dpr);

  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, width, height);

  el.trafficEmpty.hidden = points.length > 1;

  const pad = { left: 56, right: 10, top: 12, bottom: 24 };
  const innerWidth = width - pad.left - pad.right;
  const innerHeight = height - pad.top - pad.bottom;

  const peak = Math.max(1, ...points.map((point) => Math.max(point.toHost, point.toController)));
  const top = peak * 1.15;

  ctx.font = "10px Menlo, monospace";
  ctx.textAlign = "right";
  ctx.textBaseline = "middle";

  for (let line = 0; line <= 3; line++) {
    const y = pad.top + (innerHeight * line) / 3;
    ctx.strokeStyle = cssVar("--border-soft");
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(pad.left, y);
    ctx.lineTo(width - pad.right, y);
    ctx.stroke();

    ctx.fillStyle = cssVar("--text3");
    ctx.fillText(formatBytesPerSecond(top * (1 - line / 3)), pad.left - 8, y);
  }

  if (points.length < 2) {
    return;
  }

  const x = (index) => pad.left + (innerWidth * index) / (points.length - 1);
  const y = (value) => pad.top + innerHeight * (1 - value / top);

  const series = [
    { key: "toHost", color: cssVar("--accent") },
    { key: "toController", color: cssVar("--accent2") },
  ];

  for (const line of series) {
    const gradient = ctx.createLinearGradient(0, pad.top, 0, pad.top + innerHeight);
    gradient.addColorStop(0, `${line.color}44`);
    gradient.addColorStop(1, `${line.color}00`);

    ctx.beginPath();
    points.forEach((point, index) => {
      const px = x(index);
      const py = y(point[line.key]);
      if (index === 0) {
        ctx.moveTo(px, py);
      } else {
        ctx.lineTo(px, py);
      }
    });
    ctx.lineTo(x(points.length - 1), pad.top + innerHeight);
    ctx.lineTo(x(0), pad.top + innerHeight);
    ctx.closePath();
    ctx.fillStyle = gradient;
    ctx.fill();

    ctx.beginPath();
    points.forEach((point, index) => {
      const px = x(index);
      const py = y(point[line.key]);
      if (index === 0) {
        ctx.moveTo(px, py);
      } else {
        ctx.lineTo(px, py);
      }
    });
    ctx.strokeStyle = line.color;
    ctx.lineWidth = 1.6;
    ctx.lineJoin = "round";
    ctx.stroke();
  }

  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  ctx.fillStyle = cssVar("--text3");
  const labelIndexes = [0, Math.floor((points.length - 1) / 2), points.length - 1];
  for (const index of labelIndexes) {
    const point = points[index];
    ctx.fillText(clockText(new Date(point.timestamp)).slice(0, 5), x(index), pad.top + innerHeight + 8);
  }
}

/* ---------------- alerts ---------------- */

async function loadAlerts() {
  const { status, payload } = await api("/api/alerts?limit=100");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  state.alerts = payload;
  el.alertsHint.textContent = payload.length === 0 ? "" : `${payload.filter((alert) => alert.active).length} 条未恢复`;
  renderAlerts();
}

function renderAlerts() {
  el.alertTbody.replaceChildren();
  el.alertsEmpty.hidden = state.alerts.length > 0;

  const canAck = state.user && state.user.role !== "viewer";

  for (const alert of state.alerts) {
    const row = document.createElement("tr");

    const severity = document.createElement("td");
    const pill = document.createElement("span");
    pill.className = `severity ${String(alert.severity).toLowerCase()}`;
    pill.textContent = alert.severity;
    severity.append(pill);

    const ruleCell = document.createElement("td");
    const rule = state.rules.find((candidate) => candidate.id === alert.ruleId);
    ruleCell.textContent = rule ? rule.name : alert.ruleId;

    const value = document.createElement("td");
    value.className = "mono";
    value.textContent = alert.value === null || alert.value === undefined ? "—" : String(alert.value);

    const opened = document.createElement("td");
    opened.className = "mono";
    opened.textContent = formatDateTime(alert.openedAt);

    const statusCell = document.createElement("td");
    const status = document.createElement("span");
    status.textContent = alert.active ? (alert.ackedBy ? `已认领 · ${alert.ackedBy}` : "未处理") : "已恢复";
    status.className = alert.active ? (alert.ackedBy ? "state-muted" : "state-bad") : "state-ok";
    statusCell.append(status);

    const action = document.createElement("td");
    if (canAck && alert.active && !alert.ackedBy) {
      const ack = document.createElement("button");
      ack.className = "small";
      ack.type = "button";
      ack.textContent = "认领";
      ack.addEventListener("click", () => void ackAlert(alert.id));
      action.append(ack);
    }

    row.append(severity, ruleCell, value, opened, statusCell, action);
    el.alertTbody.append(row);
  }
}

async function ackAlert(id) {
  await api(`/api/alerts/${encodeURIComponent(id)}/ack`, { method: "POST" });
  await loadAlerts();
}

async function loadMetrics() {
  const { status, payload } = await api("/api/metrics");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  const options = payload.map((entry) => entry.metric);
  for (const select of [el.ruleMetric, el.ruleDenominator]) {
    select.replaceChildren();
    for (const metric of options) {
      const option = document.createElement("option");
      option.value = metric;
      option.textContent = metric;
      select.append(option);
    }
  }

  el.ruleMetricField.title = options.length === 0
    ? "还没有已抓取指标，节点抓取成功后这里会出现可选项。"
    : "";
  updateRuleFields();
}

function updateRuleFields() {
  const kind = el.ruleKind.value;
  const needsMetric = kind === "single";
  const isRatio = kind === "ratio";
  el.ruleMetricField.hidden = !needsMetric && !isRatio;
  el.ruleDenominatorField.hidden = !isRatio;
  el.ruleMetric.required = needsMetric || isRatio;
  el.ruleDenominator.required = isRatio;
}

async function loadRules() {
  const { status, payload } = await api("/api/settings/rules");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  state.rules = payload;
  el.rulesHint.textContent = `${payload.length} 条规则`;
  renderRules();
  renderAlerts();
}

function ruleCondition(rule) {
  const operator = rule.comparator === "lt" ? "<" : ">";
  if (rule.kind === "scrape") {
    return "最近一次抓取失败";
  }
  if (rule.kind === "ratio") {
    return `${rule.numerator} / ${rule.denominator} ${operator} ${rule.threshold}`;
  }
  return `${rule.metric} ${operator} ${rule.threshold}`;
}

function renderRules() {
  el.ruleTbody.replaceChildren();
  const isAdmin = state.user && state.user.role === "admin";

  for (const rule of state.rules) {
    const row = document.createElement("tr");

    const name = document.createElement("td");
    name.className = "mono";
    name.textContent = rule.name;

    const kind = document.createElement("td");
    kind.textContent = rule.kind === "scrape" ? "抓取失败" : rule.kind === "ratio" ? "比值" : "单指标";

    const condition = document.createElement("td");
    condition.className = "mono";
    condition.textContent = ruleCondition(rule);

    const duration = document.createElement("td");
    duration.className = "mono";
    duration.textContent = rule.forSeconds === 0 ? "立即" : `${rule.forSeconds} 秒`;

    const severity = document.createElement("td");
    const pill = document.createElement("span");
    pill.className = `severity ${String(rule.severity).toLowerCase()}`;
    pill.textContent = rule.severity;
    severity.append(pill);

    const enabled = document.createElement("td");
    enabled.textContent = rule.enabled ? "是" : "否";
    enabled.className = rule.enabled ? "state-ok" : "state-muted";

    const action = document.createElement("td");
    if (isAdmin) {
      const remove = document.createElement("button");
      remove.className = "small danger";
      remove.type = "button";
      remove.textContent = "删除";
      remove.addEventListener("click", () => void deleteRule(rule));
      action.append(remove);
    }

    row.append(name, kind, condition, duration, severity, enabled, action);
    el.ruleTbody.append(row);
  }
}

async function deleteRule(rule) {
  if (!window.confirm(`删除规则「${rule.name}」？`)) {
    return;
  }

  const { status } = await api(`/api/settings/rules/${encodeURIComponent(rule.id)}`, { method: "DELETE" });
  if (status === 204) {
    await loadRules();
  }
}

el.ruleKind.addEventListener("change", updateRuleFields);

el.ruleForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  el.ruleError.hidden = true;
  el.ruleSubmit.disabled = true;

  try {
    const kind = el.ruleKind.value;
    const { status, payload } = await api("/api/settings/rules", {
      method: "POST",
      body: {
        name: el.ruleName.value.trim(),
        kind,
        // Single-metric rules name one series; ratio rules name a numerator and a denominator. Session rules read
        // their own numbers, so a metric name would only be stored and ignored.
        metric: kind === "single" ? el.ruleMetric.value : null,
        numerator: kind === "ratio" ? el.ruleMetric.value : null,
        denominator: kind === "ratio" ? el.ruleDenominator.value : null,
        comparator: el.ruleComparator.value,
        threshold: Number(el.ruleThreshold.value),
        forSeconds: Number(el.ruleFor.value),
        severity: el.ruleSeverity.value,
        enabled: true,
      },
    });

    if (status === 200) {
      el.ruleForm.reset();
      await loadRules();
      return;
    }

    el.ruleError.textContent = payload && payload.message ? `保存失败：${payload.message}` : "保存失败。";
    el.ruleError.hidden = false;
  } catch {
    el.ruleError.textContent = "无法连接服务器。";
    el.ruleError.hidden = false;
  } finally {
    el.ruleSubmit.disabled = false;
  }
});

/* ---------------- audit ---------------- */

async function loadAudit() {
  const { status, payload } = await api("/api/audit?limit=100");
  if (status !== 200 || !payload) {
    return;
  }

  el.auditTbody.replaceChildren();
  el.auditEmpty.hidden = (payload.items || []).length > 0;

  for (const entry of payload.items || []) {
    const row = document.createElement("tr");
    const cells = [
      formatDateTime(entry.timestamp),
      entry.actor,
      entry.action,
      entry.target,
      entry.ip,
      entry.result,
    ];

    cells.forEach((text, index) => {
      const cell = document.createElement("td");
      cell.textContent = text;
      if (index === 0) {
        cell.className = "mono";
      }
      row.append(cell);
    });

    el.auditTbody.append(row);
  }
}

el.verifyAudit.addEventListener("click", async () => {
  el.auditVerifyResult.textContent = "校验中…";
  const { status, payload } = await api("/api/audit/verify");

  if (status !== 200 || !payload) {
    el.auditVerifyResult.textContent = "校验失败：无法读取审计链。";
    return;
  }

  el.auditVerifyResult.textContent = payload.valid
    ? `链完整（${payload.checkedEntries} 条）。`
    : `第一条不一致：${payload.firstInvalidId}`;
  el.auditVerifyResult.className = payload.valid ? "state-ok" : "state-bad";
});

/* ---------------- devices ---------------- */

function formatDateTime(milliseconds) {
  if (!milliseconds) {
    return "—";
  }
  const date = new Date(milliseconds);
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${clockText(date)}`;
}

async function loadDeviceNodes() {
  const { status, payload } = await api("/api/nodes");
  if (status !== 200 || !Array.isArray(payload)) {
    return 0;
  }

  const rendezvous = payload.filter((node) => node.enabled && node.role === "rendezvous");
  const previous = el.deviceNode.value;
  el.deviceNode.replaceChildren();

  for (const node of rendezvous) {
    const option = document.createElement("option");
    option.value = node.id;
    option.textContent = node.name;
    el.deviceNode.append(option);
  }

  if (previous && rendezvous.some((node) => node.id === previous)) {
    el.deviceNode.value = previous;
  }

  return rendezvous.length;
}

async function loadDevices(reset) {
  if (reset) {
    state.devices = [];
    state.deviceCursor = null;
  }

  const nodeId = el.deviceNode.value;
  if (!nodeId) {
    renderDevices();
    return;
  }

  let url = `/api/devices?limit=50&node=${encodeURIComponent(nodeId)}`;
  if (state.deviceCursor) {
    url += `&cursor=${encodeURIComponent(state.deviceCursor)}`;
  }

  const { status, payload } = await api(url);
  if (status !== 200 || !payload) {
    el.devicesHint.textContent = "节点不可达";
    renderDevices();
    return;
  }

  state.devices = reset ? payload.items : state.devices.concat(payload.items);
  state.deviceCursor = payload.nextCursor;
  el.devicesHint.textContent = `${state.devices.length} 个设备`;
  renderDevices();
}

function renderDevices() {
  const query = el.deviceSearch.value.replaceAll(" ", "").trim();
  const visible = query
    ? state.devices.filter((device) => String(device.deviceId).includes(query))
    : state.devices;

  el.deviceTbody.replaceChildren();
  el.devicesEmpty.hidden = visible.length > 0;
  el.devicesMore.hidden = !state.deviceCursor;

  for (const device of visible) {
    const row = document.createElement("tr");

    const id = document.createElement("td");
    id.className = "mono";
    id.textContent = String(device.deviceId);

    const statusCell = document.createElement("td");
    const status = document.createElement("span");
    status.textContent = device.revoked ? "已封禁" : device.online ? "在线" : "离线";
    status.className = device.revoked ? "state-bad" : device.online ? "state-ok" : "state-muted";
    statusCell.append(status);

    const firstSeen = document.createElement("td");
    firstSeen.className = "mono";
    firstSeen.textContent = formatDateTime(device.firstSeenUnixMilliseconds);

    const lastSeen = document.createElement("td");
    lastSeen.className = "mono";
    lastSeen.textContent = formatDateTime(device.lastSeenUnixMilliseconds);

    const actionCell = document.createElement("td");
    if (state.user && state.user.role === "admin") {
      const toggle = document.createElement("button");
      toggle.className = "small";
      toggle.type = "button";
      toggle.textContent = device.revoked ? "解封" : "封禁";
      toggle.addEventListener("click", () =>
        requestAction(device.revoked ? "unrevoke_device" : "revoke_device", el.deviceNode.value, String(device.deviceId)));
      actionCell.append(toggle);
    }

    row.append(id, statusCell, firstSeen, lastSeen, actionCell);
    el.deviceTbody.append(row);
  }
}

el.deviceSearch.addEventListener("input", renderDevices);
el.deviceNode.addEventListener("change", () => {
  void loadDevices(true);
});
el.devicesMore.addEventListener("click", () => {
  void loadDevices(false);
});

/* ---------------- sessions ---------------- */

async function loadSessions() {
  const { status, payload } = await api("/api/sessions?limit=50");
  if (status !== 200 || !payload) {
    return;
  }

  state.sessions = payload.items || [];
  renderSessions();
}

function formatDuration(milliseconds) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  if (seconds < 60) {
    return `${seconds} 秒`;
  }
  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`;
  }
  return `${Math.floor(seconds / 3600)} 时 ${Math.floor((seconds % 3600) / 60)} 分`;
}

function formatBytes(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "—";
  }
  const units = ["B", "KB", "MB", "GB", "TB"];
  let scaled = value;
  let unit = 0;
  while (scaled >= 1024 && unit < units.length - 1) {
    scaled /= 1024;
    unit++;
  }
  return `${scaled.toFixed(scaled >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function renderSessions() {
  const sessions = state.sessions || [];
  el.sessionTbody.replaceChildren();
  el.sessionsEmpty.hidden = sessions.length > 0;
  el.sessionsHint.textContent = sessions.length === 0 ? "" : `最近 ${sessions.length} 条`;

  for (const session of sessions) {
    const row = document.createElement("tr");

    const started = document.createElement("td");
    started.className = "mono";
    started.textContent = session.startedAt ? clockText(new Date(session.startedAt)) : "—";

    const devices = document.createElement("td");
    devices.className = "mono";
    devices.textContent = `${session.controllerDeviceId ?? "?"} → ${session.hostDeviceId ?? "?"}`;

    const transport = document.createElement("td");
    const pill = document.createElement("span");
    pill.className = "role-pill";
    pill.textContent = session.transport === "relay" ? "中继" : session.transport ?? "未知";
    transport.append(pill);

    const duration = document.createElement("td");
    duration.className = "mono";
    const end = session.endedAt ?? Date.now();
    duration.textContent = session.startedAt ? formatDuration(end - session.startedAt) : "—";

    const bytes = document.createElement("td");
    bytes.className = "mono";
    bytes.textContent = `${formatBytes(session.bytesIn)} / ${formatBytes(session.bytesOut)}`;

    // What a heartbeat added, if one arrived. No heartbeat means no measurement, and an em dash says exactly that.
    const quality = document.createElement("td");
    quality.className = "mono";
    const measured = [];
    if (session.platform) {
      measured.push(platformNames[session.platform] || session.platform);
    }
    if (typeof session.rttMs === "number") {
      measured.push(`${Math.round(session.rttMs)} ms`);
    }
    quality.textContent = measured.length > 0 ? measured.join(" · ") : "—";
    if (measured.length === 0) {
      quality.title = "客户端未上报（Telemetry:Enabled 默认关闭）";
    }

    const source = document.createElement("td");
    source.textContent = session.source;

    const statusCell = document.createElement("td");
    const status = document.createElement("span");
    status.textContent = session.active ? "进行中" : session.endReason || "已结束";
    status.className = session.active ? "state-ok" : "state-muted";
    statusCell.append(status);

    const action = document.createElement("td");
    const detail = document.createElement("button");
    detail.className = "small";
    detail.type = "button";
    detail.textContent = "详情";
    detail.addEventListener("click", () => showSessionDetail(session));
    action.append(detail);

    row.append(started, devices, transport, duration, bytes, quality, source, statusCell, action);
    el.sessionTbody.append(row);
  }
}

async function showSessionDetail(session) {
  const { status, payload } = await api(`/api/sessions/${encodeURIComponent(session.id)}`);
  if (status !== 200 || !payload) {
    return;
  }

  el.sessionDetailTitle.textContent =
    `会话 ${payload.session.controllerDeviceId ?? "?"} → ${payload.session.hostDeviceId ?? "?"}`;
  el.sessionEvents.replaceChildren();

  // A session can only be disconnected while it is active, and only when the console knows the host and the id the
  // host knows it by — neither is present for a signaling-only attempt that never reached a host.
  const canDisconnect = payload.session.active && payload.session.hostDeviceId && payload.session.clientSessionId;
  el.sessionDisconnect.hidden = !canDisconnect || !(state.user && state.user.role !== "viewer");
  el.sessionDisconnect.onclick = canDisconnect ? () => {
    void requestDisconnect(payload.session);
  } : null;

  for (const event of payload.events || []) {
    const item = document.createElement("div");
    item.className = "timeline-item";

    const time = document.createElement("span");
    time.className = "timeline-time";
    time.textContent = clockText(new Date(event.occurredAt));

    const kind = document.createElement("span");
    kind.className = "timeline-kind";
    kind.textContent = event.kind;

    const node = document.createElement("span");
    node.className = "timeline-node";
    node.textContent = event.node;

    const detail = document.createElement("span");
    detail.className = "timeline-payload";
    detail.textContent = typeof event.payload === "string" ? event.payload : "";

    item.append(time, kind, node, detail);
    el.sessionEvents.append(item);
  }

  el.sessionDetailCard.hidden = false;
  el.sessionDetailCard.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

el.sessionDetailClose.addEventListener("click", () => {
  el.sessionDetailCard.hidden = true;
});

/* ---------------- actions ---------------- */

const actionKindNames = {
  disconnect_session: "断开会话",
  revoke_device: "封禁设备",
  unrevoke_device: "解封设备",
};

/** Posts a management action (two-person review); the server decides who may ask and who may approve. */
async function requestAction(kind, nodeId, deviceId, sessionId) {
  const { status, payload } = await api("/api/actions", {
    method: "POST",
    body: { kind, nodeId, deviceId, sessionId: sessionId || null },
  });

  if (status !== 200) {
    const message = payload && payload.message ? payload.message : "发起失败";
    // eslint-disable-next-line no-alert
    window.alert(`无法发起：${message}`);
    return;
  }

  void loadActions();
}

async function requestDisconnect(session) {
  // The signaling node the host is on is not on the session row, so pick the first enabled rendezvous. With one
  // signaling server — the normal single-cluster deployment — that is the only choice there is.
  const node = await pickRendezvousNode();
  if (!node) {
    // eslint-disable-next-line no-alert
    window.alert("没有可用的信令节点，无法发起断开。");
    return;
  }

  await requestAction("disconnect_session", node.id, String(session.hostDeviceId), session.clientSessionId);
}

async function pickRendezvousNode() {
  if (state.nodes.length === 0) {
    await loadNodes();
  }

  return state.nodes.find((node) => node.enabled && node.role === "rendezvous") || null;
}

async function loadActions() {
  const { status, payload } = await api("/api/actions?limit=100");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  state.actions = payload;
  renderActions();
}

function renderActions() {
  const actions = state.actions || [];
  const isAdmin = state.user && state.user.role === "admin";

  el.actionTbody.replaceChildren();
  el.actionsEmpty.hidden = actions.length > 0;
  el.actionsHint.textContent = actions.length === 0 ? "" : `${actions.length} 条`;

  for (const action of actions) {
    const row = document.createElement("tr");

    const kind = document.createElement("td");
    kind.textContent = actionKindNames[action.kind] || action.kind;

    const device = document.createElement("td");
    device.className = "mono";
    device.textContent = action.deviceId;

    const requester = document.createElement("td");
    requester.textContent = action.requestedBy;

    const stateCell = document.createElement("td");
    const badge = document.createElement("span");
    badge.textContent = action.state;
    badge.className = action.state === "pending" ? "state-bad" : action.state === "failed" ? "state-bad" : "state-ok";
    stateCell.append(badge);

    const approver = document.createElement("td");
    approver.textContent = action.approvedBy || "—";

    const actionCell = document.createElement("td");
    if (action.state === "pending" && isAdmin && action.requestedBy !== state.user.name) {
      const approve = document.createElement("button");
      approve.className = "small";
      approve.type = "button";
      approve.textContent = "批准";
      approve.addEventListener("click", () => approveAction(action.id));
      actionCell.append(approve);
    }

    row.append(kind, device, requester, stateCell, approver, actionCell);
    el.actionTbody.append(row);
  }
}

async function approveAction(id) {
  const { status, payload } = await api(`/api/actions/${encodeURIComponent(id)}/approve`, { method: "POST" });
  if (status !== 200) {
    const message = payload && payload.message ? payload.message : "批准失败";
    // eslint-disable-next-line no-alert
    window.alert(`无法批准：${message}`);
    return;
  }

  void loadActions();
  void loadSessions();
}

/* ---------------- nodes ---------------- */

async function loadNodes() {
  const { status, payload } = await api("/api/nodes");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  state.nodes = payload;
  renderNodes();
}

function renderNodes() {
  const nodes = state.nodes;
  const isAdmin = state.user && state.user.role === "admin";

  el.nodeTbody.replaceChildren();
  el.nodesEmpty.hidden = nodes.length > 0;
  el.nodesHint.textContent = nodes.length === 0 ? "" : `共 ${nodes.length} 个`;

  for (const node of nodes) {
    const row = document.createElement("tr");

    const nameCell = document.createElement("td");
    nameCell.className = "mono";
    nameCell.textContent = node.name;

    const roleCell = document.createElement("td");
    const role = document.createElement("span");
    role.className = "role-pill";
    role.textContent = node.role;
    roleCell.append(role);

    const urlCell = document.createElement("td");
    urlCell.className = "mono";
    urlCell.textContent = node.baseUrl;

    const statusCell = document.createElement("td");
    const status = document.createElement("span");
    const ok = node.lastScrapeOk === true;
    status.textContent = !node.enabled ? "已停用" : node.lastScrapeOk === null ? "等待抓取" : ok ? "正常" : "抓取失败";
    status.className = !node.enabled || node.lastScrapeOk === null ? "state-muted" : ok ? "state-ok" : "state-bad";
    statusCell.append(status);

    const whenCell = document.createElement("td");
    whenCell.className = "mono";
    whenCell.textContent = formatRelative(node.lastScrapeAt);

    const actionCell = document.createElement("td");
    const actions = document.createElement("div");
    actions.className = "actions";

    const detail = document.createElement("button");
    detail.className = "small";
    detail.type = "button";
    detail.textContent = "详情";
    detail.addEventListener("click", () => showNodeDetail(node));
    actions.append(detail);

    if (isAdmin) {
      const edit = document.createElement("button");
      edit.className = "small";
      edit.type = "button";
      edit.textContent = "改地址";
      edit.addEventListener("click", () => editNode(node));
      actions.append(edit);

      const toggle = document.createElement("button");
      toggle.className = "small";
      toggle.type = "button";
      toggle.textContent = node.enabled ? "停用" : "启用";
      toggle.addEventListener("click", () => toggleNode(node));
      actions.append(toggle);

      const remove = document.createElement("button");
      remove.className = "small danger";
      remove.type = "button";
      remove.textContent = "删除";
      remove.addEventListener("click", () => deleteNode(node));
      actions.append(remove);
    }

    actionCell.append(actions);
    row.append(nameCell, roleCell, urlCell, statusCell, whenCell, actionCell);
    el.nodeTbody.append(row);
  }
}

async function showNodeDetail(node) {
  el.nodeDetailCard.hidden = false;
  el.nodeDetailTitle.textContent = `节点详情 · ${node.name}`;
  el.nodeDetailInfo.textContent = "正在读取…";
  el.nodeMetricTbody.replaceChildren();
  el.nodeMetricsEmpty.hidden = true;

  const { status, payload } = await api(`/api/nodes/${encodeURIComponent(node.id)}/detail`);
  if (status !== 200 || !payload) {
    el.nodeDetailInfo.textContent = "读取失败。";
    return;
  }

  const info = payload.info;
  if (info) {
    const started = info.startedAtUnixMilliseconds
      ? `${clockText(new Date(info.startedAtUnixMilliseconds))} · 已运行 ${formatDuration(info.uptimeSeconds * 1000)}`
      : "—";
    el.nodeDetailInfo.textContent = `${info.version || "未知版本"} · ${started}`;
  } else if (payload.infoError) {
    el.nodeDetailInfo.textContent = `节点信息不可用：${payload.infoError}`;
  } else {
    el.nodeDetailInfo.textContent = "";
  }

  const metrics = payload.metrics || [];
  el.nodeMetricsEmpty.hidden = metrics.length > 0;
  for (const point of metrics) {
    const row = document.createElement("tr");

    const name = document.createElement("td");
    name.className = "mono";
    name.textContent = point.metric;

    const value = document.createElement("td");
    value.className = "mono";
    value.textContent = formatMetricValue(point.value);

    const when = document.createElement("td");
    when.className = "mono";
    when.textContent = clockText(new Date(point.timestamp));

    row.append(name, value, when);
    el.nodeMetricTbody.append(row);
  }
}

function formatMetricValue(value) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return "—";
  }
  if (Number.isInteger(value) && Math.abs(value) < 1_000_000_000) {
    return String(value);
  }
  return value.toFixed(2);
}

async function editNode(node) {
  const baseUrl = window.prompt(`节点「${node.name}」的新基础地址（http:// 或 https://）`, node.baseUrl);
  if (baseUrl === null) {
    return;
  }

  const { status, payload } = await api(`/api/nodes/${encodeURIComponent(node.id)}`, {
    method: "PATCH",
    body: { baseUrl: baseUrl.trim() },
  });

  if (status === 204) {
    await loadNodes();
    await loadOverview();
    return;
  }

  window.alert(payload && payload.message ? payload.message : "修改失败。");
}

async function toggleNode(node) {
  const { status } = await api(`/api/nodes/${encodeURIComponent(node.id)}`, {
    method: "PATCH",
    body: { enabled: !node.enabled },
  });

  if (status === 204) {
    await loadNodes();
    await loadOverview();
  }
}

async function deleteNode(node) {
  if (!window.confirm(`删除节点「${node.name}」？它的历史读数会一起删除。`)) {
    return;
  }

  const { status } = await api(`/api/nodes/${encodeURIComponent(node.id)}`, { method: "DELETE" });
  if (status === 204) {
    await loadNodes();
    await loadOverview();
  }
}

el.nodeDetailClose.addEventListener("click", () => {
  el.nodeDetailCard.hidden = true;
});

el.nodeForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  el.nodeError.hidden = true;
  el.nodeSubmit.disabled = true;

  try {
    const { status, payload } = await api("/api/nodes", {
      method: "POST",
      body: {
        name: el.nodeName.value.trim(),
        role: el.nodeRole.value,
        baseUrl: el.nodeUrl.value.trim(),
        region: el.nodeRegion.value.trim() || null,
      },
    });

    if (status === 201) {
      el.nodeForm.reset();
      await loadNodes();
      await loadOverview();
      return;
    }

    el.nodeError.textContent = payload && payload.message ? `登记失败：${payload.message}` : "登记失败。";
    el.nodeError.hidden = false;
  } catch {
    el.nodeError.textContent = "无法连接服务器。";
    el.nodeError.hidden = false;
  } finally {
    el.nodeSubmit.disabled = false;
  }
});

/* ---------------- accounts ---------------- */

async function loadUsers() {
  const { status, payload } = await api("/api/users");
  if (status !== 200 || !Array.isArray(payload)) {
    return;
  }

  state.users = payload;
  renderUsers();
}

function setUsersNotice(message) {
  el.usersNotice.textContent = message || "";
  el.usersNotice.hidden = !message;
}

function renderUsers() {
  const users = state.users;
  const current = state.user ? state.user.name : "";

  el.userTbody.replaceChildren();
  el.usersEmpty.hidden = users.length > 0;
  el.usersHint.textContent = users.length === 0 ? "" : `共 ${users.length} 个`;

  for (const user of users) {
    const row = document.createElement("tr");

    const name = document.createElement("td");
    name.className = "mono";
    name.textContent = user.username;

    const roleCell = document.createElement("td");
    const role = document.createElement("span");
    role.className = "role-pill";
    role.textContent = user.role;
    roleCell.append(role);

    const statusCell = document.createElement("td");
    const status = document.createElement("span");
    status.textContent = user.disabled ? "已停用" : "正常";
    status.className = user.disabled ? "state-bad" : "state-ok";
    statusCell.append(status);

    const created = document.createElement("td");
    created.className = "mono";
    created.textContent = formatRelative(user.createdAt);

    const actionCell = document.createElement("td");
    const actions = document.createElement("div");
    actions.className = "actions";

    const password = document.createElement("button");
    password.className = "small";
    password.type = "button";
    password.textContent = "改密";
    password.addEventListener("click", () => resetUserPassword(user));
    actions.append(password);

    const totp = document.createElement("button");
    totp.className = "small";
    totp.type = "button";
    totp.textContent = "重置 TOTP";
    totp.addEventListener("click", () => resetUserTotp(user));
    actions.append(totp);

    if (user.username !== current) {
      const toggle = document.createElement("button");
      toggle.className = "small";
      toggle.type = "button";
      toggle.textContent = user.disabled ? "启用" : "停用";
      toggle.addEventListener("click", () => toggleUser(user));
      actions.append(toggle);

      const remove = document.createElement("button");
      remove.className = "small danger";
      remove.type = "button";
      remove.textContent = "删除";
      remove.addEventListener("click", () => deleteUser(user));
      actions.append(remove);
    }

    actionCell.append(actions);
    row.append(name, roleCell, statusCell, created, actionCell);
    el.userTbody.append(row);
  }
}

async function resetUserPassword(user) {
  const password = window.prompt(`为「${user.username}」设置新口令（至少 12 位）。\n如果这是当前账号，完成后需要重新登录。`);
  if (password === null) {
    return;
  }
  if (password.length < 12) {
    setUsersNotice("口令太短：至少 12 位。");
    return;
  }

  const confirmation = window.prompt("再输入一次新口令。");
  if (confirmation !== password) {
    setUsersNotice("两次输入不一致。");
    return;
  }

  const { status, payload } = await api(`/api/users/${encodeURIComponent(user.id)}/password`, {
    method: "POST",
    body: { password },
  });

  setUsersNotice(status === 204
    ? `「${user.username}」的口令已重置，它的所有会话已退出。`
    : payload && payload.message ? `重置失败：${payload.message}` : "重置失败。");

  if (status === 204) {
    await loadUsers();
  }
}

async function resetUserTotp(user) {
  if (!window.confirm(`为「${user.username}」生成新的 TOTP 密钥？旧的验证器条目将不能再登录。`)) {
    return;
  }

  const { status, payload } = await api(`/api/users/${encodeURIComponent(user.id)}/totp`, { method: "POST" });
  if (status !== 200 || !payload) {
    setUsersNotice(payload && payload.message ? `重置失败：${payload.message}` : "重置失败。");
    return;
  }

  if (user.username === state.user.name) {
    // Resetting your own secret revoked the session this page is using. Keep the once-only secret in front of the
    // operator before the page returns to the sign-in screen.
    window.alert(`新的 TOTP 密钥（只显示一次）：\n${payload.totpSecret}\n${payload.otpauthUri}\n\n当前会话已退出，请用新动态码重新登录。`);
    signedOut();
    return;
  }

  setUsersNotice(`「${user.username}」的新 TOTP 密钥（只显示一次）：${payload.totpSecret}\n${payload.otpauthUri}`);
  await loadUsers();
}

async function toggleUser(user) {
  if (!user.disabled && !window.confirm(`停用「${user.username}」？它的所有会话会立刻退出。`)) {
    return;
  }

  const { status, payload } = await api(`/api/users/${encodeURIComponent(user.id)}/disabled`, {
    method: "POST",
    body: { disabled: !user.disabled },
  });

  if (status !== 204) {
    setUsersNotice(payload && payload.message ? `操作失败：${payload.message}` : "操作失败。");
    return;
  }

  await loadUsers();
}

async function deleteUser(user) {
  if (!window.confirm(`删除账号「${user.username}」？这个操作不能撤销。`)) {
    return;
  }

  const { status, payload } = await api(`/api/users/${encodeURIComponent(user.id)}`, { method: "DELETE" });
  if (status !== 204) {
    setUsersNotice(payload && payload.message ? `删除失败：${payload.message}` : "删除失败。");
    return;
  }

  await loadUsers();
}

el.userForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  el.userError.hidden = true;
  el.userSubmit.disabled = true;
  setUsersNotice(null);

  try {
    const { status, payload } = await api("/api/users", {
      method: "POST",
      body: {
        username: el.newUserName.value.trim(),
        password: el.userPassword.value,
        role: el.newUserRole.value,
      },
    });

    if (status === 200 && payload) {
      el.userForm.reset();
      setUsersNotice(`账号「${payload.username}」已创建。TOTP 密钥（只显示一次）：${payload.totpSecret}\n${payload.otpauthUri}`);
      await loadUsers();
      return;
    }

    el.userError.textContent = payload && payload.message ? `创建失败：${payload.message}` : "创建失败。";
    el.userError.hidden = false;
  } catch {
    el.userError.textContent = "无法连接服务器。";
    el.userError.hidden = false;
  } finally {
    el.userSubmit.disabled = false;
  }
});

/* ---------------- polling ---------------- */

function startPolling() {
  stopPolling();
  loadOverview();
  state.timer = setInterval(() => {
    loadOverview();
    if (state.page === "nodes" && !document.hidden) {
      loadNodes();
    }
    if (state.page === "sessions" && !document.hidden) {
      loadSessions();
    }
  }, 5000);
}

function stopPolling() {
  if (state.timer) {
    clearInterval(state.timer);
    state.timer = null;
  }
}

document.addEventListener("visibilitychange", () => {
  if (!document.hidden && state.user) {
    loadOverview();
  }
});

/* ---------------- version ---------------- */

async function loadVersion() {
  if (!el.consoleVersion) {
    return;
  }

  try {
    const { status, payload } = await api("/api/version");
    if (status === 200 && payload && payload.version) {
      el.consoleVersion.textContent = `Console v${payload.version}`;
    }
  } catch {
    // The sidebar keeps "Console": a missing version is better than a made-up one.
  }
}

/* ---------------- start ---------------- */

(async () => {
  // Independent of sign-in: the sidebar is on the login screen too.
  loadVersion();

  try {
    const { status, payload } = await api("/api/me");
    if (status === 200 && payload) {
      showApp(payload);
      return;
    }
  } catch {
    // A console that cannot reach its own server is a login page, not a blank screen.
  }

  showLogin(null);
})();
