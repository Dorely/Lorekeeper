import assert from "node:assert/strict";
import {readFile} from "node:fs/promises";
import test from "node:test";
import {Schema} from "prosemirror-model";
import {EditorState} from "prosemirror-state";
import {
    addAuthoringPreconditions,
    authoringOperations,
    citationItemsFromForm,
    authoringSequenceWatermarks,
    operationsForTarget,
    roundTripManuscriptJson,
    shouldApplyAuthoringTransaction
} from "../src/semantic-editor.js";

const block = (id, type = "paragraph", content = [{type: "text", text: "Text", marks: []}], extra = {}) => ({
    id, type, styleRole: type === "figure" ? "figure-caption" : "body", content, ...extra
});
const document = content => ({schemaVersion: 7, manuscriptId: "00000000-0000-0000-0000-000000000001", revision: 1, content, notes: []});

test("list metadata survives round trips and ordinary typing stays incremental", () => {
    const original = document([block("item", "listItem", [{type: "text", text: "First", marks: []}],
        {list: {id: "list-a", ordered: true, level: 2, start: 7}})]);
    const before = JSON.parse(roundTripManuscriptJson(JSON.stringify(original)));
    assert.deepEqual(before.content[0].list, original.content[0].list);
    const after = structuredClone(before);
    after.content[0].content[0].text += " edited";
    assert.deepEqual(authoringOperations(before, after).map(operation => operation.kind), ["replaceInlineContent"]);
    after.content[0].list.start = 10;
    const operations = authoringOperations(before, after);
    assert.equal(operations[0].kind, "replaceRichDocument");
    assert.equal(operations[0].richDocument.content[0].list.start, 10);
});

test("citation form serializes the manuscript contract and preserves evidence only for an unchanged source", () => {
    const original = [{bibliographicRecordId: "00000000-0000-0000-0000-000000000099", sourceLocationId: "00000000-0000-0000-0000-000000000098"}];
    const values = {record1: original[0].bibliographicRecordId, prefix1: " see ", suffix1: " ", locatorLabel1: "chapter", locatorValue1: " 2 "};
    const items = citationItemsFromForm(values, 1, original);
    const manuscript = document([block("p", "paragraph", [{id: "citation", type: "citation", text: "", marks: [], citation: {items}}])]);
    const saved = JSON.parse(roundTripManuscriptJson(JSON.stringify(manuscript))).content[0].content[0].citation.items[0];
    assert.equal(saved.bibliographicRecordId, original[0].bibliographicRecordId);
    assert.equal(saved.sourceLocationId, original[0].sourceLocationId);
    assert.equal(saved.prefix, "see");
    assert.equal(saved.locatorValue, "2");
    assert.equal(citationItemsFromForm({...values, record1: "replacement"}, 1, original)[0].sourceLocationId, null);
});

test("rich table and note atoms round-trip and save behind one exact document precondition", () => {
    const rich = document([
        block("p", "paragraph", [
            {type: "text", text: "Text", marks: []},
            {id: "ref", type: "noteReference", text: "", noteId: "note", marks: []}
        ]),
        block("table-block", "table", [], {
            styleRole: "table",
            table: {
                id: "table",
                columnWidthWeights: [1, 2],
                headerRowCount: 1,
                rows: [{id: "row", cells: [
                    {id: "cell-a", rowSpan: 1, columnSpan: 1, content: [block("cell-p-a", "paragraph", [{type: "text", text: "A", marks: []}])]},
                    {id: "cell-b", rowSpan: 1, columnSpan: 1, content: [block("cell-p-b", "paragraph", [{type: "text", text: "B", marks: []}])]}
                ]}]
            }
        })
    ]);
    rich.notes = [{id: "note", kind: "footnote", content: [block("note-p", "paragraph", [{type: "text", text: "Body", marks: []}])]}];

    const roundTripped = JSON.parse(roundTripManuscriptJson(JSON.stringify(rich)));
    assert.deepEqual(JSON.parse(roundTripManuscriptJson(JSON.stringify(roundTripped))), roundTripped);
    assert.equal(roundTripped.content[1].table.rows[0].cells[1].id, "cell-b");
    assert.equal(roundTripped.content[0].content[1].noteId, "note");
    assert.equal(roundTripped.notes[0].content[0].content[0].text, "Body");
    const before = document([block("p")]);
    const operations = authoringOperations(before, rich);
    assert.equal(operations.length, 1);
    assert.equal(operations[0].kind, "replaceRichDocument");
    addAuthoringPreconditions(operations, before, rich, new Map(), "sha256:document");
    assert.equal(operations[0].expectedDocumentFingerprint, "sha256:document");
});

