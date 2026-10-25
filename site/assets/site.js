// NativeNCA site. Plain JS, no dependencies. One script for all languages:
// the page's <html lang> picks the strings, <html data-root> points to the shared files.
"use strict";

const CONFIG = {
  repo: "DonnerTechDev/NativeNCA",
  // Donation options shown in the donate dialog. Fill in real links; entries with an
  // empty url are hidden. With a single entry the button opens it directly.
  donate: [
    { title: "Kaspi", url: "", icon: "heart",
      note: { ru: "Перевод на карту или по номеру", en: "Card or phone transfer in Kazakhstan", kk: "Картаға немесе нөмір бойынша аударым" } },
    { title: "Boosty", url: "", icon: "heart",
      note: { ru: "Разово или по подписке", en: "One-off or monthly", kk: "Бір рет немесе жазылым арқылы" } },
    { title: "GitHub Sponsors", url: "", icon: "github",
      note: { ru: "Для тех, у кого есть зарубежная карта", en: "With any international card", kk: "Шетелдік картасы барларға" } },
  ],
};

const I18N = {
  ru: {
    locale: "ru-RU", sec: "с", mb: "МБ",
    meta: (v, beta, arch, size) => `Версия ${v}${beta ? " (бета)" : ""} · ${arch} · ${size}`,
    onlyWindows: (v) => `Версия ${v} · только для Windows`,
    forArch: (a) => `Для ${a}`, whatsNew: "Что нового",
    again: "Ещё раз",
    shot: (t, dark) => `Скриншот NativeNCA: ${t}${dark ? ", тёмная тема" : ""}`,
    star: "Поставить звезду на GitHub", starNote: "Ссылки для донатов скоро появятся",
    console: "Код сайта и программы:",
    race: [
      "> Запуск виртуальной машины Java…",
      "> Разогреваем JVM. Можно пока заварить чай.",
      "> Выберите файл ключа:\n  AUTH_RSA256_9c4f….p12\n  RSA256_9c4f….p12\n  GOST512_7e21….p12\n  Какой из них? Угадайте.",
      "> Введите пароль. Тот, что на стикере под клавиатурой.",
      "! Неверный пароль. Раскладка была KZ.",
      "> Введите пароль ещё раз.",
      "> Проверяем обновления NCALayer…",
      "> Подписываем… 99%",
      "> Подписываем… 99%",
      "> Готово. Если сайт не понял, обновите страницу.",
    ],
  },
  en: {
    locale: "en-US", sec: "s", mb: "MB",
    meta: (v, beta, arch, size) => `Version ${v}${beta ? " (beta)" : ""} · ${arch} · ${size}`,
    onlyWindows: (v) => `Version ${v} · Windows only`,
    forArch: (a) => `For ${a}`, whatsNew: "What's new",
    again: "Again",
    shot: (t, dark) => `NativeNCA screenshot: ${t}${dark ? ", dark theme" : ""}`,
    star: "Star it on GitHub", starNote: "Donation links are coming soon",
    console: "Source of the site and the app:",
    race: [
      "> Starting the Java virtual machine…",
      "> Warming up the JVM. Go make some tea.",
      "> Choose the key file:\n  AUTH_RSA256_9c4f….p12\n  RSA256_9c4f….p12\n  GOST512_7e21….p12\n  Which one? Take a guess.",
      "> Enter the password. The one on the sticky note under the keyboard.",
      "! Wrong password. The keyboard was on the Kazakh layout.",
      "> Enter the password again.",
      "> Checking for NCALayer updates…",
      "> Signing… 99%",
      "> Signing… 99%",
      "> Done. If the site didn't get it, reload the page.",
    ],
  },
  kk: {
    locale: "kk-KZ", sec: "с", mb: "МБ",
    meta: (v, beta, arch, size) => `Нұсқа ${v}${beta ? " (бета)" : ""} · ${arch} · ${size}`,
    onlyWindows: (v) => `Нұсқа ${v} · тек Windows үшін`,
    forArch: (a) => `${a} үшін`, whatsNew: "Не өзгерді",
    again: "Тағы бір рет",
    shot: (t, dark) => `NativeNCA скриншоты: ${t}${dark ? ", күңгірт тақырып" : ""}`,
    star: "GitHub-та жұлдызша қою", starNote: "Демеу сілтемелері жақында пайда болады",
    console: "Сайт пен бағдарламаның коды:",
    race: [
      "> Java виртуалды машинасы іске қосылуда…",
      "> JVM қызып жатыр. Әзірге шай қойып алыңыз.",
      "> Кілт файлын таңдаңыз:\n  AUTH_RSA256_9c4f….p12\n  RSA256_9c4f….p12\n  GOST512_7e21….p12\n  Қайсысы екен? Табыңыз.",
      "> Құпиясөзді енгізіңіз. Пернетақтаның астындағы стикерде жазылған.",
      "! Құпиясөз қате. Пернетақта KZ тілінде тұр екен.",
      "> Құпиясөзді қайта енгізіңіз.",
      "> NCALayer жаңартулары тексерілуде…",
      "> Қол қойылуда… 99%",
      "> Қол қойылуда… 99%",
      "> Дайын. Сайт түсінбесе, бетті жаңартыңыз.",
    ],
  },
};

