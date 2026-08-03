import assert from "node:assert/strict";
import test from "node:test";
import {JSDOM} from "jsdom";
import {EditorState, NodeSelection} from "prosemirror-state";
import {
    attach,
    roundTripManuscriptJson,
    semanticEditorTesting
} from "../src/semantic-editor.js";

test("round-trips Lorekeeper block IDs, semantic roles, and inline marks", () => {
    const document = {
        schemaVersion: 4,
        manuscriptId: "59897294-9390-4da6-a4df-a1bbd16e622b",
        revision: 12,
        content: [
            {
                id: "heading",
                type: "heading",
                styleRole: "chapter-heading",
                headingLevel: 1,
                imageId: null,
                altText: null,
                language: null,
                paragraphPresentation: null,
                content: [{type: "text", text: "Chapter One", marks: []}]
            },
            {
                id: "body",
                type: "paragraph",
                styleRole: "body",
                headingLevel: null,
                imageId: null,
                altText: null,
                language: null,
                paragraphPresentation: null,
                content: [
                    {
                        type: "text",
                        text: "Marked",
                        marks: [
                            {type: "emphasis", value: null},
                            {type: "language", value: "en-US"},
                            {type: "smallCaps", value: null},
                            {type: "characterStyle", value: "lead-in"}
                        ]
                    }
                ]
            },
            {
                id: "break",
                type: "sceneBreak",
                styleRole: "scene-break",
                headingLevel: null,
                imageId: null,
                altText: null,
                language: null,
                paragraphPresentation: null,
                content: []
            },
            {
                id: "figure",
                type: "figure",
                styleRole: "figure-caption",
                headingLevel: null,
                imageId: "29fd4930-0048-45a1-a2d6-3951daa5a1d9",
                altText: "A map",
                language: null,
                paragraphPresentation: null,
                decorative: false,
                accessibilityRole: "figure",
                figurePresentation: {
                    placement: "centered",
                    widthPercent: 100,
                    alignment: "center",
                    textWrap: "none",
                    fit: "contain",
                    cropXPercent: 50,
                    cropYPercent: 50,
                    spacingBeforePoints: 6,
                    spacingAfterPoints: 6,
                    startOnNewPage: false,
                    keepWithCaption: true,
                    captionPlacement: "below"
                },
                content: [{type: "text", text: "Figure caption", marks: []}]
            }
        ]
    };

    assert.deepEqual(JSON.parse(roundTripManuscriptJson(JSON.stringify(document))), document);
});

test("round-trips marked intentional line breaks", () => {
    const document = manuscript([{
        id: "verse",
        type: "paragraph",
        styleRole: "body",
        headingLevel: null,
        imageId: null,
        altText: null,
        content: [{
            type: "text",
            text: "First line\nSecond line",
            marks: [{type: "emphasis", value: null}]
        }]
    }]);

    assert.deepEqual(JSON.parse(roundTripManuscriptJson(JSON.stringify(document))), document);
});

test("heading levels three through six round-trip", () => {
    for (const level of [3, 4, 5, 6]) {
        const document = manuscript([{
            id: `heading-${level}`,
            type: "heading",
            styleRole: "custom-heading",
            headingLevel: level,
            imageId: null,
            altText: null,
            content: [{type: "text", text: `Level ${level}`, marks: []}]
        }]);
        assert.deepEqual(JSON.parse(roundTripManuscriptJson(JSON.stringify(document))), document);
    }
});

test("heading-level edits preserve Book Text Style roles", () => {
    installDom();
    const doc = semanticEditorTesting.documentFromDomain(manuscript([{
        id: "named",
        type: "paragraph",
        styleRole: "custom-opening",
        headingLevel: null,
        imageId: null,
        altText: null,
        content: [{type: "text", text: "Opening", marks: []}]
    }]));
    const view = testView(EditorState.create({doc}));

    semanticEditorTesting.applyHeadingLevel(view, 4);

    assert.equal(view.state.doc.firstChild.type.name, "heading");
    assert.equal(view.state.doc.firstChild.attrs.level, 4);
    assert.equal(view.state.doc.firstChild.attrs.styleRole, "custom-opening");
});

test("Book Text Style roles apply to selected scene-break atoms", () => {
    installDom();
    const doc = semanticEditorTesting.documentFromDomain(manuscript([{
        id: "break",
        type: "sceneBreak",
        styleRole: "scene-break",
        headingLevel: null,
        imageId: null,
        altText: null,
        content: []
    }]));
    const state = EditorState.create({
        doc,
        selection: NodeSelection.create(doc, 0)
    });
    const view = testView(state);

    semanticEditorTesting.applyBlockRole(view, "ornamental-break");

    assert.equal(view.state.doc.firstChild.attrs.styleRole, "ornamental-break");
});

