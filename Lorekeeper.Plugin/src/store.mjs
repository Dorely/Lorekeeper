import { createHash, randomUUID } from "node:crypto";
import { constants } from "node:fs";
import { lstat, mkdir, open, readdir, realpath, rename, unlink } from "node:fs/promises";
import { homedir } from "node:os";
import { isAbsolute, join, resolve } from "node:path";
import { encodeProject, maximumProjectBytes, ProjectError, projectSummary, validateProject } from "./project.mjs";

const digest = bytes => createHash("sha256").update(bytes).digest("hex");
export class LocalDocumentStore {
  constructor({ root, filename, validate, encode, maximumBytes }) {
    if (!isAbsolute(root)) throw new ProjectError("INVALID_FOLDER", "LOREKEEPER_PROJECTS_DIR must be an absolute local folder.");
    this.root = resolve(root);
    this.filename = filename;
    this.validate = validate;
    this.encode = encode;
    this.maximumBytes = maximumBytes;
  }
  async directory(create = false) {
    if (create) await mkdir(this.root, { recursive: true });
    return realpath(this.root);
  }
  async readAt(root, filename) {
    const path = join(root, this.filename(filename));
    return this.readPath(path, filename);
  }
  async readPath(path, filename) {
    const stat = await lstat(path);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1) throw new ProjectError("UNSAFE_FILE", "Linked or non-regular project files are not opened.");
    const handle = await open(path, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0));
    try {
      const opened = await handle.stat();
      if (!opened.isFile() || opened.nlink !== 1 || opened.size > this.maximumBytes) throw new ProjectError("INVALID_PROJECT", "Document must be a regular UTF-8 file within its size limit.");
      const bytes = Buffer.alloc(this.maximumBytes + 1);
      let count = 0;
      while (count < bytes.length) {
        const part = await handle.read(bytes, count, bytes.length - count, null);
        if (!part.bytesRead) break;
        count += part.bytesRead;
      }
      if (count > this.maximumBytes) throw new ProjectError("PROJECT_TOO_LARGE", "Document exceeds its local storage limit.");
      const content = bytes.subarray(0, count);
      let project;
      try { project = this.validate(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(content))); }
      catch (error) { if (error instanceof ProjectError) throw error; throw new ProjectError("INVALID_PROJECT", "This file is not valid UTF-8 project JSON. It was not changed."); }
      return { fileName: filename, project, etag: digest(content), bytes: content };
    } finally { await handle.close(); }
  }
  async read(filename) { return this.readAt(await this.directory(), filename); }
  async create(filename, project) {
    const root = await this.directory(true);
    this.filename(filename);
    return this.withLock(root, filename, async () => {
      try { await lstat(join(root, filename)); throw new ProjectError("FILE_EXISTS", "A file with that name already exists. Choose another name."); }
      catch (error) { if (error.code !== "ENOENT") throw error; }
      const content = this.encode(project);
      // Exclusive creation never replaces an existing user file.
      const handle = await open(join(root, filename), "wx", 0o600);
      try { await handle.writeFile(content, "utf8"); await handle.sync(); }
      catch (error) { await handle.close(); await unlink(join(root, filename)); throw error; }
      await handle.close();
      return this.readAt(root, filename);
    });
  }
  async withLock(root, filename, action) {
    const lockPath = join(root, `.${this.filename(filename)}.lock`);
    let lock;
    try { lock = await open(lockPath, "wx", 0o600); }
    catch (error) { if (error.code === "EEXIST") throw new ProjectError("PROJECT_BUSY", "Another writer holds this project. Retry after it finishes. A crash lock requires manual recovery; it is never removed automatically."); throw error; }
    try { return await action(); } finally { await lock.close(); await unlink(lockPath); }
  }
  async update(filename, expectedEtag, transform) {
    const root = await this.directory();
    return this.withLock(root, filename, () => this.updateAt(root, filename, expectedEtag, transform));
  }
  // A conversation owns one lease for its whole turn. Project writes use separate files/locks.
  async withUpdateLease(filename, action) {
    const root = await this.directory();
    return this.withLock(root, filename, () => action({
      read: () => this.readAt(root, filename),
      update: (etag, transform) => this.updateAt(root, filename, etag, transform)
    }));
  }
  async updateAt(root, filename, expectedEtag, transform) {
    const current = await this.readAt(root, filename);
    if (expectedEtag && current.etag !== expectedEtag) throw new ProjectError("CONFLICT", "The saved project changed. Your draft is preserved. Reload the saved project only after copying or saving your draft elsewhere.");
    const next = this.validate(await transform(structuredClone(current.project)));
    if (JSON.stringify(next) === JSON.stringify(current.project)) return current;
    next.revision = current.project.revision + 1;
    next.updatedAt = new Date().toISOString();
    const content = this.encode(next);
    const backupRoot = join(root, ".history");
    await mkdir(backupRoot, { recursive: true });
    if ((await realpath(backupRoot)) !== backupRoot || (await lstat(backupRoot)).isSymbolicLink()) throw new ProjectError("UNSAFE_FILE", "The history folder must be a regular directory inside the projects folder.");
    const backup = join(backupRoot, `${filename}.r${current.project.revision}-${current.etag}.json`);
    let backupHandle;
    try { backupHandle = await open(backup, "wx", 0o600); }
    catch (error) { if (error.code !== "EEXIST") throw error; }
    if (backupHandle) {
      try { await backupHandle.writeFile(current.bytes); await backupHandle.sync(); }
      catch (error) { await backupHandle.close(); await unlink(backup); throw error; }
      await backupHandle.close();
    } else if ((await this.readPath(backup, filename)).etag !== current.etag) {
      throw new ProjectError("INVALID_HISTORY", "The existing revision backup does not match this project. The saved project was not changed.");
    }
    const temporary = join(root, `.${filename}.${randomUUID()}.tmp`);
    try {
      const handle = await open(temporary, "wx", 0o600);
      try { await handle.writeFile(content, "utf8"); await handle.sync(); } finally { await handle.close(); }
      // Recheck immediately before replacement, including external editors.
      if ((await this.readAt(root, filename)).etag !== current.etag) throw new ProjectError("CONFLICT", "Another editor changed the saved file. The newer file is preserved.");
      await rename(temporary, join(root, filename));
    } finally { await unlink(temporary).catch(error => { if (error.code !== "ENOENT") throw error; }); }
    return this.readAt(root, filename);
  }
}

