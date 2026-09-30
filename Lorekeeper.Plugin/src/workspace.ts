import { App } from "@modelcontextprotocol/ext-apps";
import { OpenAIExtensions } from "@openai/mcp-extensions/app";

interface Chapter { id: string; title: string; synopsis: string; text: string }
interface Canon { id: string; name: string; kind: string; text: string; chapterIds: string[] }
interface Target { kind: "brief" | "chapter" | "outline" | "canon"; id?: string }
interface Proposal { id: string; target: Target; baseRevision: number; before: string; after: string; reason: string; status: string }
interface Project { format: string; schemaVersion: number; id: string; revision: number; title: string; bookBrief: string; chapters: Chapter[]; canon: Canon[]; proposals: Proposal[] }
interface State { fileName: string; project: Project; etag: string }
interface Inventory { folder: string; projects: { fileName: string; title: string }[]; unreadable: string[]; truncated: boolean; state: State | null }
interface Context { revision: number; usedCharacters: number; maximumCharacters: number; omittedSources: number; sources: { kind: string; id: string; title: string; text: string; reason: string; complete: boolean }[] }

const app = new App({ name: "Lorekeeper", version: "0.2.1" }, {}, { autoResize: true });
new OpenAIExtensions(app);
const content = document.querySelector<HTMLElement>("#content")!;
const status = document.querySelector<HTMLElement>("#status")!;
const projectList = document.querySelector<HTMLSelectElement>("#project-list")!;
const saveState = document.querySelector<HTMLElement>("#save-state")!;
const dialog = document.querySelector<HTMLDialogElement>("#discard-dialog")!;
let state: State | undefined;
let draft: Project | undefined;
let inventory: Inventory | undefined;
let view = "brief";
let selectedChapter: string | undefined;
let connected = false;
let busy = false;
let dirty = false;
let context: Context | undefined;
let query = "";
let pendingInput: string | undefined;
let discardedAction: (() => Promise<void>) | undefined;

const html = (value: string | number) => String(value).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]!));
const inputValue = (id: string) => document.querySelector<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(`#${id}`)?.value ?? "";
const authored = (project: Project) => ({ title: project.title, bookBrief: project.bookBrief, chapters: project.chapters, canon: project.canon });
const button = (id: string) => document.querySelector<HTMLButtonElement>(`#${id}`)!;

function show(message: string, error = false): void { status.textContent = message; status.dataset.error = String(error); }
function updateControls(): void {
  projectList.disabled = !connected || busy;
  button("new").disabled = !connected || busy;
  button("refresh").disabled = !connected || busy || !state;
  button("save").disabled = !connected || busy || !state || !dirty;
  for (const element of content.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>("button,input,textarea,select")) element.disabled = busy || !connected;
  saveState.textContent = state ? `${dirty ? "Unsaved changes" : "Saved"} · revision ${state.project.revision}` : "No project open";
  for (const nav of document.querySelectorAll<HTMLButtonElement>("nav button")) nav.classList.toggle("active", nav.dataset.view === view);
}

function capture(): void {
  if (!draft || !state) return;
  if (view === "brief" && document.querySelector("#book-brief")) { draft.title = inputValue("project-title"); draft.bookBrief = inputValue("book-brief"); }
  if (view === "chapters") {
    const chapter = draft.chapters.find(c => c.id === selectedChapter);
    if (chapter && document.querySelector("#chapter-text")) { chapter.title = inputValue("chapter-title"); chapter.text = inputValue("chapter-text"); }
  }
  if (view === "outline") for (const c of draft.chapters) {
    if (document.querySelector(`#title-${c.id}`)) { c.title = inputValue(`title-${c.id}`); c.synopsis = inputValue(`synopsis-${c.id}`); }
  }
  if (view === "canon") for (const c of draft.canon) {
    if (document.querySelector(`#name-${c.id}`)) {
      c.name = inputValue(`name-${c.id}`); c.kind = inputValue(`kind-${c.id}`); c.text = inputValue(`text-${c.id}`);
      c.chapterIds = [...document.querySelectorAll<HTMLInputElement>(`input[data-canon="${c.id}"]:checked`)].map(e => e.value);
    }
  }
  dirty = JSON.stringify(authored(draft)) !== JSON.stringify(authored(state.project));
  updateControls();
  const counter = document.querySelector("#word-count");
  if (counter) counter.textContent = `${inputValue("chapter-text").trim().split(/\s+/u).filter(Boolean).length} words`;
}

