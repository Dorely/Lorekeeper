import { createHash, randomUUID } from "node:crypto";
import { createServer } from "node:http";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, isAbsolute, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { getDefaultEnvironment, StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const projects = process.env.LOREKEEPER_DEV_PROJECTS_DIR ?? resolve(root, "../.artifacts/plugin-development/projects");
if (!isAbsolute(projects)) throw new Error("LOREKEEPER_DEV_PROJECTS_DIR must be absolute.");
await mkdir(projects, { recursive: true });
const runtimeRoot = resolve(root, "../.artifacts/plugin-development/runtimes");
await mkdir(runtimeRoot, { recursive: true });
const token = randomUUID();
let runtime;
let generation = 0;
let active = 0;
let restarting = false;
let origin;

async function startRuntime() {
  // The running process must never lock the repository's next build on Windows.
  const directory = await mkdtemp(resolve(runtimeRoot, "runtime-"));
  const client = new Client({ name: "lorekeeper-development", version: "1.0.0" }, { capabilities: {} });
  const transport = new StdioClientTransport({
    command: process.execPath, args: ["./server.mjs"], cwd: directory, stderr: "ignore",
    env: { ...getDefaultEnvironment(), LOREKEEPER_PROJECTS_DIR: projects }
  });
  try {
    const hashes = {};
    for (const file of ["server.mjs", "workspace.html", "storage-probe.html"]) {
      const bytes = await readFile(resolve(root, "dist", file));
      await writeFile(resolve(directory, file), bytes);
      hashes[file] = createHash("sha256").update(bytes).digest("hex");
    }
    await client.connect(transport);
    const { tools } = await client.listTools();
    const { resources } = await client.listResources();
    return {
      client, directory, tools, resources, pid: transport.pid, generation: ++generation, version: client.getServerVersion()?.version,
      serverHash: hashes["server.mjs"], editorHash: hashes["workspace.html"]
    };
  } catch (error) { await closeRuntime({ client, directory }); throw error; }
}
async function closeRuntime(current) {
  if (!current) return;
  await current.client.close();
  // Only an owned mkdtemp directory immediately below runtimeRoot may be removed.
  if (dirname(current.directory) !== runtimeRoot || !current.directory.startsWith(resolve(runtimeRoot, "runtime-"))) throw new Error("Unexpected development runtime directory.");
  await rm(current.directory, { recursive: true, force: true, maxRetries: 4, retryDelay: 100 });
}
function publicState() {
  if (!runtime) throw new Error("The MCP server is unavailable. Rebuild and reload the runtime.");
  const { generation, version, pid, serverHash, editorHash, tools } = runtime;
  return { generation, version, pid, serverHash, editorHash, tools, projects };
}
async function operation(action) {
  if (restarting || !runtime) throw new Error("The MCP server is reloading or unavailable. Retry after reloading.");
  active++;
  try { return await action(runtime); } finally { active--; }
}
async function restart() {
  if (restarting || active) throw new Error("A tool operation is still running. Reload when it finishes.");
  restarting = true;
  try {
    const previous = runtime;
    runtime = undefined;
    await closeRuntime(previous);
    runtime = await startRuntime();
    return publicState();
  } finally { restarting = false; }
}
async function requestBody(request) {
  let size = 0;
  const chunks = [];
  for await (const chunk of request) {
    size += chunk.length;
    if (size > 8 * 1024 * 1024) throw new Error("Request exceeds the development host's 8 MiB limit.");
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}
const hostScript = await build({
  absWorkingDir: root, entryPoints: ["scripts/preview.ts"], bundle: true, write: false,
  platform: "browser", target: "es2022", format: "esm", minify: true, legalComments: "none"
});
const template = await readFile(resolve(root, "scripts/preview.html"), "utf8");
const page = template.replace("/*PREVIEW_SCRIPT*/", () =>
  `const sessionToken = ${JSON.stringify(token)};\n` + hostScript.outputFiles[0].text.replace(/<\/script/gi, "<\\/script"));
runtime = await startRuntime();
const http = createServer(async (request, response) => {
  response.setHeader("Cache-Control", "no-store");
  response.setHeader("X-Content-Type-Options", "nosniff");
  response.setHeader("Content-Security-Policy", "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
  if (request.headers.host !== origin.slice("http://".length)) { response.writeHead(403).end(); return; }
  if (request.method === "GET" && request.url === "/") {
    response.setHeader("Content-Type", "text/html; charset=utf-8"); response.end(page); return;
  }
  if (request.method !== "POST" || request.headers.origin !== origin || request.headers["x-lorekeeper-session"] !== token || request.headers["content-type"] !== "application/json") {
    response.writeHead(403).end(); return;
  }
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  try {
    const args = await requestBody(request);
    let result;
    if (request.url === "/api/state") result = publicState();
    else if (request.url === "/api/restart") result = await restart();
    else if (request.url === "/api/tool") result = await operation(async current => {
      const tool = current.tools.find(tool => tool.name === args.name);
      const audience = args.audience === "app" ? "app" : args.audience === "host" ? "model" : undefined;
      if (!tool || !audience || !(tool._meta?.ui?.visibility ?? ["model", "app"]).includes(audience)) throw new Error("This tool is unavailable to the requested audience.");
      return await current.client.callTool({ name: args.name, arguments: args.arguments ?? {} });
    });
    else if (request.url === "/api/resource") result = await operation(async current => {
      if (!current.resources.some(resource => resource.uri === args.uri)) throw new Error("Unknown UI resource.");
      return await current.client.readResource({ uri: args.uri });
    });
    else { response.writeHead(404).end(); return; }
    response.end(JSON.stringify(result));
  } catch {
    // Protocol results contain safe application errors; raw host exceptions may contain content.
    response.writeHead(503).end(JSON.stringify({ error: "Development operation failed or the runtime is busy. Verify the build, wait for pending operations, then retry. No failed write is replayed automatically." }));
  }
});
http.on("clientError", (_error, socket) => socket.destroy());
try {
  await new Promise((resolve, reject) => {
    http.once("error", reject);
    http.listen(0, "127.0.0.1", resolve);
  });
} catch (error) { await closeRuntime(runtime); throw error; }
origin = `http://127.0.0.1:${http.address().port}`;
console.log(`Lorekeeper development preview: ${origin}/`);
console.log(`Synthetic project folder: ${projects}`);
console.log("Rebuild the plugin, then use Reload runtime. Ctrl+C stops this host and its owned MCP process.");
let stopping = false;
async function stop() {
  if (stopping) return;
  stopping = true;
  restarting = true;
  http.close();
  // Let an already-started write finish before closing the owned stdio transport.
  while (active) await new Promise(resolve => setTimeout(resolve, 50));
  await closeRuntime(runtime);
  http.closeAllConnections();
}
process.once("SIGINT", () => { void stop(); });
process.once("SIGTERM", () => { void stop(); });
