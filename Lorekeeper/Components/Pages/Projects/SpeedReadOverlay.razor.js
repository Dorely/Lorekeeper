// Client-side playback engine for the speed-reading overlay. The full chapter
// playlist arrives once per start() call; the timing loop, per-item DOM writes,
// and keyboard handling all stay in the browser so no per-word traffic crosses
// the Blazor circuit. .NET receives callbacks only on discrete events.

const KIND_WORD = 0;
const KIND_FIGURE = 1;
const KIND_DESIGNED_PAGE = 2;
const KIND_SCENE_BREAK = 3;

const FLAG_SENTENCE_START = 1;

const MIN_WPM = 150;
const MAX_WPM = 700;
const WPM_STEP = 25;
const RAMP_FACTOR = 0.6;
const RAMP_MS = 2000;
const FIGURE_DWELL_MS = 1600;
const DESIGNED_PAGE_DWELL_MS = 1200;
const SCENE_BREAK_DWELL_MS = 900;
const WPM_NOTIFY_DEBOUNCE_MS = 750;

export function create(stage, dotnet) {
    const reader = new SpeedReader(stage, dotnet);
    return {
        start: (payload, options, startItem) => reader.start(payload, options, startItem),
        setOptions: options => reader.setOptions(options),
        openQuickSettings: open => reader.setSuspended(open),
        requestEdit: () => reader.requestEdit(),
        requestExit: () => reader.requestExit(),
        dispose: () => reader.dispose(),
    };
}

class SpeedReader {
    constructor(stage, dotnet) {
        this.stage = stage;
        this.dotnet = dotnet;
        this.items = [];
        this.chunks = [];
        this.style = "Chunks";
        this.wpm = 300;
        this.position = 0;
        this.playing = false;
        this.suspended = false;
        this.completed = false;
        this.timer = null;
        this.rampEnd = 0;
        this.wpmNotifyTimer = null;
        this.disposed = false;
        this.keyHandler = event => this.onKeyDown(event);

        stage.innerHTML = "";
        this.wordHost = document.createElement("div");
        this.wordHost.className = "speed-read-word";
        this.statusHost = document.createElement("div");
        this.statusHost.className = "speed-read-status";
        this.progressHost = document.createElement("div");
        this.progressHost.className = "speed-read-progress";
        this.progressFill = document.createElement("div");
        this.progressFill.className = "speed-read-progress-fill";
        this.progressHost.appendChild(this.progressFill);
        stage.appendChild(this.wordHost);
        stage.appendChild(this.statusHost);
        stage.appendChild(this.progressHost);
        this.clickHandler = () => {
            if (!this.suspended && !this.completed)
                this.togglePause();
        };
        stage.addEventListener("click", this.clickHandler);
    }

    start(payload, options, startItem) {
        if (this.disposed)
            return;
        this.items = payload.items ?? [];
        this.chunks = payload.chunks ?? [];
        this.applyOptions(options);
        this.completed = false;
        this.suspended = false;
        this.position = this.positionForItem(startItem ?? 0);
        document.removeEventListener("keydown", this.keyHandler, true);
        document.addEventListener("keydown", this.keyHandler, true);
        if (this.steps().length === 0) {
            this.complete();
            return;
        }
        this.resume(true);
    }

    applyOptions(options) {
        if (!options)
            return;
        if (options.style === "Chunks" || options.style === "Rsvp") {
            const itemIndex = this.currentItemIndex();
            this.style = options.style;
            this.position = this.positionForItem(itemIndex);
        }
        if (Number.isFinite(options.wpm))
            this.wpm = clampWpm(options.wpm);
        this.renderStatus();
    }

    setOptions(options) {
        this.applyOptions(options);
        if (!this.playing && !this.completed)
            this.renderCurrent();
    }

    setSuspended(open) {
        this.suspended = open === true;
        if (this.suspended)
            this.stopTimer();
        else if (!this.completed)
            this.resume(true);
    }

    // The unit sequence for the active style: chunk ranges or individual items.
    steps() {
        return this.style === "Chunks" ? this.chunks : this.items;
    }

    stepItemIndex(position) {
        if (this.style === "Chunks") {
            const chunk = this.chunks[position];
            return chunk ? chunk.s : this.items.length;
        }
        return position;
    }

    positionForItem(itemIndex) {
        const bounded = Math.max(0, Math.min(itemIndex ?? 0, this.items.length - 1));
        if (this.style !== "Chunks")
            return bounded;
        for (let index = this.chunks.length - 1; index >= 0; index--) {
            if (this.chunks[index].s <= bounded)
                return index;
        }
        return 0;
    }

