import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerAppResource, registerAppTool, RESOURCE_MIME_TYPE } from "@modelcontextprotocol/ext-apps/server";
import { OpenAIExtensions } from "@openai/mcp-extensions/server";
import { z } from "zod";
import { createProject, hashValue, listTarget, operationSchema, ProjectError, projectSummary, readTarget, targetSchema, targetValue, changesBetween, dependents } from "./project.mjs";
import { retrieveContext, searchProject } from "./context.mjs";
import { LocalProjectStore, LocalDocumentStore } from "./store.mjs";
import { WorkspaceService } from "./workspace-service.mjs";
import { LorekeeperChat } from "./chat.mjs";
import manifest from "../plugin.json" with { type: "json" };

const resourceUri = "ui://lorekeeper/storage-probe.html";
const projectId = "85d88cf9-79a9-4cb6-8fba-a541c707833b";
const chapterId = "291527bb-6c30-40cb-9dca-5cff8c6950fa";
const maximumBytes = 65536;
const projectSchema = z.object({
  schemaVersion: z.literal(1), projectId: z.literal(projectId), revision: z.number().int().min(1).max(1000), title: z.literal("Synthetic Storage Probe"),
  chapters: z.array(z.object({ id: z.literal(chapterId), title: z.string().min(1).max(200), paragraphs: z.array(z.object({ id: z.string().uuid(), text: z.string().max(10000) }).strict()).min(1).max(8) }).strict()).length(1)
}).strict();
const server = new McpServer({ name: "lorekeeper-storage-probe", version: manifest.version });
new OpenAIExtensions(server);
const store = new LocalProjectStore(), workspace = new WorkspaceService(store), workspaceUri = "ui://lorekeeper/workspace.html";
const fileNameSchema = z.string().min(1).max(120), etagSchema = z.string().regex(/^[a-f0-9]{64}$/), uuid = z.string().uuid();
const narrativeTools = new Map(), chat = new LorekeeperChat(workspace, narrativeTools);
const knownStates = new Map();
function remember(state) {
  knownStates.set(state.etag, state);
  while (knownStates.size > 12) knownStates.delete(knownStates.keys().next().value);
  return state;
}
function snapshot(state) { remember(state); return { fileName: state.fileName, project: state.project, etag: state.etag }; }
function jsonResult(data) { return { content: [{ type: "text", text: JSON.stringify(data) }], structuredContent: data }; }
function appResult(data) { return { content: [], structuredContent: data }; }
function mutationData(state) {
  remember(state);
  return { fileName: state.fileName, etag: state.etag, baseEtag: state.baseEtag, revision: state.project.revision, updatedAt: state.project.updatedAt, changes: state.receipt?.changes ?? [], groupId: state.receipt?.groupId, receipt: state.receipt ? { id: state.receipt.id, groupId: state.receipt.groupId, revision: state.receipt.revision, createdAt: state.receipt.createdAt, source: state.receipt.source, label: state.receipt.label } : null, replayed: state.replayed };
}
function workspaceTool(name, config, handler, appOnly = false, dynamic = false) {
  if (dynamic) {
    const { fileName: _fileName, ...fields } = config.inputSchema, validate = z.object(fields).strict();
    narrativeTools.set(name, { name, description: config.description, validate, schema: z.toJSONSchema(validate), handler });
  }
  server.registerTool(name, { ...config, annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false, ...config.annotations }, _meta: { ...config._meta, ui: { visibility: appOnly ? ["app"] : ["model", "app"], ...config._meta?.ui } } }, async args => {
    try { return await handler(args); }
    catch (error) {
      const code = error instanceof ProjectError ? error.code : error.code === "ENOENT" ? "FILE_NOT_FOUND" : "LOCAL_STORAGE_ERROR";
      const message = error instanceof ProjectError ? error.message : code === "FILE_NOT_FOUND" ? "This file is no longer in the local folder." : "Local operation failed. Your draft was retained; inspect recovery before retrying an uncertain write.";
      return { isError: true, content: [{ type: "text", text: code + ": " + message }], structuredContent: { code, message, ...(appOnly && error.details ? { details: error.details } : {}) } };
    }
  });
}
registerAppResource(server, "lorekeeper-workspace", workspaceUri, {}, async () => ({ contents: [{ uri: workspaceUri, mimeType: RESOURCE_MIME_TYPE, text: await readFile(new URL("./workspace.html", import.meta.url), "utf8"), _meta: { ui: { csp: { connectDomains: [], resourceDomains: [] } }, "openai/ui": { availableDisplayModes: ["fullscreen"], preferredDisplayMode: "fullscreen" } } }] }));
workspaceTool("open_lorekeeper_workspace", {
  title: "Open Lorekeeper", description: "Display Lorekeeper only when asked. This opener creates a host tab; ordinary project operations and refreshes must not call it.",
  inputSchema: { fileName: fileNameSchema.optional() }, _meta: { ui: { resourceUri: workspaceUri }, "openai/ui": { entrypoints: [{ type: "global" }, { type: "thread" }] } }
}, async ({ fileName }) => ({ ...jsonResult({ version: manifest.version, message: "Choose a local project or create one." }), ...(fileName ? { _meta: { "lorekeeper/workspace": snapshot(await workspace.read(fileName)) } } : {}) }));
workspaceTool("list_lorekeeper_projects", { title: "List local projects", description: "List contained user-owned project files; filenames never grant arbitrary path access.", inputSchema: {} }, async () => {
  const { projects, unreadable, truncated } = await store.list(); return jsonResult({ projects, unreadable, truncated });
});
workspaceTool("get_lorekeeper_workspace", { title: "Load workspace", description: "Load project state and inventory; upgrade supported predecessors without losing content.", inputSchema: { fileName: fileNameSchema.optional() } }, async ({ fileName }) => appResult({ ...await store.list(), state: fileName ? snapshot(await workspace.read(fileName)) : null }), true);
workspaceTool("sync_lorekeeper_project", { title: "Synchronize project", description: "Read saved changes without opening tabs or replacing dirty drafts.", inputSchema: { fileName: fileNameSchema, knownEtag: etagSchema } }, async ({ fileName, knownEtag }) => {
  const current = await workspace.read(fileName);
  if (current.etag === knownEtag) return appResult({ unchanged: true, etag: knownEtag });
  const prior = knownStates.get(knownEtag);
  if (!prior || prior.fileName !== fileName || prior.project.id !== current.project.id) return appResult({ state: snapshot(current) });
  remember(current);
  return appResult({ fileName, etag: current.etag, revision: current.project.revision, updatedAt: current.project.updatedAt, changes: changesBetween(prior.project, current.project) });
}, true);
workspaceTool("create_lorekeeper_project", { title: "Create local project", description: "Create a new portable project only when requested; existing files are never replaced.", inputSchema: { fileName: fileNameSchema, title: z.string().min(1).max(200), sample: z.boolean().optional() }, annotations: { readOnlyHint: false } }, async ({ fileName, title, sample }) => {
  const state = await store.create(fileName, createProject(title, sample)); return { ...jsonResult({ fileName, ...projectSummary(state.project) }), _meta: { "lorekeeper/workspace": snapshot(state) } };
});
workspaceTool("read_lorekeeper_project", {
  title: "Read organized project", description: "Read paginated acts, chapters, beats, entities, facts and relationships. Follow nextOffset. Lists include guarded ordering hashes; exact field reads provide mutation preconditions.",
  inputSchema: { fileName: fileNameSchema, offset: z.number().int().min(0).optional(), limit: z.number().int().min(1).max(40).optional() }
}, async ({ fileName, offset = 0, limit = 20 }) => {
  const { project: p } = await workspace.read(fileName), data = { ...projectSummary(p), offset, nextOffset: null, entityTypes: p.entityTypes, briefFields: Object.keys(p.bookBrief), lists: [] };
  for (const [kind, name] of Object.entries({ act: "acts", chapter: "chapters", beat: "beats", entity: "entities", fact: "facts", relationship: "relationships" })) {
    data[name] = p[name].slice(offset, offset + limit).map(({ text: _text, summary, synopsis, properties: _properties, ...item }) => ({ ...item, ...(summary ? { summary: summary.slice(0, 300), summaryComplete: summary.length <= 300 } : {}), ...(synopsis ? { synopsis: synopsis.slice(0, 300), synopsisComplete: synopsis.length <= 300 } : {}) }));
    if (offset + limit < p[name].length) data.nextOffset = offset + limit;
    const parents = kind === "chapter" ? [null, ...p.acts.slice(offset, offset + limit).map(x => x.id)] : kind === "beat" ? p.chapters.slice(offset, offset + limit).map(x => x.id) : [null];
    for (const parent of parents) { const target = listTarget(kind, parent), value = targetValue(p, target); data.lists.push({ target, hash: hashValue(value), ids: value }); }
  }
  return jsonResult(data);
}, false, true);
workspaceTool("read_lorekeeper_target", {
  title: "Read exact target", description: "Read a field, item or ordered list with exact hash and revision. For lists use kind=list, field=singular act/chapter/beat/entity/fact/relationship, and id=parent UUID; omit id for Unassigned or top-level lists. The hash is of the canonical value, not an excerpt. Follow continuation pages before replacing complete text.",
  inputSchema: { fileName: fileNameSchema, target: targetSchema, offset: z.number().int().min(0).optional(), maximumCharacters: z.number().int().min(100).max(12000).optional() }
}, async ({ fileName, target, offset = 0, maximumCharacters = 8000 }) => {
  const { project: p } = await workspace.read(fileName), value = targetValue(p, target), full = readTarget(p, target);
  if (offset > full.length || /[\uDC00-\uDFFF]/.test(full[offset] ?? "")) throw new ProjectError("INVALID_OFFSET", "Use a returned continuation offset.");
  let end = Math.min(full.length, offset + maximumCharacters); if (/[\uDC00-\uDFFF]/.test(full[end] ?? "")) end--;
  return jsonResult({ projectId: p.id, revision: p.revision, target, targetHash: hashValue(value), ...(!target.field && dependents(p, target.kind, target.id) !== null ? { dependentsHash: hashValue(dependents(p, target.kind, target.id)) } : {}), text: full.slice(offset, end), ...(offset === 0 && end === full.length ? { value } : {}), offset, nextOffset: end < full.length ? end : null, totalCharacters: full.length, complete: offset === 0 && end === full.length });
}, false, true);
workspaceTool("retrieve_lorekeeper_context", { title: "Retrieve narrative context", description: "Read bounded lexical project context. Returned excerpts are discovery, not exact mutation evidence.", inputSchema: { fileName: fileNameSchema, query: z.string().max(500), chapterId: uuid.optional(), maximumCharacters: z.number().int().min(1000).max(24000).optional() } }, async ({ fileName, query, chapterId, maximumCharacters }) => jsonResult(retrieveContext((await workspace.read(fileName)).project, query, chapterId, maximumCharacters)), false, true);
workspaceTool("search_lorekeeper_project", { title: "Search project", description: "Search local outline, canon and prose by exact terms; return paginated source identities and excerpts.", inputSchema: { fileName: fileNameSchema, query: z.string().max(500), offset: z.number().int().min(0).optional(), limit: z.number().int().min(1).max(40).optional() } }, async ({ fileName, query, offset, limit }) => jsonResult(searchProject((await workspace.read(fileName)).project, query, offset, limit)), false, true);

