import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerAppResource, registerAppTool, RESOURCE_MIME_TYPE } from "@modelcontextprotocol/ext-apps/server";
import { OpenAIExtensions } from "@openai/mcp-extensions/server";
import { z } from "zod";

const resourceUri = "ui://lorekeeper/storage-probe.html";
const projectId = "85d88cf9-79a9-4cb6-8fba-a541c707833b";
const chapterId = "291527bb-6c30-40cb-9dca-5cff8c6950fa";
const maximumBytes = 65536;
const projectSchema = z.object({
  schemaVersion: z.literal(1),
  projectId: z.literal(projectId),
  revision: z.number().int().min(1).max(1000),
  title: z.literal("Synthetic Storage Probe"),
  chapters: z.array(z.object({
    id: z.literal(chapterId),
    title: z.string().min(1).max(200),
    paragraphs: z.array(z.object({
      id: z.string().uuid(),
      text: z.string().max(10000)
    }).strict()).min(1).max(8)
  }).strict()).length(1)
}).strict();

const server = new McpServer({ name: "lorekeeper-storage-probe", version: "0.1.0" });
new OpenAIExtensions(server);

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
    "openai/ui": { entrypoints: [{ type: "global" }, { type: "thread" }, { type: "file", extensions: ["lkproject"] }] }
  }
}, async () => ({
  content: [{ type: "text", text: "Storage probe opened. Saving and reopening require the host file capabilities reported by the editor." }],
  structuredContent: { probeVersion: "0.1.0", maximumBytes }
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
