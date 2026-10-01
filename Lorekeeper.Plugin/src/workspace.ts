import { App } from "@modelcontextprotocol/ext-apps";
import { OpenAIExtensions } from "@openai/mcp-extensions/app";
import manifest from "../plugin.json";
import { ChatPane } from "./chat-pane";
import { ManuscriptHistory } from "./manuscript-history";
import { Client, WorkspaceState, ToolError, html, hash, valueAt, items, type Json, type Target, type State, type Operation, type Project, type MutationResult } from "./workspace-state";
import { action, outline, editor, brief, detailsFields, field, morph, setFieldScope } from "./workspace-panels";
const app = new App({ name: "Lorekeeper", version: manifest.version }, {}, { autoResize: false });
new OpenAIExtensions(app);
const client = new Client(app), state = new WorkspaceState(client);
const manuscriptHistory = new ManuscriptHistory();
const manuscriptKey = (): string => (state.state?.fileName ?? "") + ":" + (state.chapterId ?? "");
const el = <T extends HTMLElement = HTMLElement>(id: string): T => document.getElementById(id) as T;
const content = el("workspace-pane"), details = el("details-pane"), dialog = el<HTMLDialogElement>("dialog");
let selected: Target = { kind: "brief" }, expanded = new Set<string>(), editorMode = "edit", context: Context | undefined, preferences: Record<string, Json> = {}, prefsTimer: number | undefined, contextTimer: number | undefined, polling = false, connected = false, unreadChanges = 0;
let renderedScope = "";
function captureView(): void {
    if (!renderedScope)
        return;
    const manuscript = el<HTMLTextAreaElement>("manuscript"), sections = Object.fromEntries([...details.querySelectorAll<HTMLDetailsElement>("details[data-key]")].map(x => [x.dataset.key!, x.open]));
    const views = (preferences.views ?? {}) as Record<string, Json>;
    views[renderedScope] = { workspaceScroll: content.scrollTop, detailsScroll: details.scrollTop, selected: selected as unknown as Json, selectionStart: manuscript?.selectionStart ?? 0, selectionEnd: manuscript?.selectionEnd ?? 0, manuscriptScroll: manuscript?.scrollTop ?? 0, sections };
    while (Object.keys(views).length > 120)
        delete views[Object.keys(views)[0]];
    preferences.views = views;
}
function restoreView(): void {
    const saved = (preferences.views as Record<string, Record<string, Json>> | undefined)?.[renderedScope];
    if (!saved)
        return;
    if (saved.selected && typeof saved.selected === "object") {
        selected = saved.selected as unknown as Target;
        renderDetails(state.project!);
    }
    content.scrollTop = Number(saved.workspaceScroll ?? 0);
    details.scrollTop = Number(saved.detailsScroll ?? 0);
    const manuscript = el<HTMLTextAreaElement>("manuscript");
    if (manuscript) {
        manuscript.setSelectionRange(Number(saved.selectionStart ?? 0), Number(saved.selectionEnd ?? 0));
        manuscript.scrollTop = Number(saved.manuscriptScroll ?? 0);
    }
    const sections = saved.sections as Record<string, boolean> | undefined;
    details.querySelectorAll<HTMLDetailsElement>("details[data-key]").forEach(x => { if (sections && x.dataset.key! in sections)
        x.open = sections[x.dataset.key!]; });
}
interface Source {
    key: string;
    title: string;
    enabled: boolean;
    pinned: boolean;
    protected: boolean;
    mandatory: boolean;
    required: boolean;
    complete: boolean;
    estimatedTokens: number;
    reason: string;
    hash: string;
    target?: Target;
}
interface Context {
    sources: Source[];
    revision: number;
    preferenceId: string;
    budget: number;
    inputBudget: number;
    estimatedRequestTokens: number;
    excludedTurns: number;
    excludedRange: {
        from: number;
        to: number;
    } | null;
    blocked: boolean;
    message?: string;
    snapshot?: unknown;
    history: unknown[];
    prompt: unknown;
    modelContextWindow: number | null;
    preparationMs: number;
}
function show(message: string, error = false): void { el("status").textContent = message; el("status").dataset.error = String(error); }
async function run(action: () => Promise<void>): Promise<void> { try {
    await action();
}
catch (e) {
    const message = e instanceof Error ? e.message : "Operation failed. Your draft is retained.";
    show(message, true);
    if (dialog.open) {
        let alert = el("dialog-content").querySelector<HTMLElement>("[role=alert]");
        if (!alert) {
            alert = document.createElement("p");
            alert.setAttribute("role", "alert");
            alert.className = "danger";
            el("dialog-content").append(alert);
        }
        alert.textContent = message;
    }
    else if (e instanceof ToolError && e.code === "RECOVERY_REQUIRED")
        await modal('<h2>Local recovery requires inspection</h2><p>' + html(message) + '</p><p>All variants were retained. Close participating writers and preserve the project, transaction, history, draft and lock files before resolving uncertain evidence.</p><div class="actions"><button data-dialog="close">Close</button></div>');
} }
function persist(): void {
    if (!connected)
        return;
    clearTimeout(prefsTimer);
    prefsTimer = window.setTimeout(() => { void client.call("save_lorekeeper_ui_preferences", { values: layoutPreferences() }).catch(e => show("Layout preferences could not be saved: " + e.message, true)); }, 700);
}
function layoutPreferences(): Record<string, Json> {
    captureView();
    preferences = { ...preferences, fileName: state.state?.fileName ?? null, surface: state.surface, chapterId: state.chapterId ?? null, selected: selected as unknown as Json, expanded: [...expanded], theme: document.documentElement.dataset.lkTheme ?? "dark", chatWidth: document.documentElement.style.getPropertyValue("--chat-width"), detailsWidth: document.documentElement.style.getPropertyValue("--details-width"), chatCollapsed: document.body.dataset.chatCollapsed === "true", detailsCollapsed: document.body.dataset.detailsCollapsed === "true", pane: document.body.dataset.pane ?? "workspace" };
    return preferences;
}
function fitPanes(): void {
    const desktop = window.innerWidth > 1050, style = document.documentElement.style;
    const width = (side: string): number => parseInt(style.getPropertyValue("--" + side + "-width")) || 330;
    const chatHidden = document.body.dataset.chatCollapsed === "true", detailsHidden = document.body.dataset.detailsCollapsed === "true";
    el("chat-pane").inert = desktop && chatHidden;
    details.inert = desktop && detailsHidden;
    if (!desktop) return;
    const chatWidth = chatHidden ? 0 : width("chat"), detailsWidth = detailsHidden ? 0 : width("details"), available = window.innerWidth - 312;
    if (chatWidth + detailsWidth > available) {
        const proportion = available / (chatWidth + detailsWidth);
        if (chatWidth) style.setProperty("--chat-width", Math.max(260, Math.min(available - (detailsWidth ? 260 : 0), chatWidth * proportion)) + "px");
        if (detailsWidth) style.setProperty("--details-width", Math.max(260, available - (chatWidth ? width("chat") : 0)) + "px");
    }
}
function render(): void {
    const p = state.project;
    if (!p) {
        morph(content, '<div class="empty"><h1>Your local writing workspace</h1><p>Create a project to begin. Project files and conversations stay on this computer.</p>' + action("new", "New project") + '</div>');
        return;
    }
    const scope = state.state!.fileName + ":" + state.surface + ":" + (state.surface === "editor" ? state.chapterId ?? "" : ""), switched = scope !== renderedScope;
    if (switched) {
        captureView();
        renderedScope = scope;
        context = undefined;
    }
    setFieldScope("workspace");
    const chapter = p.chapters.find(x => x.id === state.chapterId);
    if (chapter) manuscriptHistory.sync(manuscriptKey(), chapter.text);
    morph(content, state.surface === "outline" ? outline(p, expanded, selected) : editor(p, state.chapterId, editorMode), new Set(state.pending.keys()));
    manuscriptButtons();
    renderDetails(p);
    el<HTMLSelectElement>("project-list").value = state.state!.fileName;
    const option = [...el<HTMLSelectElement>("project-list").options].find(x => x.value === state.state!.fileName);
    if (option)
        option.textContent = p.title;
    if (switched)
        restoreView();
    document.querySelectorAll<HTMLElement>("[data-surface]").forEach(e => e.classList.toggle("active", e.dataset.surface === state.surface));
    el("save-state").textContent = (state.blocked ? "Conflict" : state.pending.size ? "Unsaved" : "Saved") + " · r" + p.revision;
}
function renderDetails(p: Project): void {
    setFieldScope("details");
    if (selected.id && valueAt(p, { kind: selected.kind, id: selected.id }) == null)
        selected = state.surface === "editor" && state.chapterId ? { kind: "chapter", id: state.chapterId } : { kind: "brief" };
    const current = state.surface === "editor" ? p.chapters.find(c => c.id === state.chapterId) : null;
    const sources = context?.sources.map(s => '<div class="source" data-key="' + s.key + '"><div class="row">' + (s.target ? '<button class="title grow" data-action="source" data-key-source="' + html(s.key) + '">' + html(s.title) + '</button>' : '<strong class="grow">' + html(s.title) + '</strong>') + '<input type="checkbox" aria-label="Include ' + html(s.title) + '" data-context-key="' + html(s.key) + '"' + (s.enabled ? ' checked' : '') + (s.mandatory ? ' disabled' : '') + '>' + action("pin", s.pinned ? "Unpin" : "Pin", undefined, undefined, ' data-key-source="' + html(s.key) + '"' + (s.protected ? ' disabled' : '')) + '</div><small>' + html(s.reason) + ' · ' + s.estimatedTokens + ' est. tokens · ' + (s.complete ? "complete" : "excerpt") + ' · r' + context!.revision + '</small><details><summary>Source identity</summary><small>' + html(s.key) + '<br>' + html(s.hash) + '</small></details></div>').join("") ?? '<p class="muted">Preparing context…</p>';
    morph(details, '<div class="pane-title"><h2>Details & context</h2></div><details class="panel" data-key="active-context" open><summary>Active context' + (current ? ' · ' + html(current.title) : ' · Outline') + '</summary><div class="row wrap"><small class="grow">' + (context ? context.estimatedRequestTokens + ' / ' + context.inputBudget + ' estimated input tokens' : '') + '</small>' + action("prompt", "Prompt") + action("reset-context", "Reset") + '</div>' + (context?.blocked ? '<p class="danger">' + html(context.message ?? "Required content exceeds the complete request budget.") + '</p>' : '') + (context?.excludedTurns ? '<p class="muted">Turns ' + context.excludedRange!.from + '–' + context.excludedRange!.to + ' are outside the model context. The complete transcript stays saved.</p>' : '') + field({ kind: "preferences", id: context?.preferenceId ?? current?.id ?? p.id, field: "budget" }, "Context input budget (estimated tokens)", p.contextPreferences.find(x => x.id === (context?.preferenceId ?? current?.id ?? p.id))?.budget ?? 32000, "number") + sources + '</details><details class="panel" data-key="search" open><summary>Project search</summary><div class="row"><input id="search-query" type="search" placeholder="Names, places, exact phrases" aria-label="Search project">' + action("search", "Search") + '</div><div id="search-results" data-key="search-results" data-preserve="true"></div></details><details class="panel" data-key="selection" open><summary>' + html(selected.kind === "brief" ? "Book Brief" : selected.kind === "project" ? "Project Guidance" : "Selected " + selected.kind) + '</summary>' + (selected.kind === "brief" || selected.kind === "project" ? '<p class="muted">Edit protected direction in the panels below.</p>' : detailsFields(p, selected)) + '</details><details class="panel" data-key="book-brief"' + (selected.kind === "brief" ? ' open' : '') + '><summary>Book Brief</summary>' + brief(p) + '</details><details class="panel" data-key="guidance"><summary>Project Guidance</summary>' + field({ kind: "project", field: "guidance" }, "Protected direction", p.guidance, "textarea") + '</details><details class="panel" data-key="facts"><summary>Project facts (' + p.facts.length + ')</summary>' + p.facts.map(f => '<div class="row">' + action("details", html(f.name), "fact", f.id) + action("delete", "×", "fact", f.id) + '</div>').join("") + action("insert", "＋ Fact", "fact") + '</details><details class="panel" data-key="entities" open><summary>Entities (' + p.entities.length + ')</summary><div class="row">' + action("insert", "＋ Entity", "entity") + action("entity-type", "＋ Type") + '</div><input id="entity-filter" type="search" placeholder="Filter entities" aria-label="Filter entities">' + p.entityTypes.map(type => '<details data-key="type-' + html(type) + '" open><summary>' + html(type) + ' (' + p.entities.filter(e => e.type === type).length + ')</summary><div class="entity-list">' + p.entities.filter(e => e.type === type).map(e => '<div class="entity-row" data-name="' + html((e.name + " " + e.aliases.join(" ")).toLowerCase()) + '">' + action("details", html(e.name), "entity", e.id) + action("delete", "×", "entity", e.id) + '</div>').join("") + '</div></details>').join("") + '</details>', new Set(state.pending.keys()));
    const filter = el<HTMLInputElement>("entity-filter").value.toLowerCase();
    details.querySelectorAll<HTMLElement>(".entity-row").forEach(row => row.hidden = !row.dataset.name!.includes(filter));
}
function selectDetails(target: Target): void {
    selected = target;
    renderDetails(state.project!);
    const key = target.kind === "brief" ? "book-brief" : target.kind === "project" ? "guidance" : "selection";
    const panel = details.querySelector<HTMLDetailsElement>('details[data-key="' + key + '"]')!;
    panel.open = true;
    if (window.innerWidth <= 1050) {
        document.body.dataset.pane = "details";
        document.querySelectorAll<HTMLElement>("[data-pane]").forEach(x => x.classList.toggle("active", x.dataset.pane === "details"));
    }
    else { document.body.dataset.detailsCollapsed = "false"; fitPanes(); }
    panel.scrollIntoView({ block: "nearest" });
    persist();
}
state.onchange = (structural) => { render(); scheduleContext(); if (structural) {
    unreadChanges++;
    if (document.body.dataset.pane !== "workspace")
        el("change-unread").textContent = "•";
} persist(); };
state.onstatus = (status, error) => { el("save-state").textContent = status + (state.state ? " · r" + state.state.project.revision : ""); if (error) {
    show((error as Error).message, true);
    void run(conflicts);
} };
const chat = new ChatPane({ client, state, openLink: url => app.openLink({ url }), changed: () => { void run(() => state.sync()); scheduleContext(); }, previewChanged: () => scheduleContext(), readScroll: scope => (preferences.chatScroll as Record<string, number> | undefined)?.[scope.fileName + ":" + scope.surface], saveScroll: (scope, position) => { const positions = (preferences.chatScroll ?? {}) as Record<string, Json>; positions[scope.fileName + ":" + scope.surface] = position; while (Object.keys(positions).length > 100) delete positions[Object.keys(positions)[0]]; preferences.chatScroll = positions; persist(); } });
function scheduleContext(): void { clearTimeout(contextTimer); contextTimer = window.setTimeout(() => { void refreshContext(); }, 500); }
async function refreshContext(): Promise<void> {
    if (!state.state)
        return;
    const captured = state.state.fileName, surface = state.surface, chapterId = state.chapterId;
    try {
        const result = await client.call<Context>("preview_lorekeeper_chat_context", { fileName: captured, surface, chapterId: surface === "editor" ? chapterId : undefined, text: chat.text, model: chat.model });
        if (state.state.fileName !== captured || surface !== state.surface || chapterId !== state.chapterId)
            return;
        context = result;
        renderDetails(state.project!);
    }
    catch (e) {
        show((e as Error).message, true);
    }
}
async function load(fileName?: string): Promise<void> {
    await state.flush();
    await chat.flushDraft();
    const data = await client.call<{
        folder: string;
        projects: {
            fileName: string;
            title: string;
        }[];
        state: State | null;
        unreadable: string[];
    }>("get_lorekeeper_workspace", fileName ? { fileName } : {});
    context = undefined;
    if (data.state?.fileName !== state.state?.fileName) {
        const results = el("search-results"), query = el<HTMLInputElement>("search-query");
        if (results) results.replaceChildren();
        if (query) query.value = "";
    }
    el<HTMLSelectElement>("project-list").innerHTML = '<option value="">Choose a project</option>' + data.projects.map(p => '<option value="' + html(p.fileName) + '">' + html(p.title) + '</option>').join("");
    if (data.state) {
        state.adopt(data.state);
        await chat.setProject();
        await recovery();
        show("Local project opened. Changes autosave after a pause.");
    }
    else {
        render();
        show(data.unreadable.length ? "Some local files need recovery. They were preserved." : "Choose or create a local project.");
    }
}
async function modal(markup: string): Promise<string | null> {
    if (dialog.open)
        dialog.close();
    el("dialog-content").innerHTML = markup;
    dialog.showModal();
    return new Promise(resolve => { const finish = (value: string | null) => { dialog.close(); dialog.removeEventListener("close", closed); resolve(value); }; const closed = () => finish(null); dialog.addEventListener("close", closed, { once: true }); el("dialog-content").onclick = e => { const target = (e.target as HTMLElement).closest<HTMLElement>("[data-dialog]"); if (target)
        finish(target.dataset.dialog!); }; });
}
async function create(): Promise<void> {
    const result = await modal('<h2>New local project</h2><label for="new-title">Working title</label><input id="new-title" value="Untitled book"><label class="check"><input id="new-sample" type="checkbox">Include synthetic sample outline and canon</label><div class="actions"><button data-dialog="cancel">Cancel</button><button data-dialog="create" class="primary">Create project</button></div>');
    if (result !== "create")
        return;
    const title = el<HTMLInputElement>("new-title").value.trim(), sample = el<HTMLInputElement>("new-sample").checked, fileName = "story-" + crypto.randomUUID().slice(0, 8) + ".lorekeeper.json";
    await state.flush();
    await client.call("create_lorekeeper_project", { fileName, title, sample });
    expanded = new Set();
    await load(fileName);
    state.project!.acts.forEach(a => expanded.add(a.id));
    render();
}
async function insert(kind: string, parent?: string): Promise<void> {
    await state.flush();
    const p = state.state!.project, id = crypto.randomUUID(), list = items(p, kind, kind === "chapter" ? parent ?? null : kind === "beat" ? parent! : null);
    const value: Record<string, Json> = kind === "act" ? { id, title: "New act" } : kind === "chapter" ? { id, title: "New chapter" } : kind === "beat" ? { id, title: "New beat" } : kind === "entity" ? { id, name: "New entity", type: p.entityTypes[0] } : { id, name: "New fact", key: "fact-" + id.slice(0, 8) };
    await state.command([{ op: "insert", kind, value, parentId: kind === "chapter" || kind === "beat" ? parent ?? null : undefined, afterId: list.at(-1)?.id as string | undefined, expectedListHash: await hash(list.map(x => x.id)) }], "Add " + kind);
    selected = { kind, id };
    if (parent)
        expanded.add(parent);
    render();
}
async function remove(kind: string, id: string): Promise<void> {
    const result = await modal('<h2>Delete ' + html(kind) + '?</h2><p>' + (kind === "chapter" ? "This removes the chapter prose, its beats and associations. Entities remain." : kind === "act" ? "Its chapters move to Unassigned." : "Links to this item are removed.") + ' A guarded restoration remains available in History.</p><div class="actions"><button data-dialog="cancel">Cancel</button><button data-dialog="delete" class="danger">Delete ' + html(kind) + '</button></div>');
    if (result !== "delete")
        return;
    await state.flush();
    const p = state.state!.project, target = { kind, id }, value = valueAt(p, target) as Record<string, Json>, parent = kind === "chapter" ? value.actId as string | null : kind === "beat" ? value.chapterId as string : null;
    const dependency = await client.call<{
        dependentsHash?: string;
    }>("read_lorekeeper_target", { fileName: state.state!.fileName, target });
    await state.command([{ op: "remove", kind, id, expectedDependentsHash: dependency.dependentsHash, expectedHash: await hash(value), expectedListHash: await hash(items(p, kind, parent).map(x => x.id)) }], "Delete " + kind);
    if (selected.id === id)
        selected = { kind: "brief" };
    render();
}
async function move(kind: string, id: string, direction?: number, parentId?: string | null, anchor?: string | null): Promise<void> {
    await state.flush();
    const p = state.state!.project, target = { kind, id }, value = valueAt(p, target) as Record<string, Json>, source = kind === "chapter" ? value.actId as string | null : kind === "beat" ? value.chapterId as string : null;
    const destination = parentId === undefined ? source : parentId, sourceItems = items(p, kind, source), destinationItems = items(p, kind, destination).filter(x => x.id !== id);
    if (direction) {
        const position = sourceItems.findIndex(x => x.id === id), next = position + direction;
        if (next < 0 || next >= sourceItems.length)
            return;
        anchor = direction < 0 ? (sourceItems[next - 1]?.id as string ?? null) : sourceItems[next].id as string;
    }
    await state.command([{ op: "move", kind, id, parentId: destination, afterId: anchor ?? null, expectedHash: await hash(value), expectedSourceHash: await hash(sourceItems.map(x => x.id)), expectedDestinationHash: await hash(items(p, kind, destination).map(x => x.id)) }], "Move " + kind);
}
async function changeContext(key: string, mode: "include" | "exclude" | "pin" | "unpin"): Promise<void> {
    const p = state.project!, id = context?.preferenceId ?? state.chapterId ?? p.id, pref = p.contextPreferences.find(x => x.id === id) ?? { includes: [], excludes: [], pins: [], budget: 32000 };
    let includes = [...pref.includes], excludes = [...pref.excludes], pins = [...pref.pins];
    if (mode === "pin") {
        pins = [...new Set([...pins, key])];
        excludes = excludes.filter(x => x !== key);
    }
    if (mode === "unpin")
        pins = pins.filter(x => x !== key);
    if (mode === "exclude") {
        includes = includes.filter(x => x !== key);
        pins = pins.filter(x => x !== key);
        excludes = [...new Set([...excludes, key])];
    }
    if (mode === "include") {
        includes = [...new Set([...includes, key])];
        excludes = excludes.filter(x => x !== key);
    }
    state.edit({ kind: "preferences", id, field: "includes" }, includes);
    state.edit({ kind: "preferences", id, field: "excludes" }, excludes);
    state.edit({ kind: "preferences", id, field: "pins" }, pins);
    await state.flush();
    await refreshContext();
}
async function history(): Promise<void> {
    await state.flush();
    let offset = 0, groups: Record<string, unknown>[] = [];
    const page = async () => {
        const data = await client.call<{
            groups: Record<string, unknown>[];
            nextOffset: number | null;
        }>("list_lorekeeper_history", { fileName: state.state!.fileName, offset });
        groups.push(...data.groups);
        offset = data.nextOffset ?? -1;
        el("dialog-content").innerHTML = '<h2>Changes / History</h2><p class="muted">Changes apply directly. Restore only affected items; unrelated edits remain.</p>' + groups.map(g => '<div class="card row"><div class="grow"><strong>' + html(g.label) + '</strong><small> · ' + html(g.source) + ' · r' + g.revision + ' · ' + html(g.createdAt) + '</small></div><button data-history="' + g.id + '">Compare</button></div>').join("") + (offset >= 0 ? '<button id="history-more">Load more</button>' : '') + (state.project!.legacyDrafts.length ? '<h3>Preserved earlier proposals</h3>' + state.project!.legacyDrafts.map(d => '<details><summary>' + html(d.reason) + ' · ' + html(d.status) + '</summary><div class="diff"><pre>' + html(d.before) + '</pre><pre>' + html(d.after) + '</pre></div><small>Historical or unapplied draft. Never applied during migration.</small></details>').join("") : "") + '<div class="actions"><button id="history-close">Close</button></div>';
        el("history-close").onclick = () => dialog.close();
        if (offset >= 0)
            el("history-more").onclick = () => void run(page);
        el("dialog-content").querySelectorAll<HTMLElement>("[data-history]").forEach(b => b.onclick = () => void run(() => compare(b.dataset.history!)));
    };
    dialog.showModal();
    await page();
}
async function compare(groupId: string): Promise<void> {
    const group = await client.call<{
        label: string;
        records: {
            changes: {
                target: Target;
                before: Json;
                after: Json;
                rangeStart?: number;
            }[];
        }[];
    }>("read_lorekeeper_change_group", { fileName: state.state!.fileName, groupId });
    el("dialog-content").innerHTML = '<h2>' + html(group.label) + '</h2>' + group.records.flatMap(r => r.changes).map(c => '<h3>' + html([c.target.kind, c.target.field, c.target.id].filter(Boolean).join(" · ")) + '</h3>' + (c.rangeStart === undefined ? "" : '<small>Changed text range at character ' + c.rangeStart + '</small>') + '<div class="diff"><pre>' + html(typeof c.before === "string" ? c.before : JSON.stringify(c.before, null, 2)) + '</pre><pre>' + html(typeof c.after === "string" ? c.after : JSON.stringify(c.after, null, 2)) + '</pre></div>').join("") + '<div class="actions"><button id="compare-back">History</button><button id="rollback" class="primary">Restore affected items</button><button id="compare-close">Close</button></div>';
    el("compare-close").onclick = () => dialog.close();
    el("compare-back").onclick = () => { dialog.close(); void run(history); };
    el("rollback").onclick = () => void run(async () => { await state.flush(); const result = await client.call<MutationResult>("rollback_lorekeeper_change_group", { fileName: state.state!.fileName, groupId, requestId: crypto.randomUUID() }); await state.receive(result); dialog.close(); render(); scheduleContext(); show("Affected changes restored as a new transaction."); });
}
async function recovery(): Promise<void> {
    const data = await client.call<{
        drafts: {
            sessionId: string;
            sequence: number;
            updatedAt: string;
            operations: Operation[];
            request?: Record<string, unknown>;
        }[];
    }>("get_lorekeeper_drafts", { fileName: state.state!.fileName });
    if (!data.drafts.length)
        return;
    const draft = data.drafts.find(d => d.sessionId !== state.sessionId);
    if (!draft)
        return;
    const result = await modal('<h2>Retained local draft</h2><p>A previous view retained an unsaved or uncertain batch. Inspect both saved and draft content before deciding.</p><pre>' + html(JSON.stringify(draft.operations, null, 2)) + '</pre><div class="actions"><button data-dialog="later">Keep for later</button><button data-dialog="discard">Keep saved content</button><button data-dialog="retry">Retry original guarded save</button></div>');
    if (result === "retry") {
        if (!draft.request)
            throw new Error("This retained journal lacks its original receipt identity. It stays preserved for inspection.");
        const receipt = await client.call<MutationResult>("apply_lorekeeper_manual_changes", draft.request);
        await state.receive(receipt);
    }
    else if (result !== "discard")
        return;
    await client.call("save_lorekeeper_draft", { fileName: state.state!.fileName, sessionId: draft.sessionId, sequence: draft.sequence + 1, operations: [], clear: true });
    render();
}
async function conflicts(): Promise<void> {
    if (!state.blocked || dialog.open)
        return;
    const current = await client.call<{
        state: State;
    }>("get_lorekeeper_workspace", { fileName: state.state!.fileName });
    const result = await modal('<h2>Save failed — both variants retained</h2><p>Retry keeps the original request identity. Applying your draft explicitly uses the latest saved hashes.</p>' + [...state.pending.values()].map(d => '<h3>' + html(d.target.field ?? d.target.kind) + '</h3><div class="diff"><pre>' + html(JSON.stringify(valueAt(current.state.project, d.target))) + '</pre><pre>' + html(typeof d.value === "string" ? d.value : JSON.stringify(d.value)) + '</pre></div>').join("") + '<div class="actions"><button data-dialog="later">Keep for later</button><button data-dialog="retry">Retry save</button><button data-dialog="mine">Apply my variant</button></div>');
    if (result === "retry")
        await state.retry();
    if (result === "mine") {
        state.state = current.state;
        const ops = await Promise.all([...state.pending.values()].map(async (d) => ({ op: "set", target: d.target, value: d.value, expectedHash: await hash(valueAt(current.state.project, d.target)) })));
        const response = await client.call<MutationResult>("apply_lorekeeper_manual_changes", { fileName: current.state.fileName, requestId: crypto.randomUUID(), expectedRevision: current.state.project.revision, surface: state.surface, label: "Resolve retained draft", operations: ops });
        await state.receive(response);
        state.pending.clear();
        state.blocked = false;
        await client.call("save_lorekeeper_draft", { fileName: current.state.fileName, sessionId: state.sessionId, sequence: ++state.sequence, operations: [], clear: true });
        render();
    }
}
async function settings(): Promise<void> {
    await modal('<h2>Connection & layout</h2><p>Codex owns authentication. Other providers, World, Voices, Sources, Images and Publish are deferred.</p><p>Project folder and content stay on this computer. Narrow layout does not connect a phone or ChatGPT web to the local server.</p><div class="row"><button id="settings-connect">Connect Codex</button><button id="settings-signin">Sign in</button><button id="settings-disconnect">Disconnect</button></div><h3>Desktop panes</h3><div class="row"><button id="collapse-chat">Toggle chat</button><button id="collapse-details">Toggle details</button></div><div class="actions"><button data-dialog="close">Close</button></div>');
}
function input(event: Event): void {
    const input = event.target as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement;
    if (input.id === "entity-filter") {
        details.querySelectorAll<HTMLElement>(".entity-row").forEach(r => r.hidden = !r.dataset.name!.includes(input.value.toLowerCase()));
        return;
    }
    if (!input.dataset.target)
        return;
    if (input.id === "manuscript") {
        manuscriptHistory.record(manuscriptKey(), input.value, { start: (input as HTMLTextAreaElement).selectionStart, end: (input as HTMLTextAreaElement).selectionEnd }, (event as InputEvent).inputType === "insertText");
        manuscriptButtons();
    }
    try {
        let value: Json = input.value;
        if (input.dataset.convert === "json")
            value = JSON.parse(input.value) as Json;
        if (input.dataset.convert === "aliases")
            value = input.value.split(",").map(x => x.trim()).filter(Boolean);
        if (input.dataset.convert === "number")
            value = input.value === "" ? null : Number(input.value);
        if (input.dataset.convert === "boolean")
            value = input.value === "" ? null : input.value === "true";
        state.edit(JSON.parse(input.dataset.target) as Target, value);
        input.removeAttribute("aria-invalid");
        if (input.id === "manuscript")
            el("word-count").textContent = input.value.trim().split(/\s+/u).filter(Boolean).length + " words";
    }
    catch {
        input.setAttribute("aria-invalid", "true");
        state.edit(JSON.parse(input.dataset.target) as Target, input.value);
        show("Enter valid JSON before this field can save. Other fields remain editable.", true);
    }
}
function manuscriptButtons(): void {
    for (const name of ["undo", "redo"] as const) {
        const button = content.querySelector<HTMLButtonElement>('[data-action="' + name + '"]');
        if (button) button.disabled = editorMode !== "edit" || !manuscriptHistory.can(manuscriptKey(), name);
    }
}
function manuscriptUndo(action: "undo" | "redo"): void {
    const field = el<HTMLTextAreaElement>("manuscript"), snapshot = field && manuscriptHistory.apply(manuscriptKey(), action);
    if (!snapshot) return;
    field.value = snapshot.text; field.focus(); field.setSelectionRange(snapshot.start, snapshot.end);
    state.edit({ kind: "chapter", id: state.chapterId, field: "text" }, snapshot.text);
    el("word-count").textContent = snapshot.text.trim().split(/\s+/u).filter(Boolean).length + " words";
    manuscriptButtons();
}
content.addEventListener("input", input);
content.addEventListener("beforeinput", event => {
    const field = event.target as HTMLTextAreaElement;
    if (field.id === "manuscript") manuscriptHistory.position(manuscriptKey(), { start: field.selectionStart, end: field.selectionEnd });
});
content.addEventListener("keydown", event => {
    if ((event.target as HTMLElement).id === "manuscript" && !event.isComposing && (event.ctrlKey || event.metaKey) && ["z", "y"].includes(event.key.toLowerCase())) {
        event.preventDefault(); manuscriptUndo(event.key.toLowerCase() === "y" || event.shiftKey ? "redo" : "undo");
    }
});
details.addEventListener("input", input);
for (const pane of [content, details]) {
    pane.addEventListener("compositionstart", event => {
        const field = event.target as HTMLElement;
        if (field.dataset.target) { field.dataset.composing = "true"; state.composing = true; }
    });
    pane.addEventListener("compositionend", event => {
        const field = event.target as HTMLElement;
        if (field.dataset.target) { delete field.dataset.composing; state.composing = false; input(event); }
    });
}
details.addEventListener("change", event => { const box = event.target as HTMLInputElement; if (box.dataset.contextKey)
    void run(() => changeContext(box.dataset.contextKey!, box.checked ? "include" : "exclude")); if (box.dataset.association) {
    const target = { kind: box.dataset.kind!, id: box.dataset.id!, field: box.dataset.association }, value = valueAt(state.project!, target) as string[];
    state.edit(target, box.checked ? [...new Set([...value, box.value])] : value.filter(x => x !== box.value));
} });
async function click(e: MouseEvent): Promise<void> {
    const button = (e.target as HTMLElement).closest<HTMLElement>("[data-action]");
    if (!button)
        return;
    const { action: name, kind, id } = button.dataset;
    if (name === "new")
        return create();
    if (!state.state)
        return;
    if (name === "insert")
        return insert(kind!, id);
    if (name === "delete")
        return remove(kind!, id!);
    if (name === "details") {
        selectDetails({ kind: kind!, id });
        return;
    }
    if (name === "expand") {
        expanded.has(id!) ? expanded.delete(id!) : expanded.add(id!);
        render();
        persist();
        return;
    }
    if (name === "up" || name === "down")
        return move(kind!, id!, name === "up" ? -1 : 1);
    if (name === "chapter" || name === "write") {
        await state.flush();
        state.chapterId = id;
        selected = { kind: "chapter", id };
        if (name === "write")
            state.surface = "editor";
        render();
        await chat.setProject();
        scheduleContext();
        persist();
        return;
    }
    if (name === "editor-mode") {
        await state.flush();
        editorMode = button.dataset.mode!;
        render();
        return;
    }
    if (name === "undo" || name === "redo") {
        manuscriptUndo(name);
        return;
    }
    if (name === "history")
        return history();
    if (name === "move-parent") {
        const result = await modal('<h2>Move chapter</h2><label for="move-act">Act</label><select id="move-act"><option value="">Unassigned</option>' + state.project!.acts.map(a => '<option value="' + a.id + '">' + html(a.title) + '</option>').join("") + '</select><div class="actions"><button data-dialog="cancel">Cancel</button><button data-dialog="move">Move</button></div>');
        if (result === "move") {
            const parent = el<HTMLSelectElement>("move-act").value || null;
            await move(kind!, id!, undefined, parent);
            if (parent)
                expanded.add(parent);
            render();
        }
        return;
    }
    if (name === "entity-type") {
        const result = await modal('<h2>Custom entity type</h2><label for="type-name">Name</label><input id="type-name"><div class="actions"><button data-dialog="cancel">Cancel</button><button data-dialog="add">Add</button></div>');
        if (result === "add") {
            state.edit({ kind: "types" }, [...state.project!.entityTypes, el<HTMLInputElement>("type-name").value.trim()]);
            await state.flush();
            render();
        }
        return;
    }
    if (name === "relationship") {
        const p = state.project!;
        if (p.entities.length < 2)
            throw new Error("Create two entities before linking a relationship.");
        await state.flush();
        await state.command([{ op: "insert", kind: "relationship", value: { id: crypto.randomUUID(), fromId: id!, toId: p.entities.find(x => x.id !== id)!.id, type: "Related", properties: {} }, expectedListHash: await hash(p.relationships.map(x => x.id)), afterId: p.relationships.at(-1)?.id }], "Add relationship");
        render();
        return;
    }
    if (name === "source") {
        const source = context!.sources.find(s => s.key === button.dataset.keySource)!;
        selectDetails(source.target!);
        return;
    }
    if (name === "pin")
        return changeContext(button.dataset.keySource!, context!.sources.find(s => s.key === button.dataset.keySource)!.pinned ? "unpin" : "pin");
    if (name === "reset-context") {
        const id = context!.preferenceId;
        state.edit({ kind: "preferences", id, field: "includes" }, []);
        state.edit({ kind: "preferences", id, field: "excludes" }, []);
        state.edit({ kind: "preferences", id, field: "pins" }, []);
        state.edit({ kind: "preferences", id, field: "budget" }, 32000);
        await state.flush();
        return refreshContext();
    }
    if (name === "prompt") {
        await state.flush();
        await refreshContext();
        await modal('<h2>Assembled request preview</h2><p>This includes authoring instructions, tool schemas, quoted saved prose, the current message, and project sources. Codex owns native turn processing.</p><pre>' + html(JSON.stringify({ inputBudget: context!.inputBudget, estimatedRequestTokens: context!.estimatedRequestTokens, preparationMs: context!.preparationMs, prompt: context!.prompt, excludedRange: context!.excludedRange }, null, 2)) + '</pre><div class="actions"><button data-dialog="close">Close</button></div>');
        return;
    }
    if (name === "search") {
        const data = await client.call<{
            results: {
                title: string;
                text: string;
                key: string;
                target: Target;
            }[];
        }>("search_lorekeeper_project", { fileName: state.state!.fileName, query: el<HTMLInputElement>("search-query").value });
        el("search-results").innerHTML = data.results.map((s, i) => '<div class="source"><strong>' + html(s.title) + '</strong><small>' + html(s.text) + '</small><div class="row"><button data-hit="' + i + '">Open fields</button><button data-hit-pin="' + i + '">Pin complete source</button></div></div>').join("") || '<p class="muted">No matching sources.</p>';
        el("search-results").querySelectorAll<HTMLElement>("[data-hit]").forEach(b => b.onclick = () => selectDetails(data.results[Number(b.dataset.hit)].target));
        el("search-results").querySelectorAll<HTMLElement>("[data-hit-pin]").forEach(b => b.onclick = () => void run(() => changeContext(data.results[Number(b.dataset.hitPin)].key, "pin")));
        return;
    }
}
content.addEventListener("click", e => void run(() => click(e)));
details.addEventListener("click", e => void run(() => click(e)));
document.addEventListener("click", e => {
    const button = (e.target as HTMLElement).closest<HTMLElement>("[data-pane],[data-surface]");
    if (button?.dataset.pane) {
        document.body.dataset.pane = button.dataset.pane;
        document.querySelectorAll<HTMLElement>("[data-pane]").forEach(b => b.classList.toggle("active", b.dataset.pane === button.dataset.pane));
        if (button.dataset.pane === "workspace") {
            unreadChanges = 0;
            el("change-unread").textContent = "";
        }
        if (button.dataset.pane === "chat")
            el("chat-unread").textContent = "";
        persist();
    }
    if (button?.dataset.surface)
        void run(async () => { await state.flush(); await chat.flushDraft(); state.boundary(); state.surface = button.dataset.surface as "outline" | "editor"; render(); await chat.setProject(); scheduleContext(); persist(); });
    const id = (e.target as HTMLElement).id;
    if (id === "settings-connect")
        void chat.connect();
    if (id === "settings-signin")
        void chat.login();
    if (id === "settings-disconnect")
        void chat.disconnect();
    if (id === "collapse-chat" || id === "collapse-details") {
        const key = id === "collapse-chat" ? "chatCollapsed" : "detailsCollapsed";
        document.body.dataset[key] = String(document.body.dataset[key] !== "true");
        fitPanes();
        persist();
    }
});
el("new").onclick = () => void run(create);
el("save-state").onclick = () => void run(() => state.blocked ? conflicts() : state.flush().then(recovery));
el("save-state").onkeydown = event => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); el("save-state").click(); } };
el("history").onclick = () => void run(history);
el("settings").onclick = () => void run(settings);
el("theme").onclick = () => { document.documentElement.dataset.lkTheme = document.documentElement.dataset.lkTheme === "dark" ? "light" : "dark"; persist(); };
el<HTMLSelectElement>("project-list").onchange = () => { const file = el<HTMLSelectElement>("project-list").value; el<HTMLSelectElement>("project-list").value = state.state?.fileName ?? ""; if (file)
    void run(() => load(file)); };
