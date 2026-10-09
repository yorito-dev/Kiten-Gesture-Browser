using System.Text.Json;
using TabBrowser.Models;

namespace TabBrowser.Services;

internal static class TabAudioScriptService
{
    public static bool IsSuccessfulTabAudioBoostResult(
        string? resultJson,
        int expectedAudioBoostDecibels)
    {
        try
        {
            using var outerDocument = JsonDocument.Parse(resultJson ?? string.Empty);
            if (outerDocument.RootElement.ValueKind == JsonValueKind.String)
            {
                using var innerDocument = JsonDocument.Parse(
                    outerDocument.RootElement.GetString() ?? string.Empty);
                return IsSuccessfulTabAudioBoostResult(
                    innerDocument.RootElement,
                    expectedAudioBoostDecibels);
            }

            return IsSuccessfulTabAudioBoostResult(
                outerDocument.RootElement,
                expectedAudioBoostDecibels);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSuccessfulTabAudioBoostResult(
        JsonElement root,
        int expectedAudioBoostDecibels)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("ok", out var ok)
            && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
            && ok.GetBoolean()
            && root.TryGetProperty("boostDecibels", out var boostDecibels)
            && boostDecibels.ValueKind == JsonValueKind.Number
            && boostDecibels.TryGetInt32(out var value)
            && value == AudioBoostProfile.Normalize(expectedAudioBoostDecibels);
    }

    public static bool HasAppliedTabAudioBoostResult(
        string? resultJson,
        int expectedAudioBoostDecibels)
    {
        try
        {
            using var outerDocument = JsonDocument.Parse(resultJson ?? string.Empty);
            if (outerDocument.RootElement.ValueKind == JsonValueKind.String)
            {
                using var innerDocument = JsonDocument.Parse(
                    outerDocument.RootElement.GetString() ?? string.Empty);
                return HasAppliedTabAudioBoostResult(
                    innerDocument.RootElement,
                    expectedAudioBoostDecibels);
            }

            return HasAppliedTabAudioBoostResult(
                outerDocument.RootElement,
                expectedAudioBoostDecibels);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasAppliedTabAudioBoostResult(
        JsonElement root,
        int expectedAudioBoostDecibels)
    {
        return IsSuccessfulTabAudioBoostResult(root, expectedAudioBoostDecibels)
            && root.TryGetProperty("applied", out var applied)
            && applied.ValueKind == JsonValueKind.Number
            && applied.TryGetInt32(out var appliedCount)
            && appliedCount > 0;
    }

    public static string BuildTabAudioPanBootstrapScript()
    {
        return """
            (() => {
                const stateKey = "__tabBrowserAudioPanState";
                const installedKey = "__tabBrowserAudioPanBootstrapInstalled";
                if (window[installedKey]) {
                    return;
                }

                window[installedKey] = true;

                const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
                const AudioNodeCtor = window.AudioNode;
                const state = window[stateKey] = window[stateKey] || {};
                state.targetPan = Number.isFinite(state.targetPan) ? state.targetPan : 0;
                state.entries = state.entries || new WeakMap();
                state.context = state.context || null;
                state.observer = state.observer || null;
                state.routeList = state.routeList || [];
                state.routesByContext = state.routesByContext || new WeakMap();
                state.routesBySource = state.routesBySource || new WeakMap();

                window.__tabBrowserSetAudioPan = pan => {
                    const nextPan = Math.max(-1, Math.min(1, Number(pan) || 0));
                    state.targetPan = nextPan;
                    for (const route of state.routeList) {
                        try {
                            route.panner.pan.value = nextPan;
                            if (route.context.state === "suspended") {
                                route.context.resume().catch(() => {});
                            }
                        } catch {
                        }
                    }
                };

                if (!AudioContextCtor || !AudioNodeCtor || !AudioNodeCtor.prototype) {
                    return;
                }

                const prototype = AudioNodeCtor.prototype;
                if (prototype.__tabBrowserAudioPanPatched) {
                    return;
                }

                const originalConnect = prototype.connect;
                const originalDisconnect = prototype.disconnect;
                if (typeof originalConnect !== "function" || typeof originalDisconnect !== "function") {
                    return;
                }

                Object.defineProperty(prototype, "__tabBrowserAudioPanPatched", {
                    value: true,
                    configurable: false
                });

                const isDestination = destination => {
                    return destination
                        && destination.context
                        && destination === destination.context.destination;
                };

                const ensureRoute = context => {
                    if (!context || !context.createStereoPanner) {
                        return null;
                    }

                    let route = state.routesByContext.get(context);
                    if (route) {
                        return route;
                    }

                    try {
                        const panner = context.createStereoPanner();
                        panner.pan.value = state.targetPan;
                        originalConnect.call(panner, context.destination);
                        route = { context, panner };
                        state.routesByContext.set(context, route);
                        state.routeList.push(route);
                        return route;
                    } catch {
                        return null;
                    }
                };

                prototype.connect = function(destination, ...args) {
                    if (isDestination(destination)) {
                        const route = ensureRoute(destination.context);
                        if (route) {
                            const output = args.length >= 1 ? args[0] : 0;
                            try {
                                originalConnect.call(this, route.panner, output, 0);
                                state.routesBySource.set(this, route);
                                return destination;
                            } catch {
                            }
                        }
                    }

                    return originalConnect.call(this, destination, ...args);
                };

                prototype.disconnect = function(destination, ...args) {
                    if (arguments.length === 0) {
                        return originalDisconnect.call(this);
                    }

                    if (isDestination(destination)) {
                        const route = state.routesBySource.get(this);
                        if (route) {
                            try {
                                originalDisconnect.call(this, route.panner);
                                state.routesBySource.delete(this);
                                return;
                            } catch {
                            }
                        }
                    }

                    return originalDisconnect.call(this, destination, ...args);
                };
            })();
            """;
    }

    public static string BuildTabAudioPanScript(
        TabAudioPan audioPan,
        int audioBoostDecibels = 0)
    {
        var pan = audioPan switch
        {
            TabAudioPan.Left => -1,
            TabAudioPan.Right => 1,
            _ => 0
        };
        var panJson = JsonSerializer.Serialize(pan);
        var normalizedAudioBoostDecibels = AudioBoostProfile.Normalize(audioBoostDecibels);
        var audioBoostDecibelsJson = JsonSerializer.Serialize(normalizedAudioBoostDecibels);
        var audioBoostGainJson = JsonSerializer.Serialize(
            AudioBoostProfile.GetLinearGain(normalizedAudioBoostDecibels));

        return BuildTabAudioPanBootstrapScript() + $$"""
            (async () => {
                const targetPan = {{panJson}};
                const targetBoostDecibels = {{audioBoostDecibelsJson}};
                const targetBoostGain = {{audioBoostGainJson}};
                const stateKey = "__tabBrowserAudioPanState";
                const AudioContextCtor = window.AudioContext || window.webkitAudioContext;

                if (typeof window.__tabBrowserSetAudioPan === "function") {
                    window.__tabBrowserSetAudioPan(targetPan);
                }

                if (!AudioContextCtor) {
                    return JSON.stringify({ ok: false, reason: "AudioContext unavailable" });
                }

                let state = window[stateKey];
                if (!state && targetPan === 0 && targetBoostDecibels === 0) {
                    return JSON.stringify({ ok: true, mode: "native", applied: 0, boostDecibels: 0 });
                }

                if (!state) {
                    state = {
                        context: null,
                        entries: new WeakMap(),
                        observer: null,
                        entryCount: 0,
                        targetPan,
                        targetBoostDecibels,
                        targetBoostGain
                    };
                    window[stateKey] = state;
                }

                state.entries = state.entries || new WeakMap();
                state.context = state.context || null;
                state.observer = state.observer || null;
                state.entryCount = Number.isFinite(state.entryCount) ? state.entryCount : 0;
                state.targetPan = targetPan;
                state.targetBoostDecibels = targetBoostDecibels;
                state.targetBoostGain = targetBoostGain;

                const ensureContext = () => {
                    if (!state.context) {
                        state.context = new AudioContextCtor();
                    }
                    return state.context;
                };

                const resumeContext = async () => {
                    try {
                        if (state.context && state.context.state === "suspended") {
                            await state.context.resume();
                        }
                    } catch {
                    }
                };

                if (state.targetBoostDecibels > 0) {
                    ensureContext();
                }

                const updateEntry = entry => {
                    const now = entry.context.currentTime;
                    entry.panner.pan.setValueAtTime(state.targetPan, now);
                    entry.directGain.gain.setValueAtTime(
                        state.targetBoostDecibels > 0 ? 0 : 1,
                        now);
                    entry.boostGain.gain.setValueAtTime(
                        state.targetBoostDecibels > 0 ? state.targetBoostGain : 0,
                        now);
                };

                const canRouteMediaElement = element => {
                    if (element.mediaKeys) {
                        return false;
                    }

                    if (element.srcObject) {
                        return true;
                    }

                    const source = element.currentSrc || element.src;
                    if (!source) {
                        return false;
                    }

                    try {
                        const sourceUrl = new URL(source, document.baseURI);
                        if (sourceUrl.protocol === "blob:" || sourceUrl.protocol === "data:") {
                            return true;
                        }

                        return sourceUrl.origin === window.location.origin
                            || element.crossOrigin !== null;
                    } catch {
                        return false;
                    }
                };

                const applyElement = element => {
                    if (!element
                        || element.dataset.tabBrowserAudioPanUnsupported === "1") {
                        return false;
                    }

                    try {
                        let entry = state.entries.get(element);
                        if (!entry
                            && state.targetPan === 0
                            && state.targetBoostDecibels === 0) {
                            return false;
                        }

                        if (!entry && !canRouteMediaElement(element)) {
                            return false;
                        }

                        if (!entry) {
                            const context = ensureContext();
                            if (!context.createStereoPanner) {
                                element.dataset.tabBrowserAudioPanUnsupported = "1";
                                return false;
                            }

                            const source = context.createMediaElementSource(element);
                            const panner = context.createStereoPanner();
                            const directGain = context.createGain();
                            const boostGain = context.createGain();
                            const compressor = context.createDynamicsCompressor();
                            compressor.threshold.value = -3;
                            compressor.knee.value = 12;
                            compressor.ratio.value = 6;
                            compressor.attack.value = 0.003;
                            compressor.release.value = 0.25;
                            try {
                                source.connect(directGain);
                                directGain.connect(panner);
                                source.connect(boostGain);
                                boostGain.connect(compressor);
                                compressor.connect(panner);
                                panner.connect(context.destination);
                            } catch {
                                try {
                                    source.disconnect();
                                    directGain.disconnect();
                                    boostGain.disconnect();
                                    compressor.disconnect();
                                    panner.disconnect();
                                    source.connect(context.destination);
                                } catch {
                                }
                                element.dataset.tabBrowserAudioPanUnsupported = "1";
                                return false;
                            }
                            entry = { context, source, panner, directGain, boostGain, compressor };
                            state.entries.set(element, entry);
                            state.entryCount++;
                            element.addEventListener("play", resumeContext, true);
                            element.addEventListener("playing", resumeContext, true);
                        }

                        updateEntry(entry);
                        resumeContext();
                        return true;
                    } catch {
                        element.dataset.tabBrowserAudioPanUnsupported = "1";
                        return false;
                    }
                };

                const applyAll = root => {
                    let applied = 0;
                    const visited = new Set();

                    const visit = currentRoot => {
                        if (!currentRoot || visited.has(currentRoot)) {
                            return;
                        }

                        visited.add(currentRoot);

                        if (currentRoot.matches && currentRoot.matches("audio,video") && applyElement(currentRoot)) {
                            applied++;
                        }

                        const elements = currentRoot.querySelectorAll
                            ? currentRoot.querySelectorAll("audio,video")
                            : [];
                        for (const element of elements) {
                            if (applyElement(element)) {
                                applied++;
                            }
                        }

                        const allElements = currentRoot.querySelectorAll
                            ? currentRoot.querySelectorAll("*")
                            : [];
                        for (const element of allElements) {
                            if (element.shadowRoot) {
                                visit(element.shadowRoot);
                            }
                        }

                        const frames = currentRoot.querySelectorAll
                            ? currentRoot.querySelectorAll("iframe,frame")
                            : [];
                        for (const frame of frames) {
                            try {
                                if (frame.contentDocument) {
                                    visit(frame.contentDocument);
                                }
                            } catch {
                            }
                        }
                    };

                    visit(root || document);

                    return applied;
                };

                if (!state.mediaEventHandlersInstalled) {
                    state.mediaEventHandlersInstalled = true;
                    const applyMediaEvent = event => {
                        if (event.target instanceof HTMLMediaElement) {
                            applyElement(event.target);
                        }
                    };
                    document.addEventListener("loadedmetadata", applyMediaEvent, true);
                    document.addEventListener("play", applyMediaEvent, true);
                    document.addEventListener("playing", applyMediaEvent, true);
                    document.addEventListener("yt-navigate-finish", () => applyAll(document), true);
                }

                if (!state.resumeHandlersInstalled) {
                    state.resumeHandlersInstalled = true;
                    document.addEventListener("pointerdown", resumeContext, true);
                    document.addEventListener("keydown", resumeContext, true);
                }

                if (state.targetPan !== 0 && !state.observer) {
                    state.observer = new MutationObserver(mutations => {
                        for (const mutation of mutations) {
                            for (const node of mutation.addedNodes) {
                                if (node && node.nodeType === Node.ELEMENT_NODE) {
                                    applyAll(node);
                                }
                            }
                        }
                    });
                    state.observer.observe(document.documentElement || document, {
                        childList: true,
                        subtree: true
                    });
                }

                const applied = applyAll(document);
                if (state.targetPan === 0
                    && state.targetBoostDecibels === 0
                    && state.entryCount === 0
                    && state.context) {
                    try {
                        await state.context.close();
                    } catch {
                    }
                    state.context = null;
                }
                if (state.targetPan !== 0) {
                    for (const delay of [100, 500, 1500, 3000]) {
                        window.setTimeout(() => applyAll(document), delay);
                    }
                }
                await resumeContext();
                const hasMediaElement = Boolean(document.querySelector("audio,video"));
                const audioContextReady = !state.context || state.context.state === "running";
                return JSON.stringify({
                    ok: targetBoostDecibels === 0
                        || ((applied > 0 || !hasMediaElement) && audioContextReady),
                    reason: targetBoostDecibels > 0 && !audioContextReady
                        ? "audio-context-suspended"
                        : targetBoostDecibels > 0 && applied === 0 && hasMediaElement
                            ? "audio-boost-unavailable"
                            : undefined,
                    pan: targetPan,
                    boostDecibels: targetBoostDecibels,
                    applied,
                    pending: targetBoostDecibels > 0 && !hasMediaElement
                });
            })();
            """;
    }
}