async function call(name: string, args: Record<string, unknown> = {}): Promise<{ data: Record<string, unknown>; state?: State }> {
  const result = await app.callServerTool({ name, arguments: args });
  if (result.isError) throw new Error(typeof result.structuredContent?.message === "string" ? result.structuredContent.message : "The operation failed. Your draft is preserved; inspect saved state before retrying a write.");
  return { data: result.structuredContent ?? {}, state: result._meta?.["lorekeeper/workspace"] as State | undefined };
}
async function run(action: () => Promise<void>): Promise<void> {
  if (!connected || busy) return;
  busy = true; updateControls();
  try { await action(); }
  catch (error) {
    show(error instanceof Error ? error.message : "Operation failed. Your draft is preserved.", true);
    if (dirty && draft) { document.querySelector<HTMLElement>("#recovery")!.hidden = false; document.querySelector<HTMLTextAreaElement>("#recovery-copy")!.value = JSON.stringify(draft, null, 2); }
  } finally { busy = false; updateControls(); }
}
function adopt(next: State): void {
  state = next; draft = structuredClone(next.project); dirty = false; context = undefined;
  if (!draft.chapters.some(c => c.id === selectedChapter)) selectedChapter = draft.chapters[0]?.id;
  document.querySelector<HTMLElement>("#recovery")!.hidden = true;
  render();
}
async function load(fileName?: string): Promise<void> {
  const result = await call("get_lorekeeper_workspace", fileName ? { fileName } : {});
  inventory = result.data as unknown as Inventory;
  projectList.innerHTML = `<option value="">Choose a project</option>${inventory.projects.map(p => `<option value="${html(p.fileName)}">${html(p.title)}</option>`).join("")}`;
  document.querySelector("#folder")!.textContent = `Project folder: ${inventory.folder}`;
  if (inventory.state) adopt(inventory.state);
  projectList.value = state?.fileName ?? "";
  if (inventory.unreadable.length) show(`${inventory.unreadable.length} project file(s) could not be opened. Their contents were not changed.`, true);
  else if (inventory.truncated) show("Showing the first 100 project files. Use a smaller projects folder to see more.");
}
async function saved(): Promise<void> {
  capture();
  if (!state || !draft || !dirty) return;
  const result = await call("save_lorekeeper_project", { fileName: state.fileName, expectedEtag: state.etag, edits: authored(draft) });
  if (!result.state) throw new Error("No saved snapshot was returned. Keep your draft and reopen the saved file before retrying.");
  adopt(result.state); await load(); show(`Saved ${state.fileName}. Revision ${state.project.revision}.`);
}
function afterDiscard(action: () => Promise<void>): void {
  capture();
  if (!dirty) { void run(action); return; }
  discardedAction = action; dialog.showModal();
}

