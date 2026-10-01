// Personal Activity Time — локальная интеграция.
// Отправляет активную вкладку (url + title) в локальный native messaging host.
// Ничего не уходит в сеть: соединение только с хостом на этой машине.

const HOST_NAME = "com.personalactivitytime.host";
const DEDUP_MS = 1500;

let port = null;
let lastUrl = null;
let lastTitle = null;
let lastSentAt = 0;

function connect() {
  if (port) return;
  try {
    port = chrome.runtime.connectNative(HOST_NAME);
    port.onDisconnect.addListener(() => { port = null; });
  } catch (e) {
    port = null;
  }
}

function send(tab) {
  if (!tab || !tab.url) return;
  const now = Date.now();
  const title = tab.title || "";
  if (tab.url === lastUrl && title === lastTitle && now - lastSentAt < DEDUP_MS) return;
  lastUrl = tab.url;
  lastTitle = title;
  lastSentAt = now;

  connect();
  if (!port) return;
  try {
    port.postMessage({ url: tab.url, title });
  } catch (e) {
    port = null;
  }
}

function sendActiveTab() {
  chrome.tabs.query({ active: true, lastFocusedWindow: true }, (tabs) => {
    if (tabs && tabs[0]) send(tabs[0]);
  });
}

// Переключение вкладки
chrome.tabs.onActivated.addListener(() => sendActiveTab());

// Навигация или смена заголовка на активной вкладке
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab && tab.active) send(tab);
});

// Переключение окна браузера
chrome.windows.onFocusChanged.addListener((windowId) => {
  if (windowId !== chrome.windows.WINDOW_ID_NONE) sendActiveTab();
});

// Старт воркера (запуск браузера или пробуждение) — зафиксировать текущую вкладку
sendActiveTab();
