// PwVault 自動入力 — Chrome / Edge / Firefox 共通のバックグラウンドスクリプト
//
// 入力欄の右クリックメニューに、表示中のサイトに合う PwVault のエントリを並べる。
// 選ぶとユーザーIDとパスワードをその欄のフォームに入力する。
// PwVault 本体とはネイティブメッセージング（PC 内の標準入出力）でのみ通信し、ネットワークは使わない。
"use strict";

const api = globalThis.browser ?? globalThis.chrome;
const menus = api.menus ?? api.contextMenus;
const HOST = "com.pwvault.native";
const ROOT = "pwvault-root";
const REFRESH = "pwvault-refresh";
const OPEN = "pwvault-open";
const ENTRY_PREFIX = "pwvault-entry:";
const OTP_PREFIX = "pwvault-otp:";
const CONTEXTS = ["editable"];
const PAGES = ["http://*/*", "https://*/*"];
const IS_FIREFOX = typeof globalThis.browser !== "undefined" && !!api.runtime.getBrowserInfo;

// 直近に作ったメニューの状態（テストと重複更新の抑制に使う）
const state = { url: null, error: null, entries: [], builtAt: 0 };
let queue = Promise.resolve();

// ---------------------------------------------------------------- PwVault 本体との通信

async function send(message) {
  let response;
  try {
    response = await api.runtime.sendNativeMessage(HOST, message);
  } catch {
    // ホスト未登録（ブラウザ連携が無効）など
    return { ok: false, error: "not_installed" };
  }
  // 入力の途中で読み込み直すと入力できなくなるので、fill のときは見送る（次の一覧の更新で行う）
  if (message.type !== "fill") void reloadIfOutdated(response?.extensionVersion);
  return response ?? { ok: false, error: "no_response" };
}

/**
 * PwVault 本体に同梱の拡張の版が、いま動いている自分の版と違えば読み込み直す。
 * フォルダから読み込んだ拡張は、PwVault の更新でファイルが新しくなっても自動では読み直されないため。
 * ファイルがまだ古いままだと読み込み直しが繰り返されるので、同じ版については 1 回だけにする。
 */
async function reloadIfOutdated(bundledVersion) {
  if (!bundledVersion || bundledVersion === api.runtime.getManifest().version) return;
  try {
    const { reloadedFor } = await api.storage.local.get("reloadedFor");
    if (reloadedFor === bundledVersion) return;
    await api.storage.local.set({ reloadedFor: bundledVersion });
    api.runtime.reload();
  } catch (e) {
    console.warn("PwVault: 拡張機能を読み込み直せませんでした", e);
  }
}

const isWebUrl = (url) => /^https?:\/\//i.test(url ?? "");

// ---------------------------------------------------------------- メニュー

function create(props) {
  return new Promise((resolve) => menus.create({ contexts: CONTEXTS, documentUrlPatterns: PAGES, ...props }, () => {
    void api.runtime.lastError; // 同じ ID の二重作成などは無視する
    resolve();
  }));
}

function label(text) {
  // Firefox では & がアクセスキーの印になるので二重にする
  return IS_FIREFOX ? text.replaceAll("&", "&&") : text;
}

function errorText(error) {
  switch (error) {
    case "locked": return "🔒 PwVault がロック中です（アンロック後に「候補を更新」）";
    case "not_running": return "PwVault が起動していません";
    case "not_installed": return "PwVault の設定で「ブラウザ連携」を有効にしてください";
    case "unsupported": return "このページでは使えません";
    default: return "PwVault と通信できませんでした";
  }
}

