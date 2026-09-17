const databaseName = "LorekeeperAuthoringJournalV1";
const storeName = "batches";
const keyPrefix = "canvas:";
const maxPayloadBytes = 2 * 1024 * 1024;

function key(projectId, targetId) {
    return `${keyPrefix}${projectId}:${targetId}`;
}

function sessionKey(projectId, targetId) {
    return `${databaseName}:canvas-session:${projectId}:${targetId}`;
}

async function database() {
    return await new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        request.onupgradeneeded = () => {
            const db = request.result;
            const store = db.objectStoreNames.contains(storeName)
                ? request.transaction.objectStore(storeName)
                : db.createObjectStore(storeName, { keyPath: "key" });
            if (!store.indexNames.contains("byTarget"))
                store.createIndex("byTarget", ["projectId", "targetId", "sequence"]);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error || new Error("The local authoring journal could not open."));
    });
}

async function transaction(mode, callback) {
    const db = await database();
    return await new Promise((resolve, reject) => {
        const tx = db.transaction(storeName, mode);
        let result;
        try { result = callback(tx.objectStore(storeName)); } catch (error) { reject(error); return; }
        tx.oncomplete = () => resolve(result);
        tx.onerror = () => reject(tx.error || new Error("The local authoring journal write failed."));
        tx.onabort = () => reject(tx.error || new Error("The local authoring journal write was aborted."));
    });
}

export function sessionId(projectId, targetId) {
    const storageKey = sessionKey(projectId, targetId);
    let value = localStorage.getItem(storageKey);
    if (!value) {
        value = crypto.randomUUID();
        localStorage.setItem(storageKey, value);
    }
    return value;
}

export async function read(projectId, targetId) {
    const entryKey = key(projectId, targetId);
    const db = await database();
    return await new Promise((resolve, reject) => {
        const request = db.transaction(storeName, "readonly").objectStore(storeName).get(entryKey);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error || new Error("The local canvas recovery journal could not be read."));
    });
}

export async function write(projectId, targetId, token, payload) {
    const serialized = JSON.stringify(payload);
    if (new TextEncoder().encode(serialized).length > maxPayloadBytes)
        throw new Error("The local canvas recovery payload exceeds its 2 MiB safety limit.");
    await transaction("readwrite", store => store.put({
        key: key(projectId, targetId), projectId, targetId, sequence: 0,
        token, payload, createdAt: Date.now(), kind: "canvas-pending"
    }));
    return true;
}

export async function remove(projectId, targetId, token) {
    const entryKey = key(projectId, targetId);
    const db = await database();
    return await new Promise((resolve, reject) => {
        const tx = db.transaction(storeName, "readwrite");
        const store = tx.objectStore(storeName);
        let removed = false;
        const request = store.get(entryKey);
        request.onsuccess = () => {
            const entry = request.result;
            if (entry && entry.token === token) {
                store.delete(entryKey);
                removed = true;
            }
        };
        request.onerror = () => reject(request.error || new Error("The local canvas recovery acknowledgement could not be read."));
        tx.oncomplete = () => resolve(removed);
        tx.onerror = () => reject(tx.error || new Error("The local canvas recovery acknowledgement failed."));
        tx.onabort = () => reject(tx.error || new Error("The local canvas recovery acknowledgement was aborted."));
    });
}
