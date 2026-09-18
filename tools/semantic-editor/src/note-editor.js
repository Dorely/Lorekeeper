import {EditorState, NodeSelection, TextSelection} from "prosemirror-state";
import {EditorView} from "prosemirror-view";

export function replaceNoteContent(notes, noteId, content) {
    if (!notes.some(note => note.id === noteId)) throw new Error("This note no longer exists.");
    if (!content.length || content.some(block => !["paragraph", "listItem", "figure"].includes(block.type)
        || (block.content || []).some(inline => inline.type === "noteReference")))
        throw new Error("Notes support paragraphs, lists, Figures, and citations. Tables, headings, and nested notes belong in the manuscript body.");
    return notes.map(note => note.id === noteId ? {...note, content: structuredClone(content)} : note);
}

// This view edits the parent's document-owned note. It has no independent save
// queue or history: every change immediately enters the parent authoring journal.
export function createNoteEditor(root, parent, options) {
    let noteId = null, editor = null, panel = null, updateControls = () => {};
    const close = (restoreFocus = true) => {
        editor?.destroy(); panel?.remove(); editor = panel = null; noteId = null;
        updateControls = () => {};
        if (restoreFocus && !parent.isDestroyed) parent.focus();
    };
    const update = () => {
        if (!editor) return;
        const note = (parent.state.doc.attrs.notes || []).find(item => item.id === noteId);
        if (!note) { close(); return; }
        const incoming = options.toDocument(note.content);
        if (!editor.state.doc.eq(incoming)) {
            const position = Math.min(editor.state.selection.head, incoming.content.size);
            editor.updateState(EditorState.create({doc: incoming,
                selection: TextSelection.near(incoming.resolve(position)), plugins: editor.state.plugins}));
        }
        editor.setProps({editable: () => !options.readOnly()});
        panel.querySelector("h3").textContent = note.kind === "endnote" ? "Endnote" : "Footnote";
        panel.querySelector('[aria-label="Note kind"]').value = note.kind;
        updateControls();
        for (const control of panel.querySelectorAll("button, select, input"))
            control.disabled = control.dataset.noteClose ? false : options.readOnly();
    };
    const open = id => {
        const note = (parent.state.doc.attrs.notes || []).find(item => item.id === id);
        if (!note) return;
        close(false); noteId = id;
        let referencePosition = null;
        parent.state.doc.descendants((node, position) => {
            if (node.type.name === "note_reference" && node.attrs.noteId === id) referencePosition = position;
        });
        if (referencePosition !== null)
            parent.dispatch(parent.state.tr.setSelection(NodeSelection.create(parent.state.doc, referencePosition)));
        panel = document.createElement("section"); panel.className = "semantic-note-editor";
        panel.setAttribute("role", "region"); panel.setAttribute("aria-label", "Edit manuscript note");
        const heading = document.createElement("h3"); heading.textContent = note.kind === "endnote" ? "Endnote" : "Footnote";
        const help = document.createElement("p"); help.textContent = "Changes save with the manuscript. Undo and Redo use the same history. Escape returns to the reference.";
        const toolbar = document.createElement("div"); toolbar.className = "semantic-note-toolbar";
        toolbar.setAttribute("role", "toolbar"); toolbar.setAttribute("aria-label", "Note formatting");
        const surface = document.createElement("div");
        const kind = document.createElement("select"); kind.setAttribute("aria-label", "Note kind");
        for (const [value, label] of [["footnote", "Footnote"], ["endnote", "Endnote"]]) {
            const option = document.createElement("option"); option.value = value; option.textContent = label; kind.append(option);
        }
        kind.value = note.kind;
        kind.addEventListener("change", () => {
            if (options.readOnly()) return;
            options.boundary();
            const notes = parent.state.doc.attrs.notes.map(item => item.id === noteId ? {...item, kind: kind.value} : item);
            let transaction = parent.state.tr.setDocAttribute("notes", notes);
            parent.state.doc.descendants((node, position) => {
                if (node.type.name === "note_reference" && node.attrs.noteId === noteId)
                    transaction = transaction.setNodeMarkup(position, undefined, {...node.attrs, kind: kind.value});
            });
            parent.dispatch(transaction);
        });
        const done = document.createElement("button"); done.type = "button"; done.textContent = "Return to manuscript";
        done.dataset.noteClose = "true"; done.addEventListener("click", () => close());
        panel.append(heading, help, kind, toolbar, surface, done); root.append(panel);
        editor = new EditorView(surface, {
            state: EditorState.create({doc: options.toDocument(note.content), plugins: options.plugins()}),
            editable: () => !options.readOnly(),
            dispatchTransaction(transaction) {
                if (transaction.docChanged && options.readOnly()) return;
                try {
                    const next = editor.state.apply(transaction);
                    const notes = transaction.docChanged
                        ? replaceNoteContent(parent.state.doc.attrs.notes || [], noteId, options.fromDocument(next.doc)) : null;
                    editor.updateState(next);
                    updateControls();
                    if (notes) parent.dispatch(parent.state.tr.setDocAttribute("notes", notes));
                } catch (error) { options.notice(error.message); }
            },
            handlePaste: (view, event) => options.handlePaste?.(view, event) ?? false,
            handleDOMEvents: {click: (view, event) => options.click(view, event)},
            transformPasted: slice => options.transformPasted(slice),
            attributes: {class: "semantic-prosemirror", role: "textbox", "aria-label": "Note text editor", "aria-multiline": "true", spellcheck: "true"}
        });
        const controls = options.controls(editor);
        updateControls = controls.update;
        toolbar.append(...controls.elements);
        toolbar.addEventListener("pointerdown", event => {
            if (!event.target.closest("[data-history-direction]")) options.boundary();
        }, true);
        panel.addEventListener("keydown", event => {
            if (event.key === "Escape" && !event.defaultPrevented) { event.preventDefault(); event.stopPropagation(); close(); }
        });
        update(); editor.focus();
    };
    return {open, update, close, get view() { return editor; }, dispose: () => close(false)};
}