function testView(state) {
    return {
        state,
        dispatch(transaction) {
            this.state = this.state.apply(transaction);
        },
        focus() {}
    };
}

function installDom() {
    const dom = new JSDOM("<!doctype html><html><body></body></html>", {
        url: "http://localhost/"
    });
    globalThis.window = dom.window;
    globalThis.document = dom.window.document;
    globalThis.DOMParser = dom.window.DOMParser;
    globalThis.Node = dom.window.Node;
    Object.defineProperty(globalThis, "navigator", {
        value: dom.window.navigator,
        configurable: true
    });
    globalThis.getComputedStyle = dom.window.getComputedStyle;
    globalThis.localStorage = dom.window.localStorage;
    Object.defineProperty(globalThis, "crypto", {
        value: dom.window.crypto,
        configurable: true
    });
    const rect = {
        left: 0, right: 0, top: 0, bottom: 0, width: 0, height: 0,
        x: 0, y: 0, toJSON() { return this; }
    };
    dom.window.HTMLElement.prototype.getBoundingClientRect = () => rect;
    dom.window.Range.prototype.getBoundingClientRect = () => rect;
    dom.window.Range.prototype.getClientRects = () => [];
    return dom;
}

function manuscript(content = [{
    id: "stable",
    type: "paragraph",
    styleRole: "body",
    headingLevel: null,
    imageId: null,
    altText: null,
    content: [{type: "text", text: "Hello world", marks: []}]
}]) {
    return {
        schemaVersion: 4,
        manuscriptId: "59897294-9390-4da6-a4df-a1bbd16e622b",
        revision: 4,
        content: content.map(block => ({
            ...block,
            language: block.language ?? null,
            paragraphPresentation: block.paragraphPresentation ?? null,
            ...(block.type === "figure"
                ? {accessibilityRole: block.accessibilityRole || "figure"}
                : {})
        }))
    };
}

test("block commands preserve stable IDs and toolbar read-only state", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    const saves = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name === "OnDocumentDebounced") {
                saves.push(JSON.parse(json));
                return {saved: true, revision: 5};
            }
            return undefined;
        }
    }, 10_000, JSON.stringify(manuscript()));

    const select = root.querySelector("select[aria-label='Block style']");
    select.value = "heading|subheading|3";
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    await handle.flush();
    assert.equal(saves.at(-1).content[0].id, "stable");
    assert.equal(saves.at(-1).content[0].type, "heading");
    handle.setReadOnly(true);
    assert.equal(root.querySelector("button[aria-label='Bold (Ctrl+B)']").disabled, true);
    handle.setReadOnly(false);
    assert.equal(root.querySelector("button[aria-label='Bold (Ctrl+B)']").disabled, false);
    handle.dispose();
    dom.window.close();
});

test("flush drains edits made while a save is in flight", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    let releaseFirst;
    const firstPending = new Promise(resolve => { releaseFirst = resolve; });
    const saved = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name !== "OnDocumentDebounced") return undefined;
            saved.push(JSON.parse(json));
            if (saved.length === 1) await firstPending;
            return {saved: true, revision: 4 + saved.length};
        }
    }, 10_000, JSON.stringify(manuscript()));
    const select = root.querySelector("select[aria-label='Block style']");
    select.value = "heading|subheading|3";
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    const flush = handle.flush();
    while (saved.length === 0)
        await new Promise(resolve => setTimeout(resolve, 0));
    select.value = "paragraph|body|2";
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    releaseFirst();
    assert.equal(await flush, true);
    assert.equal(saved.length, 2);
    assert.equal(saved[0].content[0].type, "heading");
    assert.equal(saved[1].content[0].type, "paragraph");
    handle.dispose();
    dom.window.close();
});

