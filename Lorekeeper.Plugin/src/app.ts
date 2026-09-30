import { App, applyDocumentTheme, applyHostStyleVariables } from "@modelcontextprotocol/ext-apps";
import { OpenAIExtensions, OpenAIFileEntrypointInputSchema } from "@openai/mcp-extensions/app";

interface Project {
  schemaVersion: 1;
  projectId: string;
  revision: number;
  title: string;
  chapters: { id: string; title: string; paragraphs: { id: string; text: string }[] }[];
}
interface Validation {
  projectId: string;
  chapterId: string;
  revision: number;
  sha256: string;
  byteLength: number;
}
interface HostFiles {
  uploadFile?: (file: File, options: { library: boolean }) => Promise<{ fileId: string }>;
  selectFiles?: () => Promise<{ fileId: string; fileName: string; mimeType: string }[]>;
  getFileDownloadUrl?: (input: { fileId: string }) => Promise<{ downloadUrl: string }>;
}

class ProbeError extends Error {}

const maximumBytes = 65536;
const originalParagraph = "The café’s keeper wrote: “こんにちは — hello, 🌿.”";
const revisedParagraph = "The café’s keeper revised the note: “こんにちは — hello again, 🌿.”";
const app = new App({ name: "Lorekeeper Storage Probe", version: "0.1.1" }, {}, { autoResize: true });
const extensions = new OpenAIExtensions(app);
const editor = document.querySelector<HTMLTextAreaElement>("#project")!;
const status = document.querySelector<HTMLElement>("#status")!;
const capabilities = document.querySelector<HTMLElement>("#capabilities")!;
const diagnostics = document.querySelector<HTMLElement>("#diagnostics")!;
const buttons = Object.fromEntries([...document.querySelectorAll<HTMLButtonElement>("button")].map(b => [b.id, b]));
const report: Record<string, unknown> = { probeVersion: "0.1.1", connected: false, observations: [] };
const observations = report.observations as Record<string, unknown>[];
let connected = false;
let busy = false;
let openedUri: string | undefined;
let currentEtag: string | undefined;
let originalEtag: string | undefined;
let originalFileText: string | undefined;
let loadedText: string | undefined;
let writable = false;
let unsafeWrites = false;
let pendingFileInput: unknown;

function hostFiles(): HostFiles | undefined {
  return (window as Window & { openai?: HostFiles }).openai;
}

function fixture(): Project {
  return {
    schemaVersion: 1,
    projectId: "85d88cf9-79a9-4cb6-8fba-a541c707833b",
    revision: 1,
    title: "Synthetic Storage Probe",
    chapters: [{
      id: "291527bb-6c30-40cb-9dca-5cff8c6950fa",
      title: "Sample Chapter",
      paragraphs: [{ id: "5483cc02-5fab-435b-b1e7-d030a43e3893", text: originalParagraph }]
    }]
  };
}

function show(message: string, error = false): void {
  status.textContent = message;
  status.dataset.error = String(error);
}

function observe(action: string, details: object): void {
  observations.push({ action, at: new Date().toISOString(), ...details });
  refresh();
}

function refresh(): void {
  const files = hostFiles();
  report.hostCapabilities = {
    uploadFile: typeof files?.uploadFile === "function",
    selectFiles: typeof files?.selectFiles === "function",
    getFileDownloadUrl: typeof files?.getFileDownloadUrl === "function",
    resourceBridge: extensions.resources != null,
    openedThroughFileEntrypoint: openedUri != null,
    writable,
    etagPresent: Boolean(currentEtag)
  };
  const ready = connected && !busy;
  for (const id of ["new", "report"]) buttons[id].disabled = !ready;
  for (const id of ["revise", "validate"]) buttons[id].disabled = !ready || !editor.value;
  buttons["open-library"].disabled = !ready || typeof files?.selectFiles !== "function" || typeof files?.getFileDownloadUrl !== "function";
  buttons["save-library"].disabled = !ready || !editor.value || typeof files?.uploadFile !== "function";
  buttons.reload.disabled = !ready || !openedUri || extensions.resources == null;
  buttons["save-file"].disabled = !ready || !writable || !currentEtag || !openedUri || unsafeWrites;
  buttons["stale-save"].disabled = !ready || !writable || !originalEtag || originalEtag === currentEtag || !openedUri || unsafeWrites;
  editor.disabled = !ready;
  diagnostics.textContent = JSON.stringify(report, null, 2);
  capabilities.textContent = connected
    ? `Library picker: ${typeof files?.selectFiles === "function" ? "available" : "unavailable"}. File bridge: ${extensions.resources ? "available" : "unavailable"}. Conditional save: ${writable && currentEtag ? "available for opened sample" : "not established"}.`
    : "Waiting for an actual MCP App host connection.";
}

