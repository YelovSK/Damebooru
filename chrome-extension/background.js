const DEFAULT_BASE_URL = 'http://localhost:8080';
const MENU_ID = 'damebooru-lookup';
const REFERER_RULE_ID = 1;

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU_ID, title: 'Look up in Damebooru', contexts: ['image'] });
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_ID || !info.srcUrl) {
    return;
  }

  lookUp(info, tab).catch(error => {
    console.error('Damebooru lookup failed', error);
    if (tab?.id !== undefined) {
      chrome.scripting.executeScript({
        target: { tabId: tab.id },
        func: message => alert(message),
        args: [`Damebooru lookup failed: ${error.message ?? error}`],
      }).catch(() => {});
    }
  });
});

async function lookUp(info, sourceTab) {
  const image = await fetchImage(info, sourceTab);
  const { baseUrl } = await chrome.storage.sync.get({ baseUrl: DEFAULT_BASE_URL });
  const tab = await chrome.tabs.create({
    url: new URL('/duplicates/lookup', baseUrl).href,
    index: sourceTab ? sourceTab.index + 1 : undefined,
    openerTabId: sourceTab?.id,
  });

  await waitForTabLoad(tab.id);

  let delivered;
  try {
    [{ result: delivered }] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: deliverImage,
      args: [image],
    });
  } catch {
    // The tab was closed or navigated away before the lookup page took the image.
    return;
  }

  if (!delivered) {
    await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: message => alert(message),
      args: ['The Damebooru lookup page did not accept the image. Is the client up to date?'],
    }).catch(() => {});
  }
}

async function fetchImage(info, sourceTab) {
  // Blob URLs only resolve inside the document that created them.
  if (info.srcUrl.startsWith('blob:') && sourceTab?.id !== undefined) {
    const [result] = await chrome.scripting.executeScript({
      target: { tabId: sourceTab.id, frameIds: [info.frameId ?? 0] },
      func: fetchAsBase64,
      args: [info.srcUrl],
    });
    if (!result?.result) {
      throw new Error('Could not read the image from the page.');
    }

    return { name: 'image', ...result.result };
  }

  const name = getFileName(info.srcUrl);
  const referer = info.frameUrl ?? info.pageUrl;
  if (!referer || info.srcUrl.startsWith('data:')) {
    return { name, ...(await fetchAsBase64(info.srcUrl)) };
  }

  // Hotlink-protected hosts (pixiv and the like) reject requests without the page's Referer, and fetch() from the
  // service worker can't set it.
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds: [REFERER_RULE_ID],
    addRules: [{
      id: REFERER_RULE_ID,
      priority: 1,
      action: {
        type: 'modifyHeaders',
        requestHeaders: [{ header: 'referer', operation: 'set', value: referer }],
      },
      condition: {
        requestDomains: [new URL(info.srcUrl).hostname],
        tabIds: [chrome.tabs.TAB_ID_NONE],
      },
    }],
  });

  try {
    return { name, ...(await fetchAsBase64(info.srcUrl)) };
  } finally {
    await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [REFERER_RULE_ID] });
  }
}

// Also injected into the source page, so it must be self-contained.
async function fetchAsBase64(url) {
  const response = await fetch(url, { credentials: 'include' });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status} for ${url}`);
  }

  const blob = await response.blob();
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }

  return { type: blob.type, base64: btoa(binary) };
}

function getFileName(url) {
  try {
    const segment = new URL(url).pathname.split('/').pop();
    return segment ? decodeURIComponent(segment) : 'image';
  } catch {
    return 'image';
  }
}

async function waitForTabLoad(tabId) {
  await new Promise(resolve => {
    const listener = (id, changeInfo) => {
      if (id === tabId && changeInfo.status === 'complete') {
        chrome.tabs.onUpdated.removeListener(listener);
        resolve();
      }
    };
    chrome.tabs.onUpdated.addListener(listener);

    chrome.tabs.get(tabId).then(tab => {
      if (tab.status === 'complete') {
        chrome.tabs.onUpdated.removeListener(listener);
        resolve();
      }
    });
  });
}

// Runs in the Damebooru tab. The lookup page may still be booting (or behind the login page), so keep posting
// until it acknowledges. Protocol mirrored in client/src/app/pages/duplicates/duplicates.component.ts.
function deliverImage({ name, type, base64 }) {
  const bytes = Uint8Array.from(atob(base64), char => char.charCodeAt(0));
  const file = new File([bytes], name, { type });
  const id = crypto.randomUUID();
  const deadline = Date.now() + 60_000;

  return new Promise(resolve => {
    const finish = delivered => {
      clearInterval(timer);
      window.removeEventListener('message', onMessage);
      resolve(delivered);
    };

    const onMessage = event => {
      if (event.source === window
        && event.data?.type === 'damebooru:lookup-file-received'
        && event.data.id === id) {
        finish(true);
      }
    };

    const timer = setInterval(() => {
      if (Date.now() > deadline) {
        finish(false);
        return;
      }

      window.postMessage({ type: 'damebooru:lookup-file', id, file }, window.location.origin);
    }, 250);

    window.addEventListener('message', onMessage);
  });
}