test("paste diagnostics and safe links match runtime normalization", () => {
    const dom = installDom();
    const warnings = semanticEditorTesting.pasteNormalizationWarnings({
        clipboardData: {
            getData: () => "<h4 style='color:red'>Heading</h4><hr><img src='x'>"
        }
    });
    assert.ok(warnings.some(value => value.includes("converted <h4>")));
    assert.ok(warnings.some(value => value.includes("horizontal rule")));
    assert.ok(warnings.some(value => value.includes("removed <img>")));
    assert.equal(semanticEditorTesting.safeLink("/relative"), null);
    assert.equal(semanticEditorTesting.safeLink("https://example.com"), "https://example.com");
    assert.equal(semanticEditorTesting.safeLink("#valid-fragment"), "#valid-fragment");
    assert.equal(semanticEditorTesting.safeLink("#x) <script>"), null);
    assert.equal(semanticEditorTesting.safeLanguage("en-US"), "en-US");
    assert.equal(semanticEditorTesting.safeLanguage("not a tag"), null);
    dom.window.close();
});

test("paste diagnostics disclose ordered-list normalization", () => {
    const dom = installDom();
    const warnings = semanticEditorTesting.pasteNormalizationWarnings({
        clipboardData: {
            getData: () => "<ol><li>First</li><li>Second</li></ol>"
        }
    });

    assert.ok(warnings.some(value => value.includes("ordered list to unordered")));
    dom.window.close();
});

test("HTML paste preserves br as an intentional line break", () => {
    const dom = installDom();
    const slice = semanticEditorTesting.sanitizeHtmlForPaste(
        "<p>First<br>Second</p>",
        [],
        []);
    const doc = semanticEditorTesting.schema.nodes.doc.create(null, slice.content);
    const domain = semanticEditorTesting.domainFromDocument(
        doc,
        "59897294-9390-4da6-a4df-a1bbd16e622b",
        4);

    assert.equal(domain.content[0].content[0].text, "First\nSecond");
    dom.window.close();
});

test("HTML paste preserves a known named scene-break role", () => {
    const dom = installDom();
    const slice = semanticEditorTesting.sanitizeHtmlForPaste(
        '<hr data-scene-break="true" data-style-role="ornamental-break">',
        [{
            id: "ornamental",
            name: "Ornamental break",
            kind: "paragraph",
            semanticRole: "ornamental-break",
            definition: {}
        }],
        []);
    const doc = semanticEditorTesting.schema.nodes.doc.create(null, slice.content);
    const domain = semanticEditorTesting.domainFromDocument(
        doc,
        "59897294-9390-4da6-a4df-a1bbd16e622b",
        4);

    assert.equal(domain.content[0].type, "sceneBreak");
    assert.equal(domain.content[0].styleRole, "ornamental-break");
    dom.window.close();
});

test("Shift Enter inserts an intentional line break", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    const saved = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name !== "OnDocumentDebounced") return undefined;
            saved.push(JSON.parse(json));
            return {saved: true, revision: 5};
        }
    }, 10_000, JSON.stringify(manuscript()));

    root.querySelector(".ProseMirror").dispatchEvent(new window.KeyboardEvent("keydown", {
        key: "Enter",
        shiftKey: true,
        bubbles: true,
        cancelable: true
    }));
    assert.equal(await handle.flush(), true);
    assert.equal(saved.length, 1);
    assert.ok(saved[0].content[0].content[0].text.includes("\n"));
    handle.dispose();
    dom.window.close();
});

test("paste sanitization removes unknown roles and foreign figures", () => {
    const dom = installDom();
    const knownImage = {
        id: "29fd4930-0048-45a1-a2d6-3951daa5a1d9",
        fileName: "map.png",
        altText: "Known map",
        previewUrl: "/media/map"
    };
    const slice = semanticEditorTesting.sanitizeHtmlForPaste(
        `<p data-style-role="BAD"><span data-character-style="unknown">Text</span></p>
         <figure data-image-id="00000000-0000-0000-0000-000000000001"><img alt="Foreign"><figcaption>Caption</figcaption></figure>
         <figure data-image-id="${knownImage.id}"><img alt=""><figcaption>Known</figcaption></figure>`,
        [],
        [knownImage]);
    const warnings = semanticEditorTesting.pasteNormalizationWarnings({
        clipboardData: {
            getData: () =>
                `<p data-style-role="BAD"><a href="javascript:alert(1)">Bad</a>`
                + `<span data-character-style="unknown" class="unknown">Text</span></p>`
                + `<figure data-image-id="00000000-0000-0000-0000-000000000001"></figure>`
        }
    }, new Set(["body"]), new Set(), new Map([[knownImage.id, knownImage]]));
    const doc = semanticEditorTesting.schema.nodes.doc.create(null, slice.content);
    const domain = semanticEditorTesting.domainFromDocument(
        doc,
        "59897294-9390-4da6-a4df-a1bbd16e622b",
        4);

    assert.equal(domain.content[0].styleRole, "body");
    assert.deepEqual(domain.content[0].content[0].marks, []);
    assert.equal(domain.content[1].type, "paragraph");
    assert.equal(domain.content[1].imageId, null);
    assert.equal(domain.content[2].type, "figure");
    assert.equal(domain.content[2].imageId, knownImage.id);
    assert.equal(domain.content[2].altText, "Known map");
    assert.ok(warnings.some(value => value.includes("unknown paragraph style")));
    assert.ok(warnings.some(value => value.includes("unsafe")));
    assert.ok(warnings.some(value => value.includes("unknown character style")));
    assert.ok(warnings.some(value => value.includes("did not reference a project image")));
    dom.window.close();
});