/** 表示中のページに合わせてメニューを作り直す。同時に走らないよう直列化する。 */
function rebuild(url, { force = false } = {}) {
  queue = queue.then(async () => {
    if (!force && url === state.url && !state.error && Date.now() - state.builtAt < 2000) return;

    const response = isWebUrl(url) ? await send({ type: "list", url }) : { ok: false, error: "unsupported" };
    await menus.removeAll();
    await create({ id: ROOT, title: "PwVault" });

    const entries = response.ok ? response.entries ?? [] : [];
    if (entries.length > 0) {
      const shown = entries.slice(0, 10);
      for (const e of shown) {
        const title = e.username ? `${e.title}（${e.username}）` : e.title;
        await create({ id: ENTRY_PREFIX + e.id, parentId: ROOT, title: label(title) });
      }
      // ワンタイムパスワードを設定しているエントリは、2 段階認証の欄に入れる項目も出す
      const withOtp = shown.filter((e) => e.totp);
      if (withOtp.length > 0) {
        await create({ id: "pwvault-otp-sep", parentId: ROOT, type: "separator" });
        for (const e of withOtp) {
          const name = e.username ? `${e.title}（${e.username}）` : e.title;
          await create({ id: OTP_PREFIX + e.id, parentId: ROOT, title: label(`🔢 ワンタイムパスワード: ${name}`) });
        }
      }
    } else if (response.error === "locked" || response.error === "not_running") {
      // 押すと PwVault のアンロック画面を前に出す（起動していなければ起動する）。
      // マスターパスワードはブラウザには入力させず、PwVault 本体に入力してもらう
      await create({
        id: OPEN, parentId: ROOT,
        title: response.error === "locked" ? "🔒 PwVault をアンロックする…" : "PwVault を起動してアンロックする…",
      });
    } else {
      await create({
        id: "pwvault-info", parentId: ROOT, enabled: false,
        title: response.ok ? "このサイトのエントリはありません" : errorText(response.error),
      });
    }
    await create({ id: "pwvault-sep", parentId: ROOT, type: "separator" });
    await create({ id: REFRESH, parentId: ROOT, title: "候補を更新" });

    Object.assign(state, { url, error: response.ok ? null : response.error, entries, builtAt: Date.now() });
    if (entries.length > 0) void sendIcon(url);
  }).catch((e) => console.warn("PwVault: メニューを更新できませんでした", e));
  return queue;
}

// ---------------------------------------------------------------- サイトのアイコン

// このバックグラウンドの寿命の間に渡し終えたオリジン（同じアイコンを何度も送らない）
const sentIcons = new Set();

