const { spawn } = require('child_process');
const fs = require('fs');
const PORT = 9340;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  const proc = spawn(process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome', [
    '--headless=new', '--no-sandbox', '--no-proxy-server',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile2', 'about:blank',
  ], { stdio: 'ignore' });
  try {
    let wsUrl = null;
    for (let i = 0; i < 50 && !wsUrl; i++) {
      try { wsUrl = (await (await fetch('http://127.0.0.1:' + PORT + '/json/version')).json()).webSocketDebuggerUrl; }
      catch { await sleep(200); }
    }
    const ws = new WebSocket(wsUrl);
    await new Promise((r) => (ws.onopen = r));
    let id = 1; const pending = new Map();
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
      if (m.method === 'Runtime.exceptionThrown') console.log('PAGE EXCEPTION:', JSON.stringify(m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text));
      if (m.method === 'Runtime.consoleAPICalled') console.log('CONSOLE:', m.params.type, m.params.args.map(a => a.value || a.description).join(' '));
    };
    const send = (method, params = {}) => new Promise((res) => { const i = id++; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
    const evl = async (expr) => {
      const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true });
      return r.result?.result?.value ?? ('RAW:' + JSON.stringify(r.result).slice(0, 300));
    };
    await send('Page.enable'); await send('Runtime.enable');
    const mock = await send('Page.addScriptToEvaluateOnNewDocument', { source: 'window.__posted = []; window.chrome = window.chrome || {}; window.chrome.webview = { postMessage: function (m) { window.__posted.push(m); } };' });
    console.log('mock script id:', JSON.stringify(mock.result));
    const bridge = fs.readFileSync(__dirname + '/bridge.js', 'utf8');
    const br = await send('Page.addScriptToEvaluateOnNewDocument', { source: bridge });
    console.log('bridge script id:', JSON.stringify(br.result));
    const nav = await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica.html' });
    console.log('nav:', JSON.stringify(nav.result));
    await sleep(1500);
    console.log('href:', await evl('location.href'));
    console.log('title:', await evl('document.title'));
    console.log('posted:', await evl('JSON.stringify(window.__posted)'));
    console.log('diag type:', await evl('typeof window.__museDiag'));
    console.log('composer:', await evl("!!document.getElementById('composer')"));
    ws.close();
  } finally { proc.kill('SIGKILL'); }
}
main().catch((e) => { console.error(e); process.exit(2); });