test("find spans adjacent marked text nodes", () => {
    installDom();
    const documentNode = semanticEditorTesting.documentFromDomain(manuscript([{
        id: "marked",
        type: "paragraph",
        styleRole: "body",
        headingLevel: null,
        imageId: null,
        altText: null,
        content: [
            {type: "text", text: "Hello ", marks: [{type: "emphasis", value: null}]},
            {type: "text", text: "world", marks: []}
        ]
    }]));
    assert.equal(semanticEditorTesting.countMatches(documentNode, "lo wo"), 1);
});

test("find and replace respects hard-break positions and never crosses them", () => {
    installDom();
    const documentNode = semanticEditorTesting.documentFromDomain(manuscript([{
        id: "verse",
        type: "paragraph",
        styleRole: "body",
        headingLevel: null,
        imageId: null,
        altText: null,
        content: [{type: "text", text: "First\nSecond", marks: []}]
    }]));

    assert.equal(semanticEditorTesting.countMatches(documentNode, "FirstSecond"), 0);
    assert.equal(semanticEditorTesting.countMatches(documentNode, "Second"), 1);
    const replaced = semanticEditorTesting.replaceAllInDocument(
        documentNode,
        "Second",
        "Changed");
    const domain = semanticEditorTesting.domainFromDocument(
        replaced,
        "59897294-9390-4da6-a4df-a1bbd16e622b",
        4);
    assert.equal(domain.content[0].content[0].text, "First\nChanged");
});

test("stored conflict drafts recover visibly and remain locked", async () => {
    const dom = installDom();
    const value = manuscript();
    localStorage.setItem(
        `lorekeeper.manuscript-conflict.${value.manuscriptId}`,
        JSON.stringify({...value, content: [{...value.content[0], content: [{type: "text", text: "Local draft", marks: []}]}]}));
    const calls = [];
    const root = document.createElement("div");
    document.body.append(root);
    const handle = attach(root, {
        async invokeMethodAsync(...args) { calls.push(args); }
    }, 10_000, JSON.stringify(value));
    await Promise.resolve();
    assert.equal(calls[0][0], "OnConflictPreserved");
    assert.equal(root.querySelector("button[aria-label='Bold (Ctrl+B)']").disabled, true);
    handle.setReadOnly(false);
    assert.equal(root.querySelector("button[aria-label='Bold (Ctrl+B)']").disabled, true);
    handle.resolveConflictWithCurrent(JSON.stringify(value), false);
    assert.equal(root.querySelector("button[aria-label='Bold (Ctrl+B)']").disabled, false);
    assert.equal(localStorage.getItem(`lorekeeper.manuscript-conflict.${value.manuscriptId}`), null);
    handle.dispose();
    dom.window.close();
});

test("resolving a conflict rehydrates current figure previews", () => {
    const dom = installDom();
    const imageId = "29fd4930-0048-45a1-a2d6-3951daa5a1d9";
    const value = manuscript([{
        id: "figure",
        type: "figure",
        styleRole: "figure-caption",
        headingLevel: null,
        imageId,
        altText: "Map",
        content: [{type: "text", text: "Map caption", marks: []}]
    }]);
    const root = document.createElement("div");
    document.body.append(root);
    const handle = attach(
        root,
        {async invokeMethodAsync() {}},
        10_000,
        JSON.stringify(manuscript()),
        "[]",
        JSON.stringify([{
            id: imageId,
            fileName: "map.png",
            altText: "Map",
            previewUrl: "/media/map"
        }]));

    handle.resolveConflictWithCurrent(JSON.stringify(value), false);

    assert.equal(root.querySelector("figure img")?.getAttribute("src"), "/media/map");
    handle.dispose();
    dom.window.close();
});