function toBase64(bytes) {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

/**
 * 保存済みエントリがあるサイトについて、タブのアイコンを PwVault に渡す（一覧の表示用）。
 * Chrome / Edge はブラウザ内のアイコンのキャッシュ（_favicon）から読むので通信は発生しない。
 * Firefox はタブのアイコンが data: URL のときだけ渡す（それ以外は PwVault が各サイトから取得する）。
 */
async function sendIcon(url) {
  try {
    const origin = new URL(url).origin;
    if (sentIcons.has(origin)) return;
    const [tab] = await api.tabs.query({ active: true, lastFocusedWindow: true });
    if (!tab || tab.url !== url || !tab.favIconUrl) return; // アイコンがまだ読み込まれていない

    let icon = null;
    if (/^data:image\/(png|x-icon|vnd\.microsoft\.icon|jpeg|gif|webp|bmp)[;,]/i.test(tab.favIconUrl)) {
      icon = tab.favIconUrl;
    } else if (!IS_FIREFOX) {
      const faviconUrl = api.runtime.getURL(`/_favicon/?pageUrl=${encodeURIComponent(url)}&size=32`);
      const response = await fetch(faviconUrl);
      if (!response.ok) return;
      const blob = await response.blob();
      if (blob.size === 0 || blob.size > 48 * 1024) return;
      icon = `data:${blob.type || "image/png"};base64,${toBase64(new Uint8Array(await blob.arrayBuffer()))}`;
    }
    if (!icon) return;

    const result = await send({ type: "icon", url, icon });
    if (result.ok) sentIcons.add(origin);
  } catch {
    // アイコンは無くても困らないので黙って諦める
  }
}

async function rebuildForActiveTab(options) {
  const [tab] = await api.tabs.query({ active: true, lastFocusedWindow: true });
  if (tab) await rebuild(tab.url, options);
}

// ---------------------------------------------------------------- 入力（ページ内で実行する関数）

/**
 * ページ内で実行する。右クリックされた入力欄（＝フォーカス中の欄）を起点に、
 * 同じフォームのユーザーID欄とパスワード欄を探して入力する。
 * React などの独自の入力管理にも反映されるよう、ネイティブの setter で値を入れて input/change を発火する。
 */
function fillCredentials(username, password, expectedOrigin) {
  // メニューを選んでから入力までの間に別サイトへ移っていたら入れない
  if (location.origin !== expectedOrigin) return { ok: false, reason: "origin" };

  const deepActive = () => {
    let el = document.activeElement;
    while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
    return el;
  };
  const visible = (el) => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const usable = (el) => el instanceof HTMLInputElement && !el.disabled && !el.readOnly && visible(el);
  const isPassword = (el) => usable(el) && el.type === "password";
  const isUserField = (el) => usable(el) && ["text", "email", "tel"].includes(el.type);
  const setValue = (el, value) => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
    el.focus();
    setter.call(el, value);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  };

  const active = deepActive();
  // 右クリックした欄がユーザーID欄かパスワード欄のときだけ動く（検索欄などでは何もしない）
  if (!isPassword(active) && !isUserField(active)) return { ok: false, reason: "field" };

  // 相方の欄を探す範囲。フォーム内ならそのフォームだけ。フォーム外なら、親をたどって
  // 相方（ID 欄ならパスワード欄、パスワード欄なら ID 欄）が見つかる最小の範囲の「フォーム外の欄」だけ。
  // こうすることで、同じページにある別のログインフォームに入力してしまうのを防ぐ。
  const form = active.form || active.closest("form");
  let inputs;
  if (form) {
    inputs = [...form.querySelectorAll("input")].filter(usable);
  } else {
    const wantsPartner = isPassword(active) ? isUserField : isPassword;
    const sameKind = isPassword(active) ? isPassword : isUserField;
    const formless = (root) => [...root.querySelectorAll("input")].filter((el) => usable(el) && !el.form && !el.closest("form"));
    inputs = [active];
    for (let node = active.parentElement; node; node = node.parentElement) {
      const candidates = formless(node);
      // 同じ種類の別の欄が入ってきたら、別のログイン欄の塊まで広がった（どれが相方か曖昧）ので止める
      if (candidates.some((el) => el !== active && sameKind(el))) break;
      if (candidates.some((el) => el !== active && wantsPartner(el))) { inputs = candidates; break; }
    }
    if (inputs.length === 1 && active.getRootNode() instanceof ShadowRoot) {
      const candidates = formless(active.getRootNode());
      if (candidates.some((el) => el !== active && wantsPartner(el))) inputs = candidates;
    }
  }

  const pw = isPassword(active) ? active : inputs.find(isPassword) ?? null;
  let user = null;
  if (isUserField(active)) {
    user = active;
  } else {
    const index = inputs.indexOf(pw);
    user = inputs.slice(0, index).reverse().find(isUserField) ?? null;
  }

  const filled = [];
  if (user && username) { setValue(user, username); filled.push("username"); }
  if (pw && password) { setValue(pw, password); filled.push("password"); }
  if (filled.length > 0) (pw ?? user).focus();
  return { ok: filled.length > 0, filled };
}

/**
 * ページ内で実行する。右クリックされた欄（＝フォーカス中の欄）にワンタイムパスワードを入れる。
 * 1 文字ずつの欄が並んでいる形（6 個の枠など）なら、続く欄に 1 文字ずつ入れる。
 */
