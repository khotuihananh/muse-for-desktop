        (function () {
          if (window.__museBridgeInstalled) return;
          window.__museBridgeInstalled = true;
          window.__museAttachEnabled = false;

          function deepQuery(root, sel, out) {
            var els = root.querySelectorAll(sel);
            for (var i = 0; i < els.length; i++) out.push(els[i]);
            var all = root.querySelectorAll('*');
            for (var j = 0; j < all.length; j++) {
              if (all[j].shadowRoot) deepQuery(all[j].shadowRoot, sel, out);
            }
          }

          function findComposer() {
            var found = [];
            deepQuery(document, '[contenteditable], [role="textbox"], textarea', found);
            var best = null, bestBottom = -1;
            for (var i = 0; i < found.length; i++) {
              var el = found[i];
              if (el.tagName === 'INPUT') continue;
              var r = el.getBoundingClientRect();
              if (r.width === 0 || r.height === 0) continue;
              if (r.bottom > bestBottom) { bestBottom = r.bottom; best = el; }
            }
            return best;
          }

          function composerRegion(c) {
            return (c && (c.closest('form') || c.parentElement)) || document.body;
          }

          function isSendButton(btn, region) {
            if (!btn || !region || !region.contains(btn)) return false;
            var label = (btn.getAttribute('aria-label') || btn.getAttribute('title')
              || btn.textContent || '').trim().toLowerCase();
            // Substring, not equality: real labels are "Send message",
            // "Gửi tin nhắn"… — v1.3.1's exact-match missed them, so the
            // release click never found the button at all.
            if (label.indexOf('send') >= 0 || label.indexOf('gửi') >= 0) return true;
            return btn.type === 'submit' && label === '';
          }

          function requestSend(via) {
            try {
              window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-send-request', via: via }));
            } catch (e) { /* Host not listening; the page still has its default send path. */ }
          }

          document.addEventListener('keydown', function (e) {
            if (!window.__museAttachEnabled) return;
            if (!e.isTrusted) return; // Our own synthetic release Enter must pass.
            if (e.key !== 'Enter' || e.shiftKey || e.ctrlKey || e.altKey || e.metaKey) return;
            if (e.isComposing) return; // Vietnamese IME confirming text is not a send.
            var c = findComposer();
            if (!c) return;
            var path = e.composedPath ? e.composedPath() : [e.target];
            if (path.indexOf(c) < 0 && !c.contains(e.target)) return;
            e.preventDefault();
            e.stopPropagation();
            window.__museHeldCount = (window.__museHeldCount || 0) + 1;
            requestSend('enter');
          }, true);

          document.addEventListener('click', function (e) {
            if (!window.__museAttachEnabled) return;
            if (!e.isTrusted) return; // __museSendNow releases via btn.click() — let it through.
            var c = findComposer();
            if (!c) return;
            var path = e.composedPath ? e.composedPath() : [e.target];
            var t0 = path[0];
            var btn = t0 && t0.closest ? t0.closest('button') : null;
            if (!isSendButton(btn, composerRegion(c))) return;
            e.preventDefault();
            e.stopPropagation();
            window.__museHeldCount = (window.__museHeldCount || 0) + 1;
            requestSend('click');
          }, true);

          window.__museFocusComposer = function () {
            var c = findComposer();
            if (!c) return false;
            c.focus();
            return true;
          };

          window.__museAttachViaFileInput = function (b64, mime) {
            try {
              var bin = atob(b64);
              var bytes = new Uint8Array(bin.length);
              for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
              var type = mime || 'image/jpeg';
              var file = new File([new Blob([bytes], { type: type })], 'man-hinh.jpg', { type: type });
              var inputs = [];
              deepQuery(document, 'input[type="file"]', inputs);
              for (var i = 0; i < inputs.length; i++) {
                var dt = new DataTransfer();
                dt.items.add(file);
                inputs[i].files = dt.files;
                inputs[i].dispatchEvent(new Event('input', { bubbles: true }));
                inputs[i].dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              }
              return false;
            } catch (err) {
              return false;
            }
          };

          function countAttachmentsIn(root) {
            if (!root || !root.querySelectorAll) return 0;
            var n = 0;
            var imgs = root.querySelectorAll('img');
            for (var i = 0; i < imgs.length; i++) {
              var src = imgs[i].src || '';
              if (src.indexOf('blob:') === 0 || src.indexOf('data:image') === 0) n++;
            }
            return n;
          }

          window.__museAttachmentCount = function () {
            var c = findComposer();
            if (!c) return countAttachmentsIn(document);
            // muse.ai keeps the attachment strip OUTSIDE the composer's own
            // subtree (a sibling container), so a region-only check never
            // sees the thumbnails — v1.3.0 concluded "not attached" while
            // three copies sat on screen. Widen ancestor by ancestor, then
            // fall back to the whole document; the host judges success by
            // this count RISING above its pre-send baseline.
            var node = c;
            for (var level = 0; level < 6 && node; level++) {
              var n = countAttachmentsIn(node.parentElement || node);
              if (n > 0) return n;
              node = node.parentElement;
            }
            return countAttachmentsIn(document);
          };

          // Attachment count inside the composer's ancestor at a fixed
          // level (0 = the editable's own subtree, 1 = its parent, …).
          // Level-stable: after a send, the strip at THIS level empties
          // even though the sent image now lives in the message history
          // further up the tree — a plain widening count can never fall
          // back, so "did the composer clear?" must be asked per level.
          window.__museCountAt = function (level) {
            var c = findComposer();
            if (!c) return countAttachmentsIn(document);
            var node = c;
            for (var i = 0; i < level && node; i++) node = node.parentElement;
            if (!node) return countAttachmentsIn(document);
            return countAttachmentsIn(node);
          };

          window.__museAttachmentsPresent = function () {
            return window.__museAttachmentCount() > 0;
          };

          window.__musePasteImage = function (b64, mime) {
            var c = findComposer();
            if (!c) return false;
            c.focus();
            try {
              var bin = atob(b64);
              var bytes = new Uint8Array(bin.length);
              for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
              var type = mime || 'image/jpeg';
              var file = new File([new Blob([bytes], { type: type })], 'man-hinh.jpg', { type: type });
              var dt = new DataTransfer();
              dt.items.add(file);
              c.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
              return true;
            } catch (err) {
              return false;
            }
          };

          window.__museSendNow = function () {
            var c = findComposer();
            if (!c) return 'none';
            c.focus();
            // The send control may sit several levels above the editable
            // (next to the attachment strip): widen ancestor by ancestor.
            var node = c;
            for (var level = 0; level < 6 && node; level++) {
              var scope = node.parentElement || node;
              var btns = scope.querySelectorAll('button, [role="button"]');
              for (var i = 0; i < btns.length; i++) {
                if (isSendButton(btns[i], scope)) { btns[i].click(); return 'button'; }
              }
              node = node.parentElement;
            }
            var opts = { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true };
            c.dispatchEvent(new KeyboardEvent('keydown', opts));
            c.dispatchEvent(new KeyboardEvent('keypress', opts));
            c.dispatchEvent(new KeyboardEvent('keyup', opts));
            return 'enter';
          };

          window.__museComposerState = function () {
            var c = findComposer();
            if (!c) return { hasComposer: false, textLength: 0, attachments: 0 };
            var text = c.value !== undefined ? c.value
              : (c.innerText || c.textContent || '');
            return {
              hasComposer: true,
              textLength: text.trim().length,
              attachments: window.__museAttachmentCount()
            };
          };

          window.__museDiag = function () {
            var c = findComposer();
            return {
              bridge: true,
              enabled: !!window.__museAttachEnabled,
              composer: !!c,
              composerTag: c ? c.tagName : null,
              held: window.__museHeldCount || 0,
              url: location.href
            };
          };

          try {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-bridge-ready' }));
          } catch (e) {}
        })();