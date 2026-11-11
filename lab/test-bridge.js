// Lab verification of the EXACT BridgeScript shipped in Muse Desktop:
// drives Chrome for Testing via CDP against replica chat pages whose
// composer mimics a modern React-style editor (paste -> attachment chip,
// Enter / Send button -> send). The WebView2 host half is simulated by
// calling window.__musePasteImage / __museSendNow exactly like the C# host.
const { spawn } = require('child_process');
const fs = require('fs');

const CHROME = process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome';
const PORT = 9339;
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
    '--remote-debugging-port=' + PORT,
    '--user-data-dir=/tmp/muse-lab-profile', 'about:blank',
  ], { stdio: 'ignore' });

  try {
    let wsUrl = null;
    for (let i = 0; i < 50 && !wsUrl; i++) {
      try {
        const res = await fetch('http://127.0.0.1:' + PORT + '/json/version');
        wsUrl = (await res.json()).webSocketDebuggerUrl;
      } catch { await sleep(200); }
    }
    if (!wsUrl) throw new Error('CDP endpoint never came up');

    const ws = new WebSocket(wsUrl);
    await new Promise((r) => (ws.onopen = r));
    let nextId = 1;
    const pending = new Map();
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
    };
    let sessionId = null;
    const send = (method, params = {}) => new Promise((resolve) => {
      const id = nextId++;
      pending.set(id, resolve);
      const msg = { id, method, params };
      if (sessionId) msg.sessionId = sessionId;
      ws.send(JSON.stringify(msg));
    });
    // The /json/version socket is the BROWSER endpoint: Page/Runtime calls
    // need a session from attaching to the page target first.
    const targets = await send('Target.getTargets');
    const page = targets.result.targetInfos.find((t) => t.type === 'page');
    if (!page) throw new Error('no page target');
    const attached = await send('Target.attachToTarget', { targetId: page.targetId, flatten: true });
    sessionId = attached.result.sessionId;
    if (!sessionId) throw new Error('attach failed: ' + JSON.stringify(attached));
    const evl = async (expr) => {
      const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true });
      if (r.result && r.result.exceptionDetails) {
        return 'EXC:' + (r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text);
      }
      return r.result?.result?.value;
    };
    const waitFor = async (expr, timeout = 4000) => {
      const t0 = Date.now();
      while (Date.now() - t0 < timeout) {
        if (await evl(expr)) return true;
        await sleep(150);
      }
      return false;
    };

    await send('Page.enable');
    await send('Runtime.enable');
    // Mock WebView2 message pipe BEFORE the bridge, in every new document.
    await send('Page.addScriptToEvaluateOnNewDocument', {
      source: 'window.__posted = []; window.chrome = window.chrome || {}; ' +
        'window.chrome.webview = { postMessage: function (m) { window.__posted.push(m); } };',
    });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: bridgeSrc });

    async function composerFlow(page, label) {
      console.log('--- ' + label + ' ---');
      await send('Page.navigate', { url: BASE + '/' + page });
      check(label + ': bridge installed', await waitFor("typeof window.__museDiag === 'function'"));
      await evl('window.__museAttachEnabled = true');
      await evl("document.getElementById('composer').focus()");
      await send('Input.insertText', { text: 'xin chao' });
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
      await sleep(400);
      const posted = await evl('JSON.stringify(window.__posted)');
      check(label + ': Enter held, send-request posted', typeof posted === 'string' && posted.includes('muse-send-request'), posted);
      check(label + ': message NOT sent yet', (await evl("document.querySelectorAll('.msg').length")) === 0);
      check(label + ': paste accepted', (await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')")) === true);
      check(label + ': attachment verified present', await waitFor('window.__museAttachmentsPresent() === true', 2500));
      check(label + ': sendNow runs', (await evl('window.__museSendNow()')) === true);
      await sleep(400);
      check(label + ': message sent with text', (await evl("document.querySelectorAll('.msg').length")) === 1
        && String(await evl("document.querySelector('.msg').textContent")).includes('xin chao'));
      check(label + ': message carries the image', (await evl("!!document.querySelector('.msg img')")) === true);
      // Flag off: Enter must behave completely normally again.
      await evl('window.__museAttachEnabled = false');
      await evl("document.getElementById('composer').focus()");
      await send('Input.insertText', { text: 'hai' });
      await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
      await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
      await sleep(400);
      check(label + ': flag off sends normally', (await evl("document.querySelectorAll('.msg').length")) === 2);
      const posted2 = JSON.parse(await evl('JSON.stringify(window.__posted)'));
      check(label + ': no extra send-request while off', posted2.filter((m) => m.includes('muse-send-request')).length === 1);
    }

    await composerFlow('replica.html', 'contenteditable');
    await composerFlow('replica-textarea.html', 'textarea');

    console.log('--- iframe ---');
    await send('Page.navigate', { url: BASE + '/replica-iframe.html' });
    await waitFor("!!document.getElementById('chatframe').contentWindow");
    await sleep(1200); // probe5 timings: let the frame document fully settle
    const IW = "document.getElementById('chatframe').contentWindow";
    let inFrame = await evl('!!' + IW + '.__museDiag');
    if (!inFrame) {
      // Mirror the C# host's frame injection path (frame NavigationCompleted).
      await evl(IW + '.__posted = []; ' + IW + '.chrome = ' + IW + '.chrome || {}; ' +
        IW + '.chrome.webview = { postMessage: function (m) { ' + IW + '.__posted.push(m); } }; true');
      await evl(IW + '.eval(' + JSON.stringify(bridgeSrc) + ')');
      inFrame = await evl('!!' + IW + '.__museDiag');
      console.log('NOTE: CDP did not auto-inject into the subframe; used host-style frame injection.');
    }
    check('iframe: bridge installed in frame', inFrame === true);
    // Wait until the frame's REAL document (replica.html, not the initial
    // about:blank) holds the bridge — a document swap re-runs the bridge
    // installer, which resets the flag. The production host defeats this
    // exact race with bridge-ready resync + the 2s heartbeat; the lab
    // mirrors it by setting the flag only after the final document is in
    // and re-asserting it right before the Enter.
    await waitFor(IW + ".location.href.indexOf('replica.html') >= 0 && !!(" + IW + '.__museDiag)');
    await evl(IW + '.__museAttachEnabled = true');
    // A trusted Enter cannot be faked from script (isTrusted is a
    // non-configurable own property — verified), so drive the REAL input
    // pipeline: click into the frame's composer at its absolute position,
    // type, then dispatch a genuine Enter key event.
    const rect = JSON.parse(await evl("(function () { var f = document.getElementById('chatframe'); " +
      "var fr = f.getBoundingClientRect(); var c = f.contentWindow.document.getElementById('composer'); " +
      "var cr = c.getBoundingClientRect(); return JSON.stringify({ x: fr.left + cr.left + 20, y: fr.top + cr.top + 10 }); })()"));
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: rect.x, y: rect.y, button: 'left', clickCount: 1 });
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: rect.x, y: rect.y, button: 'left', clickCount: 1 });
    await sleep(300);
    console.log('DEBUG iframe click at', JSON.stringify(rect),
      '| outer active:', await evl('document.activeElement && document.activeElement.tagName'),
      '| frame active:', await evl(IW + '.document.activeElement && ' + IW + '.document.activeElement.id'));
    await send('Input.insertText', { text: 'trong iframe' });
    await sleep(200);
    console.log('DEBUG iframe composer text:', JSON.stringify(await evl(IW + ".document.getElementById('composer').innerText")));
    await evl(IW + '.__museAttachEnabled = true'); // host-style resync before the gesture
    console.log('DEBUG iframe diag before Enter:', await evl(IW + '.JSON.stringify(' + IW + '.__museDiag())'));
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await sleep(400);
    console.log('DEBUG iframe diag after Enter:', await evl(IW + '.JSON.stringify(' + IW + '.__museDiag())'),
      '| frame msgs:', await evl(IW + ".document.querySelectorAll('.msg').length"));
    const fposted = await evl('JSON.stringify(' + IW + '.__posted || [])');
    check('iframe: Enter held, send-request posted', typeof fposted === 'string' && fposted.includes('muse-send-request'), fposted);
    check('iframe: paste accepted', (await evl(IW + ".__musePasteImage('" + b64 + "', 'image/jpeg')")) === true);
    check('iframe: attachment verified present', (await evl(IW + '.__museAttachmentsPresent()')) === true);
    await evl(IW + '.__museSendNow()');
    await sleep(400);
    check('iframe: message sent with image', (await evl(IW + ".document.querySelectorAll('.msg').length")) === 1
      && (await evl('!!' + IW + ".document.querySelector('.msg img')")) === true);

    ws.close();
  } finally {
    proc.kill('SIGKILL');
  }
  console.log(failures === 0 ? 'ALL PASS' : failures + ' FAILURES');
  process.exit(failures === 0 ? 0 : 1);
}

main().catch((e) => { console.error('ERROR', e); process.exit(2); });
