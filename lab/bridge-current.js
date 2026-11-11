        (function () {
          if (window.__museBridgeInstalled) return;
          window.__museBridgeInstalled = true;

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

          

          window.__museFocusComposer = function () {
            var c = findComposer();
            if (!c) return false;
            c.focus();
            return true;
          };

          

          function __museKillSpell(el) {
            try {
              if (el && (el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'INPUT')) {
                // Guarded writes: re-setting the same value would feed
                // the MutationObserver below an endless record stream.
                if (el.spellcheck !== false) el.spellcheck = false;
                if (el.getAttribute('spellcheck') !== 'false') el.setAttribute('spellcheck', 'false');
              }
            } catch (e) {}
          }
          function __museKillSpellAll() {
            try {
              var list = document.querySelectorAll('[contenteditable], textarea, input');
              for (var i = 0; i < list.length; i++) __museKillSpell(list[i]);
              if (document.activeElement) __museKillSpell(document.activeElement);
            } catch (e) {}
          }
          __museKillSpellAll();
          // This script runs at document creation, when
          // document.documentElement may not exist yet — observe the
          // Document node itself (always present) and re-run once the
          // DOM is parsed. (First lab run caught the element-targeted
          // observer dying silently on exactly this.)
          try {
            new MutationObserver(function () { __museKillSpellAll(); }).observe(document, {
              childList: true, subtree: true,
              attributes: true, attributeFilter: ['contenteditable', 'spellcheck'],
            });
          } catch (e) {}
          document.addEventListener('DOMContentLoaded', __museKillSpellAll);

          window.addEventListener('contextmenu', function (ev) {
            ev.preventDefault();
            ev.stopPropagation();
            try {
              window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-contextmenu' }));
            } catch (e) {}
          }, true);

          window.__museDiag = function () {
            var c = findComposer();
            return { bridge: true, composer: !!c, url: location.href };
          };

          try {
            window.chrome.webview.postMessage(JSON.stringify({ type: 'muse-bridge-ready' }));
          } catch (e) {}
        })();