async function validate(text = editor.value): Promise<Validation> {
  if (new TextEncoder().encode(text).byteLength > maximumBytes) throw new ProbeError("Project exceeds the 64 KiB probe limit.");
  const result = await app.callServerTool({ name: "validate_probe_project", arguments: { json: text } });
  const data = result.structuredContent;
  if (result.isError || typeof data?.sha256 !== "string" || typeof data?.revision !== "number") {
    throw new ProbeError("Only the synthetic fixture schema and fixed identities are accepted.");
  }
  return data as unknown as Validation;
}

function resetOpenedFile(): void {
  openedUri = currentEtag = originalEtag = originalFileText = loadedText = undefined;
  writable = false;
  unsafeWrites = false;
}

function resourceText(content: { text?: string; blob?: string }): string {
  if (typeof content.text === "string") return content.text;
  if (typeof content.blob === "string") {
    if (content.blob.length > Math.ceil(maximumBytes / 3) * 4) throw new ProbeError("File exceeds the probe limit.");
    return new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(atob(content.blob), c => c.charCodeAt(0)));
  }
  throw new ProbeError("Host did not provide readable file bytes.");
}

async function readOpenedFile(initial = false): Promise<void> {
  if (!openedUri || !extensions.resources) throw new ProbeError("No host file entrypoint is open.");
  const result = await extensions.resources.read({ uri: openedUri });
  if (result.contents.length !== 1) throw new ProbeError("Expected exactly one opened file.");
  const content = result.contents[0];
  const text = resourceText(content);
  const checked = await validate(text);
  editor.value = loadedText = text;
  currentEtag = content.openaiMetadata?.etag;
  writable = content.openaiMetadata?.writable === true;
  if (initial) {
    originalEtag = currentEtag;
    originalFileText = text;
  }
  observe("read-opened-file", { ...checked, writable, etagPresent: Boolean(currentEtag) });
  show(`Opened revision ${checked.revision}. ${writable && currentEtag ? "Conditional saving is available." : "This host has not supplied a writable file with an ETag."}`);
}

async function openInput(input: unknown): Promise<void> {
  const parsed = OpenAIFileEntrypointInputSchema.safeParse(input);
  if (!parsed.success) return;
  if (!connected) { pendingFileInput = input; return; }
  if (editor.value !== loadedText && loadedText != null) throw new ProbeError("Open blocked because the current editor has unsaved changes.");
  resetOpenedFile();
  openedUri = parsed.data.file.resourceUri;
  try { await readOpenedFile(true); }
  catch (error) { resetOpenedFile(); throw error; }
}

async function readLibraryFile(fileId: string): Promise<string> {
  const files = hostFiles();
  if (!files?.getFileDownloadUrl) throw new ProbeError("Host file downloads are unavailable.");
  const { downloadUrl } = await files.getFileDownloadUrl({ fileId });
  const url = new URL(downloadUrl);
  if (url.protocol !== "https:" || !(url.hostname === "chatgpt.com" || url.hostname === "oaiusercontent.com" || url.hostname.endsWith(".oaiusercontent.com"))) {
    throw new ProbeError("The host returned a download origin outside the probe's declared CSP.");
  }
  const response = await fetch(url, { credentials: "omit", redirect: "error" });
  if (!response.ok || !response.body) throw new ProbeError("Host file download failed.");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > maximumBytes) throw new ProbeError("Selected file exceeds the 64 KiB probe limit.");
      chunks.push(chunk.value);
    }
  } finally { await reader.cancel(); }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

async function run(action: () => Promise<void>): Promise<void> {
  if (busy || !connected) return;
  busy = true;
  refresh();
  try { await action(); }
  catch (error) {
    observe("operation-error", { errorKind: error instanceof Error ? error.name : "Error" });
    // Host errors can contain signed download URLs; keep them out of reports.
    show(error instanceof ProbeError ? error.message : "Operation failed or was unavailable. No save is confirmed. Review the capability report and retry only after checking the saved file.", true);
  } finally { busy = false; refresh(); }
}