test("gives an empty manuscript an editable paragraph without changing identity", () => {
    const document = {
        schemaVersion: 2,
        manuscriptId: "59897294-9390-4da6-a4df-a1bbd16e622b",
        revision: 0,
        content: []
    };

    const roundTrip = JSON.parse(roundTripManuscriptJson(JSON.stringify(document)));

    assert.equal(roundTrip.manuscriptId, document.manuscriptId);
    assert.equal(roundTrip.revision, 0);
    assert.equal(roundTrip.content.length, 1);
    assert.equal(roundTrip.content[0].type, "paragraph");
    assert.match(roundTrip.content[0].id, /^[a-f0-9]{32}$/);
});

test("locked views reject command transactions", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    const saves = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name === "OnDocumentDebounced") saves.push(JSON.parse(json));
            return {saved: true, revision: 5};
        }
    }, 10_000, JSON.stringify(manuscript()));
    handle.setReadOnly(true);
    const select = root.querySelector("select[aria-label='Block style']");
    select.value = "heading|subheading|3";
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    assert.equal(await handle.flush(), true);
    assert.equal(saves.length, 0);
    handle.dispose();
    dom.window.close();
});

test("reset Book Text Style preserves block type", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    const saves = [];
    const value = manuscript([{
        id: "heading",
        type: "heading",
        styleRole: "fancy-heading",
        headingLevel: 1,
        imageId: null,
        altText: null,
        content: [{type: "text", text: "Title", marks: []}]
    }]);
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name === "OnDocumentDebounced") saves.push(JSON.parse(json));
            return {saved: true, revision: 5};
        }
    }, 10_000, JSON.stringify(value), JSON.stringify([{
        id: "style",
        name: "Fancy heading",
        kind: "paragraph",
        semanticRole: "fancy-heading",
        definition: {},
        revision: 1
    }]));
    const select = root.querySelector("select[aria-label='Book Text Style']");
    select.value = "__reset__";
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    await handle.flush();
    assert.equal(saves.at(-1).content[0].type, "heading");
    assert.equal(saves.at(-1).content[0].headingLevel, 1);
    assert.equal(saves.at(-1).content[0].styleRole, "chapter-heading");
    handle.dispose();
    dom.window.close();
});

test("figure insertion resolves project image IDs without casing assumptions", async () => {
    const dom = installDom();
    const imageId = "29FD4930-0048-45A1-A2D6-3951DAA5A1D9";
    window.prompt = () => { throw new Error("native prompts are unavailable in Electron"); };
    const root = document.createElement("div");
    document.body.append(root);
    const saves = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name === "OnDocumentDebounced") saves.push(JSON.parse(json));
            return {saved: true, revision: 5};
        }
    }, 10_000, JSON.stringify(manuscript()), "[]", JSON.stringify([{
        id: imageId,
        fileName: "map.png",
        altText: "A route map",
        previewUrl: "/media/map"
    }]));

    const select = root.querySelector("select[aria-label='Insert project image as figure']");
    select.value = imageId;
    select.dispatchEvent(new window.Event("change", {bubbles: true}));
    const dialog = root.querySelector("form[aria-label='Insert figure']");
    assert.ok(dialog);
    dialog.elements.caption.value = "Map caption";
    dialog.elements.altText.value = "A route map";
    dialog.elements.fit.value = "contain";
    dialog.querySelector("button[aria-label='Insert figure']").click();
    await new Promise(resolve => setTimeout(resolve, 0));
    await handle.flush();

    assert.equal(saves.at(-1).content[0].type, "figure");
    assert.equal(saves.at(-1).content[0].imageId.toLowerCase(), imageId.toLowerCase());
    assert.equal(saves.at(-1).content[0].content[0].text, "Map caption");
    handle.dispose();
    dom.window.close();
});

