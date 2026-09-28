/*
 * ARCHITECTURE.md 2.8, 6, 6.1 — a Web kliensbe injektált EmbySyncPlay felület kontrollere.
 *
 * FONTOS (élő NAS-teszten, 2026-09-28, derült ki): az Emby SPA router a menübe rakott
 * oldalakat (EnableInMainMenu/EnableInUserMenu) "view"-ként tölti be, aminek KÖTELEZŐ egy
 * AMD-modult visszaadnia, ami a 'baseView' osztályból származik — sima <script src="...">
 * NEM működik itt (a HTML-be injektált <script> tag nem futna le, és a router
 * "instance is undefined" hibával elszállna kontroller nélkül). Ezt a mintát egy a
 * szerveren már ténylegesen működő plugin (Emby.Server.CinemaMode) élő forrásából
 * azonosítottuk be — onResume/onPause a nézet láthatóság-váltásaihoz, `view` a gyökér
 * DOM elem (nem a globális document).
 */
define(['baseView'], function (BaseView) {
    'use strict';

    var STORAGE_KEY_DEVICE_ID = 'embysyncplay-device-id';
    var STORAGE_KEY_OVERLAY = 'embysyncplay-overlay-enabled';

    // -----------------------------------------------------------------
    // Chat-overlay a videó felett — ARCHITECTURE.md 6.1. Ez a nézet-lifecycle-től
    // FÜGGETLENÜL, egyszer, a modul betöltésekor indul, mert a lejátszó bármikor
    // megjelenhet a DOM-ban, nem csak akkor, ha épp ez a config oldal aktív.
    // -----------------------------------------------------------------

    var overlayEnabled = localStorage.getItem(STORAGE_KEY_OVERLAY) === 'true';
    var overlayElement = null;
    var playerObserver = null;

    function ensurePlayerObserver() {
        if (playerObserver) return;

        playerObserver = new MutationObserver(function () {
            var playerContainer = document.querySelector('.htmlvideoplayer, .videoPlayerContainer');
            if (playerContainer && overlayEnabled && !overlayElement) {
                createOverlay(playerContainer);
            } else if (!playerContainer && overlayElement) {
                removeOverlay();
            }
        });

        playerObserver.observe(document.body, { childList: true, subtree: true });
    }

    function createOverlay(playerContainer) {
        overlayElement = document.createElement('div');
        overlayElement.className = 'syncplay-chat-overlay';
        overlayElement.style.cssText =
            'position:absolute; right:1em; bottom:4em; width:280px; max-height:40%; ' +
            'overflow-y:auto; background:rgba(0,0,0,0.55); color:#fff; font-size:0.85em; ' +
            'padding:0.5em; border-radius:6px; pointer-events:none; z-index:1000;';
        playerContainer.appendChild(overlayElement);
    }

    function removeOverlay() {
        if (overlayElement && overlayElement.parentNode) {
            overlayElement.parentNode.removeChild(overlayElement);
        }
        overlayElement = null;
    }

    function renderOverlayMessage(message) {
        if (!overlayEnabled || !overlayElement) return;
        var line = document.createElement('div');
        line.textContent = message.Monogram + ': ' + message.Text;
        overlayElement.appendChild(line);
        overlayElement.scrollTop = overlayElement.scrollHeight;
    }

    ensurePlayerObserver();

    // -----------------------------------------------------------------
    // Eszköz-azonosítás — ARCHITECTURE.md 3.2.
    // -----------------------------------------------------------------

    function getEmbyDeviceId() {
        if (window.ApiClient && typeof ApiClient.deviceId === 'function') {
            return ApiClient.deviceId();
        }
        var fallback = localStorage.getItem(STORAGE_KEY_DEVICE_ID);
        if (!fallback) {
            fallback = 'dev-' + Math.random().toString(36).slice(2);
            localStorage.setItem(STORAGE_KEY_DEVICE_ID, fallback);
        }
        return fallback;
    }

    function apiUrl(path) {
        return window.ApiClient ? ApiClient.getUrl(path) : path;
    }

    // Élő NAS-teszten (2026-09-28) derült ki: a natív böngésző EventSource API nem tud
    // egyéni fejlécet (X-Emby-Token) küldeni, ezért az SSE stream 401-et adott — a tokent
    // explicit query paraméterként kell mellékelni.
    function apiUrlWithToken(path) {
        return window.ApiClient
            ? ApiClient.getUrl(path, { 'X-Emby-Token': ApiClient.accessToken() })
            : path;
    }

    function apiGet(path) {
        return ApiClient.ajax({ type: 'GET', url: apiUrl(path), dataType: 'json' });
    }

    function apiPost(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: apiUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    // A szerver Play/Pause/Seek parancsokat csak akkor tud küldeni ennek a böngésző-
    // kliensnek, ha ismeri a MEGLÉVŐ Emby ISessionManager session-jét (SessionInfo.Id) —
    // ez NEM ugyanaz, mint az ApiClient.deviceId(). A core Emby /Sessions végponton
    // deviceId szerint szűrve kérdezzük le a sajátunkat.
    function getCurrentEmbySessionId() {
        return ApiClient.ajax({
            type: 'GET',
            url: apiUrl('Sessions?DeviceId=' + encodeURIComponent(getEmbyDeviceId())),
            dataType: 'json'
        }).then(function (sessions) {
            return sessions && sessions.length ? sessions[0].Id : null;
        });
    }

    // -----------------------------------------------------------------
    // View — a syncplay.html gyökér elemének kontrollere.
    // -----------------------------------------------------------------

    function View(view, params) {
        BaseView.apply(this, arguments);

        var self = this;
        self.view = view;
        self.currentSessionId = null;
        self.eventSource = null;

        view.querySelector('#syncplay-chat-form').addEventListener('submit', function (e) {
            e.preventDefault();
            if (!self.currentSessionId) return;

            var input = view.querySelector('#syncplay-chat-input');
            var text = (input.value || '').trim();
            if (!text) return;

            apiPost('EmbySyncPlay/Sessions/' + self.currentSessionId + '/Chat', { text: text });
            input.value = '';
        });

        view.querySelector('#syncplay-leave-btn').addEventListener('click', function () {
            if (!self.currentSessionId) return;

            apiPost('EmbySyncPlay/Sessions/' + self.currentSessionId + '/Leave', {
                deviceId: getEmbyDeviceId()
            }).then(function () {
                self.currentSessionId = null;
                view.querySelector('#syncplay-active-party').style.display = 'none';
                if (self.eventSource) self.eventSource.close();
            });
        });

        view.querySelector('#syncplay-overlay-toggle').addEventListener('change', function (e) {
            overlayEnabled = e.target.checked;
            localStorage.setItem(STORAGE_KEY_OVERLAY, String(overlayEnabled));
            if (!overlayEnabled) removeOverlay();
        });

        view.querySelector('#syncplay-create-form').addEventListener('submit', function (e) {
            e.preventDefault();

            var itemId = view.querySelector('#syncplay-create-itemid').value.trim();
            if (!itemId) return;

            self.createSession(
                itemId,
                view.querySelector('#syncplay-create-mediakind').value,
                view.querySelector('#syncplay-create-visibility').value
            );
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);

        var view = this.view;
        view.querySelector('#syncplay-overlay-toggle').checked = overlayEnabled;

        this.loadSessions();
    };

    View.prototype.onPause = function () {
        if (this.eventSource) {
            this.eventSource.close();
            this.eventSource = null;
        }
        BaseView.prototype.onPause.apply(this, arguments);
    };

    // -----------------------------------------------------------------
    // Party-lista — ARCHITECTURE.md 5. pont
    // -----------------------------------------------------------------

    View.prototype.loadSessions = function () {
        var self = this;
        apiGet('EmbySyncPlay/Sessions').then(function (sessions) {
            self.renderSessionList(sessions);
        });
    };

    View.prototype.renderSessionList = function (sessions) {
        var self = this;
        var container = this.view.querySelector('#syncplay-sessions');
        if (!container) return;
        container.innerHTML = '';

        if (!sessions || !sessions.length) {
            container.textContent = 'Jelenleg nincs aktív, kiajánlott közös nézés.';
            return;
        }

        sessions.forEach(function (session) {
            var row = document.createElement('div');
            row.className = 'syncplay-session-row';
            row.textContent = (session.HostUserId || '?') + ' — ' + session.ItemId + ' (' + session.MediaKind + ') ';

            var joinBtn = document.createElement('button');
            joinBtn.textContent = 'Csatlakozom';
            joinBtn.onclick = function () { self.joinSession(session.Id, 'Watching'); };

            var chatOnlyBtn = document.createElement('button');
            chatOnlyBtn.textContent = 'Csak chat';
            chatOnlyBtn.title = 'Ha egy másik eszközön amúgy is nézed — ARCHITECTURE.md 3.3';
            chatOnlyBtn.onclick = function () { self.joinSession(session.Id, 'ChatOnly'); };

            row.appendChild(joinBtn);
            row.appendChild(chatOnlyBtn);
            container.appendChild(row);
        });
    };

    // -----------------------------------------------------------------
    // Party indítása — ARCHITECTURE.md 2.4, 5. pont
    // -----------------------------------------------------------------

    View.prototype.createSession = function (itemId, mediaKind, visibility) {
        var self = this;
        var view = this.view;

        getCurrentEmbySessionId().then(function (embySessionId) {
            return apiPost('EmbySyncPlay/Sessions', {
                itemId: itemId,
                mediaKind: mediaKind,
                visibility: visibility,
                deviceId: getEmbyDeviceId(),
                embySessionId: embySessionId
            });
        }).then(function (session) {
            self.currentSessionId = session.Id;
            view.querySelector('#syncplay-active-party').style.display = '';
            view.querySelector('#syncplay-party-title').textContent = 'Party: ' + session.ItemId;
            self.renderParticipants(session.Participants);
            self.loadChatHistory(session.Id);
            self.connectStream();
            self.loadSessions();
        });
    };

    // -----------------------------------------------------------------
    // Csatlakozás / kilépés
    // -----------------------------------------------------------------

    View.prototype.joinSession = function (sessionId, mode) {
        var self = this;
        var view = this.view;

        (mode === 'Watching' ? getCurrentEmbySessionId() : Promise.resolve(null)).then(function (embySessionId) {
            return apiPost('EmbySyncPlay/Sessions/' + sessionId + '/Join', {
                deviceId: getEmbyDeviceId(),
                embySessionId: embySessionId,
                mode: mode
            });
        }).then(function (session) {
            self.currentSessionId = sessionId;
            view.querySelector('#syncplay-active-party').style.display = '';
            view.querySelector('#syncplay-party-title').textContent = 'Party: ' + session.ItemId;
            self.renderParticipants(session.Participants);
            self.loadChatHistory(sessionId);
            self.connectStream();
        });
    };

    View.prototype.renderParticipants = function (participants) {
        var container = this.view.querySelector('#syncplay-participants');
        if (!container) return;
        container.innerHTML = '';

        // Userenkénti csoportosítás — ARCHITECTURE.md 3.3 vége:
        // "Péter — Nézi: Nappali TV · Chat: Telefonom"
        var byUser = {};
        (participants || []).forEach(function (p) {
            byUser[p.UserId] = byUser[p.UserId] || { watching: [], chatOnly: [] };
            if (p.Mode === 'Watching') byUser[p.UserId].watching.push(p.DeviceLabel);
            else byUser[p.UserId].chatOnly.push(p.DeviceLabel);
        });

        Object.keys(byUser).forEach(function (userId) {
            var entry = byUser[userId];
            var parts = [];
            if (entry.watching.length) parts.push('Nézi: ' + entry.watching.join(', '));
            if (entry.chatOnly.length) parts.push('Chat: ' + entry.chatOnly.join(', '));

            var row = document.createElement('div');
            row.textContent = '👤 ' + userId + ' — ' + parts.join(' · ');
            container.appendChild(row);
        });
    };

    // -----------------------------------------------------------------
    // Chat — ARCHITECTURE.md 6. és 3.4 pont: monogramos, tömör sorok
    // -----------------------------------------------------------------

    View.prototype.loadChatHistory = function (sessionId) {
        var self = this;
        apiGet('EmbySyncPlay/Sessions/' + sessionId + '/Chat').then(function (messages) {
            var log = self.view.querySelector('#syncplay-chat-log');
            log.innerHTML = '';
            (messages || []).forEach(function (m) { self.appendChatMessage(m); });
        });
    };

    View.prototype.appendChatMessage = function (message) {
        var log = this.view.querySelector('#syncplay-chat-log');
        if (!log) return;
        var line = document.createElement('div');
        line.className = 'syncplay-chat-line';
        line.textContent = message.Monogram + ': ' + message.Text;
        log.appendChild(line);
        log.scrollTop = log.scrollHeight;

        renderOverlayMessage(message);
    };

    // -----------------------------------------------------------------
    // SSE — ARCHITECTURE.md 2.5. Élő frissítés a chatre és a party-állapotra.
    // -----------------------------------------------------------------

    View.prototype.connectStream = function () {
        var self = this;
        if (self.eventSource) self.eventSource.close();

        self.eventSource = new EventSource(apiUrlWithToken('EmbySyncPlay/Stream'));

        self.eventSource.addEventListener('party.chat_message', function (e) {
            self.appendChatMessage(JSON.parse(e.data));
        });

        self.eventSource.addEventListener('party.playstate_changed', function (e) {
            var session = JSON.parse(e.data);
            if (session.Id === self.currentSessionId) {
                self.renderParticipants(session.Participants);
            }
        });

        self.eventSource.addEventListener('party.invited', function (e) {
            var payload = JSON.parse(e.data);
            self.showToast('Meghívtak egy közös nézésbe: ' + payload.itemId);
            self.loadSessions();
        });

        self.eventSource.addEventListener('party.removed', function () {
            self.loadSessions();
        });
    };

    View.prototype.showToast = function (text) {
        if (window.Dashboard && typeof Dashboard.alert === 'function') {
            Dashboard.alert(text);
        } else {
            console.log('[EmbySyncPlay]', text);
        }
    };

    return View;
});
