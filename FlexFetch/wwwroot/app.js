// Shared helpers for all FlexFetch pages: included before each page's
// inline script (which relies on these globals).

function esc(s) {
    return (s || '').replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function fmtSize(bytes) {
    if (bytes == null) return '-';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) { bytes /= 1024; i++; }
    return (i ? bytes.toFixed(1) : bytes) + ' ' + units[i];
}

// Copies text to the clipboard, falling back to a temporary input +
// execCommand where the async Clipboard API is unavailable (insecure
// context, e.g. plain-http LAN access). Resolves true on success.
async function copyTextToClipboard(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch (e) {
        const tmp = document.createElement('textarea');
        tmp.value = text;
        tmp.style.position = 'fixed';
        tmp.style.opacity = '0';
        document.body.appendChild(tmp);
        tmp.select();
        const ok = document.execCommand('copy');
        tmp.remove();
        return ok;
    }
}