const LANG = I18N[document.documentElement.lang] ? document.documentElement.lang : "ru";
const T = I18N[LANG];
const ROOT = document.documentElement.dataset.root || "";

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const fmt = new Intl.NumberFormat(T.locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

/* ---------- Theme ---------- */
(function theme() {
  const root = document.documentElement;
  const mq = matchMedia("(prefers-color-scheme: dark)");
  const apply = (t) => root.setAttribute("data-theme", t);
  $("#theme-toggle").addEventListener("click", () => {
    const next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
    apply(next);
    try { localStorage.setItem("theme", next); } catch (e) {}
  });
  mq.addEventListener("change", (e) => {
    let saved = null;
    try { saved = localStorage.getItem("theme"); } catch (err) {}
    if (!saved) apply(e.matches ? "dark" : "light");
  });
})();

/* ---------- App bar: border on scroll, active section ---------- */
(function appbar() {
  const bar = $("#appbar");
  const onScroll = () => bar.classList.toggle("scrolled", scrollY > 8);
  addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  const links = $$(".nav a");
  const map = new Map(links.map((a) => [a.getAttribute("href").slice(1), a]));
  const io = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      links.forEach((a) => a.classList.remove("active"));
      map.get(e.target.id)?.classList.add("active");
    }
  }, { rootMargin: "-45% 0px -50% 0px" });
  map.forEach((_, id) => { const el = document.getElementById(id); if (el) io.observe(el); });
})();

/* ---------- Flyout menus: sections on narrow screens, language ---------- */
(function flyouts() {
  const pairs = $$("[data-flyout]").map((btn) => [btn, document.getElementById(btn.dataset.flyout)]);
  const set = (btn, fly, open) => {
    fly.hidden = !open;
    btn.setAttribute("aria-expanded", open);
    if (open) fly.querySelector("a").focus({ preventScroll: true });
  };
  const closeAll = (except) => pairs.forEach(([b, f]) => { if (f !== except && !f.hidden) set(b, f, false); });

  pairs.forEach(([btn, fly]) => {
    btn.addEventListener("click", (e) => { e.stopPropagation(); closeAll(fly); set(btn, fly, fly.hidden); });
    fly.addEventListener("click", (e) => { if (e.target.closest("a")) set(btn, fly, false); });
  });
  document.addEventListener("click", (e) => { if (!e.target.closest(".menu-anchor")) closeAll(); });
  document.addEventListener("keydown", (e) => {
    const open = pairs.find(([, f]) => !f.hidden);
    if (!open) return;
    const [btn, fly] = open;
    const items = $$("a", fly), i = items.indexOf(document.activeElement);
    if (e.key === "Escape") { set(btn, fly, false); btn.focus(); }
    if (e.key === "ArrowDown") { items[(i + 1) % items.length].focus(); e.preventDefault(); }
    if (e.key === "ArrowUp") { items[(i - 1 + items.length) % items.length].focus(); e.preventDefault(); }
  });
  addEventListener("resize", () => {
    const nav = document.getElementById("nav-flyout");
    if (nav && !nav.hidden && matchMedia("(min-width: 1101px)").matches) closeAll();
  });

  // Keep the section you are reading when switching language.
  $$("[data-lang-link]").forEach((a) => a.addEventListener("click", () => {
    if (location.hash) a.href = a.getAttribute("href").split("#")[0] + location.hash;
  }));
})();

