import { createHash, randomUUID } from "node:crypto";
import { z } from "zod";
export const maximumProjectBytes = 32 * 1024 * 1024;
export class ProjectError extends Error {
    constructor(code, message, details) { super(message); this.name = "ProjectError"; this.code = code; this.details = details; }
}
export const hashText = value => createHash("sha256").update(value, "utf8").digest("hex");
export function canonical(value) {
    if (Array.isArray(value))
        return "[" + value.map(canonical).join(",") + "]";
    if (value && typeof value === "object")
        return "{" + Object.keys(value).sort().map(key => JSON.stringify(key) + ":" + canonical(value[key])).join(",") + "}";
    return JSON.stringify(value ?? null);
}
export const hashValue = value => hashText(canonical(value));
const id = z.string().uuid(), short = z.string().max(200), prose = z.string().max(1000000), summary = z.string().max(50000);
export const briefFields = ["bookKind", "premise", "genre", "primaryThemes", "purpose", "creativeConstraints", "targetAudience", "minimumReaderAge", "maximumReaderAge", "readingLevelGuidance", "targetWordCount", "pointOfView", "tense", "voiceAndTone", "languageLocale", "houseStyle", "readAloudPriority", "accessibilityGoals", "visualDirection"];
export const bookKinds = ["Unspecified", "Novel", "Novella", "ShortStory", "StoryCollection", "NarrativeNonfiction", "GeneralNonfiction", "PictureBook", "IllustratedBook", "Poetry", "Other"];
const briefShape = Object.fromEntries(briefFields.map(field => [field, summary]));
Object.assign(briefShape, { bookKind: z.enum(bookKinds), minimumReaderAge: z.number().int().min(0).max(150).nullable(), maximumReaderAge: z.number().int().min(0).max(150).nullable(), targetWordCount: z.number().int().positive().max(10000000).nullable(), readAloudPriority: z.boolean().nullable() });
export const emptyBrief = () => Object.fromEntries(briefFields.map(field => [field, field === "bookKind" ? "Unspecified" : ["minimumReaderAge", "maximumReaderAge", "targetWordCount", "readAloudPriority"].includes(field) ? null : ""]));
const ids = z.array(id).max(10000);
export const kinds = ["act", "chapter", "beat", "entity", "fact", "relationship"];
export const collections = { act: "acts", chapter: "chapters", beat: "beats", entity: "entities", fact: "facts", relationship: "relationships" };
export const editableFields = {
    project: ["title", "guidance"], brief: briefFields, act: ["title", "synopsis"], chapter: ["title", "synopsis", "text", "entityIds"], beat: ["title", "summary", "entityIds"], entity: ["name", "type", "summary", "aliases", "properties", "chapterIds"], fact: ["key", "name", "value", "entityIds"], relationship: ["fromId", "toId", "type", "properties"], preferences: ["includes", "excludes", "pins", "budget"]
};
const schemas = {
    act: z.object({ id, title: short.min(1), synopsis: summary }).strict(),
    chapter: z.object({ id, actId: id.nullable(), title: short.min(1), synopsis: summary, text: prose, entityIds: ids }).strict(),
    beat: z.object({ id, chapterId: id, title: short.min(1), summary, entityIds: ids }).strict(),
    entity: z.object({ id, type: short.min(1), name: short.min(1), summary, aliases: z.array(short).max(100), properties: z.record(short.min(1), summary), chapterIds: ids }).strict(),
    fact: z.object({ id, key: short.min(1), name: short.min(1), value: summary, entityIds: ids }).strict(),
    relationship: z.object({ id, fromId: id, toId: id, type: short.min(1), properties: z.record(short.min(1), summary) }).strict()
};
const preferenceSchema = z.object({ id, includes: z.array(z.string().max(250)).max(10000), excludes: z.array(z.string().max(250)).max(10000), pins: z.array(z.string().max(250)).max(10000).default([]), budget: z.number().int().min(1000).max(1000000) }).strict();
export const targetSchema = z.object({ kind: z.enum(["project", "brief", ...kinds, "preferences", "types", "list"]), id: id.optional(), field: z.string().max(100).optional() }).strict();
const hash = z.string().regex(/^[a-f0-9]{64}$/);
export const operationSchema = z.discriminatedUnion("op", [
    z.object({ op: z.literal("set"), target: targetSchema, expectedHash: hash, value: z.json() }).strict(),
    z.object({ op: z.literal("insert"), kind: z.enum(kinds), value: z.record(z.string(), z.json()), parentId: id.nullable().optional(), afterId: id.nullable().optional(), expectedListHash: hash }).strict(),
    z.object({ op: z.literal("remove"), kind: z.enum(kinds), id, expectedHash: hash, expectedListHash: hash, expectedDependentsHash: hash.optional() }).strict(),
    z.object({ op: z.literal("move"), kind: z.enum(["act", "chapter", "beat"]), id, parentId: id.nullable().optional(), afterId: id.nullable().optional(), expectedHash: hash, expectedSourceHash: hash, expectedDestinationHash: hash }).strict()
]);
const legacyDraft = z.object({ id, target: z.json(), before: prose, after: prose, reason: summary, status: z.string().max(30), baseRevision: z.number().int().positive(), createdAt: z.string(), decidedAt: z.string().optional() }).strict();
export const projectSchema = z.object({
    format: z.literal("lorekeeper-plugin-project"), schemaVersion: z.literal(2), id, revision: z.number().int().positive().max(Number.MAX_SAFE_INTEGER - 1), title: short.min(1), guidance: summary,
    bookBrief: z.object(briefShape).strict(), entityTypes: z.array(short.min(1)).min(2).max(200),
    acts: z.array(schemas.act).max(200), chapters: z.array(schemas.chapter).max(1000), beats: z.array(schemas.beat).max(10000), entities: z.array(schemas.entity).max(5000), facts: z.array(schemas.fact).max(5000), relationships: z.array(schemas.relationship).max(20000), contextPreferences: z.array(preferenceSchema).max(1001), legacyDrafts: z.array(legacyDraft).max(1000), createdAt: z.string().datetime(), updatedAt: z.string().datetime()
}).strict();
export function validateProject(value) {
    const parsed = projectSchema.safeParse(value);
    if (!parsed.success)
        throw new ProjectError("INVALID_PROJECT", "Invalid schema-v2 project. No content was replaced.");
    const p = parsed.data, all = [p.id, ...kinds.flatMap(kind => p[collections[kind]].map(item => item.id)), ...p.legacyDrafts.map(item => item.id)];
    if (new Set(all).size !== all.length)
        throw new ProjectError("INVALID_PROJECT", "Project identities must be unique.");
    const acts = new Set(p.acts.map(x => x.id)), chapters = new Set(p.chapters.map(x => x.id)), entities = new Set(p.entities.map(x => x.id));
    const unique = values => new Set(values).size === values.length;
    if (!unique(p.entityTypes) || !p.entityTypes.includes("Character") || !p.entityTypes.includes("Location"))
        throw new ProjectError("INVALID_PROJECT", "Entity types must be unique and include Character and Location.");
    if (p.chapters.some(x => x.actId && !acts.has(x.actId)) || p.beats.some(x => !chapters.has(x.chapterId)))
        throw new ProjectError("INVALID_PROJECT", "Outline parents must belong to this project.");
    if ([...p.chapters, ...p.beats, ...p.facts].some(x => !unique(x.entityIds) || x.entityIds.some(key => !entities.has(key))) || p.entities.some(x => !p.entityTypes.includes(x.type) || !unique(x.chapterIds) || x.chapterIds.some(key => !chapters.has(key))))
        throw new ProjectError("INVALID_PROJECT", "Entity associations or types are invalid.");
    if (p.relationships.some(x => !entities.has(x.fromId) || !entities.has(x.toId)))
        throw new ProjectError("INVALID_PROJECT", "Relationships require current entities.");
    if (!unique(p.contextPreferences.map(x => x.id)) || p.contextPreferences.some(x => (x.id !== p.id && !chapters.has(x.id)) || !unique(x.includes) || !unique(x.excludes) || !unique(x.pins) || [...x.includes, ...x.pins].some(key => x.excludes.includes(key))))
        throw new ProjectError("INVALID_PROJECT", "Context preferences are invalid.");
    if (p.bookBrief.minimumReaderAge != null && p.bookBrief.maximumReaderAge != null && p.bookBrief.minimumReaderAge > p.bookBrief.maximumReaderAge)
        throw new ProjectError("INVALID_PROJECT", "Minimum reader age exceeds maximum reader age.");
    if (!unique(p.facts.map(x => x.key)))
        throw new ProjectError("INVALID_PROJECT", "Project fact keys must be unique.");
    return p;
}
// Predecessors exist only at this explicit migration boundary.
export function upgradeProject(value) {
    if (value?.schemaVersion === 2)
        return validateProject(value);
    const old = z.object({ format: z.literal("lorekeeper-plugin-project"), schemaVersion: z.literal(1), id, revision: z.number().int().positive(), title: short.min(1), bookBrief: z.string().max(20000), chapters: z.array(z.object({ id, title: short.min(1), synopsis: z.string().max(10000), text: z.string().max(100000) }).strict()).max(100), canon: z.array(z.object({ id, name: short.min(1), kind: z.enum(["character", "place", "fact"]), text: z.string().max(10000), chapterIds: z.array(id).max(100) }).strict()).max(300), proposals: z.array(legacyDraft).max(1000), createdAt: z.string().datetime(), updatedAt: z.string().datetime() }).strict().safeParse(value);
    if (!old.success)
        throw new ProjectError("INVALID_PROJECT", "Unsupported project. Original bytes were preserved.");
    const { canon, proposals, ...p } = old.data, brief = emptyBrief();
    brief.premise = p.bookBrief;
    if (proposals.some(x => x.baseRevision > p.revision))
        throw new ProjectError("INVALID_PROJECT", "Invalid historical revision.");
    return validateProject({ ...p, schemaVersion: 2, guidance: "", bookBrief: brief, entityTypes: ["Character", "Location", "Fact"], acts: [], chapters: p.chapters.map(x => ({ ...x, actId: null, entityIds: [] })), beats: [], entities: canon.map(x => ({ id: x.id, name: x.name, type: x.kind === "character" ? "Character" : x.kind === "place" ? "Location" : "Fact", summary: x.text, aliases: [], properties: {}, chapterIds: x.chapterIds })), facts: [], relationships: [], contextPreferences: [], legacyDrafts: proposals });
}
export function encodeProject(p) {
    const text = JSON.stringify(validateProject(p), null, 2) + "\n";
    if (Buffer.byteLength(text) > maximumProjectBytes)
        throw new ProjectError("PROJECT_TOO_LARGE", "Project exceeds 32 MiB. Saved content is unchanged.");
    return text;
}
export function createProject(title, sample = false) {
    const now = new Date().toISOString(), p = { format: "lorekeeper-plugin-project", schemaVersion: 2, id: randomUUID(), revision: 1, title, guidance: "", bookBrief: emptyBrief(), entityTypes: ["Character", "Location"], acts: [], chapters: [], beats: [], entities: [], facts: [], relationships: [], contextPreferences: [], legacyDrafts: [], createdAt: now, updatedAt: now };
    const chapterId = randomUUID();
    p.chapters.push({ id: chapterId, actId: null, title: "Chapter 1", synopsis: "", text: "", entityIds: [] });
    if (sample) {
        const actId = randomUUID(), entityId = randomUUID();
        p.bookBrief.premise = "A quiet fantasy about a keeper who preserves the memories of a coastal town.";
        p.bookBrief.bookKind = "Novel";
        p.acts.push({ id: actId, title: "Act I — The Light", synopsis: "A memory returns from the sea." });
        Object.assign(p.chapters[0], { actId, title: "The Lantern Archive", synopsis: "Mara discovers an impossible message.", text: "The café’s keeper wrote: “こんにちは — hello, 🌿.”\n\nMara set the lantern beside the window. Across the harbor, the tide carried a light that belonged to no boat.", entityIds: [entityId] });
        p.entities.push({ id: entityId, name: "Mara", type: "Character", summary: "Mara keeps the archive. She is observant and careful with others’ memories.", aliases: [], properties: { role: "Protagonist" }, chapterIds: [chapterId] });
        p.beats.push({ id: randomUUID(), chapterId, title: "An unexpected message", summary: "A lantern speaks after its owner has forgotten it.", entityIds: [entityId] });
    }
    return validateProject(p);
}
export const orderedChapters = p => [...p.acts.flatMap(act => p.chapters.filter(c => c.actId === act.id)), ...p.chapters.filter(c => c.actId === null)];
export const listTarget = (kind, parentId) => ({ kind: "list", field: kind, ...(parentId ? { id: parentId } : {}) });
export function listItems(p, kind, parentId = null) {
    const items = p[collections[kind]];
    if (!items)
        throw new ProjectError("INVALID_TARGET", "Unknown collection.");
    return items.filter(x => kind === "chapter" ? x.actId === parentId : kind === "beat" ? x.chapterId === parentId : true);
}
export function targetValue(p, target) {
    let value;
    if (target.kind === "list")
        return listItems(p, target.field, target.id ?? null).map(x => x.id);
    if (target.kind === "project")
        value = p;
    else if (target.kind === "brief")
        value = p.bookBrief;
    else if (target.kind === "types")
        return p.entityTypes;
    else if (target.kind === "preferences")
        value = p.contextPreferences.find(x => x.id === target.id) ?? (target.field ? { id: target.id ?? p.id, includes: [], excludes: [], pins: [], budget: 32000 } : null);
    else
        value = p[collections[target.kind]]?.find(x => x.id === target.id) ?? null;
    if (target.field) {
        if (!value || !(target.field in value))
            return null;
        return value[target.field];
    }
    if (target.kind === "project")
        throw new ProjectError("INVALID_TARGET", "Read project fields individually.");
    return value;
}
export function readTarget(p, target) { const value = targetValue(p, target); return typeof value === "string" ? value : canonical(value); }
export function dependents(p, kind, id) {
    if (kind === "act")
        return p.chapters.filter(x => x.actId === id).map(x => x.id);
    if (kind === "chapter")
        return { beats: p.beats.filter(x => x.chapterId === id), entityLinks: p.entities.filter(x => x.chapterIds.includes(id)).map(x => x.id), preferences: p.contextPreferences.filter(x => x.id === id) };
    if (kind === "entity")
        return { chapters: p.chapters.filter(x => x.entityIds.includes(id)).map(x => x.id), beats: p.beats.filter(x => x.entityIds.includes(id)).map(x => x.id), facts: p.facts.filter(x => x.entityIds.includes(id)).map(x => x.id), relationships: p.relationships.filter(x => x.fromId === id || x.toId === id) };
    return null;
}
function guard(value, expectedHash, target) {
    if (hashValue(value) !== expectedHash)
        throw new ProjectError("CONFLICT", "This item changed. Read it again; both variants are preserved.", { target, current: value });
}
export function defaultItem(kind, value, parentId) {
    const defaults = { act: { title: "New act", synopsis: "" }, chapter: { actId: parentId ?? null, title: "New chapter", synopsis: "", text: "", entityIds: [] }, beat: { chapterId: parentId, title: "New beat", summary: "", entityIds: [] }, entity: { type: "Character", name: "New entity", summary: "", aliases: [], properties: {}, chapterIds: [] }, fact: { key: "fact." + randomUUID().slice(0, 8), name: "New fact", value: "", entityIds: [] }, relationship: { type: "RelatedTo", properties: {} } };
    const parsed = schemas[kind].safeParse({ ...defaults[kind], ...value });
    if (!parsed.success) throw new ProjectError("INVALID_OPERATION", "The inserted item does not match its declared fields.");
    return parsed.data;
}
function place(p, kind, item, parentId, afterId) {
    const collection = p[collections[kind]], others = listItems(p, kind, parentId).filter(x => x.id !== item.id);
    if (afterId && !others.some(x => x.id === afterId))
        throw new ProjectError("INVALID_DESTINATION", "The insertion anchor is not in this parent.");
    const existing = collection.findIndex(x => x.id === item.id);
    if (existing >= 0)
        collection.splice(existing, 1);
    if (kind === "chapter")
        item.actId = parentId ?? null;
    if (kind === "beat")
        item.chapterId = parentId;
    const index = afterId ? collection.findIndex(x => x.id === afterId) + 1 : others.length ? collection.findIndex(x => x.id === others[0].id) : collection.length;
    collection.splice(index, 0, item);
}
export function applyOperations(project, operations, surface) {
    const p = structuredClone(project);
    for (const op of operations) {
        if (surface === "outline" && op.op === "set" && op.target.kind === "chapter" && op.target.field === "text")
            throw new ProjectError("SURFACE_BOUNDARY", "Outline cannot change chapter prose. Use Editor.");
        if (op.op === "set") {
            const t = op.target;
            if (t.kind === "types") {
                guard(p.entityTypes, op.expectedHash, t);
                p.entityTypes = op.value;
                continue;
            }
            if (!(editableFields[t.kind] ?? []).includes(t.field))
                throw new ProjectError("INVALID_OPERATION", "Set a declared editable field; identities and parents require structural commands.");
            guard(targetValue(p, t), op.expectedHash, t);
            const owner = t.kind === "project" ? p : t.kind === "brief" ? p.bookBrief : t.kind === "preferences" ? p.contextPreferences.find(x => x.id === t.id) : p[collections[t.kind]]?.find(x => x.id === t.id);
            if (!owner && t.kind === "preferences") {
                const item = { id: t.id ?? p.id, includes: [], excludes: [], pins: [], budget: 32000 };
                item[t.field] = op.value;
                p.contextPreferences.push(item);
            }
            else if (!owner)
                throw new ProjectError("TARGET_NOT_FOUND", "The item no longer exists.");
            else
                owner[t.field] = op.value;
        }
        else if (op.op === "insert") {
            if (!id.safeParse(op.value.id).success)
                throw new ProjectError("INVALID_OPERATION", "Insert requires a stable UUID in value.id.");
            if ([p.id, ...kinds.flatMap(kind => p[collections[kind]].map(x => x.id)), ...p.legacyDrafts.map(x => x.id)].includes(op.value.id))
                throw new ProjectError("IDENTITY_EXISTS", "Insert cannot replace an existing identity.");
            if (surface === "outline" && op.kind === "chapter" && op.value.text)
                throw new ProjectError("SURFACE_BOUNDARY", "Outline creates empty chapters; drafting belongs to Editor.");
            guard(targetValue(p, listTarget(op.kind, op.parentId)), op.expectedListHash, listTarget(op.kind, op.parentId));
            const item = defaultItem(op.kind, op.value, op.parentId);
            place(p, op.kind, item, op.parentId ?? null, op.afterId ?? null);
        }
        else {
            const item = p[collections[op.kind]].find(x => x.id === op.id), target = { kind: op.kind, id: op.id };
            if (!item)
                throw new ProjectError("TARGET_NOT_FOUND", "The item no longer exists.");
            guard(item, op.expectedHash, target);
            const parentId = op.kind === "chapter" ? item.actId : op.kind === "beat" ? item.chapterId : null;
            if (op.op === "move") {
                guard(targetValue(p, listTarget(op.kind, parentId)), op.expectedSourceHash, listTarget(op.kind, parentId));
                guard(targetValue(p, listTarget(op.kind, op.parentId)), op.expectedDestinationHash, listTarget(op.kind, op.parentId));
                place(p, op.kind, item, op.parentId ?? null, op.afterId ?? null);
            }
            else {
                const dependencies = dependents(p, op.kind, op.id);
                if (dependencies !== null)
                    guard(dependencies, op.expectedDependentsHash, { ...target, field: "dependents" });
                guard(targetValue(p, listTarget(op.kind, parentId)), op.expectedListHash, listTarget(op.kind, parentId));
                p[collections[op.kind]] = p[collections[op.kind]].filter(x => x.id !== op.id);
                if (op.kind === "act")
                    p.chapters.forEach(x => { if (x.actId === op.id)
                        x.actId = null; });
                if (op.kind === "chapter") {
                    p.beats = p.beats.filter(x => x.chapterId !== op.id);
                    p.entities.forEach(x => { x.chapterIds = x.chapterIds.filter(key => key !== op.id); });
                    p.contextPreferences = p.contextPreferences.filter(x => x.id !== op.id);
                }
                if (op.kind === "entity") {
                    [...p.chapters, ...p.beats, ...p.facts].forEach(x => { x.entityIds = x.entityIds.filter(key => key !== op.id); });
                    p.relationships = p.relationships.filter(x => x.fromId !== op.id && x.toId !== op.id);
                }
            }
        }
    }
    return validateProject(p);
}
export function changesBetween(before, after) {
    const changes = [], add = (target, a, b) => {
        if (canonical(a) === canonical(b))
            return;
        const change = { target, before: a, after: b, beforeHash: hashValue(a), afterHash: hashValue(b) };
        if (typeof a === "string" && typeof b === "string" && Math.max(a.length, b.length) > 2000) {
            let start = 0, tail = 0;
            while (start < Math.min(a.length, b.length) && a[start] === b[start])
                start++;
            if (start > 0 && (/[\uDC00-\uDFFF]/.test(a[start] ?? "") || /[\uDC00-\uDFFF]/.test(b[start] ?? "")))
                start--;
            while (tail < Math.min(a.length, b.length) - start && a[a.length - tail - 1] === b[b.length - tail - 1])
                tail++;
            if (/[\uDC00-\uDFFF]/.test(a[a.length - tail] ?? "") || /[\uDC00-\uDFFF]/.test(b[b.length - tail] ?? ""))
                tail--;
            change.rangeStart = start;
            change.before = a.slice(start, a.length - tail);
            change.after = b.slice(start, b.length - tail);
        }
        changes.push(change);
    };
    for (const field of ["title", "guidance"])
        add({ kind: "project", field }, before[field], after[field]);
    for (const field of briefFields)
        add({ kind: "brief", field }, before.bookBrief[field], after.bookBrief[field]);
    add({ kind: "types" }, before.entityTypes, after.entityTypes);
    for (const kind of [...kinds, "preferences"]) {
        const collection = kind === "preferences" ? "contextPreferences" : collections[kind], a = new Map(before[collection].map(x => [x.id, x])), b = new Map(after[collection].map(x => [x.id, x]));
        for (const key of new Set([...a.keys(), ...b.keys()])) {
            if (!a.has(key) || !b.has(key))
                add({ kind, id: key }, a.get(key) ?? null, b.get(key) ?? null);
            else
                for (const field of Object.keys(a.get(key)).filter(field => field !== "id"))
                    add({ kind, id: key, field }, a.get(key)[field], b.get(key)[field]);
        }
    }
    for (const kind of ["act", "chapter", "beat"]) {
        const parents = kind === "act" ? [null] : kind === "chapter" ? [null, ...new Set([...before.acts, ...after.acts].map(x => x.id))] : [...new Set([...before.chapters, ...after.chapters].map(x => x.id))];
        for (const parent of parents)
            add(listTarget(kind, parent), listItems(before, kind, parent).map(x => x.id), listItems(after, kind, parent).map(x => x.id));
    }
    return changes;
}
export function restoreChanges(project, changes) {
    for (const change of changes)
        guard(targetValue(project, change.target), change.afterHash, change.target);
    const p = structuredClone(project);
    for (const change of changes.filter(x => x.target.kind !== "list")) {
        const t = change.target, value = targetValue(p, t), before = change.rangeStart == null ? change.before : value.slice(0, change.rangeStart) + change.before + value.slice(change.rangeStart + change.after.length);
        if (t.kind === "types")
            p.entityTypes = before;
        else if (t.kind === "project" || t.kind === "brief")
            (t.kind === "project" ? p : p.bookBrief)[t.field] = before;
        else {
            const collection = t.kind === "preferences" ? "contextPreferences" : collections[t.kind], index = p[collection].findIndex(x => x.id === t.id);
            if (t.field) {
                if (index < 0)
                    throw new ProjectError("CONFLICT", "A changed item was removed.");
                p[collection][index][t.field] = before;
            }
            else {
                if (index >= 0)
                    p[collection].splice(index, 1);
                if (before)
                    p[collection].push(before);
            }
        }
    }
    for (const change of changes.filter(x => x.target.kind === "list")) {
        const t = change.target, bucket = new Set(change.before), items = p[collections[t.field]], positions = items.map((x, i) => bucket.has(x.id) ? i : -1).filter(i => i >= 0), ordered = change.before.map(key => items.find(x => x.id === key));
        if (ordered.some(x => !x))
            throw new ProjectError("CONFLICT", "Restoration has missing outline items.");
        positions.forEach((index, i) => { items[index] = ordered[i]; });
    }
    return validateProject(p);
}
export function projectSummary(p) { return { id: p.id, title: p.title, revision: p.revision, updatedAt: p.updatedAt, actCount: p.acts.length, chapterCount: p.chapters.length, beatCount: p.beats.length, entityCount: p.entities.length }; }
