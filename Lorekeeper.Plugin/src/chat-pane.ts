import { Client, WorkspaceState, html, ToolError } from "./workspace-state";
import { morph } from "./workspace-panels";
interface Item {
    id: string;
    type: string;
    role?: string;
    text: string;
    name?: string;
    status?: string;
}
interface Turn {
    id: string;
    status: string;
    items: Item[];
    error?: string;
    context?: {
        excludedRange?: {
            from: number;
            to: number;
        } | null;
    };
    timings?: Record<string, number>;
    lastUpdateAt?: number;
}
interface Model {
    model: string;
    name: string;
    isDefault: boolean;
    efforts: {
        reasoningEffort: string;
        description: string;
    }[];
    defaultEffort: string;
}
interface Conversation {
    id: string;
    title: string;
    model: string | null;
    effort: string | null;
}
interface Snapshot {
    version: string;
    unchanged?: boolean;
    incremental?: boolean;
    connected: boolean;
    account: {
        signedIn: boolean;
    } | null;
    models: Model[];
    activeId: string | null;
    conversations: Conversation[];
    turns?: Turn[];
    before?: number | null;
    composer?: string;
    composerRequest?: Record<string, unknown> | null;
    active: Turn | null;
    otherSurfaceBusy: boolean;
}
interface Options {
    client: Client;
    state: WorkspaceState;
    openLink(url: string): Promise<unknown>;
    changed(): void;
    previewChanged(): void;
    readScroll(scope: { fileName: string; surface: string }): number | undefined;
    saveScroll(scope: { fileName: string; surface: string }, position: number): void;
}
function markdown(text: string): string {
    const tick = String.fromCharCode(96), inline = (line: string) => html(line).replace(new RegExp(tick + "([^" + tick + "]+)" + tick, "g"), "<code>$1</code>").replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>").replace(/\*([^*]+)\*/g, "<em>$1</em>");
    let code = false, list = false, out = "";
    for (const line of text.split("\n")) {
        if (line.startsWith(tick.repeat(3))) {
            if (list) {
                out += "</ul>";
                list = false;
            }
            out += code ? "</code></pre>" : "<pre><code>";
            code = !code;
            continue;
        }
        if (code) {
            out += html(line) + "\n";
            continue;
        }
        if (/^[-*] /.test(line)) {
            if (!list) {
                out += "<ul>";
                list = true;
            }
            out += "<li>" + inline(line.slice(2)) + "</li>";
            continue;
        }
        if (list) {
            out += "</ul>";
            list = false;
        }
        if (/^#{1,4} /.test(line))
            out += "<h3>" + inline(line.replace(/^#+ /, "")) + "</h3>";
        else if (line.startsWith("> "))
            out += "<blockquote>" + inline(line.slice(2)) + "</blockquote>";
        else if (line)
            out += "<p>" + inline(line) + "</p>";
    }
    return out + (list ? "</ul>" : "") + (code ? "</code></pre>" : "");
}
export class ChatPane {
    private root = document.querySelector<HTMLElement>("#chat-pane")!;
    private scope?: {
        fileName: string;
        surface: "outline" | "editor";
    };
    private snapshot?: Snapshot;
    private turns = new Map<string, Turn>();
    private renderedTurns = new Map<string, Turn>();
    private before: number | null = null;
    private draftTimer?: number;
    private draftChanged = false;
    private busy = false;
    private polling = false;
    private connectedOnce = false;
    private generation = 0;
    private lastId?: string;
    private request?: Record<string, unknown>;
    private composerSaving?: Promise<unknown>;
    private preferenceModel?: string;
    private preferenceEffort?: string;
    constructor(private options: Options) {
        this.root.innerHTML = '<div class="pane-title"><h2 id="chat-title">Outline conversation</h2><small>Local Codex</small></div><div class="pane-body"><div class="row"><select id="chat-conversation" aria-label="Saved conversation"><option>New conversation</option></select><button id="chat-new">New chat</button></div><div id="chat-status" role="status" aria-live="polite">Choose a project to begin.</div><div id="chat-transcript" role="log" aria-label="Conversation"></div><div class="chat-models"><select id="chat-model" aria-label="Model"><option value="">Connect to discover models</option></select><select id="chat-effort" aria-label="Reasoning effort"></select></div><label class="sr-only" for="chat-message">Message Lorekeeper</label><textarea id="chat-message" maxlength="20000" placeholder="Talk about your book, or ask for a change…" rows="3"></textarea><div class="row"><button id="chat-send" class="primary">Send</button><button id="chat-stop">Stop</button><small class="grow">Enter sends · Shift+Enter new line</small></div></div>';
        this.button("chat-send").onclick = () => void this.run(() => this.send());
        this.button("chat-stop").onclick = () => void this.run(async () => { if (this.scope)
            await options.client.call("stop_lorekeeper_chat", { fileName: this.scope.fileName }); this.message("Stop requested. Partial output and completed changes remain saved."); });
        this.button("chat-new").onclick = () => void this.run(async () => { await this.flushDraft(); const next = await options.client.call<Snapshot>("choose_lorekeeper_conversation", { ...this.scope }); this.request = undefined; this.draftChanged = true; await this.flushDraft(); this.turns.clear(); this.apply(next); });
        this.select("chat-conversation").onchange = () => void this.run(async () => { await this.flushDraft(); const next = await options.client.call<Snapshot>("choose_lorekeeper_conversation", { ...this.scope, conversationId: this.select("chat-conversation").value }); this.turns.clear(); this.apply(next); });
        this.select("chat-model").onchange = () => { this.preferenceModel = this.model; this.preferenceEffort = undefined; this.efforts(); void this.run(() => this.saveModel()); options.previewChanged(); };
        this.select("chat-effort").onchange = () => { this.preferenceEffort = this.select("chat-effort").value; void this.run(() => this.saveModel()); };
        this.textarea.oninput = () => { this.draftChanged = true; clearTimeout(this.draftTimer); this.draftTimer = window.setTimeout(() => void this.flushDraft().catch(e => this.message(e.message, true)), 500); options.previewChanged(); };
        this.textarea.onkeydown = e => { if (e.key === "Enter" && !e.shiftKey && !e.isComposing && e.keyCode !== 229) {
            e.preventDefault();
            void this.run(() => this.send());
        } };
        this.root.querySelector<HTMLElement>("#chat-transcript")!.onscroll = () => { if (this.scope) options.saveScroll(this.scope, this.root.querySelector<HTMLElement>("#chat-transcript")!.scrollTop); };
        this.schedule();
    }
    private button(id: string): HTMLButtonElement { return this.root.querySelector("#" + id)!; }
    private select(id: string): HTMLSelectElement { return this.root.querySelector("#" + id)!; }
    private get textarea(): HTMLTextAreaElement { return this.root.querySelector("#chat-message")!; }
    get text(): string { return this.textarea.value; }
    get model(): string | undefined { return this.select("chat-model").value || undefined; }
    private message(text: string, error = false): void { const e = this.root.querySelector<HTMLElement>("#chat-status")!; e.textContent = text; e.classList.toggle("chat-error", error); }
    private async run(action: () => Promise<void>): Promise<void> { if (this.busy)
        return; this.busy = true; this.controls(); try {
        await action();
    }
    catch (e) {
        this.message((e as Error).message, true);
    }
    finally {
        this.busy = false;
        this.controls();
    } }
    async connect(): Promise<void> { await this.run(async () => { const before = performance.now(); await this.options.client.call("connect_lorekeeper_chat"); await this.refresh(true); this.message("Connected to local Codex · startup " + Math.round(performance.now() - before) + " ms"); this.options.previewChanged(); }); }
    async login(): Promise<void> { await this.run(async () => { const result = await this.options.client.call<{
        authUrl: string;
    }>("sign_in_lorekeeper_chat"); await this.options.openLink(result.authUrl); this.message("Complete OpenAI sign-in, then Connect Codex."); }); }
    async disconnect(): Promise<void> { await this.run(async () => { await this.options.client.call("disconnect_lorekeeper_chat"); await this.refresh(true); this.message("Disconnected. Saved and unsent text remain local."); }); }
    async setProject(): Promise<void> {
        const state = this.options.state;
        if (!state.state)
            return;
        if (this.scope?.fileName === state.state.fileName && this.scope.surface === state.surface)
            return;
        await this.flushDraft();
        if (this.scope) this.options.saveScroll(this.scope, this.root.querySelector<HTMLElement>("#chat-transcript")!.scrollTop);
        this.scope = { fileName: state.state.fileName, surface: state.surface };
        const savedScroll = this.options.readScroll(this.scope);
        this.generation++;
        this.snapshot = undefined;
        this.lastId = undefined;
        this.request = undefined;
        this.preferenceModel = undefined;
        this.preferenceEffort = undefined;
        this.turns.clear();
        this.textarea.value = "";
        this.draftChanged = false;
        this.root.querySelector("#chat-title")!.textContent = state.surface === "outline" ? "Outline conversation" : "Editor conversation";
        morph(this.root.querySelector<HTMLElement>("#chat-transcript")!, "");
        await this.refresh(true);
        if (savedScroll !== undefined) this.root.querySelector<HTMLElement>("#chat-transcript")!.scrollTop = savedScroll;
        if (!this.connectedOnce) {
            this.connectedOnce = true;
            void this.connect();
        }
    }
    async flushDraft(): Promise<void> { clearTimeout(this.draftTimer); if (this.composerSaving)
        await this.composerSaving; if (!this.scope || !this.draftChanged)
        return; const scope = this.scope, text = this.textarea.value; this.composerSaving = this.options.client.call("save_lorekeeper_chat_draft", { ...scope, text, request: this.request ?? null }); try {
        await this.composerSaving;
        if (this.scope === scope && this.textarea.value === text)
            this.draftChanged = false;
    }
    finally {
        this.composerSaving = undefined;
    } if (this.scope === scope && this.draftChanged)
        await this.flushDraft(); }
    private efforts(saved?: string | null): void {
        const model = this.snapshot?.models.find(x => x.model === this.model), selected = saved ?? this.preferenceEffort ?? (model?.efforts.some(x => x.reasoningEffort === "medium") ? "medium" : model?.defaultEffort);
        this.select("chat-effort").innerHTML = model?.efforts.map(x => '<option value="' + html(x.reasoningEffort) + '"' + (x.reasoningEffort === selected ? ' selected' : '') + '>' + html(x.reasoningEffort) + '</option>').join("") ?? "";
        if (selected && !model?.efforts.some(x => x.reasoningEffort === selected)) {
            this.select("chat-effort").insertAdjacentHTML("afterbegin", '<option selected value="' + html(selected) + '">' + html(selected) + ' (unavailable)</option>');
            this.message("The saved reasoning effort is unavailable. Choose a supported effort.", true);
        }
    }
    private controls(): void {
        this.button("chat-send").disabled = this.busy || !this.scope || !this.snapshot?.account?.signedIn || Boolean(this.snapshot?.active) || Boolean(this.snapshot?.otherSurfaceBusy) || !this.model;
        this.button("chat-stop").disabled = this.busy || !this.snapshot?.active;
        this.button("chat-new").disabled = this.busy || Boolean(this.snapshot?.active) || Boolean(this.snapshot?.otherSurfaceBusy);
        this.select("chat-model").disabled = this.busy || Boolean(this.snapshot?.active) || Boolean(this.snapshot?.otherSurfaceBusy);
        this.select("chat-effort").disabled = this.select("chat-model").disabled;
    }
    private async saveModel(): Promise<void> {
        if (!this.scope || !this.model) return;
        const next = await this.options.client.call<Snapshot>("set_lorekeeper_conversation_model", { ...this.scope, model: this.model, effort: this.select("chat-effort").value });
        this.apply(next);
    }
    private apply(next: Snapshot, older = false): void {
        if (next.unchanged)
            return;
        const previous = this.snapshot;
        if (next.incremental)
            this.snapshot = { ...previous!, ...next, turns: previous?.turns, composer: previous?.composer };
        else
            this.snapshot = next;
        const s = this.snapshot;
        for (const turn of next.turns ?? [])
            this.turns.set(turn.id, turn);
        if (!next.incremental)
            this.before = next.before ?? null;
        if (s.active)
            this.turns.set(s.active.id, s.active);
        if (!older && s.activeId !== this.lastId) {
            this.lastId = s.activeId ?? undefined;
            const c = s.conversations.find(x => x.id === s.activeId);
            this.preferenceModel = c?.model ?? undefined;
            this.preferenceEffort = c?.effort ?? undefined;
            if (!this.draftChanged) {
                this.textarea.value = next.composer ?? "";
                this.request = next.composerRequest ?? undefined;
                if (this.request)
                    this.message("A retained send identity is available. Inspect saved messages before retrying; no inference was resent.");
            }
        }
        const model = this.preferenceModel ?? s.conversations.find(x => x.id === s.activeId)?.model ?? s.models.find(m => m.model === "gpt-6.1-sol")?.model ?? s.models.find(m => m.isDefault)?.model ?? s.models[0]?.model;
        const select = this.select("chat-model");
        if (JSON.stringify(previous?.models) !== JSON.stringify(s.models) || !select.value || !s.models.some(m => m.model === select.value)) {
            select.innerHTML = s.models.map(m => '<option value="' + html(m.model) + '">' + html(m.name) + '</option>').join("");
            if (model && !s.models.some(m => m.model === model)) {
                select.insertAdjacentHTML("afterbegin", '<option value="' + html(model) + '">' + html(model) + ' (unavailable)</option>');
                this.message("The saved model is unavailable. Choose a model explicitly.", true);
            }
            select.value = model ?? "";
            this.efforts(this.preferenceEffort);
        }
        else if (this.preferenceModel && select.value !== this.preferenceModel) {
            select.value = this.preferenceModel;
            this.efforts(this.preferenceEffort);
        }
        const conversation = this.select("chat-conversation");
        morph(conversation, '<option value="">New conversation</option>' + s.conversations.map(c => '<option value="' + c.id + '"' + (c.id === s.activeId ? ' selected' : '') + '>' + html(c.title) + '</option>').join(""));
        const transcript = this.root.querySelector<HTMLElement>("#chat-transcript")!, nearBottom = transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight < 80, oldHeight = transcript.scrollHeight, started = performance.now(), turns = [...this.turns.values()];
        this.renderTranscript(transcript, turns);
        if (older)
            transcript.scrollTop += transcript.scrollHeight - oldHeight;
        else if (nearBottom)
            transcript.scrollTop = transcript.scrollHeight;
        const olderButton = this.root.querySelector<HTMLButtonElement>("#chat-older");
        if (olderButton)
            olderButton.onclick = () => void this.run(async () => { const page = await this.options.client.call<Snapshot>("get_lorekeeper_chat", { ...this.scope, before: this.before }); const old = [...this.turns.entries()]; this.turns.clear(); for (const t of page.turns ?? [])
                this.turns.set(t.id, t); for (const [id, t] of old)
                this.turns.set(id, t); this.apply(page, true); });
        if (!older && previous?.version !== s.version && document.body.dataset.pane !== "chat")
            document.querySelector("#chat-unread")!.textContent = "•";
        if (s.active)
            this.message("Codex " + s.active.status + " · UI delivery " + (s.active.lastUpdateAt ? Math.max(0, Date.now() - s.active.lastUpdateAt) : Math.round(performance.now() - started)) + " ms");
        else if (previous?.active) {
            this.message("Turn " + (turns.at(-1)?.status ?? "finished") + ". Applied changes are in History.");
            this.options.changed();
        }
        this.controls();
    }
    private renderTranscript(transcript: HTMLElement, turns: Turn[]): void {
        let older = this.root.querySelector<HTMLButtonElement>("#chat-older");
        if (this.before !== null && !older) {
            older = document.createElement("button"); older.id = "chat-older"; older.textContent = "Earlier messages"; transcript.prepend(older);
        }
        if (this.before === null) older?.remove();
        const nodes = new Map([...transcript.querySelectorAll<HTMLElement>(":scope > .chat-turn")].map(x => [x.dataset.key!, x]));
        let position = this.before === null ? 0 : 1;
        for (const turn of turns) {
            let section = nodes.get(turn.id);
            if (!section) { section = document.createElement("section"); section.className = "chat-turn"; section.dataset.key = turn.id; }
            if (this.renderedTurns.get(turn.id) !== turn || !nodes.has(turn.id)) {
                morph(section, turn.items.map(i => i.type === "message" ? '<div class="chat-message ' + i.role + '" data-key="' + i.id + '">' + (i.role === "assistant" ? markdown(i.text) : html(i.text)) + '</div>' : '<details data-key="' + i.id + '"><summary>' + html(i.type === "tool" ? i.name + " · " + i.status : "Reasoning") + '</summary><pre>' + html(i.text) + '</pre></details>').join("") + (turn.error ? '<p class="chat-error">' + html(turn.error) + '</p>' : '') + (turn.context?.excludedRange ? '<small>Earlier turns ' + turn.context.excludedRange.from + '–' + turn.context.excludedRange.to + ' were excluded from this request.</small>' : '') + '<small>' + html(turn.status) + (turn.timings?.firstOutputMs ? " · first output " + turn.timings.firstOutputMs + " ms" : "") + '</small>');
                this.renderedTurns.set(turn.id, turn);
            }
            if (transcript.children[position] !== section) transcript.insertBefore(section, transcript.children[position] ?? null);
            nodes.delete(turn.id); position++;
        }
        for (const [id, section] of nodes) { section.remove(); this.renderedTurns.delete(id); }
        for (const id of this.renderedTurns.keys()) if (!this.turns.has(id)) this.renderedTurns.delete(id);
    }
    private async refresh(force = false): Promise<void> {
        if (!this.scope || this.polling)
            return;
        this.polling = true;
        const scope = this.scope, generation = this.generation;
        try {
            const next = await this.options.client.call<Snapshot>("get_lorekeeper_chat", { ...scope, knownVersion: force ? undefined : this.snapshot?.version });
            if (generation === this.generation)
                this.apply(next);
        }
        finally {
            this.polling = false;
        }
    }
    private async send(): Promise<void> {
        if (!this.scope || this.snapshot?.active || !this.text.trim())
            return;
        await this.options.state.flush();
        await this.flushDraft();
        const saved = this.options.state.state!, text = this.text.trim(), scope = this.scope;
        if (this.request && this.request.text !== text)
            throw new Error("Inspect the previous uncertain send before changing its text, or start a new chat explicitly. The retained request was not resent.");
        this.request ??= { ...scope, chapterId: scope.surface === "editor" ? this.options.state.chapterId : undefined, text, requestId: crypto.randomUUID(), expectedEtag: saved.etag, model: this.model, effort: this.select("chat-effort").value };
        this.draftChanged = true;
        await this.flushDraft();
        let result: Snapshot;
        try {
            result = await this.options.client.call<Snapshot>("send_lorekeeper_chat_message", this.request);
        }
        catch (e) {
            if (e instanceof ToolError && ["MODEL_UNAVAILABLE", "SIGN_IN_REQUIRED", "CONTEXT_FULL", "CONFLICT"].includes(e.code)) {
                this.request = undefined;
                this.draftChanged = true;
                await this.flushDraft();
            }
            throw e;
        }
        if (this.scope === scope) {
            this.apply(result);
            this.request = undefined;
            this.textarea.value = "";
            this.draftChanged = true;
            await this.flushDraft();
            this.message("Message saved. Codex is working.");
        }
    }
    private schedule(): void { window.setTimeout(async () => { try {
        await this.refresh();
        if (this.snapshot?.active)
            this.options.changed();
    }
    catch (e) {
        this.message((e as Error).message, true);
    } this.schedule(); }, 250); }
}