/* ---------- Reveal on scroll ---------- */
(function reveal() {
  const els = $$(".reveal");
  if (reduceMotion || !("IntersectionObserver" in window)) { els.forEach((e) => e.classList.add("in")); return; }
  const io = new IntersectionObserver((entries) => {
    entries.forEach((e) => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } });
  }, { rootMargin: "0px 0px -8% 0px" });
  els.forEach((e, i) => { e.style.transitionDelay = (i % 4) * 50 + "ms"; io.observe(e); });
})();

/* ---------- Download: latest GitHub release ---------- */
// The build already put a direct link to the newest x64 zip into the buttons, so a click
// downloads at once. Here we only switch ARM64 visitors to their zip and pick up a
// release that came out after the last build.
(async function download() {
  const releasesPage = `https://github.com/${CONFIG.repo}/releases`;

  async function detectArch() {
    try {
      if (navigator.userAgentData?.getHighEntropyValues) {
        const v = await navigator.userAgentData.getHighEntropyValues(["architecture", "bitness"]);
        if (v.architecture === "arm") return "arm64";
      }
    } catch (e) {}
    return "x64";
  }
  const isWindows = () =>
    (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent).toLowerCase().includes("win");

  async function getJson(url, ms) {
    const ctl = new AbortController();
    const t = setTimeout(() => ctl.abort(), ms);
    try {
      const r = await fetch(url, { signal: ctl.signal, headers: { Accept: "application/vnd.github+json" } });
      if (!r.ok) throw new Error(r.status);
      return await r.json();
    } finally { clearTimeout(t); }
  }

  // Newest stable release; a pre-release only if nothing stable exists yet.
  const pick = (list) => {
    const ok = (list || []).filter((r) => !r.draft && r.assets?.some((a) => a.name.endsWith(".zip")));
    return ok.find((r) => !r.prerelease) || ok[0] || null;
  };

  let release = null;
  // Our own copy first (same origin, no rate limit), then the API for anything newer.
  try { release = pick(await getJson(ROOT + "release.json", 3000)); } catch (e) {}
  try {
    const fresh = pick(await getJson(`https://api.github.com/repos/${CONFIG.repo}/releases?per_page=10`, 4000));
    if (fresh) release = fresh;
  } catch (e) {}

  const baked = (a) => /\.zip$/i.test(a.getAttribute("href") || "");
  if (!release) {
    $$("[data-download]").forEach((a) => { if (!baked(a)) a.href = releasesPage; });
    return;
  }

  const arch = await detectArch();
  const zip = (rid) => release.assets.find((a) => a.name.endsWith(`-${rid}.zip`));
  const main = zip(`win-${arch}`) || zip("win-x64") || release.assets.find((a) => a.name.endsWith(".zip"));
  const otherRid = main.name.includes("arm64") ? "win-x64" : "win-arm64";
  const other = zip(otherRid);
  const sha = release.assets.find((a) => a.name === main.name + ".sha256");
  const version = release.tag_name.replace(/^v/, "");
  const label = main.name.includes("arm64") ? "ARM64" : "x64";

  $$("[data-download]").forEach((a) => { a.href = main.browser_download_url; });
  $$("[data-dl-meta]").forEach((m) => {
    m.textContent = isWindows()
      ? T.meta(version, release.prerelease, label, `${fmt.format(main.size / 1048576)} ${T.mb}`)
      : T.onlyWindows(version);
  });

  const parts = [];
  if (other) parts.push(`<a href="${other.browser_download_url}" data-download-alt>${T.forArch(otherRid.endsWith("arm64") ? "ARM64" : "x64")}</a>`);
  if (sha) parts.push(`<a href="${sha.browser_download_url}">SHA-256</a>`);
  parts.push(`<a href="${release.html_url}">${T.whatsNew}</a>`);
  $$("[data-dl-alt]").forEach((el) => { el.innerHTML = parts.join(" · "); el.hidden = false; });
})();

