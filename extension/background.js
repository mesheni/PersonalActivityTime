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
    port.postMessage({ url: tab.url, title, browser: navigator.userAgent.includes("Edg/") ? "msedge" : "chrome" });
  } catch (e) {
    port = null;
  }
}

function sendActiveTab() {
  chrome.windows.getLastFocused((window) => {
    if (chrome.runtime.lastError || !window || !window.focused) return;
    chrome.tabs.query({ active: true, windowId: window.id }, (tabs) => {
      if (!chrome.runtime.lastError && tabs && tabs[0]) send(tabs[0]);
    });
  });
}

// Переключение вкладки
chrome.tabs.onActivated.addListener(() => sendActiveTab());

// Навигация или смена заголовка на активной вкладке
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if ((changeInfo.url || changeInfo.title) && tab && tab.active) {
    chrome.windows.get(tab.windowId, (window) => {
      if (!chrome.runtime.lastError && window && window.focused) send(tab);
    });
  }
});

// Переключение окна браузера
chrome.windows.onFocusChanged.addListener((windowId) => {
  if (windowId !== chrome.windows.WINDOW_ID_NONE) sendActiveTab();
});

// Старт воркера (запуск браузера или пробуждение) — зафиксировать текущую вкладку
sendActiveTab();
