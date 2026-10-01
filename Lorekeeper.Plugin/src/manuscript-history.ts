interface Position { start: number; end: number }
interface Snapshot extends Position { text: string }
interface Edit { before: Snapshot; after: Snapshot; at: number; typing: boolean }
interface Journal { current: Snapshot; undo: Edit[]; redo: Edit[] }

// Undo belongs to the plain-text editor. External saved changes reset its local
// stack; durable cross-session restoration belongs to guarded project History.
export class ManuscriptHistory {
    private journals = new Map<string, Journal>();
    sync(key: string, text: string): void {
        if (this.journals.get(key)?.current.text === text) return;
        this.journals.delete(key);
        this.journals.set(key, { current: { text, start: 0, end: 0 }, undo: [], redo: [] });
        while (this.journals.size > 8) this.journals.delete(this.journals.keys().next().value!);
    }
    position(key: string, position: Position): void { const journal = this.journals.get(key); if (journal) Object.assign(journal.current, position); }
    record(key: string, text: string, position: Position, typing: boolean): void {
        const journal = this.journals.get(key)!;
        if (journal.current.text === text) return;
        const after = { text, ...position }, previous = journal.undo.at(-1), at = performance.now();
        if (typing && previous?.typing && at - previous.at < 500) { previous.after = after; previous.at = at; }
        else journal.undo.push({ before: { ...journal.current }, after, at, typing });
        journal.current = after; journal.redo = [];
        while (journal.undo.length > 40 || journal.undo.length > 1 && journal.undo.reduce((n, x) => n + x.before.text.length + x.after.text.length, 0) > 2000000) journal.undo.shift();
    }
    can(key: string, action: "undo" | "redo"): boolean { return Boolean(this.journals.get(key)?.[action].length); }
    apply(key: string, action: "undo" | "redo"): Snapshot | undefined {
        const journal = this.journals.get(key), edit = journal?.[action].pop();
        if (!journal || !edit) return;
        journal[action === "undo" ? "redo" : "undo"].push(edit);
        journal.current = { ...(action === "undo" ? edit.before : edit.after) };
        return journal.current;
    }
}