export function projectFilename(value) {
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}\.lorekeeper\.json$/.test(value) || /^(con|prn|aux|nul|com[1-9]|lpt[1-9])\./i.test(value)) {
    throw new ProjectError("INVALID_FILENAME", "Use a simple filename ending in .lorekeeper.json inside the projects folder.");
  }
  return value;
}
export class LocalProjectStore extends LocalDocumentStore {
  constructor(root = process.env.LOREKEEPER_PROJECTS_DIR ?? join(homedir(), "Documents", "Lorekeeper Projects")) {
    super({ root, filename: projectFilename, validate: validateProject, encode: encodeProject, maximumBytes: maximumProjectBytes });
  }
  async list() {
    let root;
    try { root = await this.directory(); } catch (error) { if (error.code === "ENOENT") return { folder: this.root, projects: [], unreadable: [], truncated: false }; throw error; }
    const entries = (await readdir(root)).filter(x => x.endsWith(".lorekeeper.json")).sort();
    const projects = [], unreadable = [];
    for (const name of entries.slice(0, 100)) {
      try { projects.push({ fileName: name, ...projectSummary((await this.readAt(root, name)).project) }); }
      catch { unreadable.push(name); }
    }
    return { folder: root, projects, unreadable, truncated: entries.length > 100 };
  }
}
