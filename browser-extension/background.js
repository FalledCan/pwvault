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
const ENTRY_PREFIX = "pwvault-entry:";
const CONTEXTS = ["editable"];
const PAGES = ["http://*/*", "https://*/*"];
const IS_FIREFOX = typeof globalThis.browser !== "undefined" && !!api.runtime.getBrowserInfo;

// 直近に作ったメニューの状態（テストと重複更新の抑制に使う）
const state = { url: null, error: null, entries: [], builtAt: 0 };
let queue = Promise.resolve();

// ---------------------------------------------------------------- PwVault 本体との通信

async function send(message) {
  try {
    const response = await api.runtime.sendNativeMessage(HOST, message);
    return response ?? { ok: false, error: "no_response" };
  } catch {
    // ホスト未登録（ブラウザ連携が無効）など
    return { ok: false, error: "not_installed" };
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
      for (const e of entries.slice(0, 10)) {
        const title = e.username ? `${e.title}（${e.username}）` : e.title;
        await create({ id: ENTRY_PREFIX + e.id, parentId: ROOT, title: label(title) });
      }
    } else {
      await create({
        id: "pwvault-info", parentId: ROOT, enabled: false,
        title: response.ok ? "このサイトのエントリはありません" : errorText(response.error),
      });
    }
    await create({ id: "pwvault-sep", parentId: ROOT, type: "separator" });
    await create({ id: REFRESH, parentId: ROOT, title: "候補を更新" });

    Object.assign(state, { url, error: response.ok ? null : response.error, entries, builtAt: Date.now() });
  }).catch((e) => console.warn("PwVault: メニューを更新できませんでした", e));
  return queue;
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

async function handleClick(info, tab) {
  if (info.menuItemId === REFRESH) return rebuild(tab?.url, { force: true });
  if (typeof info.menuItemId !== "string" || !info.menuItemId.startsWith(ENTRY_PREFIX) || !tab) return;

  const id = info.menuItemId.slice(ENTRY_PREFIX.length);
  const frameUrl = info.frameUrl || info.pageUrl || tab.url;
  if (!isWebUrl(frameUrl)) return toast(tab.id, "このページでは使えません。");

  const response = await send({ type: "fill", url: frameUrl, id });
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
globalThis.__pwvault = { state, rebuild, handleClick, fillCredentials };
