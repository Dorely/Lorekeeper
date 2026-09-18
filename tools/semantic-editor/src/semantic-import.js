export function findImportPosition(document, blockId, offset) {
    const find = (blocks, path) => {
        for (const block of blocks) {
            if (block.id === blockId) {
                let text = 0, editor = 0;
                for (const inline of block.content || []) {
                    if (inline.type !== "text") {
                        if (offset === editor || offset === editor + 1) return {documentId: document.manuscriptId, containerPath: path,
                            blockOrAtomId: inline.id, offset: text, affinity: offset === editor ? "before" : "after"};
                        editor++;
                    } else {
                        if (offset >= editor && offset < editor + inline.text.length) return {documentId: document.manuscriptId, containerPath: path,
                            blockOrAtomId: blockId, offset: text + offset - editor, affinity: "after"};
                        editor += inline.text.length; text += inline.text.length;
                    }
                }
                return {documentId: document.manuscriptId, containerPath: path, blockOrAtomId: blockId, offset: text, affinity: "after"};
            }
            for (const row of block.table?.rows || []) for (const cell of row.cells || []) {
                const nested = find(cell.content, [...path, `table:${block.table.id}`, `row:${row.id}`, `cell:${cell.id}`]);
                if (nested) return nested;
            }
        }
        return null;
    };
    return find(document.content, ["document"])
        || (document.notes || []).map(note => find(note.content, ["notes", note.id])).find(Boolean) || null;
}

export function insertSemanticFragment(source, position, fragment, trailingBlockId) {
    if (source.manuscriptId !== position.documentId) throw new Error("The import position belongs to another manuscript.");
    if (position.containerPath.some(part => part.startsWith("cell:")) && fragment.content.some(block => !["paragraph", "listItem", "figure"].includes(block.type)))
        throw new Error("Table cells support paragraphs, lists, and Figures. Choose a paragraph outside the table for this Word content.");
    if (position.containerPath[0] === "notes" && ((fragment.notes || []).length
        || fragment.content.some(block => !["paragraph", "listItem", "figure"].includes(block.type))))
        throw new Error("Notes support paragraphs, lists, and Figures. Choose a body paragraph for this Word content.");
    const document = structuredClone(source);
    let found = false;
    const rewrite = (blocks, path) => blocks.flatMap(block => {
        if ((block.id === position.blockOrAtomId || block.content?.some(inline => inline.id === position.blockOrAtomId))
            && JSON.stringify(path) === JSON.stringify(position.containerPath)) {
            if (found || ["figure", "sceneBreak", "designedPage", "table"].includes(block.type))
                throw new Error("Choose a text paragraph as the import insertion point.");
            found = true;
            let canonical = 0, editor = 0, offset = null;
            for (const inline of block.content || []) {
                if (inline.type !== "text") {
                    if (inline.id === position.blockOrAtomId && canonical === position.offset) { offset = editor + (position.affinity === "after" ? 1 : 0); break; }
                    if (block.id === position.blockOrAtomId && canonical === position.offset && position.affinity === "before") { offset = editor; break; }
                    editor++;
                } else {
                    if (block.id === position.blockOrAtomId && position.offset >= canonical && position.offset < canonical + inline.text.length)
                        { offset = editor + position.offset - canonical; break; }
                    editor += inline.text.length; canonical += inline.text.length;
                }
            }
            if (offset === null && block.id === position.blockOrAtomId && canonical === position.offset) offset = editor;
            const text = (block.content || []).map(inline => inline.type === "text" ? inline.text : "\ufffc").join("");
            if (!Number.isInteger(offset) || offset < 0 || offset > text.length
                || offset > 0 && offset < text.length
                    && /[\uD800-\uDBFF]/.test(text[offset - 1]) && /[\uDC00-\uDFFF]/.test(text[offset]))
                throw new Error("The import position is outside the paragraph or splits a Unicode character.");
            const slice = (start, end) => {
                let offset = 0;
                return (block.content || []).flatMap(inline => {
                    const size = inline.type === "text" ? inline.text.length : 1;
                    const from = Math.max(start, offset), to = Math.min(end, offset + size);
                    const result = from < to ? [{...inline, ...(inline.type === "text" ? {text: inline.text.slice(from - offset, to - offset)} : {})}] : [];
                    offset += size; return result;
                });
            };
            const before = slice(0, offset), after = slice(offset, text.length);
            return [...(before.length ? [{...block, content: before}] : []), ...structuredClone(fragment.content),
                ...(after.length ? [{...block, id: trailingBlockId, content: after}] : [])];
        }
        if (block.table) for (const row of block.table.rows) for (const cell of row.cells)
            cell.content = rewrite(cell.content, [...path, `table:${block.table.id}`, `row:${row.id}`, `cell:${cell.id}`]);
        return [block];
    });
    document.content = rewrite(document.content, ["document"]);
    document.notes = (document.notes || []).map(note => ({...note, content: rewrite(note.content, ["notes", note.id])}));
    if (!found) throw new Error("The import position is no longer available.");
    document.notes.push(...structuredClone(fragment.notes || []));
    return document;
}

export function journalImportResources(resources) {
    const result = structuredClone(resources);
    for (const image of result.images) {
        if (typeof image.data === "string") continue;
        let binary = "";
        for (let index = 0; index < image.data.length; index += 8192)
            binary += String.fromCharCode(...image.data.slice(index, index + 8192));
        image.data = btoa(binary);
    }
    return result;
}
