// v1.3.1 verification: replica2 mimics muse.ai's real layout — the
// attachment strip lives OUTSIDE the composer <form>, and paste is handled
// at document level. v1.3.0's region-only presence check can never see the
// thumbnails there (the real-world "3 copies + not sent" bug). The host's
// new algorithm (baseline count -> one path -> poll for the count to rise)
// is simulated exactly.
const { spawn } = require('child_process');
const fs = require('fs');
const CHROME = process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome';
const PORT = 9350;
const BASE = 'http://127.0.0.1:8899';
const bridgeSrc = fs.readFileSync(__dirname + '/bridge.js', 'utf8');
const b64 = fs.readFileSync(__dirname + '/tiny.jpg.b64', 'utf8').trim();
let failures = 0;
function check(name, cond, extra) {
  console.log((cond ? 'PASS' : 'FAIL') + '  ' + name + (extra ? '  [' + extra + ']' : ''));
  if (!cond) failures++;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function main() {
  const proc = spawn(CHROME, [
    '--headless=new', '--no-sandbox', '--no-proxy-server', '--window-size=1280,900',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile9', 'about:blank',
  ], { stdio: 'ignore' });
  try {
    let wsUrl = null;
    for (let i = 0; i < 50 && !wsUrl; i++) {
      try { wsUrl = (await (await fetch('http://127.0.0.1:' + PORT + '/json/version')).json()).webSocketDebuggerUrl; }
      catch { await sleep(200); }
    }
    const ws = new WebSocket(wsUrl);
    await new Promise((r) => (ws.onopen = r));
    let id = 1; const pending = new Map(); let sessionId = null;
    ws.onmessage = (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
    const send = (method, params = {}) => new Promise((res) => { const i = id++; pending.set(i, res); const m = { id: i, method, params }; if (sessionId) m.sessionId = sessionId; ws.send(JSON.stringify(m)); });
    const targets = await send('Target.getTargets');
    sessionId = (await send('Target.attachToTarget', { targetId: targets.result.targetInfos.find(t => t.type === 'page').targetId, flatten: true })).result.sessionId;
    const evl = async (expr) => {
      const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
      const ex = r.result?.exceptionDetails;
      if (ex) return 'EXC: ' + (ex.exception?.description || ex.text);
      return r.result?.result?.value;
    };
    const waitFor = async (expr, timeout = 4000) => { const t0 = Date.now(); while (Date.now() - t0 < timeout) { if (await evl(expr)) return true; await sleep(150); } return false; };

    await send('Page.enable'); await send('Runtime.enable');
    await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__posted = []; window.chrome = window.chrome || {}; window.chrome.webview = { postMessage: function (m) { window.__posted.push(m); } };' });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: bridgeSrc });
    await send('Page.navigate', { url: BASE + '/replica2.html' });
    check('bridge installed', await waitFor("typeof window.__museDiag === 'function'"));
    await evl('window.__museAttachEnabled = true');
    await evl("document.getElementById('composer').focus()");
    await send('Input.insertText', { text: 'anh ngoai vung' });
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await sleep(400);
    const posted = await evl('JSON.stringify(window.__posted)');
    check('Enter held, send-request posted', typeof posted === 'string' && posted.includes('muse-send-request'));

    // Host v1.3.1 algorithm: baseline, ONE paste, poll for count to rise.
    const baseline = await evl('window.__museAttachmentCount()');
    check('baseline count is 0', baseline === 0, String(baseline));
    check('paste dispatched', (await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')")) === true);
    let count = 0;
    for (let i = 0; i < 16 && count <= baseline; i++) { await sleep(200); count = await evl('window.__museAttachmentCount()'); }
    check('count rose above baseline', count > baseline, 'count=' + count);
    check('exactly ONE copy attached (no tripling)', count === 1, 'count=' + count);
    // The v1.3.0 region-only check, replayed verbatim, must MISS this layout:
    const oldCheck = await evl("(function () { var c = document.querySelector('[contenteditable]'); var region = c.closest('form') || c.parentElement; var imgs = region.querySelectorAll('img'); for (var i = 0; i < imgs.length; i++) { var s = imgs[i].src || ''; if (s.indexOf('blob:') === 0 || s.indexOf('data:image') === 0) return true; } return !!region.querySelector('figure, [class*=\"attachment\" i], [data-testid*=\"attach\" i]'); })()");
    check('v1.3.0 region check indeed misses this layout (bug reproduced)', oldCheck === false);
    check('sendNow finds the "Send message" button (substring match)', (await evl('window.__museSendNow()')) === 'button');
    await sleep(400);
    check('message sent with exactly one image', (await evl("document.querySelectorAll('.msg').length")) === 1
      && (await evl("document.querySelectorAll('.msg img').length")) === 1);
    ws.close();
  } finally { proc.kill('SIGKILL'); }
  console.log(failures === 0 ? 'ALL PASS' : failures + ' FAILURES');
  process.exit(failures === 0 ? 0 : 1);
}
main().catch((e) => { console.error('ERROR', e); process.exit(2); });
