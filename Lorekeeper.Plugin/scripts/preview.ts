import { AppBridge, PostMessageTransport, getToolUiResourceUri } from "@modelcontextprotocol/ext-apps/app-bridge";
import type { CallToolResult, ReadResourceResult, Tool } from "@modelcontextprotocol/sdk/types.js";
import { ErrorCode, McpError } from "@modelcontextprotocol/sdk/types.js";

declare const sessionToken: string;
interface Runtime { generation: number; version: string; pid: number; serverHash: string; editorHash: string; projects: string; tools: Tool[] }
let bridge: AppBridge | undefined;
let frame: HTMLIFrameElement | undefined;
let runtime: Runtime;
let busy = false;
const button = (id: string) => document.querySelector<HTMLButtonElement>(`#${id}`)!;
const status = document.querySelector<HTMLElement>("#status")!;
const tools = document.querySelector<HTMLSelectElement>("#tool")!;
const result = document.querySelector<HTMLElement>("#result")!;
const confirmation = document.querySelector<HTMLElement>("#reload-confirmation")!;

async function api<T>(path: string, args: unknown = {}): Promise<T> {
  const response = await fetch(`/api/${path}`, {
    method: "POST", headers: { "Content-Type": "application/json", "X-Lorekeeper-Session": sessionToken },
    body: JSON.stringify(args), credentials: "omit", redirect: "error"
  });
  if (!response.ok) throw new Error("Development operation failed or is busy. Verify the build and retry after pending calls finish. An uncertain write must be inspected before retrying.");
  return await response.json() as T;
}
async function run(action: () => Promise<void>): Promise<void> {
  if (busy) return;
  busy = true; button("reload").disabled = true; button("call").disabled = true; button("reload-now").disabled = true;
  try { await action(); }
  catch (error) { status.textContent = error instanceof Error ? error.message : "Development operation failed."; }
  finally { busy = false; button("reload").disabled = false; button("call").disabled = false; button("reload-now").disabled = false; }
}
function identify(): void {
  document.querySelector<HTMLElement>("#identity")!.textContent = `Server ${runtime.version} · generation ${runtime.generation} · PID ${runtime.pid} · server ${runtime.serverHash.slice(0, 12)} · editor ${runtime.editorHash.slice(0, 12)}`;
  document.querySelector<HTMLElement>("#projects")!.textContent = `Project folder: ${runtime.projects}`;
  tools.replaceChildren(...runtime.tools.filter(tool => !getToolUiResourceUri(tool) && ((tool._meta?.ui as { visibility?: string[] } | undefined)?.visibility?.includes("model") ?? true)).map(tool => {
    const option = document.createElement("option"); option.value = tool.name; option.textContent = tool.title ?? tool.name; return option;
  }));
  tools.value = "list_lorekeeper_projects";
}
async function mount(tool: Tool, args: Record<string, unknown>, toolResult: CallToolResult): Promise<void> {
  const uri = getToolUiResourceUri(tool);
  if (!uri) return;
  const resource = await api<ReadResourceResult>("resource", { uri });
  const html = resource.contents.find(content => "text" in content)?.text;
  if (typeof html !== "string") throw new Error("The MCP UI resource did not contain HTML.");
  if (bridge) {
    try { await bridge.teardownResource({}, { timeout: 3000 }); }
    catch (error) {
      // These editors do not implement the optional teardown method. Discard was explicitly confirmed.
      if (!(error instanceof McpError) || error.code !== ErrorCode.MethodNotFound) throw new Error("The editor could not finish closing. Preserve its draft before retrying reload.");
    }
    await bridge.close();
  }
  frame?.remove();
  const current = document.createElement("iframe");
  current.title = tool.title ?? "Lorekeeper editor";
  // An opaque iframe cannot access the parent's authenticated loopback API.
  current.setAttribute("sandbox", "allow-scripts");
  document.querySelector("#editor")!.append(current);
  frame = current;
  const next = new AppBridge(null, { name: "Lorekeeper development preview", version: "1.0.0" }, { serverTools: {} }, {
    hostContext: { theme: "dark", displayMode: "fullscreen", availableDisplayModes: ["fullscreen"], platform: "web" }
  });
  next.oncalltool = async params => await api<CallToolResult>("tool", { ...params, audience: "app" });
  next.onrequestdisplaymode = async () => ({ mode: "fullscreen" });
  next.oninitialized = () => {
    status.textContent = `Editor ${next.getAppVersion()?.version ?? "unknown"} connected through the real MCP Apps bridge. Generation ${runtime.generation}.`;
    void next.sendToolInput({ arguments: args }).then(() => next.sendToolResult(toolResult)).catch(() => {
      status.textContent = "Editor initialized, but its opening result could not be delivered. Reload when ready.";
    });
  };
  bridge = next;
  await next.connect(new PostMessageTransport(current.contentWindow!, current.contentWindow!));
  const policy = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'none'; base-uri 'none'; form-action 'none'">`;
  current.srcdoc = html.replace(/<head>/i, match => match + policy);
}
async function openWorkspace(): Promise<void> {
  const tool = runtime.tools.find(tool => tool.name === "open_lorekeeper_workspace");
  if (!tool) throw new Error("The server did not register the workspace opener.");
  const opening = await api<CallToolResult>("tool", { name: tool.name, arguments: {}, audience: "host" });
  await mount(tool, {}, opening);
}
button("reload").onclick = () => { confirmation.hidden = false; };
button("reload-cancel").onclick = () => { confirmation.hidden = true; };
button("reload-now").onclick = () => { void run(async () => {
  runtime = await api<Runtime>("restart");
  identify();
  await openWorkspace();
  confirmation.hidden = true;
}); };
button("call").onclick = () => { void run(async () => {
  const args: unknown = JSON.parse(document.querySelector<HTMLTextAreaElement>("#arguments")!.value);
  if (!args || typeof args !== "object" || Array.isArray(args)) throw new Error("Tool arguments must be a JSON object.");
  const tool = runtime.tools.find(tool => tool.name === tools.value)!;
  const response = await api<CallToolResult>("tool", { name: tool.name, arguments: args, audience: "host" });
  result.textContent = JSON.stringify(response.structuredContent ?? response.content, null, 2);
  status.textContent = response.isError ? "Tool returned an application error. Inspect its result before retrying." : "Tool completed. The existing editor observes saved changes independently.";
}); };
await run(async () => { runtime = await api<Runtime>("state"); identify(); await openWorkspace(); });