function heading(title: string, description: string): string { return `<div class="eyebrow">${html(draft?.title ?? "Your story")}</div><h1>${title}</h1><p class="muted">${description}</p>`; }
function render(): void {
  if (!draft) { updateControls(); return; }
  const chapters = draft.chapters;
  content.className = "";
  if (view === "brief") content.innerHTML = heading("The intent behind the book", "Give your story a direction that every conversation can return to.") + `<label for="project-title">Working title</label><input id="project-title" value="${html(draft.title)}" maxlength="200"><label for="book-brief">Book Brief</label><textarea id="book-brief" style="min-height:300px" maxlength="20000" placeholder="Premise, audience, voice, themes, and what matters most…">${html(draft.bookBrief)}</textarea>`;
  if (view === "outline") content.innerHTML = heading("Shape the story", "Chapter order and intent, beside the writing they guide.") + draft.chapters.map((c, i) => `<div class="card"><div class="row between"><span class="eyebrow">Chapter ${i + 1}</span><div class="row"><button data-move="${c.id}" data-direction="-1" aria-label="Move ${html(c.title)} earlier">↑</button><button data-move="${c.id}" data-direction="1" aria-label="Move ${html(c.title)} later">↓</button></div></div><label for="title-${c.id}">Chapter title</label><input id="title-${c.id}" value="${html(c.title)}" maxlength="200"><label for="synopsis-${c.id}">What happens here</label><textarea id="synopsis-${c.id}" maxlength="10000">${html(c.synopsis)}</textarea></div>`).join("") + `<button id="add-chapter">Add chapter</button>`;
  if (view === "chapters") {
    const chapter = draft.chapters.find(c => c.id === selectedChapter) ?? draft.chapters[0];
    selectedChapter = chapter?.id;
    content.innerHTML = heading("A page for the story", "Write here, then bring a focused question to ChatGPT.") + `<div class="chapter-list">${draft.chapters.map(c => `<button data-chapter="${c.id}" class="${c.id === chapter?.id ? "active" : ""}">${html(c.title)}</button>`).join("")}</div>` + (chapter ? `<label for="chapter-title">Chapter title</label><input id="chapter-title" value="${html(chapter.title)}" maxlength="200"><label for="chapter-text">Manuscript</label><textarea class="manuscript" id="chapter-text" maxlength="100000" placeholder="Begin your chapter…">${html(chapter.text)}</textarea><div class="row between"><small id="word-count">${chapter.text.trim().split(/\s+/u).filter(Boolean).length} words</small><button id="discuss">Discuss this chapter</button></div>` : `<button id="add-chapter">Add your first chapter</button>`);
  }
  if (view === "canon") content.innerHTML = heading("Keep the world consistent", "Record established characters, places, and facts. Link them to the chapters they constrain.") + draft.canon.map(c => `<div class="card"><label for="name-${c.id}">Name</label><input id="name-${c.id}" value="${html(c.name)}" maxlength="200"><label for="kind-${c.id}">Kind</label><select id="kind-${c.id}">${["character", "place", "fact"].map(k => `<option ${k === c.kind ? "selected" : ""}>${k}</option>`).join("")}</select><label for="text-${c.id}">Established canon</label><textarea id="text-${c.id}" maxlength="10000">${html(c.text)}</textarea><label>Relevant chapters</label>${chapters.map(ch => `<label class="check"><input type="checkbox" data-canon="${c.id}" value="${ch.id}" ${c.chapterIds.includes(ch.id) ? "checked" : ""}>${html(ch.title)}</label>`).join("")}</div>`).join("") + `<button id="add-canon">Add canon entry</button>`;
  if (view === "context") content.innerHTML = heading("Choose what the conversation sees", "Preview the saved brief, linked canon, and relevant writing for a specific task.") + `<label for="context-chapter">Chapter</label><select id="context-chapter"><option value="">Whole project</option>${draft.chapters.map(c => `<option value="${c.id}" ${c.id === selectedChapter ? "selected" : ""}>${html(c.title)}</option>`).join("")}</select><label for="context-query">What are you working on?</label><input id="context-query" value="${html(query)}" maxlength="500" placeholder="Mara, lantern memories, harbor…"><div class="row" style="margin-top:12px"><button id="preview-context">Preview context</button><button id="share-context">Share with ChatGPT</button></div>${context ? `<p class="muted">Saved revision ${context.revision} · ${context.usedCharacters.toLocaleString()} / ${context.maximumCharacters.toLocaleString()} characters${context.omittedSources ? ` · ${context.omittedSources} sources omitted` : ""}</p>${context.sources.map(s => `<div class="source"><h3>${html(s.title)}</h3><small>${html(s.reason)} · ${s.complete ? "Complete text" : "Excerpt"}</small><pre>${html(s.text)}</pre></div>`).join("") || `<p>No matching sources. Try distinctive names or terms from the project.</p>`}` : ""}`;
  if (view === "review") {
    const pending = draft.proposals.filter(p => p.status === "pending");
    content.innerHTML = heading("Your words, your decision", "Proposed edits stay separate from the writing until you accept them.") + (pending.length ? pending.map(p => `<article class="card"><h2>${html(targetTitle(p.target))}</h2><p>${html(p.reason)}</p><small>Based on revision ${p.baseRevision}</small><div class="diff"><div><h3>Current at proposal</h3><pre>${html(p.before) || "(empty)"}</pre></div><div class="after"><h3>Proposed</h3><pre>${html(p.after) || "(empty)"}</pre></div></div><div class="row" style="margin-top:16px"><button class="primary" data-accept="${p.id}">Accept edit</button><button data-reject="${p.id}">Reject edit</button></div></article>`).join("") : `<div class="card"><p>No edits waiting for review.</p><small>Ask ChatGPT to propose a change, then reopen the saved project if the view has not refreshed.</small></div>`) + `<details><summary>Reviewed edits (${draft.proposals.length - pending.length})</summary>${draft.proposals.filter(p => p.status !== "pending").map(p => `<p>${html(targetTitle(p.target))} · ${html(p.status)}<small>${html(p.reason)}</small></p>`).join("")}</details><details><summary>Try the review workflow</summary><p class="muted">Create a proposed sentence for the selected chapter. It will wait for your approval.</p><button id="example-proposal">Create example proposal</button></details>`;
  }
  updateControls();
}
function targetTitle(target: Target): string {
  if (target.kind === "brief") return "Book Brief";
  if (target.kind === "canon") return draft?.canon.find(c => c.id === target.id)?.name ?? "Canon entry";
  return `${draft?.chapters.find(c => c.id === target.id)?.title ?? "Chapter"}${target.kind === "outline" ? " — outline" : ""}`;
}
function requireSaved(): State {
  capture();
  if (!state) throw new Error("Open a project first.");
  if (dirty) throw new Error("Save your changes first so the conversation uses the same text you see.");
  return state;
}
async function previewContext(): Promise<void> {
  const loaded = requireSaved(); query = inputValue("context-query"); selectedChapter = inputValue("context-chapter") || undefined;
  const result = await call("retrieve_lorekeeper_context", { fileName: loaded.fileName, query, ...(selectedChapter ? { chapterId: selectedChapter } : {}) });
  context = result.data as unknown as Context; render(); show("Context preview uses saved project content.");
}
function newProjectForm(): void {
  content.className = "";
  content.innerHTML = `<div class="eyebrow">A new beginning</div><h1>Start a project</h1><p class="muted">A portable file on your computer. No account or API key needed.</p><label for="new-title">Project title</label><input id="new-title" value="Untitled story" maxlength="200"><label for="new-file">Filename</label><input id="new-file" value="story-${crypto.randomUUID().slice(0, 8)}.lorekeeper.json"><div class="row" style="margin-top:18px"><button class="primary" id="create-project">Create project</button><button id="create-sample">Create sample project</button></div><small style="margin-top:15px">The sample contains artificial characters and Unicode text for exploring the workspace.</small>`;
  updateControls();
}