const commandInputs = { fileName: fileNameSchema, requestId: uuid, groupId: uuid.optional(), expectedRevision: z.number().int().positive(), surface: z.enum(["outline", "editor"]), label: z.string().min(1).max(200).optional(), operations: z.array(operationSchema).min(1).max(100) };
workspaceTool("apply_lorekeeper_changes", {
  title: "Apply guarded changes", description: "Apply author-requested changes directly and preserve reversible history. Use exact current field/list hashes; stable requestId makes identical retries idempotent. Outline cannot modify prose. Insert requires a stable UUID in value.id. Changes are visible immediately.",
  inputSchema: commandInputs, annotations: { readOnlyHint: false, destructiveHint: true }
}, async args => {
  const state = await workspace.command(args, "ai"), changes = state.receipt?.changes ?? [];
  remember(state);
  return jsonResult({ projectId: state.project.id, revision: state.project.revision, requestId: args.requestId, receipt: mutationData(state).receipt, groupId: state.receipt?.groupId, changed: changes.map(x => ({ target: x.target, currentHash: hashValue(targetValue(state.project, x.target)) })), guidance: "Changes were applied. Existing workspace views synchronize automatically; do not open another tab." });
}, false, true);
workspaceTool("apply_lorekeeper_manual_changes", { title: "Save manual changes", description: "Apply autosave or structural commands through the shared exact-precondition service.", inputSchema: commandInputs, annotations: { readOnlyHint: false } }, async args => {
  const started = performance.now(), prior = [...knownStates.values()].find(s => s.fileName === args.fileName && s.project.revision === args.expectedRevision);
  const current = await workspace.command(args, "manual"), data = mutationData(current);
  if (prior && !current.replayed) { data.changes = changesBetween(prior.project, current.project); data.baseEtag = prior.etag; }
  else if (!prior) data.replayed = true; // An evicted baseline requires a private reread, never a partial stale adoption.
  return appResult({ ...data, localActionMs: Math.round(performance.now() - started) });
}, true);
workspaceTool("save_lorekeeper_draft", { title: "Preserve recoverable draft", description: "Persist a captured draft batch locally; does not change canonical project content.", inputSchema: { fileName: fileNameSchema, sessionId: uuid, sequence: z.number().int().min(0), operations: z.array(operationSchema).max(100), request: z.record(z.string(), z.json()).optional(), clear: z.boolean().optional() }, annotations: { readOnlyHint: false } }, async args => appResult(await workspace.draft(args)), true);
workspaceTool("get_lorekeeper_drafts", { title: "Read recoverable drafts", description: "Inspect retained local batches after failed saves or interrupted views.", inputSchema: { fileName: fileNameSchema } }, async ({ fileName }) => appResult({ drafts: await workspace.drafts(fileName) }), true);
workspaceTool("list_lorekeeper_history", { title: "List change groups", description: "Read paginated compact local change summaries.", inputSchema: { fileName: fileNameSchema, offset: z.number().int().min(0).optional(), limit: z.number().int().min(1).max(40).optional() } }, async ({ fileName, offset, limit }) => appResult(await workspace.history(fileName, offset, limit)), true);
workspaceTool("read_lorekeeper_change_group", { title: "Compare changes", description: "Read exact before/after changes in one manual or AI group. Long text records display only the changed range.", inputSchema: { fileName: fileNameSchema, groupId: uuid } }, async ({ fileName, groupId }) => appResult(await workspace.group(fileName, groupId)), true);
workspaceTool("rollback_lorekeeper_change_group", { title: "Restore affected items", description: "Roll back a change group atomically; conflicting later edits are preserved.", inputSchema: { fileName: fileNameSchema, groupId: uuid, requestId: uuid }, annotations: { readOnlyHint: false, destructiveHint: true } }, async args => appResult(mutationData(await workspace.rollback(args))), true);

