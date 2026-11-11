// v1.3.2 verification on replica3 — the page whose release path is broken
// the way real muse.ai behaved on 2026-11-11: its Send button is a no-op
// and it ignores synthetic Enter (isTrusted check). Only a REAL Enter
// sends. The host algorithm is mirrored from the C# (ReleaseWithVerifyAsync
// + the stuck-attach dedupe rule):
//   attach -> in-page release -> verify composer cleared (poll)
//          -> suspend bridge flag -> focus -> REAL Enter -> verify again.
// Part B proves the dedupe: a second user Enter on a stuck message must
// NOT paste another copy; it only retries the release.
const { spawn } = require('child_process');
const fs = require('fs');
const CHROME = process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome';
const PORT = 9351;
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
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile10', 'about:blank',
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
    const realEnter = async () => {
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    };
    const state = async () => JSON.parse(await evl('JSON.stringify(window.__museComposerState())'));
    const countAt = async (l) => await evl('window.__museCountAt(' + l + ')');
    // Level-stable cleared check, mirroring the C# host: text empty AND
    // the attachment count at the strip's own level back at baseline
    // (history images higher in the tree must not pollute the verdict).
    let stripLevel = -1; const baseLevels = [0, 0, 0, 0, 0, 0, 0];
    const takeBaselines = async () => { for (let l = 0; l < 7; l++) baseLevels[l] = await countAt(l); };
    const findStripLevel = async () => { stripLevel = -1; for (let l = 0; l < 7; l++) { if ((await countAt(l)) > baseLevels[l]) { stripLevel = l; break; } } };
    const waitCleared = async (ms) => {
      const t0 = Date.now();
      while (Date.now() - t0 < ms) {
        const s = await state();
        if (s.hasComposer && s.textLength === 0) {
          if (stripLevel >= 0) { if ((await countAt(stripLevel)) <= baseLevels[stripLevel]) return true; }
          else if (s.attachments === 0) return true;
        }
        await sleep(200);
      }
      return false;
    };
    const postedCount = async () => (JSON.parse(await evl('JSON.stringify(window.__posted)')) || [])
      .filter((m) => m.includes('muse-send-request')).length;

    await send('Page.enable'); await send('Runtime.enable');
    await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__posted = []; window.chrome = window.chrome || {}; window.chrome.webview = { postMessage: function (m) { window.__posted.push(m); } };' });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: bridgeSrc });

    async function freshPage(text) {
      await send('Page.navigate', { url: BASE + '/replica3.html' });
      await waitFor("typeof window.__museDiag === 'function'");
      await evl('window.__museAttachEnabled = true');
      await evl("document.getElementById('composer').focus()");
      await send('Input.insertText', { text });
      await realEnter();
      await sleep(400);
    }

    console.log('--- Part A: release escalation saves a stuck send ---');
    await freshPage('tin ket');
    check('A: Enter held by bridge', (await postedCount()) === 1);
    await takeBaselines();
    check('A: paste dispatched', (await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')")) === true);
    await waitFor('window.__museAttachmentCount() === 1', 2500);
    await findStripLevel();
    check('A: exactly one attachment', (await state()).attachments === 1);
    check('A: strip level identified', stripLevel >= 0, 'level=' + stripLevel);
    const how = await evl('window.__museSendNow()');
    check('A: in-page release tried the button', how === 'button', String(how));
    check('A: composer NOT cleared by in-page release (as on real muse.ai)', !(await waitCleared(1800)));
    // Real Enter with the bridge flag suspended:
    await evl('window.__museAttachEnabled = false');
    await evl('window.__museFocusComposer()');
    await realEnter();
    check('A: real Enter (flag suspended) sends it', await waitCleared(1800));
    check('A: no extra send-request during suspended Enter', (await postedCount()) === 1);
    check('A: message went out with exactly one image', (await evl("document.querySelectorAll('.msg').length")) === 1
      && (await evl("document.querySelectorAll('.msg img').length")) === 1);

    console.log('--- Part B: stuck dedupe — second Enter adds NO copy ---');
    await freshPage('ket nua');
    check('B: Enter held by bridge', (await postedCount()) === 1);
    await takeBaselines();
    await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')");
    await waitFor('window.__museAttachmentCount() === 1', 2500);
    await findStripLevel();
    await evl('window.__museSendNow()'); // fails on this page; sim marks stuckAttach = true
    check('B: release failed, message stuck with 1 image', !(await waitCleared(1200)) && (await state()).attachments === 1);
    await realEnter(); // user presses Enter again; bridge holds it again
    await sleep(400);
    check('B: second Enter held again', (await postedCount()) === 2);
    // Host rule: stuckAttach && count > 0 -> release ONLY, never paste again.
    check('B: still exactly one attachment (no pile-up)', (await state()).attachments === 1);
    await evl('window.__museAttachEnabled = false');
    await evl('window.__museFocusComposer()');
    await realEnter();
    check('B: release-only retry sends it', await waitCleared(1800));
    check('B: sent message carries exactly one image', (await evl("document.querySelectorAll('.msg img').length")) === 1);
    ws.close();
  } finally { proc.kill('SIGKILL'); }
  console.log(failures === 0 ? 'ALL PASS' : failures + ' FAILURES');
  process.exit(failures === 0 ? 0 : 1);
}
main().catch((e) => { console.error('ERROR', e); process.exit(2); });