for (const side of ["chat", "details"]) {
    const separator = el(side + "-resizer"), key = "--" + side + "-width";
    separator.onpointerdown = e => { separator.setPointerCapture(e.pointerId); const start = e.clientX, width = parseInt(getComputedStyle(document.documentElement).getPropertyValue(key)) || 330; separator.onpointermove = move => { document.documentElement.style.setProperty(key, Math.max(260, Math.min(600, width + (move.clientX - start) * (side === "chat" ? 1 : -1))) + "px"); fitPanes(); }; separator.onpointerup = () => { separator.onpointermove = null; persist(); }; };
    separator.onkeydown = e => { if (e.key === "ArrowLeft" || e.key === "ArrowRight") {
        e.preventDefault();
        const width = parseInt(getComputedStyle(document.documentElement).getPropertyValue(key)) || 330;
        document.documentElement.style.setProperty(key, Math.max(260, Math.min(600, width + (e.key === "ArrowRight" ? 20 : -20) * (side === "chat" ? 1 : -1))) + "px");
        fitPanes();
        persist();
    } };
}
let dragging: {
    kind: string;
    id: string;
} | undefined;
content.addEventListener("dragstart", e => { const target = (e.target as HTMLElement).closest<HTMLElement>("[data-drag-kind]"); if (target) {
    dragging = { kind: target.dataset.dragKind!, id: target.dataset.id! };
    e.dataTransfer!.setData("text/plain", dragging.id);
} });
content.addEventListener("dragover", e => { if (dragging && (e.target as HTMLElement).closest("[data-drop-kind]"))
    e.preventDefault(); });