const preferenceStore = new LocalDocumentStore({ root: store.root, filename: value => { if (value !== ".lorekeeper-ui.json") throw new ProjectError("INVALID_FILENAME", "Unknown preference file."); return value; }, maximumBytes: 65536, backups: false,
  validate: value => z.object({ revision: z.number().int().positive(), updatedAt: z.string(), values: z.record(z.string(), z.json()) }).strict().parse(value), encode: value => JSON.stringify(value) + "\n" });
workspaceTool("get_lorekeeper_ui_preferences", { title: "Read layout preferences", description: "Read local theme, pane and project selection preferences.", inputSchema: {} }, async () => {
  try { return appResult((await preferenceStore.read(".lorekeeper-ui.json")).project.values); } catch (error) { if (error.code === "ENOENT") return appResult({}); throw error; }
}, true);
workspaceTool("save_lorekeeper_ui_preferences", { title: "Save layout preferences", description: "Persist non-content UI preferences in the user's local folder.", inputSchema: { values: z.record(z.string(), z.json()) }, annotations: { readOnlyHint: false } }, async ({ values }) => {
  if (Buffer.byteLength(JSON.stringify(values)) > 60000) throw new ProjectError("PREFERENCES_TOO_LARGE", "Layout preferences exceed their bound.");
  try { await preferenceStore.update(".lorekeeper-ui.json", undefined, p => ({ ...p, values: { ...p.values, ...values } })); }
  catch (error) { if (error.code !== "ENOENT") throw error; await preferenceStore.create(".lorekeeper-ui.json", { revision: 1, updatedAt: new Date().toISOString(), values }); }
  return appResult({ saved: true });
}, true);