test("citation atoms retain stable identity and structured cluster metadata", () => {
    const cited = document([block("p", "paragraph", [
        {type: "text", text: "Claim", marks: []},
        {id: "cite-1", type: "citation", text: "", noteId: null, marks: [], citation: {items: [{
            bibliographicRecordId: "00000000-0000-0000-0000-000000000099",
            prefix: "see", suffix: "", locatorLabel: "page", locatorValue: "42", sourceLocationId: null
        }]}}
    ])]);
    const roundTripped = JSON.parse(roundTripManuscriptJson(JSON.stringify(cited)));
    assert.equal(roundTripped.schemaVersion, 7);
    assert.deepEqual(roundTripped.content[0].content[1], cited.content[0].content[1]);
    const operations = authoringOperations(document([block("p")]), cited);
    assert.equal(operations[0].kind, "replaceInlineContent");
    assert.deepEqual(operations[0].position.containerPath, ["document"]);
    assert.equal(operations[0].inlineContent[1].citation.items[0].locatorValue, "42");
});

test("ordinary typing in table cells and notes sends only the changed inline content", () => {
    const before = document([block("table-block", "table", [], {table: {
        id: "table", columnWidthWeights: [1], headerRowCount: 0,
        rows: [{id: "row", cells: [{id: "cell", rowSpan: 1, columnSpan: 1, content: [block("cell-text")]}]}]
    }})]);
    before.notes = [{id: "note", kind: "footnote", content: [block("note-text")]}];
    const after = structuredClone(before);
    after.content[0].table.rows[0].cells[0].content[0].content[0].text = "Cell edit";
    after.notes[0].content[0].content[0].text = "Note edit";
    const operations = authoringOperations(before, after);
    assert.deepEqual(operations.map(operation => operation.kind), ["replaceInlineContent", "replaceInlineContent"]);
    assert.deepEqual(operations.map(operation => operation.position.containerPath), [
        ["document", "table:table", "row:row", "cell:cell"], ["notes", "note"]
    ]);
    addAuthoringPreconditions(operations, before, after, new Map([["cell-text", "cell-hash"], ["note-text", "note-hash"]]));
    assert.deepEqual(operations.map(operation => operation.expectedElementFingerprint), ["cell-hash", "note-hash"]);
    assert.ok(operations.every(operation => !operation.richDocument));
});

test("authoring delta preserves mark-only additions and removals through text replacement", () => {
    const before = document([block("p")]);
    const bold = document([block("p", "paragraph", [{type: "text", text: "Text", marks: [{type: "strong", value: null}]}])]);
    const link = document([block("p", "paragraph", [{type: "text", text: "Text", marks: [{type: "link", value: "https://example.test"}]}])]);

    for (const after of [bold, link]) {
        const operations = authoringOperations(before, after);
        assert.equal(operations[0].kind, "replaceBlockText");
        assert.equal(operations[1].kind, "setInlineMark");
        assert.equal(operations[1].enabled, true);
    }
    assert.deepEqual(authoringOperations(bold, before).map(operation => operation.kind), ["replaceBlockText"]);
});

test("authoring delta carries every Figure and Designed Page semantic field", () => {
    const beforeFigure = block("figure", "figure", [{type: "text", text: "Caption", marks: []}], {
        imageId: "00000000-0000-0000-0000-000000000011", altText: "old", decorative: false,
        language: "en", accessibilityRole: "figure", figurePresentation: {placement: "centered", widthPercent: 50}
    });
    const afterFigure = {...beforeFigure, imageId: "00000000-0000-0000-0000-000000000012", altText: null,
        decorative: true, language: "fr", accessibilityRole: "illustration",
        figurePresentation: {placement: "fullWidth", widthPercent: 100}};
    const figureOperation = authoringOperations(document([beforeFigure]), document([afterFigure]))
        .find(operation => operation.kind === "setFigurePresentation");
    assert.deepEqual(figureOperation, {
        kind: "setFigurePresentation", blockId: "figure", imageId: "00000000-0000-0000-0000-000000000012",
        altText: null, decorative: true, language: "fr", accessibilityRole: "illustration",
        figurePresentation: {placement: "fullWidth", widthPercent: 100}
    });

    const page = block("page", "designedPage", [], {styleRole: "designed-page", designedPageId: "00000000-0000-0000-0000-000000000021"});
    const inserted = authoringOperations(document([block("p")]), document([block("p"), page]))[0];
    assert.equal(inserted.kind, "insertBlock");
    assert.equal(inserted.designedPageId, page.designedPageId);
    assert.equal(inserted.styleRole, "designed-page");
});

