import { randomUUID } from "node:crypto";
import { constants } from "node:fs";
import { lstat, mkdir, open, readdir, realpath, rename, unlink } from "node:fs/promises";
import { join } from "node:path";
import { z } from "zod";
import { LocalDocumentStore } from "./store.mjs";
import { applyOperations, canonical, changesBetween, encodeProject, hashText, ProjectError, restoreChanges, targetSchema, upgradeProject, operationSchema } from "./project.mjs";
const uuid = z.string().uuid();
const changeSchema = z.object({ target: targetSchema, before: z.json(), after: z.json(), beforeHash: z.string().regex(/^[a-f0-9]{64}$/), afterHash: z.string().regex(/^[a-f0-9]{64}$/), rangeStart: z.number().int().min(0).max(1000000).optional() }).strict();
const recordSchema = z.object({ id: uuid, groupId: uuid, projectId: uuid, requestHash: z.string().regex(/^[a-f0-9]{64}$/), source: z.enum(["manual", "ai", "rollback", "migration"]), label: z.string().max(200), createdAt: z.string().datetime(), revision: z.number().int().positive(), changes: z.array(changeSchema).max(60000) }).strict();
async function safeDirectory(path) {
    await mkdir(path, { recursive: true });
    const stat = await lstat(path);
    if (!stat.isDirectory() || stat.isSymbolicLink() || await realpath(path) !== path)
        throw new ProjectError("UNSAFE_FOLDER", "A local content directory is linked or outside its owner.");
    return path;
}
async function readBytes(path, maximum = 64 * 1024 * 1024) {
    const stat = await lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1 || stat.size > maximum)
        throw new ProjectError("UNSAFE_FILE", "Local recovery/history file is unsafe.");
    const file = await open(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    try {
        const opened = await file.stat();
        if (!opened.isFile() || opened.nlink !== 1 || opened.size > maximum)
            throw new ProjectError("UNSAFE_FILE", "Local file changed before reading.");
        const bytes = await file.readFile();
        if (bytes.length > maximum)
            throw new ProjectError("UNSAFE_FILE", "Local file exceeded its bound.");
        return bytes;
    }
    finally {
        await file.close();
    }
}
async function json(path, maximum) { return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(await readBytes(path, maximum))); }
async function exists(path) { try {
    await lstat(path);
    return true;
}
catch (error) {
    if (error.code === "ENOENT")
        return false;
    throw error;
} }
async function exclusive(path, bytes) {
    const file = await open(path, "wx", 0o600);
    try {
        await file.writeFile(bytes);
        await file.sync();
    }
    finally {
        await file.close();
    }
}
export class WorkspaceService {
    constructor(store) { this.store = store; this.groups = new Map(); this.historySignatures = new Map(); this.version = 0; this.draftStores = new Map(); }
    async historyRoot(root, fileName) {
        const history = await safeDirectory(join(root, ".history"));
        return safeDirectory(join(history, this.store.filename(fileName)));
    }
    async recover(root, fileName) {
        const pendingPath = join(root, "." + fileName + ".transaction.json");
        if (!await exists(pendingPath))
            return;
        const pending = z.object({ id: uuid, beforeHash: z.string().regex(/^[a-f0-9]{64}$/), afterHash: z.string().regex(/^[a-f0-9]{64}$/), temp: z.string(), recordHash: z.string().regex(/^[a-f0-9]{64}$/) }).strict().parse(await json(pendingPath, 4096));
        if (pending.temp !== "." + fileName + "." + pending.id + ".tmp")
            throw new ProjectError("RECOVERY_REQUIRED", "Recovery names do not match their owning project.");
        const history = await this.historyRoot(root, fileName), recordPath = join(history, pending.id + ".json");
        if (hashText(await readBytes(recordPath)) !== pending.recordHash)
            throw new ProjectError("RECOVERY_REQUIRED", "The prepared change record is damaged. All variants were retained.");
        const record = recordSchema.parse(await json(recordPath)), current = await this.store.readAt(root, fileName);
        if (record.projectId !== current.project.id)
            throw new ProjectError("RECOVERY_REQUIRED", "Recovery belongs to another project identity.");
        if (current.etag === pending.beforeHash) {
            const bytes = await readBytes(join(root, pending.temp), this.store.maximumBytes);
            if (hashText(bytes) !== pending.afterHash)
                throw new ProjectError("RECOVERY_REQUIRED", "Prepared content hash differs. Files were preserved.");
            const next = upgradeProject(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)));
            if (next.id !== current.project.id || next.revision !== record.revision)
                throw new ProjectError("RECOVERY_REQUIRED", "Prepared content identity or revision differs.");
            await rename(join(root, pending.temp), join(root, fileName));
        }
        else if (current.etag !== pending.afterHash)
            throw new ProjectError("RECOVERY_REQUIRED", "Saved content diverged from the interrupted transaction. Preserve both variants and resolve recovery.");
        const committed = join(history, pending.id + ".commit");
        if (!await exists(committed))
            await exclusive(committed, JSON.stringify({ hash: pending.afterHash }) + "\n");
        else if ((await json(committed, 1024)).hash !== pending.afterHash)
            throw new ProjectError("RECOVERY_REQUIRED", "Commit evidence differs.");
        await unlink(pendingPath);
        this.store.cache.delete(fileName);
        this.version++;
    }
    async read(fileName) {
        const initial = await this.store.read(fileName);
        const root = await this.store.directory();
        if (initial.sourceVersion === 2 && !await exists(join(root, "." + fileName + ".transaction.json")))
            return initial;
        return this.store.withLock(root, fileName, async () => {
            await this.recover(root, fileName);
            const current = await this.store.readAt(root, fileName), value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(current.bytes));
            if (value.schemaVersion === 2)
                return current;
            const history = await this.historyRoot(root, fileName), backup = join(history, "pre-v2-" + current.etag + ".json");
            if (!await exists(backup))
                await exclusive(backup, current.bytes);
            else if (hashText(await readBytes(backup)) !== current.etag)
                throw new ProjectError("RECOVERY_REQUIRED", "Migration backup differs.");
            return this.commit(root, fileName, current, current.project, { id: randomUUID(), groupId: randomUUID(), requestHash: hashText(current.bytes), source: "migration", label: "Upgrade project to schema v2" }, []);
        });
    }
    async commit(root, fileName, current, next, meta, changes) {
        next.revision = current.project.revision + 1;
        next.updatedAt = new Date().toISOString();
        const content = encodeProject(next), afterHash = hashText(content), history = await this.historyRoot(root, fileName);
        const record = recordSchema.parse({ ...meta, projectId: next.id, revision: next.revision, createdAt: new Date().toISOString(), changes });
        const recordPath = join(history, record.id + ".json"), bytes = JSON.stringify(record) + "\n", tempName = "." + fileName + "." + record.id + ".tmp";
        // Compact preimages are durable before replacement; a single prepared transaction owns recovery.
        await exclusive(join(root, tempName), content);
        await exclusive(recordPath, bytes);
        await exclusive(join(root, "." + fileName + ".transaction.json"), JSON.stringify({ id: record.id, beforeHash: current.etag, afterHash, temp: tempName, recordHash: hashText(bytes) }) + "\n");
        if ((await this.store.readAt(root, fileName)).etag !== current.etag)
            throw new ProjectError("RECOVERY_REQUIRED", "An external writer changed the project during commit. Both variants are retained.");
        await rename(join(root, tempName), join(root, fileName));
        await exclusive(join(history, record.id + ".commit"), JSON.stringify({ hash: afterHash }) + "\n");
        await unlink(join(root, "." + fileName + ".transaction.json"));
        this.store.cache.delete(fileName);
        this.groups.delete(fileName);
        this.version++;
        return { fileName, project: next, etag: afterHash, baseEtag: current.etag, bytes: Buffer.from(content), receipt: record };
    }
    async command(args, source = "manual") {
        await this.read(args.fileName);
        const root = await this.store.directory(), requestHash = hashText(canonical({ operations: args.operations, expectedRevision: args.expectedRevision, surface: args.surface, groupId: args.groupId, label: args.label }));
        return this.store.withLock(root, args.fileName, async () => {
            await this.recover(root, args.fileName);
            const current = await this.store.readAt(root, args.fileName), history = await this.historyRoot(root, args.fileName), path = join(history, args.requestId + ".json");
            if (await exists(path)) {
                const prior = recordSchema.parse(await json(path));
                if (prior.projectId !== current.project.id || prior.requestHash !== requestHash || prior.source !== source)
                    throw new ProjectError("REQUEST_ID_REUSED", "This request identity belongs to different input.");
                if (!await exists(join(history, prior.id + ".commit")))
                    throw new ProjectError("RECOVERY_REQUIRED", "This request was prepared without a confirmed outcome. It was not resent.");
                return { ...current, receipt: prior, replayed: true };
            }
            if (args.expectedRevision > current.project.revision)
                throw new ProjectError("CONFLICT", "Expected revision is newer than this project.");
            // Exact per-target guards allow changes elsewhere without replacing newer content.
            const next = applyOperations(current.project, args.operations, args.surface), changes = changesBetween(current.project, next);
            if (!changes.length) {
                const receipt = recordSchema.parse({ id: args.requestId, groupId: args.groupId ?? args.requestId, requestHash, source, label: args.label ?? "No change", projectId: current.project.id, revision: current.project.revision, createdAt: new Date().toISOString(), changes: [] });
                await exclusive(path, JSON.stringify(receipt) + "\n");
                await exclusive(join(history, receipt.id + ".commit"), JSON.stringify({ hash: current.etag }) + "\n");
                this.groups.delete(args.fileName);
                return { ...current, receipt, noop: true };
            }
            return this.commit(root, args.fileName, current, next, { id: args.requestId, groupId: args.groupId ?? args.requestId, requestHash, source, label: args.label ?? "Edit project" }, changes);
        });
    }
    async history(fileName, offset = 0, limit = 20) {
        const state = await this.read(fileName), root = await this.historyRoot(await this.store.directory(), fileName);
        const entries = (await readdir(root)).filter(x => /^[a-f0-9-]{36}\.commit$/.test(x)).map(x => x.slice(0, -7));
        const signature = hashText(entries.sort().join("\n"));
        if (this.groups.has(fileName) && this.historySignatures.get(fileName) === signature) {
            const list = [...this.groups.get(fileName).values()].reverse();
            return { groups: list.slice(offset, offset + limit).map(({ records, ...group }) => ({ ...group, changeCount: records.reduce((n, x) => n + x.changes.length, 0) })), nextOffset: offset + limit < list.length ? offset + limit : null, total: list.length };
        }
        const records = [];
        for (const key of entries) {
            const record = recordSchema.parse(await json(join(root, key + ".json")));
            if (record.projectId !== state.project.id)
                throw new ProjectError("INVALID_HISTORY", "History belongs to a different project.");
            records.push(record);
        }
        records.sort((a, b) => a.revision - b.revision);
        const groups = new Map();
        for (const record of records) {
            const group = groups.get(record.groupId) ?? { id: record.groupId, label: record.label, source: record.source, revision: record.revision, createdAt: record.createdAt, records: [] };
            group.revision = record.revision;
            group.records.push(record);
            groups.set(record.groupId, group);
        }
        this.groups.set(fileName, groups);
        this.historySignatures.set(fileName, signature);
        while (this.groups.size > 3) {
            const oldest = this.groups.keys().next().value;
            this.groups.delete(oldest);
            this.historySignatures.delete(oldest);
        }
        const list = [...groups.values()].reverse();
        return { groups: list.slice(offset, offset + limit).map(({ records, ...group }) => ({ ...group, changeCount: records.reduce((n, x) => n + x.changes.length, 0) })), nextOffset: offset + limit < list.length ? offset + limit : null, total: list.length };
    }
    async group(fileName, groupId) {
        // Refresh evidence from participating writers before selecting a group.
        await this.history(fileName, 0, 1);
        const group = this.groups.get(fileName).get(groupId);
        if (!group)
            throw new ProjectError("HISTORY_NOT_FOUND", "Choose a saved change group.");
        return group;
    }
    async rollback({ fileName, groupId, requestId }) {
        const group = await this.group(fileName, groupId), root = await this.store.directory();
        return this.store.withLock(root, fileName, async () => {
            await this.recover(root, fileName);
            const current = await this.store.readAt(root, fileName), history = await this.historyRoot(root, fileName), requestHash = hashText(groupId);
            if (await exists(join(history, requestId + ".json"))) {
                const record = recordSchema.parse(await json(join(history, requestId + ".json")));
                if (record.requestHash !== requestHash || record.source !== "rollback" || !await exists(join(history, requestId + ".commit")))
                    throw new ProjectError("REQUEST_ID_REUSED", "Rollback request identity differs or has an uncertain outcome.");
                return { ...current, receipt: record, replayed: true };
            }
            let next = structuredClone(current.project);
            for (const record of [...group.records].reverse())
                next = restoreChanges(next, record.changes);
            return this.commit(root, fileName, current, next, { id: requestId, groupId: requestId, requestHash, source: "rollback", label: "Roll back: " + group.label.slice(0, 170) }, changesBetween(current.project, next));
        });
    }
    async draft({ fileName, sessionId, sequence, operations, request, clear = false }) {
        uuid.parse(sessionId);
        await this.store.read(fileName);
        const root = await safeDirectory(join(await this.historyRoot(await this.store.directory(), fileName), "drafts"));
        let drafts = this.draftStores.get(root);
        if (!drafts) {
            drafts = new LocalDocumentStore({ root, maximumBytes: 32 * 1024 * 1024, backups: false, filename: value => { if (!/^[a-f0-9-]{36}\.json$/.test(value)) throw new ProjectError("INVALID_FILENAME", "Invalid draft identity."); return value; }, validate: value => z.object({ revision: z.number().int().positive().default(1), sessionId: uuid, sequence: z.number().int().nonnegative(), updatedAt: z.string().datetime(), operations: z.array(operationSchema).max(100), request: z.record(z.string(), z.json()).optional() }).strict().parse(value), encode: value => JSON.stringify(value) + "\n" });
            this.draftStores.set(root, drafts);
        }
        const value = { revision: 1, sessionId, sequence, updatedAt: new Date().toISOString(), operations: clear ? [] : operations, ...(request && !clear ? { request } : {}) }, filename = sessionId + ".json";
        let saved;
        try { saved = await drafts.update(filename, undefined, prior => prior.sequence > sequence ? prior : value); }
        catch (error) { if (error.code !== "ENOENT") throw error; saved = await drafts.create(filename, value); }
        return { saved: saved.project.sequence === sequence, sequence: saved.project.sequence };
    }
    async drafts(fileName) {
        const root = await this.historyRoot(await this.store.directory(), fileName), path = join(root, "drafts");
        if (!await exists(path))
            return [];
        if (await realpath(path) !== path || (await lstat(path)).isSymbolicLink())
            throw new ProjectError("UNSAFE_FOLDER", "Draft recovery folder is linked.");
        const result = [];
        for (const name of (await readdir(path)).filter(x => /^[a-f0-9-]{36}\.json$/.test(x))) {
            const draft = await json(join(path, name), 32 * 1024 * 1024);
            if (Array.isArray(draft.operations) && draft.operations.length)
                result.push(draft);
        }
        return result;
    }
}
