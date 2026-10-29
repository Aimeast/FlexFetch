// Shared helpers for all FlexFetch pages: included before each page's
// inline script (which relies on these globals).

// URL of the directory holding the current page (origin plus the path up
// to the last '/'): under a virtual-path deployment the configured prefix
// is part of it. Absolute URLs that leave the page context (share links
// and download links copied for other people) must be built against this
// base; plain fetch/navigation references stay document-relative.
function pageDirectoryUrl() {
    return location.origin + location.pathname.replace(/[^/]*$/, '');
}

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
