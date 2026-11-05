// Tapedeck player bridge.
// Injected into the hidden music.youtube.com page. The native app owns the queue; this script
// asks YouTube Music's own web app to play one track at a time, reports playback state back,
// and advances to the next track the app nominated when a track ends.
(() => {
    // Only ever run in YouTube Music's own top-level page.
    if (window.top !== window || window.__td || location.protocol !== 'https:' || location.hostname !== 'music.youtube.com') return;

    const post = (msg) => {
        try { window.chrome.webview.postMessage(msg); } catch (_) { /* not hosted */ }
    };

    const td = (window.__td = {
        expected: null,   // video id the app wants playing; '__stopped__' blocks autoplay
        next: null,       // video id to play when the current one ends
        repeatOne: false,
        volume: null,     // 0..1, re-applied when a new track loads
        retries: 0,
        lastSent: 0,
        lastVideoId: null,
        ready: false,
    });

    const app = () => document.querySelector('ytmusic-app');
    const api = () => {
        const p = document.querySelector('ytmusic-player');
        return (p && p.playerApi) || document.getElementById('movie_player');
    };
    const video = () => document.querySelector('video');
    const currentId = () => {
        try {
            const d = api() && api().getVideoData && api().getVideoData();
            return (d && (d.video_id || d.videoId)) || null;
        } catch (_) {
            return null;
        }
    };
    const isAd = () => !!document.querySelector('#movie_player.ad-showing, .ytp-ad-player-overlay, .ytp-ad-module .ytp-ad-text');

    function snapshot(type) {
        const v = video();
        return {
            type: type || 'state',
            videoId: currentId(),
            playing: !!v && !v.paused && !v.ended,
            buffering: !!v && !v.paused && v.readyState < 3,
            position: v && isFinite(v.currentTime) ? v.currentTime : 0,
            duration: v && isFinite(v.duration) ? v.duration : 0,
            ad: isAd(),
        };
    }

    function send(force) {
        const now = performance.now();
        if (!force && now - td.lastSent < 240) return;
        td.lastSent = now;
        post(snapshot());
    }

    // YouTube Music's router listens for 'yt-navigate'; older builds also exposed resolveCommand().
    const isAppReady = (a) => !!a && (typeof a.resolveCommand === 'function' || !!a.queue);

    function navigate(videoId) {
        const a = app();
        if (!isAppReady(a)) return false;
        const endpoint = { watchEndpoint: { videoId } };
        if (typeof a.resolveCommand === 'function') {
            a.resolveCommand(endpoint);
        } else {
            a.dispatchEvent(new CustomEvent('yt-navigate', { bubbles: true, composed: true, detail: { endpoint } }));
        }
        return true;
    }

    function applyVolume() {
        if (td.volume == null) return;
        const p = api();
        try {
            if (p && p.setVolume) p.setVolume(Math.round(td.volume * 100));
            else if (video()) video().volume = td.volume;
        } catch (_) { /* ignore */ }
    }

    // ───────────── Commands called from the app via ExecuteScriptAsync ─────────────

    td.load = (videoId, next) => {
        td.expected = videoId;
        td.next = next || null;
        td.retries = 0;
        return navigate(videoId) ? 'ok' : 'not-ready';
    };

    td.setNext = (next, repeatOne) => {
        td.next = next || null;
        td.repeatOne = !!repeatOne;
        return 'ok';
    };

    td.play = () => {
        const p = api();
        if (td.expected === '__stopped__') td.expected = currentId();
        if (p && p.playVideo) p.playVideo(); else if (video()) video().play();
        return 'ok';
    };

    td.pause = () => {
        const p = api();
        if (p && p.pauseVideo) p.pauseVideo(); else if (video()) video().pause();
        return 'ok';
    };

    td.seek = (seconds) => {
        const v = video();
        if (v) v.currentTime = Math.max(0, seconds);
        send(true);
        return 'ok';
    };

    td.setVolume = (volume) => {
        td.volume = Math.max(0, Math.min(1, volume));
        applyVolume();
        return 'ok';
    };

    td.stop = () => {
        td.expected = '__stopped__';
        td.next = null;
        td.pause();
        return 'ok';
    };

    // ───────────── Media events (captured at the document; media events don't bubble) ─────────────

    document.addEventListener('timeupdate', () => send(false), true);
    for (const ev of ['play', 'pause', 'playing', 'waiting', 'durationchange', 'seeked', 'loadedmetadata']) {
        document.addEventListener(ev, () => send(true), true);
    }

    document.addEventListener('loadeddata', () => {
        applyVolume();
        send(true);
    }, true);

    // Keep YouTube Music from wandering off to its own autoplay suggestions.
    document.addEventListener('playing', () => {
        const id = currentId();
        if (!td.expected || !id || isAd()) return;
        if (td.expected === '__stopped__') {
            td.pause();
            return;
        }
        if (id !== td.expected) {
            if (td.retries < 3) {
                td.retries++;
                navigate(td.expected);
            } else {
                // Give up fighting; tell the app what's actually playing.
                td.expected = id;
                post({ type: 'advanced', videoId: id, unexpected: true });
            }
        }
    }, true);

    // True when the media that just ended is the track itself, not a pre-roll ad whose
    // "ad-showing" marker was already cleared: their lengths differ.
    function contentEnded(v) {
        const p = api();
        try {
            const contentDuration = p && p.getDuration ? p.getDuration() : 0;
            if (contentDuration > 0 && v && isFinite(v.duration) && Math.abs(v.duration - contentDuration) > 3) return false;
        } catch (_) { /* fall through */ }
        return true;
    }

    document.addEventListener('ended', () => {
        const v = video();
        if (isAd() || !contentEnded(v)) return;
        if (td.repeatOne && v) {
            v.currentTime = 0;
            v.play();
            return;
        }
        if (td.expected && currentId() && currentId() !== td.expected) return; // a stray track we're already replacing
        if (td.next) {
            const n = td.next;
            td.next = null;
            td.expected = n;
            td.retries = 0;
            post({ type: 'advanced', videoId: n });
            navigate(n);
        } else {
            td.expected = '__stopped__';
            post(snapshot('ended'));
        }
    }, true);

    document.addEventListener('error', (e) => {
        if (e.target && e.target.tagName === 'VIDEO') post({ type: 'error', code: (e.target.error && e.target.error.code) || 0 });
    }, true);

    // ───────────── Housekeeping ─────────────

    setInterval(() => {
        // Report track identity changes even when no media event fires (e.g. during ad → content handoff).
        const id = currentId();
        if (id !== td.lastVideoId) {
            td.lastVideoId = id;
            send(true);
        }

        // Dismiss "Video paused. Continue watching?" so long sessions keep playing.
        const stillThere = document.querySelector('ytmusic-you-there-renderer');
        if (stillThere && stillThere.offsetParent !== null) {
            const btn = stillThere.querySelector('button, yt-button-renderer, tp-yt-paper-button');
            if (btn) btn.click();
        }

        // Report unplayable tracks surfaced as an in-page error rather than a media error.
        const err = document.querySelector('#movie_player .ytp-error');
        if (err && err.offsetParent !== null && td.expected && td.expected !== '__stopped__' && !td.errorReported) {
            td.errorReported = true;
            post({ type: 'error', code: -1, message: (err.innerText || '').trim().slice(0, 200) });
        } else if (!err || err.offsetParent === null) {
            td.errorReported = false;
        }
    }, 1000);

    post({ type: 'log', message: 'bridge injected into ' + location.href });

    let readyChecks = 0;
    const readyTimer = setInterval(() => {
        const a = app();
        readyChecks++;
        if (readyChecks % 20 === 0) {
            post({ type: 'log', message: `waiting: app=${!!a} ready=${isAppReady(a)} api=${!!api()} url=${location.href}` });
        }
        // The player API object may only appear once something plays; the router is what we need.
        if (!isAppReady(a)) return;
        clearInterval(readyTimer);
        td.ready = true;
        let cfg = {};
        try {
            cfg = {
                clientVersion: window.ytcfg.get('INNERTUBE_CLIENT_VERSION'),
                visitorData: window.ytcfg.get('VISITOR_DATA'),
                loggedIn: !!window.ytcfg.get('LOGGED_IN'),
                hl: window.ytcfg.get('HL'),
                gl: window.ytcfg.get('GL'),
            };
        } catch (_) { /* older layout */ }
        post(Object.assign({ type: 'ready', href: location.href }, cfg));
    }, 250);
})();
