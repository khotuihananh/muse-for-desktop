const { spawn } = require('child_process');
const fs = require('fs');
const PORT = 9342;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  const proc = spawn(process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome', [
    '--headless=new', '--no-sandbox', '--no-proxy-server',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile4', 'about:blank',
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
    await send('Page.enable'); await send('Runtime.enable');
    await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__posted = []; window.chrome = window.chrome || {}; window.chrome.webview = { postMessage: function (m) { window.__posted.push(m); } };' });
    await send('Page.addScriptToEvaluateOnNewDocument', { source: fs.readFileSync(__dirname + '/bridge.js', 'utf8') });
    await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica-iframe.html' });
    await sleep(1200);
    const IW = "document.getElementById('chatframe').contentWindow";
    console.log('frame diag:', await evl(IW + '.JSON.stringify(' + IW + '.__museDiag())'));
    console.log('fake-isTrusted test:', await evl("(function(){ var iw=" + IW + "; var e = new iw.KeyboardEvent('keydown', {key:'Enter'}); var before = e.isTrusted; var err=''; try { Object.defineProperty(e, 'isTrusted', { value: true }); } catch (x) { err = String(x); } return JSON.stringify({before: before, after: e.isTrusted, err: err}); })()"));
    console.log('dispatch result:', await evl("(function(){ var iw=" + IW + "; iw.__museAttachEnabled = true; var c = iw.document.getElementById('composer'); c.focus(); iw.document.execCommand('insertText', false, 'abc'); var e = new iw.KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }); try { Object.defineProperty(e, 'isTrusted', { value: true }); } catch (x) {} var r = c.dispatchEvent(e); return JSON.stringify({dispatchReturn: r, held: iw.__museHeldCount || 0}); })()"));
    await sleep(300);
    console.log('frame posted:', await evl('JSON.stringify(' + IW + '.__posted || [])'));
    ws.close();
  } finally { proc.kill('SIGKILL'); }
}
main().catch((e) => { console.error(e); process.exit(2); });