content.addEventListener("drop", e => { e.preventDefault(); const target = (e.target as HTMLElement).closest<HTMLElement>("[data-drop-kind]"), drag = dragging; dragging = undefined; if (!target || !drag || drag.id === target.dataset.id || drag.kind !== target.dataset.dropKind)
    return; void run(async () => { const value = valueAt(state.project!, { kind: drag.kind, id: target.dataset.id }) as Record<string, Json>; await move(drag.kind, drag.id, undefined, drag.kind === "chapter" ? value.actId as string | null : drag.kind === "beat" ? value.chapterId as string : null, target.dataset.id); }); });
content.addEventListener("scroll", persist, { passive: true });
details.addEventListener("scroll", persist, { passive: true });
details.addEventListener("toggle", persist, true);
document.addEventListener("focusout", () => { state.boundary(); });
window.addEventListener("resize", () => { fitPanes(); persist(); });
app.onteardown = async () => { await state.flush(); await chat.flushDraft(); clearTimeout(prefsTimer); await client.call("save_lorekeeper_ui_preferences", { values: layoutPreferences() }); return {}; };
app.ontoolresult = result => { const incoming = result._meta?.["lorekeeper/workspace"] as State | undefined; if (incoming && connected && !state.state)
    void run(() => load(incoming.fileName)); };