    currentItemIndex() {
        return this.stepItemIndex(Math.min(this.position, this.steps().length - 1));
    }

    resume(ramp) {
        if (this.disposed || this.suspended || this.completed)
            return;
        this.playing = true;
        if (ramp) {
            this.position = this.positionForItem(this.sentenceStartAt(this.currentItemIndex()));
            this.rampEnd = performance.now() + RAMP_MS;
        }
        this.renderCurrent();
        this.scheduleNext();
    }

    pause(notify) {
        this.playing = false;
        this.stopTimer();
        this.renderStatus();
        if (notify)
            this.invoke("OnPausedAsync", this.currentItemIndex());
    }

    togglePause() {
        if (this.playing)
            this.pause(true);
        else
            this.resume(true);
    }

    stopTimer() {
        if (this.timer !== null) {
            clearTimeout(this.timer);
            this.timer = null;
        }
    }

    scheduleNext() {
        this.stopTimer();
        if (!this.playing)
            return;
        const step = this.steps()[this.position];
        if (!step) {
            this.complete();
            return;
        }
        this.timer = setTimeout(() => this.advance(), this.dwellFor(step));
    }

    advance() {
        if (!this.playing)
            return;
        this.position++;
        if (this.position >= this.steps().length) {
            this.complete();
            return;
        }
        this.renderCurrent();
        this.scheduleNext();
    }

    complete() {
        this.playing = false;
        this.completed = true;
        this.stopTimer();
        this.renderCard("End of chapter", "speed-read-card-end");
        this.renderStatus();
        this.progressFill.style.width = "100%";
        this.invoke("OnChapterCompletedAsync");
    }

    dwellFor(step) {
        const item = this.style === "Chunks" ? this.items[step.s] : step;
        let dwell;
        if (item.k === KIND_FIGURE)
            dwell = FIGURE_DWELL_MS;
        else if (item.k === KIND_DESIGNED_PAGE)
            dwell = DESIGNED_PAGE_DWELL_MS;
        else if (item.k === KIND_SCENE_BREAK)
            dwell = SCENE_BREAK_DWELL_MS;
        else
            dwell = (step.w ?? 1) * (60000 / this.wpm);

        const now = performance.now();
        if (now < this.rampEnd) {
            const progress = 1 - (this.rampEnd - now) / RAMP_MS;
            const factor = RAMP_FACTOR + (1 - RAMP_FACTOR) * progress;
            dwell /= factor;
        }
        return dwell;
    }

    sentenceStartAt(itemIndex) {
        for (let index = Math.min(itemIndex, this.items.length - 1); index >= 0; index--) {
            const item = this.items[index];
            if (item.k !== KIND_WORD)
                return Math.min(index + 1, itemIndex);
            if ((item.f & FLAG_SENTENCE_START) !== 0)
                return index;
        }
        return 0;
    }

    rewindSentence() {
        if (this.completed)
            this.completed = false;
        const current = this.currentItemIndex();
        const currentStart = this.sentenceStartAt(current);
        const target = currentStart >= current && currentStart > 0
            ? this.sentenceStartAt(currentStart - 1)
            : currentStart;
        this.position = this.positionForItem(target);
        this.rampEnd = performance.now() + RAMP_MS;
        this.renderCurrent();
        if (this.playing)
            this.scheduleNext();
    }

    adjustWpm(delta) {
        this.wpm = clampWpm(this.wpm + delta);
        this.renderStatus();
        if (this.wpmNotifyTimer !== null)
            clearTimeout(this.wpmNotifyTimer);
        this.wpmNotifyTimer = setTimeout(() => {
            this.wpmNotifyTimer = null;
            this.invoke("OnWpmChangedAsync", this.wpm);
        }, WPM_NOTIFY_DEBOUNCE_MS);
    }

    requestEdit() {
        const itemIndex = this.currentItemIndex();
        this.pause(false);
        this.invoke("OnEditRequestedAsync", itemIndex);
    }

    requestExit() {
        const itemIndex = this.currentItemIndex();
        this.stop();
        this.invoke("OnExitedAsync", itemIndex);
    }