test("Designed Page insertion opens the new page workspace", async () => {
    const dom = installDom();
    window.prompt = () => { throw new Error("native prompts are unavailable in Electron"); };
    const root = document.createElement("div");
    document.body.append(root);
    const calls = [];
    const compositionId = "e7b913f2-9f4d-4012-8cb6-a93045eed82d";
    const created = manuscript([{
        id: "page",
        type: "designedPage",
        styleRole: "designed-page",
        headingLevel: null,
        imageId: null,
        altText: null,
        pageCompositionId: compositionId,
        content: []
    }]);
    const handle = attach(root, {
        async invokeMethodAsync(name, ...args) {
            calls.push([name, ...args]);
            if (name === "OnCreateDesignedPage") {
                return {
                    id: compositionId,
                    manuscriptJson: JSON.stringify(created),
                    summary: {id: compositionId, name: "Opening spread"}
                };
            }
            return null;
        }
    }, 10_000, JSON.stringify(manuscript()));

    root.querySelector("button[aria-label='Insert a designed page at the current manuscript position']").click();
    const dialog = root.querySelector("form[aria-label='Insert Designed Page']");
    assert.ok(dialog);
    dialog.elements.name.value = "Opening spread";
    dialog.elements.layoutMode.value = "FacingSpread";
    dialog.querySelector("button[aria-label='Create page']").click();
    await new Promise(resolve => setTimeout(resolve, 0));

    assert.ok(calls.some(call => call[0] === "OnCreateDesignedPage"
        && call[1] === "Opening spread"
        && call[2] === "FacingSpread"));
    assert.ok(calls.some(call => call[0] === "OnOpenDesignedPage" && call[1] === compositionId));
    handle.dispose();
    dom.window.close();
});

test("List toolbar button toggles a list item back to body text", async () => {
    const dom = installDom();
    const root = document.createElement("div");
    document.body.append(root);
    const saves = [];
    const handle = attach(root, {
        async invokeMethodAsync(name, _revision, json) {
            if (name === "OnDocumentDebounced") saves.push(JSON.parse(json));
            return {saved: true, revision: 5};
        }
    }, 10_000, JSON.stringify(manuscript()));
    const list = root.querySelector("button[aria-label='Toggle list formatting']");

    list.click();
    await handle.flush();
    assert.equal(saves.at(-1).content[0].type, "listItem");

    list.click();
    await handle.flush();
    assert.equal(saves.at(-1).content[0].type, "paragraph");
    assert.equal(saves.at(-1).content[0].styleRole, "body");
    handle.dispose();
    dom.window.close();
});

test("Advanced controls remain inside the editor at either toolbar edge", () => {
    const dom = installDom();
    Object.defineProperty(window, "innerWidth", {value: 1000, configurable: true});
    Object.defineProperty(window, "innerHeight", {value: 600, configurable: true});
    const root = document.createElement("div");
    document.body.append(root);
    const handle = attach(root, {async invokeMethodAsync() {}}, 10_000, JSON.stringify(manuscript()));
    const toolbar = root.querySelector(".semantic-editor-toolbar");
    const details = root.querySelector(".semantic-editor-advanced");
    const summary = details.querySelector("summary");
    const controls = details.querySelector(".semantic-editor-advanced-controls");
    root.getBoundingClientRect = () => ({
        left: 30, right: 930, top: 0, bottom: 600, width: 900, height: 600,
        x: 30, y: 0, toJSON() { return this; }
    });
    toolbar.getBoundingClientRect = () => ({
        left: 30, right: 930, top: 0, bottom: 80, width: 900, height: 80,
        x: 30, y: 0, toJSON() { return this; }
    });

    summary.getBoundingClientRect = () => ({
        left: -80, right: 0, top: 40, bottom: 72, width: 80, height: 32,
        x: -80, y: 40, toJSON() { return this; }
    });
    details.open = true;
    details.dispatchEvent(new window.Event("toggle"));
    assert.equal(controls.style.left, "8px");
    assert.equal(controls.style.width, "672px");

    summary.getBoundingClientRect = () => ({
        left: 900, right: 980, top: 40, bottom: 72, width: 80, height: 32,
        x: 900, y: 40, toJSON() { return this; }
    });
    details.dispatchEvent(new window.Event("toggle"));
    assert.equal(controls.style.left, "220px");
    handle.dispose();
    dom.window.close();
});

test("editorial counts exclude scene-break markers and preserve block separators", () => {
    installDom();
    const doc = semanticEditorTesting.documentFromDomain(manuscript([
        {
            id: "one",
            type: "paragraph",
            styleRole: "body",
            headingLevel: null,
            imageId: null,
            altText: null,
            content: [{type: "text", text: "One", marks: []}]
        },
        {
            id: "break",
            type: "sceneBreak",
            styleRole: "scene-break",
            headingLevel: null,
            imageId: null,
            altText: null,
            content: []
        },
        {
            id: "two",
            type: "paragraph",
            styleRole: "body",
            headingLevel: null,
            imageId: null,
            altText: null,
            content: [{type: "text", text: "Two", marks: []}]
        }
    ]));
    assert.equal(
        semanticEditorTesting.editorialText(
            doc,
            "59897294-9390-4da6-a4df-a1bbd16e622b",
            4),
        "One\n\nTwo");
});
