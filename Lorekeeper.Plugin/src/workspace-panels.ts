import { html, keyOf, type Target, type Project, type Json, type Chapter } from "./workspace-state";
let fieldScope = "details";
export const setFieldScope = (scope: string): void => { fieldScope = scope; };
export const targetAttribute = (target: Target): string => ' data-target="' + html(JSON.stringify(target)) + '" data-key="' + html(JSON.stringify(target)) + '"';
export function field(target: Target, label: string, value: Json, type = "text", options?: string[]): string {
    const attr = targetAttribute(target), id = "f-" + fieldScope + "-" + [target.kind, target.id ?? "", target.field ?? ""].join("-");
    const control = type === "textarea" ? '<textarea id="' + id + '"' + attr + '>' + html(value) + '</textarea>' : type === "select" ? '<select id="' + id + '"' + attr + '>' + options!.map(x => '<option' + (x === value ? ' selected' : '') + '>' + html(x) + '</option>').join("") + '</select>' : type === "json" ? '<textarea id="' + id + '"' + attr + ' data-convert="json">' + html(JSON.stringify(value, null, 2)) + '</textarea>' : type === "aliases" ? '<input id="' + id + '"' + attr + ' data-convert="aliases" value="' + html((value as string[]).join(", ")) + '">' : '<input id="' + id + '"' + attr + ' type="' + type + '"' + (type === "number" ? ' data-convert="number"' : '') + ' value="' + html(value) + '">';
    return '<label for="' + id + '">' + html(label) + '</label>' + control;
}
export const action = (name: string, label: string, kind?: string, id?: string, extra = ""): string => '<button data-action="' + name + '"' + (kind ? ' data-kind="' + kind + '"' : '') + (id ? ' data-id="' + id + '"' : '') + extra + '>' + label + '</button>';
export function controls(kind: string, id: string): string { return '<span class="actions"><span class="handle" draggable="true" data-drag-kind="' + kind + '" data-id="' + id + '" aria-label="Drag to reorder">⠿</span>' + action("up", "↑", kind, id, ' aria-label="Move up"') + action("down", "↓", kind, id, ' aria-label="Move down"') + action("details", "Details", kind, id) + action("delete", "×", kind, id, ' aria-label="Delete ' + kind + '"') + '</span>'; }
export function outline(p: Project, expanded: Set<string>, selected?: Target): string {
    const chapter = (c: Chapter): string => '<div class="chapter card' + (selected?.id === c.id ? ' selected' : '') + '" data-key="' + c.id + '" data-drop-kind="chapter" data-id="' + c.id + '"><div class="row">' + field({ kind: "chapter", id: c.id, field: "title" }, "Chapter title", c.title) + controls("chapter", c.id) + '</div>' + field({ kind: "chapter", id: c.id, field: "synopsis" }, "Chapter synopsis", c.synopsis, "textarea") + '<div class="row wrap">' + action("expand", expanded.has(c.id) ? "▾ Beats" : "▸ Beats", "chapter", c.id) + action("insert", "＋ Beat", "beat", c.id) + action("move-parent", "Move to act…", "chapter", c.id) + action("write", "Write", "chapter", c.id) + '<small>' + p.beats.filter(b => b.chapterId === c.id).length + ' beats · ' + c.text.trim().split(/\s+/u).filter(Boolean).length + ' words</small></div>' + (expanded.has(c.id) ? p.beats.filter(b => b.chapterId === c.id).map(b => '<div class="beat" data-key="' + b.id + '" data-drop-kind="beat" data-id="' + b.id + '"><div class="row">' + field({ kind: "beat", id: b.id, field: "title" }, "Beat title", b.title) + controls("beat", b.id) + '</div>' + field({ kind: "beat", id: b.id, field: "summary" }, "Beat summary", b.summary, "textarea") + '<div class="chips">' + b.entityIds.map(id => '<span class="chip">' + html(p.entities.find(e => e.id === id)?.name) + '</span>').join("") + '</div></div>').join("") : "") + '</div>';
    return '<div class="pane-title"><h2>Outline</h2><span class="row">' + action("insert", "＋ Act", "act") + action("insert", "＋ Chapter", "chapter") + '</span></div><div class="pane-body"><p class="muted">Acts, chapters and beats. Changes save automatically.</p>' + p.acts.map(a => '<section class="act" data-key="' + a.id + '" data-drop-kind="act" data-id="' + a.id + '"><div class="row">' + field({ kind: "act", id: a.id, field: "title" }, "Act title", a.title) + controls("act", a.id) + '</div>' + field({ kind: "act", id: a.id, field: "synopsis" }, "Act synopsis", a.synopsis, "textarea") + '<div class="row">' + action("expand", expanded.has(a.id) ? "▾ Chapters" : "▸ Chapters", "act", a.id) + action("insert", "＋ Chapter", "chapter", a.id) + '</div>' + (expanded.has(a.id) ? p.chapters.filter(c => c.actId === a.id).map(chapter).join("") : "") + '</section>').join("") + '<section class="act" data-key="unassigned"><h3>Unassigned</h3>' + p.chapters.filter(c => !c.actId).map(chapter).join("") + '</section></div>';
}
export function editor(p: Project, chapterId: string | undefined, mode: string): string {
    const ordered = [...p.acts.flatMap(a => p.chapters.filter(c => c.actId === a.id)), ...p.chapters.filter(c => !c.actId)], c = p.chapters.find(c => c.id === chapterId);
    return '<div class="editor-layout"><nav class="chapter-nav" aria-label="Chapters">' + ordered.map(x => '<button data-action="chapter" data-id="' + x.id + '" class="' + (c?.id === x.id ? 'active' : '') + '">' + html(x.title) + '</button>').join("") + '</nav><div class="editor-content">' + (c ? '<div class="row"><h1 class="grow">' + html(c.title) + '</h1>' + action("details", "Details", "chapter", c.id) + '</div><div class="row editor-toolbar">' + ["edit", "read", "changes"].map(v => '<button data-action="editor-mode" data-mode="' + v + '" class="' + (mode === v ? 'active' : '') + '">' + v[0].toUpperCase() + v.slice(1) + '</button>').join("") + action("undo", "Undo") + action("redo", "Redo") + '<small id="word-count" class="grow">' + c.text.trim().split(/\s+/u).filter(Boolean).length + ' words</small></div><div class="format-placeholder">Formatting tools — planned for a later version. Manuscript is plain text.</div>' + (mode === "read" ? '<div class="reading" data-key="read-' + c.id + '">' + html(c.text) + '</div>' : mode === "changes" ? '<div class="empty">Compare and restore chapter changes through ' + action("history", "Changes / History") + '.</div>' : '<label class="sr-only" for="manuscript">Chapter manuscript</label><textarea id="manuscript" class="manuscript" spellcheck="true"' + targetAttribute({ kind: "chapter", id: c.id, field: "text" }) + '>' + html(c.text) + '</textarea>') : '<div class="empty">Add a chapter in Outline to begin writing.</div>') + '</div></div>';
}
const briefLabels: Record<string, string> = { bookKind: "Book kind", premise: "Premise", genre: "Genre", primaryThemes: "Primary themes", purpose: "Purpose", creativeConstraints: "Creative constraints", targetAudience: "Target audience", minimumReaderAge: "Minimum reader age", maximumReaderAge: "Maximum reader age", readingLevelGuidance: "Reading level guidance", targetWordCount: "Target word count", pointOfView: "Point of view", tense: "Tense", voiceAndTone: "Voice and tone", languageLocale: "Language and locale", houseStyle: "House style", readAloudPriority: "Read aloud priority", accessibilityGoals: "Accessibility goals", visualDirection: "Visual direction" };
export function brief(p: Project): string { return field({ kind: "project", field: "title" }, "Working title", p.title) + Object.entries(p.bookBrief).map(([name, value]) => name === "bookKind" ? field({ kind: "brief", field: name }, briefLabels[name], value, "select", ["Unspecified", "Novel", "Novella", "ShortStory", "StoryCollection", "NarrativeNonfiction", "GeneralNonfiction", "PictureBook", "IllustratedBook", "Poetry", "Other"]) : name === "readAloudPriority" ? '<label for="brief-read-aloud">Read aloud priority</label><select id="brief-read-aloud"' + targetAttribute({ kind: "brief", field: name }) + ' data-convert="boolean"><option value=""' + (value === null ? ' selected' : '') + '>Unspecified</option><option value="true"' + (value === true ? ' selected' : '') + '>Yes</option><option value="false"' + (value === false ? ' selected' : '') + '>No</option></select>' : field({ kind: "brief", field: name }, briefLabels[name] ?? name, value, ["minimumReaderAge", "maximumReaderAge", "targetWordCount"].includes(name) ? "number" : "textarea")).join(""); }
export function detailsFields(p: Project, t: Target): string {
    const all = (p as unknown as Record<string, Record<string, Json>[]>)[{ act: "acts", chapter: "chapters", beat: "beats", entity: "entities", fact: "facts", relationship: "relationships" }[t.kind] ?? ""], item = all?.find(x => x.id === t.id);
    if (!item)
        return t.kind === "brief" ? brief(p) : t.kind === "project" ? field({ kind: "project", field: "guidance" }, "Project Guidance", p.guidance, "textarea") : '<p class="muted">Select a chapter, beat or entity.</p>';
    const f = (name: string, label: string, type = "text", opts?: string[]) => field({ ...t, field: name }, label, item[name], type, opts);
    let out = f(item.name !== undefined ? "name" : "title", item.name !== undefined ? "Name" : "Title");
    if (t.kind === "act" || t.kind === "chapter")
        out += f("synopsis", "Synopsis", "textarea");
    if (t.kind === "beat" || t.kind === "entity")
        out += f("summary", "Summary", "textarea");
    if (t.kind === "entity")
        out += f("type", "Entity type", "select", p.entityTypes) + f("aliases", "Aliases (comma separated)", "aliases") + f("properties", "Properties (JSON object of text values)", "json");
    if (t.kind === "fact")
        out += f("key", "Stable fact key") + f("value", "Fact", "textarea");
    if (t.kind === "relationship")
        out = '<label for="relationship-from">From entity</label><select id="relationship-from"' + targetAttribute({ ...t, field: "fromId" }) + '>' + p.entities.map(e => '<option value="' + e.id + '"' + (e.id === item.fromId ? ' selected' : '') + '>' + html(e.name) + '</option>').join("") + '</select><label for="relationship-to">To entity</label><select id="relationship-to"' + targetAttribute({ ...t, field: "toId" }) + '>' + p.entities.map(e => '<option value="' + e.id + '"' + (e.id === item.toId ? ' selected' : '') + '>' + html(e.name) + '</option>').join("") + '</select>' + f("type", "Relationship type") + f("properties", "Properties (JSON object)", "json");
    if (["beat", "chapter", "fact"].includes(t.kind))
        out += '<h3>Linked entities</h3><div class="entity-list">' + p.entities.map(e => '<label class="check"><input type="checkbox" data-association="entityIds" data-kind="' + t.kind + '" data-id="' + t.id + '" value="' + e.id + '"' + ((item.entityIds as string[]).includes(e.id) ? ' checked' : '') + '>' + html(e.name) + '</label>').join("") + '</div>';
    if (t.kind === "entity")
        out += '<h3>Chapter links</h3><div class="entity-list">' + p.chapters.map(c => '<label class="check"><input type="checkbox" data-association="chapterIds" data-kind="entity" data-id="' + t.id + '" value="' + c.id + '"' + ((item.chapterIds as string[]).includes(c.id) ? ' checked' : '') + '>' + html(c.title) + '</label>').join("") + '</div><h3>Relationships</h3>' + p.relationships.filter(r => r.fromId === t.id || r.toId === t.id).map(r => action("details", html(r.type + " · " + p.entities.find(e => e.id === (r.fromId === t.id ? r.toId : r.fromId))?.name), "relationship", r.id) + action("delete", "×", "relationship", r.id)).join("") + action("relationship", "＋ Relationship", "entity", t.id);
    return out;
}
// Keyed DOM reconciliation keeps inputs, IME sessions, undo stacks and scroll containers alive.
export function morph(root: HTMLElement, markup: string, pending: Set<string> = new Set()): void {
    const template = document.createElement("template");
    template.innerHTML = markup;
    const identity = (n: Node): string => n instanceof HTMLElement ? n.dataset.key ?? n.id ?? "" : "";
    const updateValue = (old: HTMLInputElement | HTMLTextAreaElement, next: HTMLInputElement | HTMLTextAreaElement): void => {
        if (!old.dataset.target || old.getAttribute("aria-invalid") === "true" || old.dataset.composing === "true")
            return;
        const active = old === document.activeElement;
        if (active && old.dataset.convert && pending.has(keyOf(JSON.parse(old.dataset.target) as Target)))
            return;
        if (old.value === next.value)
            return;
        const previous = old.value, start = old.selectionStart, end = old.selectionEnd, direction = old.selectionDirection, scroll = old.scrollTop;
        let prefix = 0, suffix = 0;
        while (prefix < previous.length && prefix < next.value.length && previous[prefix] === next.value[prefix]) prefix++;
        while (suffix < previous.length - prefix && suffix < next.value.length - prefix && previous[previous.length - 1 - suffix] === next.value[next.value.length - 1 - suffix]) suffix++;
        old.value = next.value;
        if (active && start !== null && end !== null) {
            const position = (value: number): number => Math.max(0, Math.min(next.value.length, value <= prefix ? value : value >= previous.length - suffix ? value + next.value.length - previous.length : next.value.length - suffix));
            old.setSelectionRange(position(start), position(end), direction ?? undefined);
        }
        old.scrollTop = scroll;
    };
    const patch = (old: Node, next: Node): void => {
        if (old.nodeType !== next.nodeType || old.nodeName !== next.nodeName) {
            old.parentNode!.replaceChild(next.cloneNode(true), old);
            return;
        }
        if (old instanceof HTMLElement && next instanceof HTMLElement) {
            const active = old === document.activeElement;
            if (old instanceof HTMLTextAreaElement && next instanceof HTMLTextAreaElement) {
                updateValue(old, next);
                return;
            }
            if (old instanceof HTMLInputElement && next instanceof HTMLInputElement) {
                updateValue(old, next);
                if (old.type === "checkbox")
                    old.checked = next.checked;
            }
            if (old instanceof HTMLDetailsElement && next instanceof HTMLDetailsElement)
                next.open = old.open;
            for (const a of [...old.attributes])
                if (!next.hasAttribute(a.name) && a.name !== "data-composing")
                    old.removeAttribute(a.name);
            for (const a of [...next.attributes])
                if (old.getAttribute(a.name) !== a.value)
                    old.setAttribute(a.name, a.value);
            if (old.dataset.preserve === "true")
                return;
            children(old, next);
            if (old instanceof HTMLSelectElement && next instanceof HTMLSelectElement)
                old.value = next.value;
        }
        else if (old.textContent !== next.textContent)
            old.textContent = next.textContent;
    };
    const children = (old: Node, next: Node): void => {
        const available: Node[] = [...old.childNodes], keys = new Map(available.filter(identity).map(n => [identity(n), n])), used = new Set<Node>();
        [...next.childNodes].forEach((n, index) => {
            const key = identity(n);
            let existing: Node | undefined = key ? keys.get(key) : available[index];
            if (existing && used.has(existing))
                existing = undefined;
            if (existing && identity(existing) !== key)
                existing = undefined;
            if (!existing) {
                existing = n.cloneNode(true);
                old.insertBefore(existing, old.childNodes[index] ?? null);
            }
            else {
                if (old.childNodes[index] !== existing)
                    old.insertBefore(existing, old.childNodes[index] ?? null);
                patch(existing, n);
            }
            used.add(existing!);
        });
        for (const node of available)
            if (!used.has(node) && node.parentNode === old)
                old.removeChild(node);
    };
    children(root, template.content);
}