content.addEventListener("input", () => capture());
content.addEventListener("change", () => capture());
content.addEventListener("click", event => {
  const element = (event.target as HTMLElement).closest<HTMLButtonElement>("button");
  if (!element) return;
  void run(async () => {
    capture();
    if (element.dataset.chapter) { selectedChapter = element.dataset.chapter; render(); }
    if (element.dataset.move && draft) {
      const index = draft.chapters.findIndex(c => c.id === element.dataset.move);
      const next = index + Number(element.dataset.direction);
      if (next >= 0 && next < draft.chapters.length) { [draft.chapters[index], draft.chapters[next]] = [draft.chapters[next], draft.chapters[index]]; dirty = true; render(); }
    }
    if (element.id === "add-chapter" && draft) { draft.chapters.push({ id: crypto.randomUUID(), title: `Chapter ${draft.chapters.length + 1}`, synopsis: "", text: "" }); dirty = true; render(); }
    if (element.id === "add-canon" && draft) { draft.canon.push({ id: crypto.randomUUID(), name: "New canon entry", kind: "fact", text: "", chapterIds: [] }); dirty = true; render(); }
    if (element.id === "create-project" || element.id === "create-sample") {
      const result = await call("create_lorekeeper_project", { fileName: inputValue("new-file"), title: element.id === "create-sample" ? "The Lantern Archive" : inputValue("new-title").trim(), sample: element.id === "create-sample" });
      if (!result.state) throw new Error("No created snapshot returned. Check the project list before retrying creation.");
      view = "brief"; adopt(result.state); await load(); show(`Created ${state!.fileName} on your computer.`);
    }
    if (element.id === "preview-context") await previewContext();
    if (element.id === "share-context") {
      await previewContext();
      await app.updateModelContext({ content: [{ type: "text", text: JSON.stringify({ fileName: state!.fileName, ...context }) }] });
      show("Selected context shared with this conversation. No message was sent.");
    }
    if (element.id === "discuss") {
      const loaded = requireSaved();
      const response = await app.sendMessage({ role: "user", content: [{ type: "text", text: `Help me work on chapter ${selectedChapter} in Lorekeeper project ${loaded.fileName}. Read its current overview, retrieve relevant context, and read the exact chapter before discussing changes. Propose any requested text changes through propose_lorekeeper_edit for my review; do not directly rewrite the local file.` }] });
      if (response.isError) throw new Error("The host declined this message. Ask about the saved chapter directly in chat.");
      show("Chapter discussion requested in this conversation.");
    }
    if (element.id === "example-proposal") {
      const loaded = requireSaved(); const chapter = loaded.project.chapters.find(c => c.id === selectedChapter) ?? loaded.project.chapters[0];
      if (!chapter) throw new Error("Add a chapter first.");
      const hash = [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(chapter.text)))].map(b => b.toString(16).padStart(2, "0")).join("");
      const result = await call("propose_lorekeeper_edit", { fileName: loaded.fileName, proposalId: crypto.randomUUID(), expectedRevision: loaded.project.revision, expectedTargetHash: hash, target: { kind: "chapter", id: chapter.id }, after: chapter.text + "\n\nA new sentence waits at the edge of the page.", reason: "Example proposal to try accepting or rejecting a reviewed edit." });
      if (result.state) adopt(result.state); show("Proposal saved. The chapter is unchanged until you accept it.");
    }
    const proposalId = element.dataset.accept ?? element.dataset.reject;
    if (proposalId) {
      const loaded = requireSaved();
      const result = await call("resolve_lorekeeper_proposal", { fileName: loaded.fileName, expectedEtag: loaded.etag, proposalId, decision: element.dataset.accept ? "accept" : "reject" });
      if (result.state) adopt(result.state); show(element.dataset.accept ? "Edit accepted and saved." : "Proposal rejected. Writing is unchanged.");
    }
  });
});
for (const nav of document.querySelectorAll<HTMLButtonElement>("nav button")) nav.onclick = () => { if (busy || !draft) return; capture(); view = nav.dataset.view!; render(); };
button("save").onclick = () => void run(saved);
button("new").onclick = () => { capture(); if (dirty) { show("Save your changes before creating another project.", true); return; } newProjectForm(); };
button("refresh").onclick = () => afterDiscard(async () => { await load(state?.fileName); show("Reopened the saved project from disk."); });
projectList.onchange = () => {
  const selected = projectList.value;
  projectList.value = state?.fileName ?? "";
  if (selected) afterDiscard(async () => { await load(selected); show("Project opened from disk."); });
};
button("keep-draft").onclick = () => { discardedAction = undefined; dialog.close(); };
button("discard-draft").onclick = () => { const action = discardedAction; discardedAction = undefined; dialog.close(); if (action) void run(action); };

app.ontoolresult = result => {
  const next = result._meta?.["lorekeeper/workspace"] as State | undefined;
  if (!next) return;
  if (!connected) { pendingInput = next.fileName; return; }
  if (busy) return;
  capture();
  if (dirty) { show("Saved project changes are available. Your unsaved draft is preserved; save or copy it before reopening.", true); return; }
  if (!state || state.fileName === next.fileName) adopt(next);
};
app.addEventListener("toolinput", ({ arguments: args }) => {
  if (typeof args?.fileName !== "string") return;
  if (!connected) { pendingInput = args.fileName; return; }
  if (!state && !busy) void run(() => load(args.fileName as string));
});

try {
  await app.connect(); connected = true;
  await run(async () => { await load(pendingInput); show("Connected. Open a local project or create one."); });
} catch { show("The workspace needs an MCP App connection. Reopen Lorekeeper from the installed plugin.", true); }
updateControls();
