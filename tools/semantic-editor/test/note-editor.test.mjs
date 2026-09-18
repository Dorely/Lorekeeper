import test from "node:test";
import assert from "node:assert/strict";
import {replaceNoteContent} from "../src/note-editor.js";
import {authoringOperations} from "../src/semantic-editor.js";
import {findImportPosition, insertSemanticFragment} from "../src/semantic-import.js";

test("rich note edits preserve ownership and citations and produce a bounded inline delta", () => {
    const citation = {id: "citation", type: "citation", text: "", marks: [], citation: {items: [{bibliographicRecordId: "record"}]}};
    const before = {schemaVersion: 7, manuscriptId: "manuscript", revision: 3,
        content: [{id: "body", type: "paragraph", content: [{id: "reference", type: "noteReference", text: "", noteId: "note", marks: []}]}],
        notes: [{id: "note", kind: "footnote", content: [{id: "paragraph", type: "paragraph", content: [{type: "text", text: "Before", marks: []}, citation]}]},
            {id: "other", kind: "endnote", content: [{id: "other-paragraph", type: "paragraph", content: []}]}]};
    const content = structuredClone(before.notes[0].content);
    content[0].content[0].text = "After";
    const after = {...before, notes: replaceNoteContent(before.notes, "note", content)};
    assert.deepEqual(after.content, before.content);
    assert.deepEqual(after.notes[1], before.notes[1]);
    assert.deepEqual(after.notes[0].content[0].content[1], citation);
    assert.equal(before.notes[0].content[0].content[0].text, "Before");
    const operations = authoringOperations(before, after);
    assert.equal(operations.length, 1);
    assert.equal(operations[0].kind, "replaceInlineContent");
    assert.deepEqual(operations[0].position.containerPath, ["notes", "note"]);
    assert.throws(() => replaceNoteContent(before.notes, "missing", content), /no longer exists/);
    assert.throws(() => replaceNoteContent(before.notes, "note", [{id: "bad", type: "table"}]), /Notes support/);
    assert.throws(() => replaceNoteContent(before.notes, "note", before.content), /nested notes/);
});

test("Word insertion inside a note retains its identity and rejects nested ownership before mutation", () => {
    const source = {manuscriptId: "document", content: [], notes: [{id: "note", kind: "endnote",
        content: [{id: "paragraph", type: "paragraph", content: [{type: "text", text: "AB"}]}]}]};
    const position = findImportPosition(source, "paragraph", 1);
    assert.deepEqual(position.containerPath, ["notes", "note"]);
    const fragment = {content: [{id: "new", type: "listItem", content: [{type: "text", text: "Imported"}], list: {ordered: true, level: 0}}], notes: []};
    const inserted = insertSemanticFragment(source, position, fragment, "tail");
    assert.equal(inserted.notes[0].id, "note");
    assert.deepEqual(inserted.notes[0].content.map(block => block.id), ["paragraph", "new", "tail"]);
    assert.throws(() => insertSemanticFragment(source, position, {...fragment, notes: [{id: "nested"}]}, "tail"), /Notes support/);
    assert.equal(source.notes[0].content.length, 1);
});
