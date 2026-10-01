import { App } from "@modelcontextprotocol/ext-apps";
export type Json = null | boolean | number | string | Json[] | {
    [key: string]: Json;
};
export interface Target {
    kind: string;
    id?: string;
    field?: string;
}
export interface Act {
    id: string;
    title: string;
    synopsis: string;
}
export interface Chapter extends Act {
    actId: string | null;
    text: string;
    entityIds: string[];
}
export interface Beat {
    id: string;
    chapterId: string;
    title: string;
    summary: string;
    entityIds: string[];
}
export interface Entity {
    id: string;
    name: string;
    type: string;
    summary: string;
    aliases: string[];
    properties: Record<string, string>;
    chapterIds: string[];
}
export interface Fact {
    id: string;
    name: string;
    key: string;
    value: string;
    entityIds: string[];
}
export interface Relationship {
    id: string;
    fromId: string;
    toId: string;
    type: string;
    properties: Record<string, string>;
}
export interface Preference {
    id: string;
    includes: string[];
    excludes: string[];
    pins: string[];
    budget: number;
}
export interface LegacyDraft {
    id: string;
    before: string;
    after: string;
    reason: string;
    status: string;
    target: Target;
}
export interface Project {
    id: string;
    revision: number;
    title: string;
    guidance: string;
    bookBrief: Record<string, Json>;
    entityTypes: string[];
    acts: Act[];
    chapters: Chapter[];
    beats: Beat[];
    entities: Entity[];
    facts: Fact[];
    relationships: Relationship[];
    contextPreferences: Preference[];
    legacyDrafts: LegacyDraft[];
    updatedAt: string;
}
export interface State {
    fileName: string;
    project: Project;
    etag: string;
}
export interface Change {
    target: Target;
    before: Json;
    after: Json;
    beforeHash: string;
    afterHash: string;
    rangeStart?: number;
}
export interface MutationResult {
    fileName: string;
    etag: string;
    baseEtag?: string;
    revision: number;
    updatedAt: string;
    changes: Change[];
    groupId?: string;
    replayed?: boolean;
    localActionMs?: number;
}
export interface Operation {
    op: string;
    target?: Target;
    expectedHash?: string;
    value?: Json;
    kind?: string;
    id?: string;
    parentId?: string | null;
    afterId?: string | null;
    expectedListHash?: string;
    expectedDependentsHash?: string;
    expectedSourceHash?: string;
    expectedDestinationHash?: string;
}
export const collection: Record<string, string> = { act: "acts", chapter: "chapters", beat: "beats", entity: "entities", fact: "facts", relationship: "relationships", preferences: "contextPreferences" };
export const html = (value: unknown): string => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));
export const canonical = (value: unknown): string => Array.isArray(value) ? "[" + value.map(canonical).join(",") + "]" : value && typeof value === "object" ? "{" + Object.keys(value).sort().map(key => JSON.stringify(key) + ":" + canonical((value as Record<string, unknown>)[key])).join(",") + "}" : JSON.stringify(value ?? null);
export async function hash(value: unknown): Promise<string> { return [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonical(value))))].map(x => x.toString(16).padStart(2, "0")).join(""); }
export const keyOf = (target: Target): string => canonical(target);
export function items(p: Project, kind: string, parentId: string | null = null): Record<string, Json>[] {
    const all = (p as unknown as Record<string, Record<string, Json>[]>)[collection[kind]] ?? [];
    return all.filter(x => kind === "chapter" ? x.actId === parentId : kind === "beat" ? x.chapterId === parentId : true);
}
export function valueAt(p: Project, t: Target): Json {
    if (t.kind === "list")
        return items(p, t.field!, t.id ?? null).map(x => x.id);
    if (t.kind === "types")
        return p.entityTypes;
    const owner = t.kind === "project" ? p : t.kind === "brief" ? p.bookBrief : t.kind === "preferences" ? p.contextPreferences.find(x => x.id === t.id) ?? (t.field ? { id: t.id ?? p.id, includes: [], excludes: [], pins: [], budget: 32000 } : null) : ((p as unknown as Record<string, Record<string, Json>[]>)[collection[t.kind]] ?? []).find(x => x.id === t.id);
    return (t.field ? (owner as Record<string, Json> | null)?.[t.field] ?? null : owner ?? null) as Json;
}
export function setValue(p: Project, t: Target, value: Json): void {
    if (t.kind === "types") {
        p.entityTypes = value as string[];
        return;
    }
    if (t.kind === "project" || t.kind === "brief") {
        (t.kind === "project" ? p as unknown as Record<string, Json> : p.bookBrief)[t.field!] = value;
        return;
    }
    const all = (p as unknown as Record<string, Record<string, Json>[]>)[collection[t.kind]], index = all.findIndex(x => x.id === t.id);
    if (t.field) {
        if (index < 0 && t.kind === "preferences")
            all.push({ id: t.id!, includes: [], excludes: [], pins: [], budget: 32000, [t.field]: value });
        else if (index >= 0)
            all[index][t.field] = value;
    }
    else {
        if (index >= 0)
            all.splice(index, 1);
        if (value)
            all.push(value as Record<string, Json>);
    }
}
export function patchProject(p: Project, changes: Change[]): void {
    for (const change of changes.filter(x => x.target.kind !== "list")) {
        const previous = valueAt(p, change.target);
        setValue(p, change.target, change.rangeStart == null ? change.after : (previous as string).slice(0, change.rangeStart) + change.after + (previous as string).slice(change.rangeStart + (change.before as string).length));
    }
    for (const change of changes.filter(x => x.target.kind === "list")) {
        const t = change.target, all = (p as unknown as Record<string, Record<string, Json>[]>)[collection[t.field!]], keys = change.after as string[], selected = new Set(keys), positions = all.map((x, i) => selected.has(x.id as string) ? i : -1).filter(x => x >= 0), ordered = keys.map(key => all.find(x => x.id === key)!);
        positions.forEach((position, i) => { all[position] = ordered[i]; });
    }
}
export class ToolError extends Error {
    constructor(public code: string, message: string, public details?: unknown) { super(message); }
}
export class Client {
    constructor(public app: App) { }
    async call<T = Record<string, unknown>>(name: string, args: Record<string, unknown> = {}): Promise<T> {
        const result = await this.app.callServerTool({ name, arguments: args });
        if (result.isError) {
            const data = result.structuredContent as {
                code?: string;
                message?: string;
                details?: unknown;
            } | undefined;
            throw new ToolError(data?.code ?? "OPERATION_FAILED", data?.message ?? "The operation failed. Your draft is preserved.", data?.details);
        }
        return result.structuredContent as T;
    }
}
interface Draft {
    target: Target;
    value: Json;
    expectedHash: Promise<string>;
}
export class WorkspaceState {
    state?: State;
    surface: "outline" | "editor" = "outline";
    chapterId?: string;
    pending = new Map<string, Draft>();
    sessionId = crypto.randomUUID();
    sequence = 0;
    groupId = crypto.randomUUID();
    private timer?: number;
    private saving?: Promise<void>;
    private commanding?: Promise<void>;
    private request?: Record<string, unknown>;
    blocked = false;
    composing = false;
    onchange: (structural: boolean) => void = () => { };
    onstatus: (status: string, error?: unknown) => void = () => { };
    constructor(public client: Client) { }
    get project(): Project | undefined {
        if (!this.state)
            return;
        const p = structuredClone(this.state.project);
        for (const draft of this.pending.values())
            setValue(p, draft.target, draft.value);
        return p;
    }
    adopt(state: State): void {
        this.state = state;
        this.selectExistingChapter();
        this.onchange(true);
    }
    private selectExistingChapter(): void {
        if (!this.state?.project.chapters.some(x => x.id === this.chapterId)) this.chapterId = this.state?.project.chapters[0]?.id;
    }
    edit(target: Target, value: Json): void {
        if (!this.state)
            return;
        const key = keyOf(target), existing = this.pending.get(key);
        if (existing)
            existing.value = value;
        else
            this.pending.set(key, { target, value, expectedHash: hash(valueAt(this.state.project, target)) });
        this.onstatus(this.blocked ? "Conflict — draft retained" : "Unsaved");
        window.clearTimeout(this.timer);
        if (!this.blocked && !this.composing)
            this.timer = window.setTimeout(() => { void this.flush().catch(() => { }); }, 500);
    }
    boundary(): void { this.groupId = crypto.randomUUID(); }
    async flush(): Promise<void> {
        window.clearTimeout(this.timer);
        if (this.composing)
            throw new ToolError("DRAFT_COMPOSING", "Finish composing text before saving or switching views.");
        if (this.commanding)
            await this.commanding;
        if (this.saving)
            await this.saving;
        if (!this.pending.size)
            return;
        if (this.blocked)
            throw new ToolError("DRAFT_BLOCKED", "Resolve or retry the retained draft before continuing.");
        this.saving = this.save();
        try {
            await this.saving;
        }
        finally {
            this.saving = undefined;
        }
        if (this.pending.size)
            return this.flush();
    }
    private async save(): Promise<void> {
        const state = this.state!, entries = [...this.pending.values()].map(x => ({ ...x, value: structuredClone(x.value) }));
        const operations = await Promise.all(entries.map(async (x) => ({ op: "set", target: x.target, value: x.value, expectedHash: await x.expectedHash })));
        const args = { fileName: state.fileName, requestId: crypto.randomUUID(), groupId: this.groupId, expectedRevision: state.project.revision, surface: this.surface, label: "Edit " + (entries.length === 1 ? entries[0].target.field ?? entries[0].target.kind : "fields"), operations };
        this.request = args;
        this.onstatus("Saving…");
        try {
            await this.client.call("save_lorekeeper_draft", { fileName: state.fileName, sessionId: this.sessionId, sequence: ++this.sequence, operations, request: args });
            const result = await this.client.call<MutationResult>("apply_lorekeeper_manual_changes", args);
            await this.receive(result);
            for (const entry of entries) {
                const current = this.pending.get(keyOf(entry.target));
                if (!current)
                    continue;
                if (canonical(current.value) === canonical(entry.value))
                    this.pending.delete(keyOf(entry.target));
                else
                    current.expectedHash = hash(valueAt(this.state!.project, entry.target));
            }
            if (!this.pending.size)
                await this.client.call("save_lorekeeper_draft", { fileName: state.fileName, sessionId: this.sessionId, sequence: ++this.sequence, operations: [], clear: true });
            this.request = undefined;
            this.onchange(result.changes.some(x => !x.target.field || ["actId", "chapterId"].includes(x.target.field)));
            this.onstatus(this.pending.size ? "Unsaved" : "Saved" + (result.localActionMs == null ? "" : " · " + result.localActionMs + " ms local"));
        }
        catch (error) {
            this.blocked = !(error instanceof ToolError && ["INVALID_PROJECT", "INVALID_OPERATION"].includes(error.code));
            this.onstatus(this.blocked ? "Save failed — draft retained" : "Invalid field — draft retained", error);
            throw error;
        }
    }
    async retry(): Promise<void> {
        if (!this.request) {
            this.blocked = false;
            return this.flush();
        }
        const result = await this.client.call<MutationResult>("apply_lorekeeper_manual_changes", this.request);
        await this.receive(result);
        const operations = this.request.operations as Operation[];
        for (const operation of operations) {
            const draft = this.pending.get(keyOf(operation.target!));
            if (draft && canonical(draft.value) === canonical(operation.value))
                this.pending.delete(keyOf(operation.target!));
            else if (draft)
                draft.expectedHash = hash(valueAt(this.state!.project, draft.target));
        }
        this.request = undefined;
        this.blocked = false;
        await this.client.call("save_lorekeeper_draft", { fileName: this.state!.fileName, sessionId: this.sessionId, sequence: ++this.sequence, operations: [], clear: true });
        this.onchange(true);
        await this.flush();
        this.onstatus("Saved");
    }
    async receive(result: MutationResult): Promise<void> {
        if (this.state!.etag === result.etag)
            return;
        if (result.replayed || result.baseEtag && this.state!.etag !== result.baseEtag || this.state!.project.revision > result.revision) {
            const data = await this.client.call<{
                state: State;
            }>("get_lorekeeper_workspace", { fileName: result.fileName });
            this.state = data.state;
        }
        else {
            patchProject(this.state!.project, result.changes);
            this.state!.project.revision = result.revision;
            this.state!.project.updatedAt = result.updatedAt;
            this.state!.etag = result.etag;
        }
        this.selectExistingChapter();
    }
    async command(operations: Operation[], label: string): Promise<void> {
        await this.flush();
        this.boundary();
        const apply = async (): Promise<void> => {
            const result = await this.client.call<MutationResult>("apply_lorekeeper_manual_changes", { fileName: this.state!.fileName, requestId: crypto.randomUUID(), groupId: this.groupId, expectedRevision: this.state!.project.revision, surface: this.surface, label, operations });
            await this.receive(result);
            this.onchange(true);
            this.onstatus("Saved" + (result.localActionMs == null ? "" : " · " + result.localActionMs + " ms local"));
            this.boundary();
        };
        this.commanding = apply();
        try {
            await this.commanding;
        }
        finally {
            this.commanding = undefined;
        }
    }
    async sync(): Promise<void> {
        if (!this.state || this.saving || this.commanding)
            return;
        const fileName = this.state.fileName, etag = this.state.etag;
        const data = await this.client.call<MutationResult & {
            unchanged?: boolean;
            state?: State;
        }>("sync_lorekeeper_project", { fileName, knownEtag: etag });
        if (!this.state || this.state.fileName !== fileName || this.state.etag !== etag || data.unchanged)
            return;
        if (data.state)
            this.state = data.state;
        else
            await this.receive(data);
        this.selectExistingChapter();
        this.onchange(true);
    }
}
