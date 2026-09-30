interface Item { id: string; type: string; role?: string; text: string; name?: string; status?: string }
interface Usage { last: { inputTokens: number }; total: { outputTokens: number }; modelContextWindow: number | null }
interface Turn { id: string; status: string; items: Item[]; error?: string; usage?: Usage }
interface Conversation { id: string; title: string; model: string | null; effort: string | null; turns: Turn[] }
interface Model { model: string; name: string; isDefault: boolean; efforts: { reasoningEffort: string; description: string }[]; defaultEffort: string }
interface Snapshot { version: string; connected: boolean; account: { signedIn: boolean; type: string } | null; models: Model[]; chat: { activeId: string; conversations: Conversation[] } | null; active: { id: string; conversationId: string; status: string; items: Item[]; error?: string; usage?: Usage } | null }
interface Saved { fileName: string; etag: string; project: { id: string; chapters: { id: string; title: string }[] } }
interface Options { call(name: string, args?: Record<string, unknown>): Promise<{ data: Record<string, unknown> }>; saved(): Saved; selectedChapter(): string | undefined; openLink(url: string): Promise<unknown> }
const escape = (value: string) => value.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));

export class ChatPane {
  private readonly root = document.querySelector<HTMLElement>("#chat-pane")!;
  private snapshot?: Snapshot;
  private fileName?: string;
  private generation = 0;
  private busy = false;
  private polling = false;
  private lastConversation?: string;
  private models: Model[] = [];
  private drafts = new Map<string, string>();
  private request?: { text: string; id: string };
  constructor(private readonly options: Options) {
    this.root.innerHTML = `<div class="chat-header"><h2>Writing conversation</h2><small>Local Codex · your conversation stays on this computer</small></div>
      <div class="row"><button id="chat-connect">Connect Codex</button><button id="chat-disconnect" disabled>Disconnect</button><button id="chat-signin" hidden>Sign in</button></div>
      <p id="chat-status" role="status" aria-live="polite">Open a project, then connect your local Codex sign-in.</p>
      <div class="row"><label class="sr-only" for="chat-conversation">Conversation</label><select id="chat-conversation" disabled><option>No conversation yet</option></select><button id="chat-new" disabled>New chat</button></div>
      <div id="chat-transcript" role="log" aria-label="Writing conversation"></div>
      <details id="chat-context"><summary>Context for the next message</summary><label for="chat-chapter">Chapter</label><select id="chat-chapter"><option value="">Current chapter</option></select><label for="chat-budget">Retrieved context budget (characters)</label><input id="chat-budget" type="number" min="1000" max="24000" step="1000" value="12000"><small>The complete Book Brief is always included. Later turns replay conversation prose, with fresh project context. Previous tool results and reasoning stay in the local transcript.</small><button id="chat-preview">Preview context</button><div id="chat-context-preview"></div></details>
      <div class="chat-models"><label class="sr-only" for="chat-model">Model</label><select id="chat-model"><option value="">Connect to discover models</option></select><label class="sr-only" for="chat-effort">Reasoning effort</label><select id="chat-effort"><option value="">Reasoning</option></select></div>
      <label class="sr-only" for="chat-message">Message Lorekeeper</label><textarea id="chat-message" maxlength="10000" placeholder="Talk about your story, or ask for a draft…" rows="4"></textarea>
      <div class="row between"><button id="chat-send" class="primary" disabled>Send</button><button id="chat-stop" disabled>Stop</button><small id="chat-activity"></small></div>`;
    this.button("chat-connect").onclick = () => { void this.run(async () => {
      const result = await options.call("connect_lorekeeper_chat");
      this.models = result.data.models as Model[]; this.populateModels();
      this.message((result.data.account as { signedIn: boolean }).signedIn ? "Connected to your local Codex sign-in." : "Codex needs sign-in. Use Sign in, then Connect Codex again.");
      this.button("chat-signin").hidden = (result.data.account as { signedIn: boolean }).signedIn;
      await this.refresh(true);
    }); };
    this.button("chat-signin").onclick = () => { void this.run(async () => {
      const result = await options.call("sign_in_lorekeeper_chat");
      if (typeof result.data.authUrl !== "string") throw new Error("Codex did not return a sign-in link. Run codex login locally, then reconnect.");
      await options.openLink(result.data.authUrl);
      this.message("Complete Codex sign-in in the browser, then choose Connect Codex.");
    }); };
    this.button("chat-disconnect").onclick = () => { void this.run(async () => {
      await options.call("disconnect_lorekeeper_chat"); await this.refresh(true);
      this.message("Local chat connection closed. Saved transcripts and unsent text are preserved.");
    }); };
    this.button("chat-new").onclick = () => { void this.run(async () => {
      if (!this.fileName) return;
      const response = await options.call("choose_lorekeeper_conversation", { fileName: this.fileName });
      this.apply(response.data as unknown as Snapshot); this.message("New conversation. Earlier transcripts are still saved.");
    }); };
    this.select("chat-conversation").onchange = () => { void this.run(async () => {
      const result = await options.call("choose_lorekeeper_conversation", { fileName: this.fileName, conversationId: this.select("chat-conversation").value });
      this.apply(result.data as unknown as Snapshot);
    }); };
    this.select("chat-model").onchange = () => { this.populateEfforts(); this.controls(); };
    this.select("chat-effort").onchange = () => { this.controls(); };
    this.button("chat-preview").onclick = () => { void this.run(async () => {
      const result = await options.call("preview_lorekeeper_chat_context", this.arguments());
      const value = result.data as { revision: number; bookBrief: string; sources: { title: string; text: string; complete: boolean }[]; usedCharacters: number; omittedSources: number };
      document.querySelector("#chat-context-preview")!.innerHTML = `<small>Saved revision ${value.revision} · ${value.usedCharacters} retrieved characters · ${value.omittedSources} omitted sources</small><h3>Complete Book Brief</h3><pre>${escape(value.bookBrief)}</pre>${value.sources.filter(s => s.title !== "Book Brief").map(s => `<h3>${escape(s.title)}${s.complete ? "" : " (excerpt)"}</h3><pre>${escape(s.text)}</pre>`).join("")}`;
    }); };
    this.button("chat-send").onclick = () => { void this.send(); };
    this.button("chat-stop").onclick = () => { void this.run(async () => {
      const result = await options.call("stop_lorekeeper_chat", { fileName: this.fileName }); this.apply(result.data as unknown as Snapshot);
      this.message("Stop requested. Completed proposals remain available for review.");
    }); };
    this.textarea.oninput = () => { if (this.fileName) this.drafts.set(this.fileName, this.textarea.value); this.controls(); };
    this.textarea.onkeydown = event => { if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); void this.send(); } };
    this.schedule();
  }
  private button(id: string): HTMLButtonElement { return this.root.querySelector(`#${id}`)!; }
  private select(id: string): HTMLSelectElement { return this.root.querySelector(`#${id}`)!; }
  private get textarea(): HTMLTextAreaElement { return this.root.querySelector("#chat-message")!; }
  private message(text: string, error = false): void { const element = this.root.querySelector<HTMLElement>("#chat-status")!; element.textContent = text; element.dataset.error = String(error); }
  setProject(state: Saved): void {
    const selection = this.select("chat-chapter"), selected = selection.value;
    selection.innerHTML = `<option value="">Current chapter</option><option value="project">Whole project</option>${state.project.chapters.map(c => `<option value="${c.id}">${escape(c.title)}</option>`).join("")}`;
    if ([...selection.options].some(option => option.value === selected)) selection.value = selected;
    if (state.fileName === this.fileName) return;
    if (this.fileName) this.drafts.set(this.fileName, this.textarea.value);
    this.fileName = state.fileName; this.generation++; this.snapshot = undefined; this.lastConversation = undefined; this.request = undefined;
    this.textarea.value = this.drafts.get(state.fileName) ?? "";
    document.querySelector("#chat-context-preview")!.replaceChildren();
    document.querySelector("#chat-transcript")!.replaceChildren();
    this.message("Connect local Codex to write here. Saved conversations load from this project's local files.");
    void this.refresh(true);
    this.controls();
  }
  private arguments(): Record<string, unknown> {
    const state = this.options.saved();
    const selected = this.select("chat-chapter").value;
    return { fileName: state.fileName, expectedEtag: state.etag, text: this.textarea.value.trim(), chapterId: selected === "project" ? undefined : selected || this.options.selectedChapter(), maximumCharacters: Number(this.root.querySelector<HTMLInputElement>("#chat-budget")!.value) };
  }
  private async send(): Promise<void> {
    if (this.busy || this.snapshot?.active || !this.textarea.value.trim()) return;
    await this.run(async () => {
      const args = this.arguments(), generation = this.generation;
      if (this.request?.text !== args.text) this.request = { text: args.text as string, id: crypto.randomUUID() };
      const requestId = this.request!.id;
      const result = await this.options.call("send_lorekeeper_chat_message", { ...args, requestId, model: this.select("chat-model").value, effort: this.select("chat-effort").value });
      this.drafts.delete(args.fileName as string);
      if (generation !== this.generation) return;
      this.textarea.value = ""; this.request = undefined; this.apply(result.data as unknown as Snapshot);
      this.message(this.snapshot?.active ? "Message saved. Codex is working in this conversation." : "Reply saved.");
    });
  }
  private async run(action: () => Promise<void>): Promise<void> {
    if (this.busy) return;
    this.busy = true; this.generation++; this.controls();
    try { await action(); }
    catch (error) { this.message(error instanceof Error ? error.message : "Chat operation failed. Your message is preserved; inspect the transcript before retrying.", true); }
    finally { this.busy = false; this.controls(); }
  }
  private populateModels(selected?: string): void {
    const picker = this.select("chat-model"), previous = selected ?? picker.value;
    picker.innerHTML = this.models.map(model => `<option value="${escape(model.model)}">${escape(model.name)}</option>`).join("");
    if (previous && !this.models.some(m => m.model === previous)) { const option = document.createElement("option"); option.value = previous; option.textContent = `Unavailable: ${previous}`; picker.append(option); }
    picker.value = previous || this.models.find(m => m.isDefault)?.model || this.models[0]?.model || "";
    this.populateEfforts();
  }
  private populateEfforts(selected?: string): void {
    const model = this.models.find(m => m.model === this.select("chat-model").value), picker = this.select("chat-effort");
    picker.innerHTML = model?.efforts.map(e => `<option value="${escape(e.reasoningEffort)}">${escape(e.reasoningEffort)}</option>`).join("") ?? "";
    if (selected && !model?.efforts.some(e => e.reasoningEffort === selected)) { const option = document.createElement("option"); option.value = selected; option.textContent = `Unavailable: ${selected}`; picker.append(option); }
    picker.value = selected ?? model?.defaultEffort ?? "";
  }
  private controls(): void {
    const running = Boolean(this.snapshot?.active), ready = this.snapshot?.connected && this.snapshot?.account?.signedIn;
    const selected = this.models.find(m => m.model === this.select("chat-model").value), effortValid = selected?.efforts.some(e => e.reasoningEffort === this.select("chat-effort").value);
    this.button("chat-send").disabled = this.busy || running || !ready || !this.fileName || !this.textarea.value.trim() || !effortValid;
    this.button("chat-stop").disabled = this.busy || !running;
    this.button("chat-connect").disabled = this.busy || running;
    this.button("chat-disconnect").disabled = this.busy || !this.snapshot?.connected;
    this.button("chat-signin").disabled = this.busy || running;
    this.button("chat-new").disabled = this.busy || running || !this.fileName;
    this.select("chat-conversation").disabled = this.busy || running || !this.snapshot?.chat;
    for (const id of ["chat-model", "chat-effort", "chat-chapter"]) this.select(id).disabled = this.busy || running;
    this.button("chat-preview").disabled = this.busy || !this.fileName || !this.textarea.value.trim();
  }
  private apply(snapshot: Snapshot): void {
    const ended = this.snapshot?.active && !snapshot.active;
    this.snapshot = snapshot;
    if (snapshot.models.length && !this.models.length) { this.models = snapshot.models; this.populateModels(); }
    const conversation = snapshot.chat?.conversations.find(c => c.id === snapshot.chat!.activeId);
    this.select("chat-conversation").innerHTML = snapshot.chat?.conversations.map(c => `<option value="${c.id}">${escape(c.title)}</option>`).join("") || "<option>No conversation yet</option>";
    if (conversation) {
      this.select("chat-conversation").value = conversation.id;
      if (this.lastConversation !== conversation.id && this.models.length) { this.populateModels(conversation.model ?? undefined); this.populateEfforts(conversation.effort ?? undefined); this.lastConversation = conversation.id; }
    }
    const turns = conversation?.turns.map(t => snapshot.active?.id === t.id ? { ...t, ...snapshot.active } : t.status === "running" ? { ...t, error: "The previous runtime ended before saving an outcome. Inspect its proposals before sending again; this message is never resent automatically." } : t) ?? [];
    const transcript = this.root.querySelector<HTMLElement>("#chat-transcript")!, nearEnd = transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight < 80;
    const opened = new Set([...transcript.querySelectorAll<HTMLDetailsElement>("details[open]")].map(d => d.dataset.id));
    transcript.innerHTML = turns.map(turn => `<div class="chat-turn">${turn.items.map(item => item.type === "message" ? `<article class="chat-message ${item.role}"><small>${item.role === "user" ? "You" : "Lorekeeper"}</small><div>${escape(item.text)}</div></article>` : `<details data-id="${escape(item.id)}" ${opened.has(item.id) ? "open" : ""}><summary>${item.type === "reasoning" ? "Reasoning summary" : escape(item.name ?? "Tool")}${item.status ? ` · ${escape(item.status)}` : ""}</summary><pre>${escape(item.text)}</pre></details>`).join("")}<small>${escape(turn.status)}${turn.error ? ` · ${escape(turn.error)}` : ""}</small>${turn.usage ? `<small>Latest input: ${turn.usage.last.inputTokens.toLocaleString()} tokens · turn output: ${turn.usage.total.outputTokens.toLocaleString()}${turn.usage.modelContextWindow ? ` · context window: ${turn.usage.modelContextWindow.toLocaleString()}` : ""}</small>` : ""}</div>`).join("");
    if (nearEnd) transcript.scrollTop = transcript.scrollHeight;
    if (ended) {
      const last = turns.at(-1);
      this.message(last?.status === "completed" ? "Reply saved." : last?.error ?? "Turn stopped. Partial output and completed proposals are preserved.", last?.status === "failed");
    }
    this.root.querySelector("#chat-activity")!.textContent = snapshot.active ? snapshot.active.status : "";
    this.button("chat-signin").hidden = Boolean(snapshot.account?.signedIn);
    this.controls();
  }
  private async refresh(force = false): Promise<void> {
    if (!this.fileName || this.polling || (!force && (this.busy || document.visibilityState === "hidden"))) return;
    const generation = this.generation, fileName = this.fileName;
    this.polling = true;
    try {
      const result = await this.options.call("get_lorekeeper_chat", { fileName, knownVersion: force ? undefined : this.snapshot?.version });
      if (generation === this.generation && !result.data.unchanged) this.apply(result.data as unknown as Snapshot);
    } catch { if (generation === this.generation) this.message("Conversation refresh is unavailable. Your unsent message is preserved; reconnect before retrying.", true); }
    finally { this.polling = false; }
  }
  private schedule(): void { window.setTimeout(() => { void this.refresh().finally(() => this.schedule()); }, this.snapshot?.active ? 300 : 2500); }
}
