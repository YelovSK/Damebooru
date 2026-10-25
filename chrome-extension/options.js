const input = document.getElementById('base-url');
const status = document.getElementById('status');
let statusTimer;

chrome.storage.sync.get({ baseUrl: 'http://localhost:8080' }).then(({ baseUrl }) => {
  input.value = baseUrl;
});

document.getElementById('save').addEventListener('click', save);
input.addEventListener('keydown', event => {
  if (event.key === 'Enter') {
    save();
  }
});

async function save() {
  try {
    const baseUrl = new URL(input.value.trim()).origin;
    await chrome.storage.sync.set({ baseUrl });
    input.value = baseUrl;
    showStatus('Saved', false);
  } catch {
    showStatus('Not a valid URL', true);
  }
}

function showStatus(text, isError) {
  clearTimeout(statusTimer);
  status.textContent = text;
  status.classList.toggle('error', isError);
  status.style.opacity = '1';
  statusTimer = setTimeout(() => {
    status.style.opacity = '0';
  }, 2000);
}