workspaceTool("connect_lorekeeper_chat", { title: "Connect local Codex", description: "Connect the owned local app-server and discover account models without inference.", inputSchema: {}, annotations: { readOnlyHint: false } }, async () => appResult(await chat.connect()), true);
workspaceTool("disconnect_lorekeeper_chat", { title: "Disconnect local chat", description: "Preserve partial replies and close only this plugin's app-server.", inputSchema: {}, annotations: { readOnlyHint: false } }, async () => { await chat.close(); return appResult({ disconnected: true }); }, true);
workspaceTool("sign_in_lorekeeper_chat", { title: "Sign in to Codex", description: "Start the official Codex-owned ChatGPT sign-in flow.", inputSchema: {}, annotations: { readOnlyHint: false, openWorldHint: true } }, async () => appResult(await chat.login()), true);
workspaceTool("get_lorekeeper_chat", { title: "Read embedded conversation", description: "Read cached conversation pages or changed streaming items, without full transcript replay or inference.", inputSchema: { fileName: fileNameSchema, surface: z.enum(["outline", "editor"]), knownVersion: z.string().max(200).optional(), before: z.number().int().min(0).optional(), limit: z.number().int().min(1).max(40).optional() } }, async args => appResult(await chat.view(args.fileName, args)), true);
workspaceTool("choose_lorekeeper_conversation", { title: "Choose embedded conversation", description: "Select a surface-scoped conversation or create one without deleting history.", inputSchema: { fileName: fileNameSchema, surface: z.enum(["outline", "editor"]), conversationId: uuid.optional() }, annotations: { readOnlyHint: false } }, async args => appResult(await chat.conversation(args.fileName, args.conversationId, args.surface)), true);
const contextInputs = { fileName: fileNameSchema, surface: z.enum(["outline", "editor"]), chapterId: uuid.optional(), text: z.string().max(20000), model: z.string().max(200).optional() };
workspaceTool("set_lorekeeper_conversation_model", { title: "Save conversation model", description: "Persist an explicitly chosen discovered model and reasoning effort without inference.", inputSchema: { fileName: fileNameSchema, surface: z.enum(["outline", "editor"]), model: z.string().min(1).max(200), effort: z.string().min(1).max(50) }, annotations: { readOnlyHint: false } }, async args => appResult(await chat.selectModel(args)), true);
workspaceTool("preview_lorekeeper_chat_context", { title: "Preview next context", description: "Preview exact complete project sources, overrides, token estimates and rolling transcript selection.", inputSchema: contextInputs }, async args => appResult(await chat.preview(args)), true);
workspaceTool("save_lorekeeper_chat_draft", { title: "Save composer", description: "Save unsent text and an uncertain send identity locally without inference.", inputSchema: { fileName: fileNameSchema, surface: z.enum(["outline", "editor"]), text: z.string().max(20000), request:z.record(z.string(),z.json()).nullable().optional() }, annotations: { readOnlyHint: false } }, async args => appResult(await chat.composer(args)), true);
workspaceTool("send_lorekeeper_chat_message", { title: "Send message", description: "Persist outgoing text before inference. Run project-scoped tools with fresh selected context; uncertain sends are never resent automatically.", inputSchema: { ...contextInputs, requestId: uuid, expectedEtag: etagSchema, model: z.string().min(1).max(200), effort: z.string().min(1).max(50) }, annotations: { readOnlyHint: false, openWorldHint: true } }, async args => appResult(await chat.send(args)), true);
workspaceTool("stop_lorekeeper_chat", { title: "Stop local turn", description: "Interrupt the active turn; retain partial output and completed changes.", inputSchema: { fileName: fileNameSchema } }, async ({ fileName }) => appResult(await chat.stop(fileName)), true);

