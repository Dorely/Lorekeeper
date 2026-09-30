import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import { ProjectError } from "./project.mjs";
import manifest from "../plugin.json" with { type: "json" };

// Codex owns authentication. This client never opens/copies its credential files.
export class CodexAppServer {
  constructor() {
    this.nextId = 1;
    this.pending = new Map();
    this.onnotification = () => {};
    this.onrequest = async () => { throw new Error("Unsupported server request"); };
    this.onclosed = () => {};
  }
  async connect() {
    if (this.connecting) return this.connecting;
    this.connecting = this.start().catch(async error => { await this.close(); this.connecting = undefined; throw error; });
    return this.connecting;
  }
  async start() {
    const command = process.env.LOREKEEPER_CODEX_PATH ?? (process.platform === "win32" ? "codex.exe" : "codex");
    const child = spawn(command, ["app-server", "--listen", "stdio://"], {
      stdio: ["pipe", "pipe", "pipe"], windowsHide: true, cwd: this.cwd
    });
    this.child = child;
    child.stderr.resume(); // Never forward auth/provider diagnostics or content into MCP stdout/logs.
    child.stdin.on("error", () => {});
    const closed = () => {
      if (this.child !== child) return;
      this.child = undefined;
      this.connecting = undefined;
      for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(new ProjectError("CODEX_DISCONNECTED", "The local Codex connection closed. Partial output is preserved; no message is resent automatically.")); }
      this.pending.clear();
      this.onclosed();
    };
    child.once("error", closed);
    child.once("exit", closed);
    this.reader = createInterface({ input: child.stdout, crlfDelay: Infinity });
    this.reader.on("line", line => {
      if (line.length > 8 * 1024 * 1024) { void this.close(); return; }
      let message;
      try { message = JSON.parse(line); } catch { void this.close(); return; }
      if (message.method && message.id != null) {
        void this.onrequest(message.method, message.params).then(result => {
          if (this.child === child) this.write({ id: message.id, result });
        }, () => {
          if (this.child === child) this.write({ id: message.id, error: { code: -32601, message: "This client permits only its project-scoped Lorekeeper tools." } });
        }).catch(() => { if (this.child === child) void this.close(); });
      } else if (message.id != null) {
        const entry = this.pending.get(message.id);
        if (!entry) return;
        clearTimeout(entry.timer); this.pending.delete(message.id);
        if (message.error) {
          const invalidRequest = [-32600, -32601, -32602].includes(message.error.code);
          entry.reject(new ProjectError("CODEX_REQUEST_FAILED", `Codex rejected ${entry.method} (code ${message.error.code}). ${invalidRequest ? "The plugin request is incompatible with this Codex protocol. Refresh Lorekeeper and check CLI compatibility." : "Check sign-in, account access, and CLI compatibility."} An uncertain turn is not resent.`));
        }
        else entry.resolve(message.result);
      } else if (message.method) this.onnotification(message.method, message.params ?? {});
    });
    const initialized = await this.request("initialize", {
      clientInfo: { name: "lorekeeper_local", title: "Lorekeeper Local", version: manifest.version },
      capabilities: { experimentalApi: true }
    });
    this.write({ method: "initialized", params: {} });
    this.version = initialized.serverInfo?.version ?? initialized.userAgent ?? "Codex app-server";
    return this;
  }
  write(message) {
    if (!this.child || this.child.stdin.destroyed) throw new ProjectError("CODEX_UNAVAILABLE", "Install Codex CLI and sign in, then connect again. LOREKEEPER_CODEX_PATH can select an absolute executable path.");
    this.child.stdin.write(JSON.stringify(message) + "\n");
  }
  request(method, params = {}) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new ProjectError("CODEX_TIMEOUT", `Codex did not confirm ${method}. Inspect the conversation before retrying; nothing is resent automatically.`));
      }, 45000);
      this.pending.set(id, { method, resolve, reject, timer });
      try { this.write({ id, method, params }); }
      catch (error) { clearTimeout(timer); this.pending.delete(id); reject(error); }
    });
  }
  async close() {
    const child = this.child;
    if (!child || child.exitCode !== null) return;
    await new Promise(resolve => {
      const timer = setTimeout(() => { child.kill(); }, 5000);
      child.once("exit", () => { clearTimeout(timer); resolve(); });
      child.stdin.end();
    });
    this.reader?.close();
  }
}
