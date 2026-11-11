const { spawn } = require('child_process');
const fs = require('fs');
const PORT = 9347;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  const proc = spawn(process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome', [
    '--headless=new', '--no-sandbox', '--no-proxy-server', '--window-size=1280,900',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-ctx', 'about:blank',
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
    await send('Page.addScriptToEvaluateOnNewDocument', { source: fs.readFileSync(__dirname + '/bridge-current.js', 'utf8') });
    await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica-ctx.html' });
    await sleep(1000);
    // real right-click via the input pipeline (trusted event)
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: 200, y: 150, button: 'right', clickCount: 1 });
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: 200, y: 150, button: 'right', clickCount: 1 });
    await sleep(400);
    const posted = await evl('JSON.stringify(window.__posted || [])');
    const pageSaw = await evl('JSON.stringify(window.__pageSaw)');
    const focusOk = await evl("typeof window.__museFocusComposer === 'function'");
    let pass = 0, total = 3;
    const has = posted.includes('muse-contextmenu');
    console.log((has ? 'PASS' : 'FAIL') + ' — bridge posted muse-contextmenu on right-click'); if (has) pass++;
    const suppressed = pageSaw === 'null' || pageSaw === undefined || (pageSaw && JSON.parse(pageSaw).defaultPrevented === true);
    console.log((suppressed ? 'PASS' : 'FAIL') + ' — page listener never ran unprevented (native menu path cancelled), pageSaw=' + pageSaw); if (suppressed) pass++;
    console.log((focusOk ? 'PASS' : 'FAIL') + ' — __museFocusComposer still installed'); if (focusOk) pass++;
    console.log(`RESULT: ${pass}/${total}`);
    ws.close();
    process.exit(pass === total ? 0 : 1);
  } finally { proc.kill('SIGKILL'); }
}
main().catch((e) => { console.error(e); process.exit(2); });