registerAppResource(server, "storage-probe", resourceUri, {}, async () => ({
  contents: [{
    uri: resourceUri,
    mimeType: RESOURCE_MIME_TYPE,
    text: await readFile(new URL("./storage-probe.html", import.meta.url), "utf8"),
    _meta: {
      ui: {
        csp: {
          connectDomains: ["https://chatgpt.com", "https://files.oaiusercontent.com", "https://*.oaiusercontent.com"],
          resourceDomains: []
        }
      },
      "openai/ui": { availableDisplayModes: ["inline", "fullscreen"], preferredDisplayMode: "fullscreen" }
    }
  }]
}));

registerAppTool(server, "open_storage_probe", {
  title: "Open Lorekeeper Storage Probe",
  description: "Open a synthetic project editor to inspect ChatGPT file storage. No user content is read or persisted by the server.",
  inputSchema: { file: z.object({ resourceUri: z.string() }).passthrough().optional() },
  outputSchema: { probeVersion: z.string(), maximumBytes: z.number() },
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
  _meta: {
    ui: { resourceUri, visibility: ["model", "app"] },
    "openai/ui": { entrypoints: [{ type: "file", extensions: ["lkproject"] }] }
  }
}, async () => ({
  content: [{ type: "text", text: "Storage probe opened. Saving and reopening require the host file capabilities reported by the editor." }],
  structuredContent: { probeVersion: "0.1.1", maximumBytes }
}));