test("inserts use stable base neighbours and compound history filters target ordinals", () => {
    const before = document([block("a"), block("b")]);
    const inserted = block("x");
    const after = document([block("a"), inserted, block("b")]);
    const operations = authoringOperations(before, after);
    addAuthoringPreconditions(operations, before, after, new Map([["a", "fa"], ["b", "fb"]]));
    assert.deepEqual(operations[0].expectedOrder, {
        previousBlockId: "a", previousBlockFingerprint: "fa", nextBlockId: "b", nextBlockFingerprint: "fb"
    });
    assert.deepEqual(operations[0].insertAt, {beforeBlockId: "b"});
    assert.equal(operations[0].expectedAnchorFingerprint, "fb");
    assert.deepEqual(operationsForTarget([{targetOrdinal: 0, kind: "deleteBlock"}, {targetOrdinal: 1, kind: "restoreBlock"}], 1),
        [{targetOrdinal: 1, kind: "restoreBlock"}]);
});

test("in-flight undo stays locally visible while its saved transition is reconciled", async () => {
    const source = await readFile(new URL("../src/semantic-editor.js", import.meta.url), "utf8");
    const start = source.indexOf("if (!visibleAlreadyApplied && savedGeneration < changeGeneration)");
    const end = source.indexOf("const nextCursor", start);
    const branch = source.slice(start, end);
    assert.ok(branch.indexOf("const dispatch = saveNow();") < branch.indexOf("applyAuthoringOperations(view, transition.inverse)"));
    assert.ok(branch.indexOf("applyAuthoringOperations(view, transition.inverse)") < branch.indexOf("await dispatch"));
    assert.match(branch, /performPersistentHistory\(false, true\)/);
    assert.match(source, /\|\| pendingVisibleHistoryMove/);
});

test("discarding a rejected batch resets the next sequence and fence watermarks", () => {
    // Batch 17 was rejected and rolled back. The server still expects 17;
    // using 18 would fail closed and leave a captured writer sequence unmet.
    assert.deepEqual(authoringSequenceWatermarks(17), {
        nextSequence: 17,
        highestLocalSequence: 16,
        lastDispatchedSequence: 16,
        lastAcknowledgedSequence: 16
    });
    assert.throws(() => authoringSequenceWatermarks(0), /valid next sequence/);
});

test("optimistic history inverse dispatches while a delayed receipt keeps user edits locked", async () => {
    const schema = new Schema({
        nodes: {
            doc: {content: "paragraph+"},
            paragraph: {content: "text*", toDOM: () => ["p", 0]},
            text: {inline: true}
        }
    });
    const state = EditorState.create({
        schema,
        doc: schema.node("doc", null, [schema.node("paragraph", null, schema.text("after"))])
    });
    const inverse = state.tr.insertText("before", 1, 6);
    assert.equal(inverse.docChanged, true);

    // While the batch receipt is delayed, no ordinary editor transaction is
    // admitted; the synchronously applied inverse is explicitly authorized.
    assert.equal(shouldApplyAuthoringTransaction(inverse, {
        readOnly: true, hasUnresolvedHistory: false, applyingHistory: false
    }), false);
    assert.equal(shouldApplyAuthoringTransaction(inverse, {
        readOnly: true, hasUnresolvedHistory: false, applyingHistory: true
    }), true);
    const inverseState = state.apply(inverse);
    assert.equal(inverseState.doc.textContent, "before");
    await new Promise(resolve => setTimeout(resolve, 0));
    const followup = inverseState.tr.insertText("!");
    assert.equal(shouldApplyAuthoringTransaction(followup, {
        readOnly: false, hasUnresolvedHistory: false, applyingHistory: false
    }), true);
});