function fillOtp(code, expectedOrigin) {
  if (location.origin !== expectedOrigin) return { ok: false, reason: "origin" };

  let active = document.activeElement;
  while (active && active.shadowRoot && active.shadowRoot.activeElement) active = active.shadowRoot.activeElement;
  const visible = (el) => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const usable = (el) => el instanceof HTMLInputElement && !el.disabled && !el.readOnly && visible(el) &&
    ["text", "tel", "number", "password", ""].includes(el.type);
  const setValue = (el, value) => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
    el.focus();
    setter.call(el, value);
    el.dispatchEvent(new Event("input", { bubbles: true }));
    el.dispatchEvent(new Event("change", { bubbles: true }));
  };
  if (!usable(active)) return { ok: false, reason: "field" };

  if (active.maxLength === 1) {
    // 1 文字ずつの欄: 右クリックした欄を含む、ちょうど桁数ぶんの 1 文字欄の並び（近い親の中）に、先頭から入れる。
    // 桁数より多く並んでいたら（別の欄の並びまで広がった）どれか分からないので入れない
    for (let node = active.parentElement, depth = 0; node && depth < 4; node = node.parentElement, depth++) {
      const boxes = [...node.querySelectorAll("input")].filter((el) => usable(el) && el.maxLength === 1);
      if (boxes.length < code.length) continue;
      if (boxes.length > code.length) break;
      [...code].forEach((ch, i) => setValue(boxes[i], ch));
      boxes[code.length - 1].focus();
      return { ok: true };
    }
  }
  // 桁数より短い欄（1 文字欄が足りないなど）には入れない
  if (active.maxLength > 0 && active.maxLength < code.length) return { ok: false, reason: "field" };
  setValue(active, code);
  return { ok: true };
}

/** ページ内に短い通知を出す（失敗時の案内）。 */
function showToast(message) {
  const host = document.createElement("div");
  host.style.cssText = "position:fixed;z-index:2147483647;right:16px;bottom:16px;";
  const root = host.attachShadow({ mode: "closed" });
  const box = document.createElement("div");
  box.textContent = "PwVault: " + message;
  box.style.cssText = "font:14px/1.5 system-ui,sans-serif;color:#fff;background:#333;padding:10px 14px;border-radius:8px;box-shadow:0 4px 16px rgba(0,0,0,.3);max-width:360px;";
  root.append(box);
  document.documentElement.append(host);
  setTimeout(() => host.remove(), 4000);
}

async function toast(tabId, message) {
  try {
    await api.scripting.executeScript({ target: { tabId }, func: showToast, args: [message] });
  } catch { /* 通知すら出せないページ（ブラウザの内部ページなど）は諦める */ }
}

/**
 * PwVault のアンロック画面を前に出し、アンロックされるまで様子を見て、されたらメニューを作り直す
 * （利用者がブラウザに戻って右クリックしたときには候補が並んでいるように）。
 */
let watching = null;
async function openAndWatch(tab) {
  const response = await send({ type: "open" });
  if (!response.ok) return toast(tab.id, errorText(response.error));
  if (response.unlocked) return rebuild(tab.url, { force: true });

  if (watching) clearInterval(watching);
  const started = Date.now();
  watching = setInterval(async () => {
    const status = await send({ type: "status" });
    if (status.ok && status.unlocked) {
      clearInterval(watching);
      watching = null;
      await rebuild(tab.url, { force: true });
    } else if (Date.now() - started > 120_000) {
      clearInterval(watching); // 2 分待ってもアンロックされなければやめる
      watching = null;
    }
  }, 1500);
}

async function handleClick(info, tab) {
  if (info.menuItemId === REFRESH) return rebuild(tab?.url, { force: true });
  if (info.menuItemId === OPEN && tab) return openAndWatch(tab);
  if (typeof info.menuItemId === "string" && info.menuItemId.startsWith(OTP_PREFIX) && tab) return handleOtpClick(info, tab);
  if (typeof info.menuItemId !== "string" || !info.menuItemId.startsWith(ENTRY_PREFIX) || !tab) return;

  const id = info.menuItemId.slice(ENTRY_PREFIX.length);
  const frameUrl = info.frameUrl || info.pageUrl || tab.url;
  if (!isWebUrl(frameUrl)) return toast(tab.id, "このページでは使えません。");

  const response = await send({ type: "fill", url: frameUrl, id });
  if (!response.ok && response.error === "locked") {
    // メニューを出した後に自動ロックされていた。アンロック画面を出す
    await toast(tab.id, "PwVault がロックされたので、アンロック画面を開きました。アンロック後にもう一度選んでください。");
    return openAndWatch(tab);
  }
  if (!response.ok) {
    const message = response.error === "no_match"
      ? "このエントリはこのサイト用ではないため入力しませんでした。"
      : errorText(response.error);
    return toast(tab.id, message);
  }

  try {
    const [result] = await api.scripting.executeScript({
      target: { tabId: tab.id, frameIds: [info.frameId ?? 0] },
      func: fillCredentials,
      args: [response.username ?? "", response.password ?? "", new URL(frameUrl).origin],
    });
    if (!result?.result?.ok) await toast(tab.id, "入力欄が見つかりませんでした。");
  } catch {
    await toast(tab.id, "この入力欄には入力できませんでした（別サイトの埋め込み枠など）。");
  }
}

