const { spawn } = require('child_process');
const fs = require('fs');
const PORT = 9341;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function main() {
  const proc = spawn(process.env.HOME + '/tools/chrome-for-testing/chrome-linux64/chrome', [
    '--headless=new', '--no-sandbox', '--no-proxy-server',
    '--remote-debugging-port=' + PORT, '--user-data-dir=/tmp/muse-lab-profile3', 'about:blank',
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
    await send('Page.navigate', { url: 'http://127.0.0.1:8899/replica.html' });
    await sleep(1000);
    const b64 = fs.readFileSync(__dirname + '/tiny.jpg.b64', 'utf8').trim();
    console.log('diag:', await evl('JSON.stringify(window.__museDiag())'));
    await evl('window.__museAttachEnabled = true');
    await evl("document.getElementById('composer').focus()");
    await send('Input.insertText', { text: 'xin chao' });
    console.log('composer text after typing:', JSON.stringify(await evl("document.getElementById('composer').innerText")));
    await send('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13 });
    await sleep(300);
    console.log('posted:', await evl('JSON.stringify(window.__posted)'));
    console.log('paste:', await evl("window.__musePasteImage('" + b64 + "', 'image/jpeg')"));
    await sleep(400);
    console.log('chips:', await evl("document.querySelectorAll('#attachments img').length"), '| present:', await evl('window.__museAttachmentsPresent()'));
    console.log('region buttons:', await evl("(function(){var c=document.querySelector('[contenteditable]');var r=c.closest('form')||c.parentElement;return JSON.stringify(Array.from(r.querySelectorAll('button')).map(b=>({label:b.getAttribute('aria-label'),text:b.textContent,type:b.type})));})()"));
    console.log('sendNow:', await evl('window.__museSendNow()'));
    await sleep(400);
    console.log('msgs after sendNow:', await evl("document.querySelectorAll('.msg').length"));
    console.log('manual click:', await evl("document.getElementById('sendBtn').click(), 'clicked'"));
    await sleep(300);
    console.log('msgs after manual click:', await evl("document.querySelectorAll('.msg').length"));
    ws.close();
  } finally { proc.kill('SIGKILL'); }
}
main().catch((e) => { console.error(e); process.exit(2); });
