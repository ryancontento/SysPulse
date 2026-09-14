// BlazorWebView.Wpf renders through WebView2CompositionControl, which can miss presenting the last frame
// after a DOM update when nothing else on screen changes. The page then looks stuck (e.g. a loading
// placeholder that's already gone) until the mouse moves. After any DOM change, paint one more frame by
// nudging a single corner pixel by one shade, so the latest content always reaches the screen.
(() => {
    const colors = ['#0e0d0b', '#0e0d0c']; // --bg-sidebar and an imperceptibly different shade
    const nudge = document.createElement('div');
    nudge.setAttribute('aria-hidden', 'true');
    nudge.style.cssText = `position:fixed;left:0;bottom:0;width:1px;height:1px;pointer-events:none;z-index:2147483647;background:${colors[0]}`;
    document.body.appendChild(nudge);

    let flip = 0;
    let scheduled = false;

    new MutationObserver((mutations) => {
        if (scheduled || mutations.every((m) => m.target === nudge)) {
            return;
        }

        scheduled = true;
        setTimeout(() => {
            scheduled = false;
            requestAnimationFrame(() => {
                flip ^= 1;
                nudge.style.background = colors[flip];
            });
        }, 50);
    }).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
})();