/** 「ワンタイムパスワード: …」を選んだとき。コードは PwVault 本体が作り、キーはブラウザに渡らない。 */
async function handleOtpClick(info, tab) {
  const id = info.menuItemId.slice(OTP_PREFIX.length);
  const frameUrl = info.frameUrl || info.pageUrl || tab.url;
  if (!isWebUrl(frameUrl)) return toast(tab.id, "このページでは使えません。");

  const response = await send({ type: "otp", url: frameUrl, id });
  if (!response.ok && response.error === "locked") {
    await toast(tab.id, "PwVault がロックされたので、アンロック画面を開きました。アンロック後にもう一度選んでください。");
    return openAndWatch(tab);
  }
  if (!response.ok || !response.code) {
    const message = response.error === "no_match"
      ? "このエントリはこのサイト用ではないため入力しませんでした。"
      : errorText(response.error);
    return toast(tab.id, message);
  }

  try {
    const [result] = await api.scripting.executeScript({
      target: { tabId: tab.id, frameIds: [info.frameId ?? 0] },
      func: fillOtp,
      args: [response.code, new URL(frameUrl).origin],
    });
    if (!result?.result?.ok) await toast(tab.id, "ワンタイムパスワードの入力欄で右クリックしてください。");
  } catch {
    await toast(tab.id, "この入力欄には入力できませんでした（別サイトの埋め込み枠など）。");
  }
}

// ---------------------------------------------------------------- イベント

menus.onClicked.addListener((info, tab) => { void handleClick(info, tab); });

// Chrome / Edge ではメニューを開いた瞬間に内容を変えられないので、タブの切り替え・読み込み・
// ウィンドウへのフォーカス（PwVault をアンロックしてブラウザに戻ったとき）に先回りして更新する。
api.runtime.onInstalled.addListener(() => { void rebuildForActiveTab({ force: true }); });
api.runtime.onStartup.addListener(() => { void rebuildForActiveTab({ force: true }); });
api.tabs.onActivated.addListener(async ({ tabId }) => {
  const tab = await api.tabs.get(tabId);
  void rebuild(tab.url);
});
api.tabs.onUpdated.addListener((tabId, change, tab) => {
  if (tab.active && (change.url || change.status === "complete")) void rebuild(tab.url);
  // アイコンは読み込み完了より後に届くことがある。候補のあるページなら渡す
  if (tab.active && change.favIconUrl && tab.url === state.url && state.entries.length > 0) void sendIcon(tab.url);
});
api.windows.onFocusChanged.addListener((windowId) => {
  if (windowId !== api.windows.WINDOW_ID_NONE) void rebuildForActiveTab({ force: true });
});

// Firefox はメニューを開いたときに内容を差し替えられる
if (menus.onShown && menus.refresh) {
  menus.onShown.addListener(async (info) => {
    if (!info.menuIds.includes(ROOT)) return;
    await rebuild(info.frameUrl || info.pageUrl, { force: true });
    menus.refresh();
  });
}

// E2E テスト用の入口（テストが DevTools から呼ぶ）
globalThis.__pwvault = { state, rebuild, handleClick, fillCredentials, fillOtp, openAndWatch };
