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
        previous: null,   // the track we last switched away from
        next: null,       // video id to play when the current one ends
        repeatOne: false,
        volume: null,     // 0..1, re-applied when a new track loads
        retries: 0,
        loadedAt: 0,      // when the app last asked for a track (performance.now())
        lastSent: 0,
        lastVideoId: null,
        ready: false,
    });

    // After asking for a track, YouTube Music can keep reporting the old one for a moment.
    const SETTLE_MS = 4000;
    // For this long after the app picks a track, anything else playing is YouTube Music interfering
    // (e.g. its autoplay racing ours) and is put back. After it, a change means YouTube Music moved
    // on by itself (its crossfade starts the next song shortly before the current one ends).
    const GUARD_MS = 15000;

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

    // Switch the page to a track and remember it as the one that should be playing.
    function switchTo(videoId) {
        if (td.expected && td.expected !== '__stopped__' && td.expected !== videoId) td.previous = td.expected;
        td.expected = videoId;
        td.retries = 0;
        td.loadedAt = performance.now();
        return navigate(videoId);
    }

    td.load = (videoId, next) => {
        td.next = next || null;
        return switchTo(videoId) ? 'ok' : 'not-ready';
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

    // Move on to the app's next track, or stop at the end of the queue.
    function advance(reason) {
        if (td.next) {
            const n = td.next;
            td.next = null;
            post({ type: 'advanced', videoId: n, reason });
            switchTo(n);
        } else {
            td.expected = '__stopped__';
            td.pause();
            post(snapshot('ended'));
        }
    }

    // Make sure what's playing is what the app expects. Runs on 'playing' and every second, because
    // YouTube Music can change tracks with no media events at all: its crossfade starts the next song
    // a few seconds before the current one ends, without an 'ended' event.
    function reconcile() {
        const id = currentId();
        if (!id || !td.expected || isAd()) return;

        if (td.expected === '__stopped__') {
            // End of the queue: don't let YouTube Music's autoplay carry on.
            const v = video();
            if (v && !v.paused) td.pause();
            return;
        }
        if (id === td.expected) {
            td.retries = 0;
            return;
        }

        const now = performance.now();
        const sinceLoad = now - td.loadedAt;
        if (sinceLoad < SETTLE_MS) return; // brief in-between states while a switch completes

        if (sinceLoad < GUARD_MS || id === td.previous) {
            // Either the page hasn't left the old track, or something else took over right after the
            // app chose this one. Put the app's choice back (a few times, spaced out), then report it.
            if (now - (td.lastNudge || 0) < 2500) return;
            td.lastNudge = now;
            if (td.retries < 3) {
                td.retries++;
                navigate(td.expected);
            } else if (td.retries === 3) {
                td.retries++;
                post({ type: 'error', code: -2, message: "YouTube Music didn't switch to the selected song" });
            }
            return;
        }

        // Well into the track, YouTube Music moved on by itself. If it picked the app's next song (the
        // usual case with crossfade), accept it so the transition stays seamless; otherwise follow
        // the app's queue.
        if (td.repeatOne) {
            switchTo(td.expected);
        } else if (id === td.next) {
            td.next = null;
            td.previous = td.expected;
            td.expected = id;
            td.retries = 0;
            td.loadedAt = performance.now();
            post({ type: 'advanced', videoId: id, reason: 'page' });
        } else {
            advance('page');
        }
    }

    document.addEventListener('playing', reconcile, true);

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

    document.addEventListener('ended', (e) => {
        const v = video();
        if (isAd() || !contentEnded(v) || !td.expected || td.expected === '__stopped__') return;
        // The song really finished and the app decides what's next: keep the event from reaching
        // YouTube Music's player (we listen in the capture phase, before it does), so its own autoplay
        // doesn't also advance and skip a song.
        e.stopImmediatePropagation();
        if (td.repeatOne && v) {
            v.currentTime = 0;
            v.play();
            return;
        }
        if (currentId() && currentId() !== td.expected) return; // a stray track; reconcile() handles it
        advance('ended');
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
        reconcile();

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