buttons.new.onclick = () => void run(async () => {
  resetOpenedFile();
  editor.value = JSON.stringify(fixture(), null, 2) + "\n";
  observe("new-synthetic-project", await validate());
  show("Revision 1 is in memory. It has not been saved.");
});
buttons.validate.onclick = () => void run(async () => {
  const checked = await validate();
  observe("validated", checked);
  show(`Valid revision ${checked.revision}; ${checked.byteLength} UTF-8 bytes.`);
});
buttons.revise.onclick = () => void run(async () => {
  await validate();
  const project = JSON.parse(editor.value) as Project;
  project.revision++;
  project.chapters[0].paragraphs[0].text = revisedParagraph;
  editor.value = JSON.stringify(project, null, 2) + "\n";
  observe("revised-in-memory", await validate());
  show(`Revision ${project.revision} is in memory. Save it before closing the editor.`);
});
buttons["save-library"].onclick = () => void run(async () => {
  const checked = await validate();
  const file = new File([editor.value], `lorekeeper-storage-probe-r${checked.revision}.lkproject`, { type: "application/json" });
  const result = await hostFiles()!.uploadFile!(file, { library: true });
  if (typeof result.fileId !== "string" || !result.fileId) throw new ProbeError("No upload receipt. Check ChatGPT's files before retrying the upload.");
  observe("uploaded-new-version", { ...checked, fileId: result.fileId });
  show(`Uploaded revision ${checked.revision}. Close and reopen it from ChatGPT to verify persistence.`);
});
buttons["open-library"].onclick = () => void run(async () => {
  if (loadedText != null && editor.value !== loadedText) throw new ProbeError("Open blocked because this editor has unsaved changes.");
  const selected = await hostFiles()!.selectFiles!();
  if (selected.length === 0) { show("No file selected."); return; }
  if (selected.length !== 1) throw new ProbeError("Select exactly one synthetic project.");
  const text = await readLibraryFile(selected[0].fileId);
  const checked = await validate(text);
  resetOpenedFile();
  editor.value = loadedText = text;
  observe("read-library-file", { ...checked, fileId: selected[0].fileId });
  show(`Read saved revision ${checked.revision}. To test updating the same file, open it through ChatGPT's file viewer.`);
});
buttons.reload.onclick = () => void run(async () => {
  if (editor.value !== loadedText) throw new ProbeError("Reload blocked because this editor has unsaved changes.");
  await readOpenedFile();
});
buttons["save-file"].onclick = () => void run(async () => {
  if (!openedUri || !writable || !currentEtag || !extensions.resources || unsafeWrites) throw new ProbeError("No safe conditional save is available.");
  const checked = await validate();
  const text = editor.value;
  const result = await extensions.resources.write(openedUri, { text, ifMatch: currentEtag });
  observe("conditional-save", { ...checked, outcome: result.outcome });
  if (result.outcome === "saved") {
    currentEtag = result.etag;
    loadedText = text;
    show(`Saved revision ${checked.revision} to the opened file. Close and reopen to verify.`);
  } else if (result.outcome === "conflict") {
    show("Save conflicted. Your unsaved snapshot remains in the editor; the newer file was not overwritten.", true);
  } else if (result.outcome === "too-large") show(`Save rejected: the host's limit is ${result.maxBytes} bytes.`, true);
});
buttons["stale-save"].onclick = () => void run(async () => {
  if (!openedUri || !writable || !originalEtag || originalEtag === currentEtag || originalFileText == null || !extensions.resources || unsafeWrites) throw new ProbeError("Save a newer version of the opened sample before trying its stale ETag.");
  const before = await validate(loadedText!);
  await validate(originalFileText);
  const result = await extensions.resources.write(openedUri, { text: originalFileText, ifMatch: originalEtag });
  if (result.outcome !== "conflict") {
    unsafeWrites = true;
    observe("stale-save", { outcome: result.outcome, passed: false });
    show("Stale save was not rejected as a conflict. Conditional editing is unsafe or unproven on this host; further writes are disabled.", true);
    return;
  }
  const latest = await extensions.resources.read({ uri: openedUri });
  const content = latest.contents[0];
  if (latest.contents.length !== 1 || !content) throw new ProbeError("Unable to verify preservation after the conflict.");
  const after = await validate(resourceText(content));
  const passed = before.sha256 === after.sha256;
  if (!passed) unsafeWrites = true;
  observe("stale-save", { outcome: result.outcome, passed, preservedRevision: after.revision, sha256: after.sha256 });
  show(passed ? "Stale save rejected; the newer saved bytes are unchanged." : "Newer bytes were not preserved. Further writes are disabled.", !passed);
});
buttons.report.onclick = () => void run(async () => {
  await app.updateModelContext({ content: [{ type: "text", text: JSON.stringify(report) }] });
  show("Observed capability report shared with this chat. No project text was included.");
});

app.addEventListener("toolinput", ({ arguments: args }) => {
  if (!connected) { pendingFileInput = args; return; }
  void run(() => openInput(args));
});
app.ontoolresult = () => refresh();
app.addEventListener("hostcontextchanged", () => {
  const context = app.getHostContext();
  if (context?.theme) applyDocumentTheme(context.theme);
  if (context?.styles?.variables) applyHostStyleVariables(context.styles.variables);
  refresh();
});

try {
  await app.connect();
  connected = true;
  report.connected = true;
  observe("host-connected", {});
  show("Connected. Create a synthetic project or open its saved file.");
  if (pendingFileInput != null) await run(() => openInput(pendingFileInput));
} catch {
  report.connectionFailed = true;
  show("No MCP App host connection. This page cannot establish ChatGPT storage capabilities.", true);
  refresh();
}
