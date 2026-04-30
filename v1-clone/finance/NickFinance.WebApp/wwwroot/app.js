// Global keyboard shortcuts for NickFinance. Mounted once via App.razor.
//
// Bindings:
//   Ctrl+/ or Cmd+/   focus the global search input
//   n                 new petty-cash voucher
//   g h               home
//   g a               approvals
//   g p               petty cash
//   g r               reports
//   g j               journals
//
// Shortcuts that are not Ctrl/Cmd-modified are suppressed while the
// operator is typing into a form field, so 'n' inside an <input> still
// types the letter n.
(function () {
    document.addEventListener('keydown', function (e) {
        // Ignore if typing into an input / textarea / select / contenteditable
        var t = e.target;
        var tag = (t && t.tagName) ? t.tagName.toUpperCase() : '';
        var inField = tag === 'INPUT'
            || tag === 'TEXTAREA'
            || tag === 'SELECT'
            || (t && t.isContentEditable);

        // Ctrl+/ or Cmd+/ — focus the global search box. Works even
        // while typing, because the only way to "type" Ctrl+/ is to
        // mean it.
        if ((e.ctrlKey || e.metaKey) && e.key === '/') {
            e.preventDefault();
            var search = document.getElementById('global-search-input');
            if (search) {
                search.focus();
                if (typeof search.select === 'function') {
                    search.select();
                }
            }
            return;
        }

        if (inField) return;

        // 'n' — new petty-cash voucher
        if (e.key === 'n' && !e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
            e.preventDefault();
            window.location.href = '/petty-cash/new';
            return;
        }

        // 'g' — start a vim-style two-key sequence. The next key that
        // matches one of the cases below will navigate.
        if (e.key === 'g' && !e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
            window.__nepGoMode = true;
            setTimeout(function () { window.__nepGoMode = false; }, 1000);
            return;
        }
        if (window.__nepGoMode && !e.altKey && !e.ctrlKey && !e.metaKey && !e.shiftKey) {
            window.__nepGoMode = false;
            switch (e.key) {
                case 'h': window.location.href = '/'; e.preventDefault(); break;
                case 'a': window.location.href = '/approvals'; e.preventDefault(); break;
                case 'p': window.location.href = '/petty-cash'; e.preventDefault(); break;
                case 'r': window.location.href = '/reports'; e.preventDefault(); break;
                case 'j': window.location.href = '/journal'; e.preventDefault(); break;
            }
        }
    });

    // Compatibility shim — the GlobalSearch component invokes this from
    // C# on Escape. Keeping it here means the JS lives in one place and
    // GlobalSearch doesn't need its own <script>.
    window.nickfinance_blurGs = function () {
        var el = document.getElementById('global-search-input');
        if (el && typeof el.blur === 'function') el.blur();
    };
})();