async function poll(): Promise<void> { if (connected && state.state && !polling) {
    polling = true;
    try {
        await state.sync();
    }
    catch (e) {
        show((e as Error).message, true);
    }
    finally {
        polling = false;
    }
} setTimeout(() => void poll(), 450); }
try {
    await app.connect();
    connected = true;
    el("plugin-version").textContent = "Local · " + manifest.version;
    preferences = await client.call("get_lorekeeper_ui_preferences");
    state.surface = preferences.surface === "editor" ? "editor" : "outline";
    state.chapterId = typeof preferences.chapterId === "string" ? preferences.chapterId : undefined;
    if (preferences.selected && typeof preferences.selected === "object")
        selected = preferences.selected as unknown as Target;
    expanded = new Set(Array.isArray(preferences.expanded) ? preferences.expanded as string[] : []);
    document.documentElement.dataset.lkTheme = preferences.theme === "light" || preferences.theme === "dark" ? preferences.theme : app.getHostContext()?.theme ?? "dark";
    for (const side of ["chat", "details"]) {
        const width = preferences[side + "Width"];
        if (typeof width === "string" && /^\d{3}px$/.test(width))
            document.documentElement.style.setProperty("--" + side + "-width", width);
        document.body.dataset[side + "Collapsed"] = String(preferences[side + "Collapsed"] === true);
    }
    document.body.dataset.pane = typeof preferences.pane === "string" ? preferences.pane : "workspace";
    fitPanes();
    await load(typeof preferences.fileName === "string" ? preferences.fileName : undefined);
    void poll();
}
catch (e) {
    show((e as Error).message + " Reopen from the installed plugin or development host.", true);
}