/* Toast after a download click */
(function toast() {
  const t = $("#toast");
  let timer;
  const hide = () => t.classList.remove("show");
  document.addEventListener("click", (e) => {
    const a = e.target.closest("[data-download], [data-download-alt]");
    if (!a || !/\.zip$/i.test(a.getAttribute("href") || "")) return;
    t.classList.add("show");
    clearTimeout(timer);
    timer = setTimeout(hide, 9000);
  });
  $("#toast-close").addEventListener("click", hide);
})();

/* ---------- The race: NCALayer parody vs NativeNCA ---------- */
(function race() {
  const oldLog = $("#old-log"), oldBar = $("#old-bar"), oldBtn = $("#old-run"), tOld = $("#t-old");
  const win = $("#new-win"), newBtn = $("#new-run"), tNew = $("#t-new");
  let oldBusy = false, newBusy = false;
  const secs = (ms) => `${fmt.format(ms / 1000)} ${T.sec}`;

  function stopwatch(el) {
    const start = performance.now();
    el.className = "timer";
    const id = setInterval(() => { el.textContent = secs(performance.now() - start); }, 100);
    return (cls) => {
      clearInterval(id);
      el.textContent = secs(performance.now() - start);
      el.classList.add(cls);
    };
  }

  // [delay before the line, progress %, css class]. About 18 seconds in total,
  // closer to a real first signature in NCALayer than the old 10.
  const timing = [[1600, 8], [2300, 22], [2300, 38], [2400, 52], [1700, 52, "err"],
                  [1600, 64], [1400, 80], [1200, 99], [3000, 99], [1000, 100]];

  async function runOld() {
    if (oldBusy) return;
    oldBusy = true; oldBtn.disabled = true;
    oldLog.textContent = ""; oldBar.style.width = "0%";
    const stop = stopwatch(tOld);
    for (let i = 0; i < timing.length; i++) {
      const [ms, pct, cls] = timing[i];
      await sleep(reduceMotion ? 150 : ms);
      const line = document.createElement("div");
      line.textContent = T.race[i];
      if (cls) line.className = cls;
      oldLog.append(line);
      oldLog.scrollTop = oldLog.scrollHeight;
      oldBar.style.width = pct + "%";
    }
    stop("done-bad");
    oldBusy = false; oldBtn.disabled = false; oldBtn.textContent = T.again;
  }

  async function runNew() {
    if (newBusy) return;
    newBusy = true;
    const stop = stopwatch(tNew);
    win.dataset.state = "hello";
    await sleep(reduceMotion ? 150 : 850);
    win.dataset.state = "done";
    stop("done-good");
    newBusy = false;
    $("#new-reset").focus({ preventScroll: true });
  }

  oldBtn.addEventListener("click", runOld);
  newBtn.addEventListener("click", runNew);
  $("#new-reset").addEventListener("click", () => {
    win.dataset.state = "idle"; tNew.textContent = secs(0); tNew.className = "timer";
    newBtn.focus({ preventScroll: true });
  });
  $("#race").addEventListener("click", () => {
    if (win.dataset.state === "done") { win.dataset.state = "idle"; }
    runOld(); setTimeout(runNew, 250);
  });
})();

