// Keeping the page alive across tab switches and phone sleep.
//
// Blazor Server holds the UI in a circuit on the server, tied to a WebSocket. Put the
// tab in the background - or lock the phone - and the socket drops while the page is
// suspended, so the client's retry budget is spent on a page nobody is looking at. You
// come back to a dead page and a Reload link.
//
// Three changes here:
//   * retry on our own schedule rather than giving up after a fixed burst;
//   * reconnect the moment the tab is visible again, instead of waiting for a timer
//     that was frozen along with the page;
//   * when the server has genuinely dropped the circuit, reload silently rather than
//     leaving a dead page. All real state lives on the server in the receiver
//     registry, so a reload costs nothing but a repaint, and the view is restored
//     from the URL.

(() => {
    const banner = document.getElementById('reconnect-banner');
    const label = document.getElementById('reconnect-label');

    let down = false;
    let attempting = false;
    let reloading = false;
    let attempts = 0;

    const show = text => {
        if (!banner) return;
        if (label) label.textContent = text;
        banner.classList.add('visible');
    };

    const hide = () => banner && banner.classList.remove('visible');

    const reload = () => {
        if (reloading) return;
        reloading = true;
        show('Reloading');
        location.reload();
    };

    // Back off gently, then keep trying slowly - a phone can be away for hours.
    const delayFor = n => (n < 5 ? 500 : n < 15 ? 2000 : 5000);

    async function attempt() {
        if (attempting || reloading || !down) return;
        attempting = true;

        try {
            // false means the server no longer has our circuit, so there is nothing
            // to reconnect to and only a reload will do.
            const resumed = await Blazor.reconnect();
            if (resumed === false) { reload(); return; }
        } catch {
            // Server unreachable - it may still be starting up, so keep trying.
        } finally {
            attempting = false;
        }

        if (down && !reloading) {
            attempts++;
            show('Reconnecting');
            setTimeout(attempt, delayFor(attempts));
        }
    }

    const handler = {
        onConnectionDown: () => {
            if (down) return;
            down = true;
            attempts = 0;
            show('Reconnecting');
            attempt();
        },
        onConnectionUp: () => {
            down = false;
            attempts = 0;
            hide();
        },
    };

    // The moment the page is back in front of someone, try immediately: any timer we
    // scheduled was frozen while the tab was in the background.
    const wakeUp = () => {
        if (document.visibilityState !== 'visible' || !down) return;
        attempts = 0;
        attempt();
    };

    document.addEventListener('visibilitychange', wakeUp);
    window.addEventListener('focus', wakeUp);
    window.addEventListener('online', wakeUp);

    // Restored from the back/forward cache: the socket is definitely gone.
    window.addEventListener('pageshow', event => {
        if (event.persisted && down) attempt();
    });

    // blazor.server.js takes these at the top level. Nesting them under `circuit`
    // is the blazor.web.js shape: it is accepted silently, the handler below is
    // never installed, and the page dies quietly on the first disconnect. The
    // nested copy is here only so this keeps working if the app ever moves to the
    // unified script.
    const options = {
        reconnectionHandler: handler,
        reconnectionOptions: { maxRetries: 0, retryIntervalMilliseconds: 1000 },
    };

    Blazor.start({ ...options, circuit: { ...options } })
        .catch(() => setTimeout(reload, 2000));
})();
