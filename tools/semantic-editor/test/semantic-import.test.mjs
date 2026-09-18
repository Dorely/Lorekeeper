import test from "node:test";
import assert from "node:assert/strict";
import {findImportPosition, insertSemanticFragment, journalImportResources} from "../src/semantic-import.js";

test("Word insertion preserves zero-width atoms and uses canonical text coordinates", () => {
    const citation = {id: "cite", type: "citation", text: "", citation: {items: [{bibliographicRecordId: "record"}]}};
    const note = {id: "ref", type: "noteReference", text: "", noteId: "note"};
    const source = {manuscriptId: "document", content: [{id: "body", type: "paragraph", content: [
        {type: "text", text: "A😀"}, citation, note, {type: "text", text: "Z"}]}], notes: [{id: "note", content: []}]};
    const fragment = {content: [{id: "new", type: "paragraph", content: [{type: "text", text: "Word"}]}], notes: []};
    const position = findImportPosition(source, "body", 4);
    assert.deepEqual(position, {documentId: "document", containerPath: ["document"], blockOrAtomId: "cite", offset: 3, affinity: "after"});
    const result = insertSemanticFragment(source, position, fragment, "tail");
    assert.deepEqual(result.content[0].content, [{type: "text", text: "A😀"}, citation]);
    assert.deepEqual(result.content[2].content, [note, {type: "text", text: "Z"}]);
    assert.deepEqual(result.notes, source.notes);
    assert.equal(source.content.length, 1);
    assert.throws(() => insertSemanticFragment(source, {...position, blockOrAtomId: "body", offset: 2}, fragment, "tail"), /Unicode/);
});

test("Word recovery resources serialize exact image bytes into the request hash and journal", () => {
    const resources = {images: [{id: "image", data: new Uint8Array([0, 255, 34, 92, 128])}], styles: [], bibliography: []};
    const journal = journalImportResources(resources);
    const recovered = JSON.parse(JSON.stringify(journal));
    assert.deepEqual([...Buffer.from(recovered.images[0].data, "base64")], [0, 255, 34, 92, 128]);
    assert.ok(resources.images[0].data instanceof Uint8Array);
});