/* ---------- Screenshot gallery ---------- */
(function gallery() {
  const tabs = $$("#shots-tabs [role=tab]");
  const light = $("#shot-light"), dark = $("#shot-dark");
  const title = $("#shot-title"), text = $("#shot-text");
  const pips = $$("#shot-pips span");
  let index = 0;

  const frame = $("#shot-open");
  const src = (name, theme) => `${ROOT}screenshots/${LANG}/${name}-${theme}.webp`;
  const currentTheme = () => document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
  let token = 0;

  function show(i, focus) {
    index = (i + tabs.length) % tabs.length;
    const t = tabs[index];
    tabs.forEach((b, k) => { b.setAttribute("aria-selected", k === index); b.tabIndex = k === index ? 0 : -1; });
    pips.forEach((p, k) => p.classList.toggle("on", k === index));
    title.textContent = t.dataset.title;
    text.textContent = t.dataset.text;
    if (focus) t.focus();

    // Hide the old picture at once, swap, and fade the new one in once it is decoded,
    // so a slow connection never shows the previous tab's screenshot under the new caption.
    const my = ++token;
    frame.classList.add("loading");
    const name = t.dataset.shot;
    const [w, h] = "portrait" in t.dataset ? [900, 1089] : [1600, 1090];
    for (const [img, theme] of [[light, "light"], [dark, "dark"]]) {
      img.width = w; img.height = h;
      img.alt = T.shot(t.dataset.title, theme === "dark");
      img.src = src(name, theme);
    }
    const visible = currentTheme() === "dark" ? dark : light;
    const done = () => { if (my === token) frame.classList.remove("loading"); };
    (visible.decode ? visible.decode() : Promise.resolve()).then(done, done);
  }

  // Warm the cache for the other tabs once the gallery is near, so switching is instant.
  let warmed = false;
  const warm = () => {
    if (warmed) return; warmed = true;
    const theme = currentTheme(), other = theme === "dark" ? "light" : "dark";
    for (const th of [theme, other]) tabs.forEach((t) => { const im = new Image(); im.decoding = "async"; im.src = src(t.dataset.shot, th); });
  };
  if ("IntersectionObserver" in window) {
    const io = new IntersectionObserver((es) => { if (es.some((e) => e.isIntersecting)) { warm(); io.disconnect(); } }, { rootMargin: "800px 0px" });
    io.observe(frame);
  } else { warm(); }

  tabs.forEach((t, i) => t.addEventListener("click", () => show(i)));
  $("#shots-tabs").addEventListener("keydown", (e) => {
    if (e.key === "ArrowRight") { show(index + 1, true); e.preventDefault(); }
    if (e.key === "ArrowLeft") { show(index - 1, true); e.preventDefault(); }
  });
  $("#shot-prev").addEventListener("click", () => show(index - 1));
  $("#shot-next").addEventListener("click", () => show(index + 1));

  // Swipe on touch screens
  let x0 = null;
  const stage = $("#shots-panel");
  stage.addEventListener("touchstart", (e) => { x0 = e.touches[0].clientX; }, { passive: true });
  stage.addEventListener("touchend", (e) => {
    if (x0 === null) return;
    const dx = e.changedTouches[0].clientX - x0;
    if (Math.abs(dx) > 50) show(index + (dx < 0 ? 1 : -1));
    x0 = null;
  });

  // Lightbox
  const lb = $("#lightbox"), lbImg = $("#lightbox-img");
  frame.addEventListener("click", () => {
    lbImg.src = src(tabs[index].dataset.shot, currentTheme());
    lbImg.alt = T.shot(tabs[index].dataset.title, false);
    lb.showModal();
  });
  lb.addEventListener("click", (e) => { if (e.target === lb || e.target.closest("[data-close]")) lb.close(); });
})();

/* ---------- Donate dialog ---------- */
(function donate() {
  const items = CONFIG.donate.filter((d) => d.url);
  const btn = $("#donate-open");
  const dlg = $("#donate-dialog");
  const list = $("#donate-list");
  const ext = '<svg class="icon" aria-hidden="true"><use href="#i-open"/></svg>';
  const note = (d) => (typeof d.note === "object" ? d.note[LANG] || d.note.ru : d.note) || "";

  if (items.length === 1) {
    btn.addEventListener("click", () => window.open(items[0].url, "_blank", "noopener"));
    return;
  }
  if (items.length === 0) {
    // No donation links configured yet: offer a star instead of a dead end.
    items.push({ title: T.star, note: T.starNote, url: `https://github.com/${CONFIG.repo}`, icon: "github" });
  }
  list.innerHTML = items.map((d) =>
    `<a class="donate-item" href="${d.url}" target="_blank" rel="noopener">
       <svg class="icon icon-24" aria-hidden="true"><use href="#i-${d.icon || "heart"}"/></svg>
       <span><b>${d.title}</b><small>${note(d)}</small></span>${ext}</a>`).join("");
  btn.addEventListener("click", () => dlg.showModal());
  dlg.addEventListener("click", (e) => { if (e.target === dlg || e.target.closest("[data-close]")) dlg.close(); });
})();

/* ---------- FAQ: one expander open at a time ---------- */
$$("#faq-list details").forEach((d) => d.addEventListener("toggle", () => {
  if (d.open) $$("#faq-list details[open]").forEach((o) => { if (o !== d) o.open = false; });
}));

/* Hi, curious one. */
console.log("%cNativeNCA", "font:600 20px 'Segoe UI';color:#005fb8", `\n${T.console} https://github.com/${CONFIG.repo}`);