    onKeyDown(event) {
        if (this.disposed)
            return;
        const target = event.target;
        if (target instanceof HTMLElement
            && (target.isContentEditable
                || target.tagName === "INPUT"
                || target.tagName === "TEXTAREA"
                || target.tagName === "SELECT")) {
            if (event.key === "Escape" && this.suspended) {
                event.preventDefault();
                event.stopPropagation();
                this.invoke("OnQuickSettingsToggledAsync", false);
            }
            return;
        }

        if (this.suspended) {
            if (event.key === "Escape" || event.key === "s" || event.key === "S") {
                event.preventDefault();
                event.stopPropagation();
                this.invoke("OnQuickSettingsToggledAsync", false);
            }
            return;
        }

        let handled = true;
        switch (event.key) {
            case " ":
                this.togglePause();
                break;
            case "Escape":
                this.requestExit();
                break;
            case "s":
            case "S":
                this.pause(false);
                this.invoke("OnQuickSettingsToggledAsync", true);
                break;
            case "e":
            case "E":
                this.requestEdit();
                break;
            case "ArrowLeft":
            case "r":
            case "R":
                this.rewindSentence();
                break;
            case "ArrowUp":
            case "+":
            case "=":
                this.adjustWpm(WPM_STEP);
                break;
            case "ArrowDown":
            case "-":
                this.adjustWpm(-WPM_STEP);
                break;
            default:
                handled = false;
                break;
        }
        if (handled) {
            event.preventDefault();
            event.stopPropagation();
        }
    }

    renderCurrent() {
        const step = this.steps()[Math.min(this.position, this.steps().length - 1)];
        if (!step)
            return;
        const item = this.style === "Chunks" ? this.items[step.s] : step;
        if (item.k === KIND_FIGURE) {
            this.renderCard(item.t ? `Figure — ${item.t}` : "Figure", "speed-read-card-figure");
        } else if (item.k === KIND_DESIGNED_PAGE) {
            this.renderCard("Designed page — view it in Pages", "speed-read-card-page");
        } else if (item.k === KIND_SCENE_BREAK) {
            this.renderCard("* * *", "speed-read-card-scene");
        } else if (this.style === "Chunks") {
            const words = [];
            for (let index = step.s; index < step.s + step.n; index++)
                words.push(this.items[index].t);
            this.renderChunk(words.join(" "));
        } else {
            this.renderRsvpWord(item.t);
        }
        this.renderStatus();
        const total = this.items.length || 1;
        this.progressFill.style.width = `${Math.round((this.currentItemIndex() / total) * 100)}%`;
    }

    renderChunk(text) {
        this.wordHost.innerHTML = "";
        const span = document.createElement("span");
        span.className = "speed-read-chunk";
        span.textContent = text;
        this.wordHost.appendChild(span);
    }

    renderRsvpWord(word) {
        this.wordHost.innerHTML = "";
        const container = document.createElement("span");
        container.className = "speed-read-rsvp";
        const orpIndex = Math.max(0, Math.min(Math.floor((word.length - 1) * 0.35), word.length - 1));
        const before = document.createElement("span");
        before.className = "speed-read-rsvp-before";
        before.textContent = word.slice(0, orpIndex);
        const orp = document.createElement("span");
        orp.className = "speed-read-orp";
        orp.textContent = word.charAt(orpIndex);
        const after = document.createElement("span");
        after.className = "speed-read-rsvp-after";
        after.textContent = word.slice(orpIndex + 1);
        container.appendChild(before);
        container.appendChild(orp);
        container.appendChild(after);
        this.wordHost.appendChild(container);
    }

    renderCard(text, className) {
        this.wordHost.innerHTML = "";
        const card = document.createElement("span");
        card.className = `speed-read-card ${className}`;
        card.textContent = text;
        this.wordHost.appendChild(card);
    }

    renderStatus() {
        const state = this.completed ? "finished" : this.playing ? "" : "paused — Space resumes";
        this.statusHost.textContent = state ? `${this.wpm} wpm · ${state}` : `${this.wpm} wpm`;
    }

    invoke(method, ...args) {
        if (this.disposed)
            return;
        try {
            this.dotnet.invokeMethodAsync(method, ...args).catch(() => this.stop());
        } catch {
            this.stop();
        }
    }

    stop() {
        this.playing = false;
        this.stopTimer();
        if (this.wpmNotifyTimer !== null) {
            clearTimeout(this.wpmNotifyTimer);
            this.wpmNotifyTimer = null;
        }
        document.removeEventListener("keydown", this.keyHandler, true);
    }

    dispose() {
        this.stop();
        this.disposed = true;
        this.stage.removeEventListener("click", this.clickHandler);
    }
}

function clampWpm(value) {
    return Math.max(MIN_WPM, Math.min(MAX_WPM, Math.round(value)));
}
