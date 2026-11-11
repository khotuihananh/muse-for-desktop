const { spawn } = require('child_process');
const fs = require('fs');
const PORT = 9346;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  const proc = spawn(process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome', [
    '--headless=new', '--no-sandbox', '--no-proxy-server', '--window-size=1280,900',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile8', 'about:blank',
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
    await send('Page.addScriptToEvaluateOnNewDocument', { source: fs.readFileSync(__dirname + '/bridge.js', 'utf8') });
    const b64 = fs.readFileSync(__dirname + '/tiny.jpg.b64', 'utf8').trim();
    // ONE full composerFlow on the main frame first (the suite difference).
    await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica.html' });
    await waitFor("typeof window.__museDiag === 'function'");
    await evl('window.__museAttachEnabled = true');
    await evl("document.getElementById('composer').focus()");
    await send('Input.insertText', { text: 'xin chao' });
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await sleep(300);
    console.log('main flow held:', await evl('(window.__museHeldCount || 0)'));
    await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')");
    await sleep(300);
    await evl('window.__museSendNow()');
    await sleep(300);
    await evl('window.__museAttachEnabled = false');
    // Now the iframe page.
    await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica-iframe.html' });
    await waitFor("!!document.getElementById('chatframe').contentWindow");
    await sleep(1200);
    const IW = "document.getElementById('chatframe').contentWindow";
    await waitFor(IW + ".location.href.indexOf('replica.html') >= 0 && !!(" + IW + '.__museDiag)');
    await evl(IW + '.__museAttachEnabled = true');
    await evl(IW + '.__marker = { t: 1 }');
    const rect = JSON.parse(await evl("(function () { var f = document.getElementById('chatframe'); var fr = f.getBoundingClientRect(); var c = f.contentWindow.document.getElementById('composer'); var cr = c.getBoundingClientRect(); return JSON.stringify({ x: fr.left + cr.left + 20, y: fr.top + cr.top + 10 }); })()"));
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: rect.x, y: rect.y, button: 'left', clickCount: 1 });
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: rect.x, y: rect.y, button: 'left', clickCount: 1 });
    await sleep(300);
    await send('Input.insertText', { text: 'trong iframe' });
    await sleep(200);
    // Fresh capture-listener probe inside the frame document.
    await evl("(function(){ var iw = " + IW + "; iw.__probeHits = 0; iw.document.addEventListener('keydown', function (e) { if (e.key === 'Enter') iw.__probeHits++; }, true); return true; })()");
    console.log('diag before Enter:', await evl(IW + '.JSON.stringify(' + IW + '.__museDiag())'));
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await sleep(400);
    console.log('marker still there:', await evl('!!' + IW + '.__marker'), '| nav entries:', await evl(IW + ".performance.getEntriesByType('navigation').length"));
    console.log('probe capture hits:', await evl(IW + '.__probeHits || 0'),
      '| bridge held:', await evl(IW + '.__museHeldCount || 0'),
      '| frame msgs:', await evl(IW + ".document.querySelectorAll('.msg').length"));
    ws.close();
  } finally { proc.kill('SIGKILL'); }
}
main().catch((e) => { console.error(e); process.exit(2); });