server.registerTool("validate_probe_project", {
  title: "Validate synthetic project",
  description: "Validate the synthetic storage fixture and return its exact UTF-8 digest, identities and revision. Retains no project content.",
  inputSchema: { json: z.string().max(maximumBytes) },
  outputSchema: {
    projectId: z.string(), chapterId: z.string(), revision: z.number(), sha256: z.string(), byteLength: z.number()
  },
  annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false },
  _meta: { ui: { visibility: ["app"] } }
}, async ({ json }) => {
  try {
    const byteLength = Buffer.byteLength(json, "utf8");
    if (byteLength > maximumBytes) throw new Error("Synthetic project exceeds 64 KiB.");
    const project = projectSchema.parse(JSON.parse(json));
    const result = {
      projectId: project.projectId,
      chapterId: project.chapters[0].id,
      revision: project.revision,
      sha256: createHash("sha256").update(json, "utf8").digest("hex"),
      byteLength
    };
    return { content: [{ type: "text", text: JSON.stringify(result) }], structuredContent: result };
  } catch {
    return { isError: true, content: [{ type: "text", text: "Not a valid synthetic Lorekeeper storage fixture. Check its schema, identities and 64 KiB limit." }] };
  }
});

await server.connect(new StdioServerTransport());
let closing = false;
async function close() {
  if (closing) return;
  closing = true;
  await chat.close();
  await server.close();
}
process.stdin.once("end", () => { void close(); });
process.once("SIGINT", () => { void close(); });
process.once("SIGTERM", () => { void close(); });
