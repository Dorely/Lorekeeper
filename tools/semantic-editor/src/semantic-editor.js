import {DOMParser as ProseMirrorDOMParser, Fragment, Schema, Slice} from "prosemirror-model";
import {EditorState, NodeSelection, Plugin, PluginKey, Selection, TextSelection} from "prosemirror-state";
import {Decoration, DecorationSet, EditorView} from "prosemirror-view";
import {baseKeymap, chainCommands, createParagraphNear, liftEmptyBlock, newlineInCode, toggleMark} from "prosemirror-commands";
import {GapCursor, gapCursor} from "prosemirror-gapcursor";
import {closeHistory} from "prosemirror-history";
import {keymap} from "prosemirror-keymap";
import {findImportPosition, insertSemanticFragment, journalImportResources} from "./semantic-import.js";
import {createNoteEditor} from "./note-editor.js";

const idsKey = new PluginKey("lorekeeper-block-ids");
const annotationsKey = new PluginKey("lorekeeper-review-annotations");
// This is deliberately not ProseMirror's history() plugin. The process-owned
// AuthoringBatch cursor is the only Undo/Redo authority; this key merely makes
// transaction boundaries visible to its adapter.
const authoringHistoryAdapterKey = new PluginKey("lorekeeper-authoring-history-adapter");
const blockTypeToNode = {
    paragraph: "paragraph",
    heading: "heading",
    sceneBreak: "scene_break",
    blockQuote: "blockquote",
    listItem: "list_item",
    figure: "figure",
    designedPage: "designed_page",
    table: "table"
};
const nodeToBlockType = Object.fromEntries(Object.entries(blockTypeToNode).map(([key, value]) => [value, key]));
const markTypeToName = {
    emphasis: "em",
    strong: "strong",
    underline: "underline",
    strikethrough: "strikethrough",
    code: "code",
    link: "link",
    language: "language",
    smallCaps: "small_caps",
    superscript: "superscript",
    subscript: "subscript",
    characterStyle: "character_style"
};
const markNameToType = Object.fromEntries(Object.entries(markTypeToName).map(([key, value]) => [value, key]));
const builtInParagraphRoles = new Set([
    "body",
    "heading",
    "chapter-heading",
    "subheading",
    "scene-break",
    "block-quote",
    "list-item",
    "figure-caption"
]);
const defaultRoleByNode = {
    paragraph: "body",
    heading: "subheading",
    blockquote: "block-quote",
    list_item: "list-item",
    scene_break: "scene-break",
    figure: "figure-caption",
    designed_page: "designed-page",
    table: "table"
};

function newBlockId() {
    return crypto.randomUUID().replaceAll("-", "");
}

function importedTableColumnWeights(element) {
    const rows = [...element.querySelectorAll(":scope > thead > tr, :scope > tbody > tr, :scope > tfoot > tr, :scope > tr")];
    const columns = rows.reduce((maximum, row) => Math.max(maximum,
        [...row.children]
            .filter(cell => cell.tagName === "TD" || cell.tagName === "TH")
            .reduce((total, cell) => total + Math.max(1, Number(cell.getAttribute("colspan") || 1)), 0)), 0);
    return Array.from({length: Math.max(1, columns)}, () => 1);
}

function importedTableHeaderRows(element) {
    const rows = [...element.querySelectorAll(":scope > thead > tr, :scope > tbody > tr, :scope > tfoot > tr, :scope > tr")];
    let count = 0;
    for (const row of rows) {
        const cells = [...row.children].filter(cell => cell.tagName === "TD" || cell.tagName === "TH");
        if (cells.length === 0 || cells.some(cell => cell.tagName !== "TH")) break;
        count++;
    }
    return count;
}

function safeLink(value) {
    const candidate = value?.trim();
    if (!candidate) return null;
    if (candidate.startsWith("#"))
        return /^#[A-Za-z0-9][A-Za-z0-9._~:%-]*$/u.test(candidate) ? candidate : null;
    try {
        const parsed = new URL(candidate);
        return ["http:", "https:", "mailto:", "tel:"].includes(parsed.protocol)
            ? candidate
            : null;
    } catch {
        return null;
    }
}

function safeLanguage(value) {
    const candidate = value?.trim();
    return candidate && /^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$/u.test(candidate)
        ? candidate
        : null;
}

function validSemanticRole(value) {
    return typeof value === "string"
        && value.length <= 80
        && /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/u.test(value);
}

const blockAttrs = {
    id: {default: null},
    styleRole: {default: "body"},
    imageId: {default: null},
    altText: {default: null},
    imageUrl: {default: null},
    decorative: {default: false},
    language: {default: null},
    accessibilityRole: {default: null},
    presentation: {default: null},
    paragraphPresentation: {default: null},
    list: {default: null},
    designedPageId: {default: null},
    designedPageName: {default: null},
    designedPageSurfaceLabel: {default: null},
    designedPageStatus: {default: null},
    designedPagePreviewUrl: {default: null}
};

function paragraphStyle(presentation) {
    if (!presentation) return null;
    const styles = [];
    const family = editorFontFamily(presentation.fontFamilyKey);
    if (family) styles.push(`font-family:${family}`);
    if (Number.isFinite(presentation.fontSizePoints)) styles.push(`font-size:${presentation.fontSizePoints}pt`);
    if (Number.isInteger(presentation.fontWeight)) styles.push(`font-weight:${presentation.fontWeight}`);
    if (presentation.italic !== null && presentation.italic !== undefined)
        styles.push(`font-style:${presentation.italic ? "italic" : "normal"}`);
    if (presentation.smallCaps !== null && presentation.smallCaps !== undefined)
        styles.push(`font-variant-caps:${presentation.smallCaps ? "small-caps" : "normal"}`);
    if (Number.isFinite(presentation.lineHeight)) styles.push(`line-height:${presentation.lineHeight}`);
    const alignment = {start: "left", center: "center", end: "right", justify: "justify"}[presentation.alignment];
    if (alignment) styles.push(`text-align:${alignment}`);
    if (Number.isFinite(presentation.leftIndentEm))
        styles.push(`margin-left:${presentation.leftIndentEm}em`, `--lk-blockquote-left-indent:${presentation.leftIndentEm}em`, `--lk-caption-left-indent:${presentation.leftIndentEm}em`);
    if (Number.isFinite(presentation.rightIndentEm))
        styles.push(`margin-right:${presentation.rightIndentEm}em`, `--lk-caption-right-indent:${presentation.rightIndentEm}em`);
    if (Number.isFinite(presentation.firstLineIndentEm)) styles.push(`text-indent:${presentation.firstLineIndentEm}em`);
    if (Number.isFinite(presentation.spacingBeforePoints)) styles.push(`margin-top:${presentation.spacingBeforePoints}pt`);
    if (Number.isFinite(presentation.spacingAfterPoints)) styles.push(`margin-bottom:${presentation.spacingAfterPoints}pt`);
    return styles.join(";") || null;
}

function editorFontFamily(key) {
    const normalized = key?.trim().toLowerCase();
    if (!normalized) return null;
    if (normalized === "serif") return "'Lorekeeper-builtin-lora', serif";
    if (normalized === "sans") return "'Lorekeeper-builtin-nunito', sans-serif";
    if (normalized === "mono") return "'Lorekeeper-builtin-roboto-mono', monospace";
    const safeName = normalized.replaceAll(/[^a-z0-9]+/gu, "-").replaceAll(/^-|-$/gu, "");
    return safeName ? `'Lorekeeper-${safeName}'` : null;
}

function textBlockDom(tag, node, extra = {}) {
    return [tag, {
        ...extra,
        class: extra.class || null,
        "data-block-id": node.attrs.id,
        "data-style-role": node.attrs.styleRole,
        "data-manuscript-list": node.type.name === "list_item" && node.attrs.list ? JSON.stringify(node.attrs.list) : null,
        style: paragraphStyle(node.attrs.paragraphPresentation)
    }, 0];
}

function textBlockAttrs(element, defaultRole) {
    return {
        id: element.dataset.blockId,
        styleRole: element.dataset.styleRole || defaultRole,
        list: parseListMetadata(element.dataset.manuscriptList),
    };
}

function figureDomStyle(presentation) {
    const value = {...defaultFigurePresentation, ...(presentation || {})};
    const styles = [
        `width:${Math.max(5, Math.min(100, Number(value.widthPercent || 100)))}%`,
        "position:relative",
        "display:flex",
        "flex-direction:column"
    ];
    if (value.alignment === "start") styles.push("margin-left:0", "margin-right:auto");
    else if (value.alignment === "end") styles.push("margin-left:auto", "margin-right:0");
    else styles.push("margin-left:auto", "margin-right:auto");
    if (value.placement === "float") styles.push(value.textWrap === "start" ? "float:right" : "float:left");
    if (value.startOnNewPage) styles.push("break-before:page");
    styles.push(`padding-top:${Number(value.spacingBeforePoints || 0)}pt`);
    styles.push(`padding-bottom:${Number(value.spacingAfterPoints || 0)}pt`);
    return styles.join(";");
}

function designedPageDom(node) {
    const designedPageId = node.attrs.designedPageId;
    const preview = node.attrs.designedPagePreviewUrl
        ? ["img", {
            class: "semantic-designed-page-preview",
            src: node.attrs.designedPagePreviewUrl,
            alt: "",
            draggable: "false"
        }]
        : ["span", {class: "semantic-designed-page-preview semantic-designed-page-preview--empty"}, "No preview yet"];
    return ["section", {
        class: "semantic-designed-page",
        "data-block-id": node.attrs.id,
        "data-style-role": "designed-page",
        "data-designed-page-id": designedPageId
    },
    preview,
    ["span", {class: "semantic-designed-page-details"},
        ["strong", node.attrs.designedPageName || "Designed page"],
        ["span", {class: "semantic-designed-page-meta"},
            `${node.attrs.designedPageSurfaceLabel || "Geometry not configured"} · ${node.attrs.designedPageStatus || "Open to configure"}`]],
    ["button", {
        type: "button",
        class: "semantic-designed-page-open",
        "data-open-designed-page": designedPageId,
        "aria-label": `Open ${node.attrs.designedPageName || "Designed page"} editor`
    }, "Edit page"]];
}

const schema = new Schema({
    nodes: {
        doc: {content: "block*", attrs: {notes: {default: []}}},
        text: {group: "inline"},
        note_reference: {
            inline: true,
            group: "inline",
            atom: true,
            selectable: true,
            attrs: {id: {}, noteId: {}, kind: {default: "footnote"}},
            parseDOM: [{tag: "sup[data-note-id]", getAttrs: element => ({
                id: element.dataset.noteReferenceId,
                noteId: element.dataset.noteId,
                kind: element.dataset.noteKind || "footnote"
            })}],
            toDOM: node => ["sup", {
                class: "semantic-note-reference",
                "data-note-reference-id": node.attrs.id,
                "data-note-id": node.attrs.noteId,
                "data-note-kind": node.attrs.kind,
                title: node.attrs.kind === "endnote" ? "Endnote" : "Footnote"
            }, node.attrs.kind === "endnote" ? "[e]" : "[n]"]
        },
        citation: {
            inline: true,
            group: "inline",
            atom: true,
            selectable: true,
            attrs: {id: {}, items: {default: []}},
            parseDOM: [{tag: "cite[data-citation-id]", getAttrs: element => ({
                id: element.dataset.citationId,
                items: JSON.parse(element.dataset.citationItems || "[]")
            })}],
            toDOM: node => ["cite", {
                class: "semantic-citation",
                "data-citation-id": node.attrs.id,
                "data-citation-items": JSON.stringify(node.attrs.items || []),
                title: `${node.attrs.items?.length || 0} citation item${node.attrs.items?.length === 1 ? "" : "s"}`
            }, "[cite]"]
        },
        hard_break: {
            inline: true,
            group: "inline",
            selectable: false,
            parseDOM: [{tag: "br"}],
            toDOM: () => ["br"]
        },
        paragraph: {
            group: "block",
            content: "inline*",
            attrs: blockAttrs,
            parseDOM: [{tag: "p", getAttrs: element => textBlockAttrs(element, "body")}],
            toDOM: node => textBlockDom("p", node)
        },
        heading: {
            group: "block",
            content: "inline*",
            attrs: {...blockAttrs, level: {default: 2}},
            parseDOM: [1, 2, 3, 4, 5, 6].map(level => ({
                tag: `h${level}`,
                getAttrs: element => ({
                    ...textBlockAttrs(element, level === 1 ? "chapter-heading" : "subheading"),
                    level
                })
            })),
            toDOM: node => textBlockDom(`h${node.attrs.level}`, node)
        },
        blockquote: {
            group: "block",
            content: "inline*",
            attrs: {...blockAttrs, styleRole: {default: "block-quote"}},
            parseDOM: [{tag: "blockquote", getAttrs: element => textBlockAttrs(element, "block-quote")}],
            toDOM: node => textBlockDom("blockquote", node)
        },
        list_item: {
            group: "block",
            content: "inline*",
            attrs: {...blockAttrs, styleRole: {default: "list-item"}},
            parseDOM: [
                {tag: "li", getAttrs: element => textBlockAttrs(element, "list-item")},
                {tag: "div.semantic-list-item", getAttrs: element => textBlockAttrs(element, "list-item")}
            ],
            toDOM: node => textBlockDom("div", node, {class: "semantic-list-item"})
        },
        scene_break: {
            group: "block",
            atom: true,
            selectable: true,
            attrs: {...blockAttrs, styleRole: {default: "scene-break"}},
            parseDOM: [{
                tag: "hr[data-scene-break]",
                getAttrs: element => ({
                    id: element.dataset.blockId,
                    styleRole: element.dataset.styleRole || "scene-break"
                })
            }],
            toDOM: node => ["hr", {"data-scene-break": "true", "data-block-id": node.attrs.id, "data-style-role": node.attrs.styleRole}]
        },
        figure: {
            group: "block",
            content: "inline*",
            createGapCursor: true,
            attrs: {...blockAttrs, styleRole: {default: "figure-caption"}},
            parseDOM: [{
                tag: "figure[data-image-id]",
                getAttrs: element => ({
                    id: element.dataset.blockId,
                    styleRole: element.dataset.styleRole || "figure-caption",
                    imageId: element.dataset.imageId,
                    altText: element.querySelector("img")?.getAttribute("alt") || "",
                    imageUrl: element.querySelector("img")?.getAttribute("src") || "",
                    decorative: element.dataset.decorative === "true",
                    language: element.getAttribute("lang") || null,
                    accessibilityRole: element.dataset.accessibilityRole || "figure",
                    presentation: element.dataset.presentation
                        ? JSON.parse(element.dataset.presentation)
                        : null
                })
            }],
            toDOM: node => [
                "figure",
                {
                    "data-block-id": node.attrs.id,
                    "data-style-role": node.attrs.styleRole,
                    "data-image-id": node.attrs.imageId,
                    "data-decorative": String(node.attrs.decorative),
                    "data-accessibility-role": node.attrs.accessibilityRole || "figure",
                    "data-caption-placement": (node.attrs.presentation || {}).captionPlacement || "below",
                    lang: node.attrs.language || null,
                    "data-presentation": JSON.stringify(node.attrs.presentation || {}),
                    style: figureDomStyle(node.attrs.presentation)
                },
                ["img", {
                    src: node.attrs.imageUrl,
                    alt: node.attrs.decorative ? "" : node.attrs.altText,
                    draggable: "false",
                    style: `object-fit:${node.attrs.presentation?.fit === "cover" ? "cover" : "contain"};object-position:${node.attrs.presentation?.cropXPercent ?? 50}% ${node.attrs.presentation?.cropYPercent ?? 50}%`
                }],
                ["figcaption", {style: paragraphStyle(node.attrs.paragraphPresentation)}, 0]
            ]
        },
        designed_page: {
            group: "block",
            atom: true,
            selectable: true,
            attrs: {...blockAttrs, styleRole: {default: "designed-page"}},
            parseDOM: [{
                tag: "section[data-designed-page-id]",
                getAttrs: element => ({
                    id: element.dataset.blockId,
                    styleRole: "designed-page",
                    designedPageId: element.dataset.designedPageId
                })
            }],
            toDOM: designedPageDom
        },
        table: {
            group: "block",
            content: "table_row+",
            isolating: true,
            attrs: {
                ...blockAttrs,
                styleRole: {default: "table"},
                tableId: {},
                columnWidthWeights: {default: []},
                headerRowCount: {default: 0}
            },
            parseDOM: [{tag: "table", getAttrs: element => ({
                id: element.dataset.blockId || newBlockId(),
                styleRole: "table",
                tableId: element.dataset.tableId || newBlockId(),
                columnWidthWeights: element.dataset.columnWidths
                    ? JSON.parse(element.dataset.columnWidths)
                    : importedTableColumnWeights(element),
                headerRowCount: element.dataset.headerRows
                    ? Number(element.dataset.headerRows)
                    : importedTableHeaderRows(element)
            })}],
            toDOM: node => ["table", {
                class: "semantic-rich-table",
                "data-block-id": node.attrs.id,
                "data-style-role": "table",
                "data-table-id": node.attrs.tableId,
                "data-column-widths": JSON.stringify(node.attrs.columnWidthWeights || []),
                "data-header-rows": String(node.attrs.headerRowCount || 0)
            }, tableColumnGroup(node.attrs.columnWidthWeights), ["tbody", 0]]
        },
        table_row: {
            content: "table_cell+",
            attrs: {id: {default: null}, header: {default: false}},
            parseDOM: [{tag: "tr", getAttrs: element => ({
                id: element.dataset.rowId || newBlockId(),
                header: element.dataset.header === "true" || [...element.children].every(cell => cell.tagName === "TH")
            })}],
            toDOM: node => ["tr", {"data-row-id": node.attrs.id, "data-header": String(node.attrs.header)}, 0]
        },
        table_cell: {
            content: "(paragraph|list_item|figure)+",
            isolating: true,
            attrs: {id: {default: null}, rowSpan: {default: 1}, columnSpan: {default: 1}, header: {default: false}},
            parseDOM: [{tag: "td, th", getAttrs: element => ({
                id: element.dataset.cellId || newBlockId(),
                rowSpan: Number(element.getAttribute("rowspan") || 1),
                columnSpan: Number(element.getAttribute("colspan") || 1),
                header: element.tagName.toLowerCase() === "th"
            })}],
            toDOM: node => [node.attrs.header ? "th" : "td", {
                "data-cell-id": node.attrs.id,
                rowspan: node.attrs.rowSpan > 1 ? node.attrs.rowSpan : null,
                colspan: node.attrs.columnSpan > 1 ? node.attrs.columnSpan : null
            }, 0]
        }
    },
    marks: {
        em: {
            parseDOM: [{tag: "em"}, {tag: "i"}, {style: "font-style=italic"}],
            toDOM: () => ["em", 0]
        },
        strong: {
            parseDOM: [{tag: "strong"}, {tag: "b"}, {style: "font-weight=bold"}],
            toDOM: () => ["strong", 0]
        },
        underline: {
            parseDOM: [{tag: "u"}, {style: "text-decoration=underline"}],
            toDOM: () => ["u", 0]
        },
        strikethrough: {
            parseDOM: [{tag: "s"}, {tag: "del"}, {style: "text-decoration=line-through"}],
            toDOM: () => ["s", 0]
        },
        code: {
            parseDOM: [{tag: "code"}],
            toDOM: () => ["code", 0]
        },
        link: {
            attrs: {value: {}},
            inclusive: false,
            parseDOM: [{
                tag: "a[href]",
                getAttrs: element => {
                    const value = safeLink(element.getAttribute("href"));
                    return value ? {value} : false;
                }
            }],
            toDOM: mark => ["a", {href: mark.attrs.value, rel: "noopener noreferrer"}, 0]
        },
        language: {
            attrs: {value: {}},
            parseDOM: [{
                tag: "span[lang]",
                getAttrs: element => {
                    const value = safeLanguage(element.getAttribute("lang"));
                    return value ? {value} : false;
                }
            }],
            toDOM: mark => ["span", {lang: mark.attrs.value}, 0]
        },
        small_caps: {
            parseDOM: [{tag: "span.semantic-small-caps"}],
            toDOM: () => ["span", {class: "semantic-small-caps"}, 0]
        },
        superscript: {
            excludes: "subscript",
            parseDOM: [{tag: "sup"}],
            toDOM: () => ["sup", 0]
        },
        subscript: {
            excludes: "superscript",
            parseDOM: [{tag: "sub"}],
            toDOM: () => ["sub", 0]
        },
        character_style: {
            attrs: {value: {}},
            parseDOM: [{tag: "span[data-character-style]", getAttrs: element => ({value: element.dataset.characterStyle})}],
            toDOM: mark => ["span", {"data-character-style": mark.attrs.value}, 0]
        }
    }
});

function inlineFromDomain(inline) {
    if (inline.type === "noteReference") {
        return [schema.nodes.note_reference.create({
            id: inline.id || newBlockId(),
            noteId: inline.noteId,
            kind: inline.kind || "footnote"
        })];
    }
    if (inline.type === "citation") {
        return [schema.nodes.citation.create({
            id: inline.id || newBlockId(),
            items: structuredClone(inline.citation?.items || [])
        })];
    }
    if (!inline.text) return [];
    const marks = (inline.marks || []).flatMap(mark => {
        const name = markTypeToName[mark.type];
        if (!name || !schema.marks[name]) return [];
        const needsValue = ["link", "language", "character_style"].includes(name);
        if (needsValue && !mark.value) return [];
        return schema.marks[name].create(needsValue ? {value: mark.value} : null);
    });
    const nodes = [];
    const parts = inline.text.replaceAll("\r\n", "\n").replaceAll("\r", "\n").split("\n");
    for (let index = 0; index < parts.length; index++) {
        if (parts[index])
            nodes.push(schema.text(parts[index], marks));
        if (index < parts.length - 1)
            nodes.push(schema.nodes.hard_break.create(null, null, marks));
    }
    return nodes;
}

function parseListMetadata(value) {
    if (!value) return null;
    try {
        const list = JSON.parse(value);
        return typeof list.id === "string" && /^[A-Za-z0-9_-]{1,128}$/u.test(list.id)
            && typeof list.ordered === "boolean" && Number.isInteger(list.level) && list.level >= 0 && list.level <= 8
            && (list.start === null || list.ordered && Number.isInteger(list.start) && list.start >= 1 && list.start <= 1000000)
            ? {id: list.id, ordered: list.ordered, level: list.level, start: list.start} : null;
    } catch { return null; }
}

function blockFromDomain(block, noteKinds) {
    const nodeName = blockTypeToNode[block.type] || "paragraph";
    const nodeType = schema.nodes[nodeName];
    const attrs = {
        id: block.id || newBlockId(),
        styleRole: block.styleRole || "body",
        imageId: block.imageId || null,
        altText: block.altText || null,
        imageUrl: block.imageUrl || null,
        decorative: block.decorative === true,
        language: block.language || null,
        accessibilityRole: block.accessibilityRole || (nodeName === "figure" ? "figure" : null),
        presentation: block.figurePresentation || null,
        paragraphPresentation: block.paragraphPresentation || null,
        list: block.list || null,
        designedPageId: block.designedPageId || null,
        designedPageName: block.designedPageName || null,
        designedPageSurfaceLabel: block.designedPageSurfaceLabel || null,
        designedPageStatus: block.designedPageStatus || null,
        designedPagePreviewUrl: block.designedPagePreviewUrl || null
    };
    if (nodeName === "heading") attrs.level = block.headingLevel || 2;
    if (nodeName === "table") {
        const table = block.table || {};
        attrs.tableId = table.id || newBlockId();
        attrs.columnWidthWeights = table.columnWidthWeights || [];
        attrs.headerRowCount = Number(table.headerRowCount || 0);
        const rows = (table.rows || []).map((row, rowIndex) => schema.nodes.table_row.create({
            id: row.id || newBlockId(),
            header: rowIndex < attrs.headerRowCount
        }, (row.cells || []).map(cell => schema.nodes.table_cell.create({
            id: cell.id || newBlockId(),
            rowSpan: Number(cell.rowSpan || 1),
            columnSpan: Number(cell.columnSpan || 1),
            header: rowIndex < attrs.headerRowCount
        }, (cell.content || []).map(value => blockFromDomain(value, noteKinds))))));
        return nodeType.create(attrs, rows);
    }
    const content = ["scene_break", "designed_page"].includes(nodeName)
        ? null
        : (block.content || []).flatMap(inline => inlineFromDomain({
            ...inline,
            kind: inline.type === "noteReference"
                ? noteKinds.get(inline.noteId) || "footnote"
                : inline.kind
        }));
    return nodeType.create(attrs, content);
}

function documentFromDomain(document) {
    const notes = structuredClone(document.notes || []);
    const noteKinds = new Map(notes.map(note => [note.id, note.kind || "footnote"]));
    const blocks = (document.content || []).map(block => blockFromDomain(block, noteKinds));
    if (blocks.length === 0) {
        blocks.push(schema.nodes.paragraph.create({
            id: newBlockId(),
            styleRole: "body"
        }));
    }
    return schema.nodes.doc.create({notes}, blocks);
}

function domainBlockFromNode(node) {
    if (node.type.name === "table") {
        const rows = [];
        node.forEach(row => {
            const cells = [];
            row.forEach(cell => cells.push({
                id: cell.attrs.id || newBlockId(),
                rowSpan: Number(cell.attrs.rowSpan || 1),
                columnSpan: Number(cell.attrs.columnSpan || 1),
                content: Array.from({length: cell.childCount}, (_, index) => domainBlockFromNode(cell.child(index)))
            }));
            rows.push({id: row.attrs.id || newBlockId(), cells});
        });
        return {
            id: node.attrs.id || newBlockId(),
            type: "table",
            styleRole: "table",
            headingLevel: null,
            imageId: null,
            altText: null,
            language: null,
            paragraphPresentation: null,
            table: {
                id: node.attrs.tableId || newBlockId(),
                columnWidthWeights: node.attrs.columnWidthWeights || [],
                headerRowCount: Number(node.attrs.headerRowCount || 0),
                rows
            },
            content: []
        };
    }
    const inlines = [];
    if (node.isTextblock) {
        node.forEach(child => {
            if (child.type.name === "note_reference") {
                inlines.push({id: child.attrs.id || newBlockId(), type: "noteReference", text: "", noteId: child.attrs.noteId, marks: []});
                return;
            }
            if (child.type.name === "citation") {
                inlines.push({
                    id: child.attrs.id || newBlockId(),
                    type: "citation",
                    text: "",
                    noteId: null,
                    citation: {items: structuredClone(child.attrs.items || [])},
                    marks: []
                });
                return;
            }
            const marks = child.marks.flatMap(mark => {
                const type = markNameToType[mark.type.name];
                if (!type) return [];
                const value = mark.attrs?.value ?? null;
                return [{type, value}];
            });
            const text = child.isText ? child.text : child.type.name === "hard_break" ? "\n" : null;
            if (!text) return;
            const previous = inlines.at(-1);
            if (previous?.type === "text" && JSON.stringify(previous.marks) === JSON.stringify(marks)) previous.text += text;
            else inlines.push({type: "text", text, marks});
        });
    }
    const block = {
        id: node.attrs.id || newBlockId(),
        type: nodeToBlockType[node.type.name] || "paragraph",
        styleRole: node.attrs.styleRole || "body",
        headingLevel: node.type.name === "heading" ? node.attrs.level : null,
        list: node.type.name === "list_item" ? node.attrs.list || null : null,
        imageId: node.type.name === "figure" ? node.attrs.imageId : null,
        altText: node.type.name === "figure" ? node.attrs.altText : null,
        language: node.attrs.language || null,
        paragraphPresentation: ["paragraph", "heading", "blockquote", "list_item", "figure"].includes(node.type.name)
            ? node.attrs.paragraphPresentation || null
            : null,
        content: inlines
    };
    if (node.type.name === "figure") {
        block.decorative = node.attrs.decorative === true;
        block.accessibilityRole = node.attrs.accessibilityRole || "figure";
        block.figurePresentation = node.attrs.presentation || {...defaultFigurePresentation};
    }
    if (node.type.name === "designed_page") block.designedPageId = node.attrs.designedPageId;
    return block;
}

function domainFromDocument(doc, manuscriptId, revision) {
    const content = [];
    doc.forEach(node => content.push(domainBlockFromNode(node)));
    return {schemaVersion: 7, manuscriptId, revision, content, notes: structuredClone(doc.attrs.notes || [])};
}

export function roundTripManuscriptJson(json) {
    const document = JSON.parse(json);
    return JSON.stringify(domainFromDocument(
        documentFromDomain(document),
        document.manuscriptId,
        document.revision));
}

function blockIdPlugin() {
    return new Plugin({
        key: idsKey,
        appendTransaction(_transactions, _oldState, newState) {
            const seen = new Set();
            let transaction = newState.tr;
            let changed = false;
            const referencedNotes = new Set();
            newState.doc.descendants((node, position) => {
                if (node.type.name === "note_reference") referencedNotes.add(node.attrs.noteId);
                if ((!node.isBlock && !["note_reference", "citation"].includes(node.type.name)) || node.type.name === "doc") return true;
                const id = node.attrs.id;
                if (!id || seen.has(id)) {
                    transaction = transaction.setNodeMarkup(position, undefined, {...node.attrs, id: newBlockId(),
                        ...(node.attrs.list && seen.has(id) ? {list: {...node.attrs.list, start: null}} : {})});
                    changed = true;
                } else {
                    seen.add(id);
                }
                // Textblocks hold the note references and citations checked above.
                return node.isTextblock || ["table", "table_row", "table_cell"].includes(node.type.name);
            });
            const notes = newState.doc.attrs.notes || [];
            const retainedNotes = notes.filter(note => referencedNotes.has(note.id));
            if (retainedNotes.length !== notes.length) {
                transaction = transaction.setDocAttribute("notes", retainedNotes);
                changed = true;
            }
            return changed ? transaction : null;
        }
    });
}

function listNumberingPlugin() {
    return new Plugin({props: {decorations(state) {
        const decorations = [];
        const scopes = new Map();
        state.doc.descendants((node, position) => {
            if (node.type.name !== "list_item" || !node.attrs.list) return true;
            const list = node.attrs.list;
            const owner = state.doc.resolve(position).parent.attrs.id || "document";
            const key = `${owner}/${list.id}`;
            const counts = scopes.get(key) || [];
            const level = Math.max(0, Math.min(8, list.level || 0));
            counts.length = level + 1;
            const number = list.start || (counts[level] || 0) + 1;
            if (list.ordered) counts[level] = number;
            scopes.set(key, counts);
            decorations.push(Decoration.node(position, position + node.nodeSize, {
                "data-list-marker": list.ordered ? `${number}. ` : "• ",
                "aria-level": String(level + 1),
                style: `margin-left:${(level + 1) * 1.5}em`
            }));
            return false;
        });
        return DecorationSet.create(state.doc, decorations);
    }}});
}

function initialEditorSelection(doc) {
    const start = doc.resolve(0);
    return GapCursor.valid(start)
        ? new GapCursor(start)
        : TextSelection.atStart(doc);
}

function button(label, title, action) {
    const element = document.createElement("button");
    element.type = "button";
    element.className = "semantic-editor-button";
    element.textContent = label;
    element.title = title;
    element.setAttribute("aria-label", title);
    element.addEventListener("mousedown", event => event.preventDefault());
    element.addEventListener("click", action);
    return element;
}

function iconButton(label, title, action) {
    const element = button(label, title, action);
    element.classList.add("semantic-editor-icon-button");
    return element;
}

function alignmentButton(alignment, title, action) {
    const element = iconButton("", title, action);
    const icon = document.createElement("span");
    icon.className = `semantic-editor-alignment-icon semantic-editor-alignment-icon--${alignment}`;
    for (const width of [14, 9, 14, 11]) {
        const line = document.createElement("span");
        line.style.width = `${width}px`;
        icon.append(line);
    }
    element.append(icon);
    return element;
}

function toolGroup(label, controls) {
    const group = document.createElement("div");
    group.className = "semantic-editor-tool-group";
    group.setAttribute("role", "group");
    group.setAttribute("aria-label", label);
    for (const control of controls) {
        if (control) group.append(control);
    }
    const divider = document.createElement("span");
    divider.className = "semantic-editor-tool-divider";
    divider.setAttribute("aria-hidden", "true");
    group.append(divider);
    return group;
}

function selectControl(label, options, onChange, resetAfterChange = true) {
    const wrapper = document.createElement("label");
    wrapper.className = "semantic-editor-select-label";
    const text = document.createElement("span");
    text.className = "visually-hidden";
    text.textContent = label;
    const select = document.createElement("select");
    select.className = "semantic-editor-select";
    select.setAttribute("aria-label", label);
    select.title = label;
    for (const [value, name] of options) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = name;
        select.append(option);
    }
    select.addEventListener("change", () => {
        const requestedReset = onChange(select.value);
        if (resetAfterChange || requestedReset === true) select.value = "";
    });
    wrapper.append(text, select);
    return wrapper;
}

function showEditorNotice(root, message) {
    root.querySelector(".semantic-editor-action-notice")?.remove();
    const notice = document.createElement("div");
    notice.className = "semantic-editor-action-notice";
    notice.setAttribute("role", "alert");
    const text = document.createElement("span");
    text.textContent = message;
    const dismiss = button("Dismiss", "Dismiss message", () => notice.remove());
    notice.append(text, dismiss);
    root.prepend(notice);
}

function showEditorForm(root, {title, description, submitLabel, fields, validate}) {
    return new Promise(resolve => {
        const previousFocus = document.activeElement;
        const backdrop = document.createElement("div");
        backdrop.className = "semantic-editor-dialog-backdrop";
        const form = document.createElement("form");
        form.className = "semantic-editor-dialog";
        form.setAttribute("role", "dialog");
        form.setAttribute("aria-modal", "true");
        form.setAttribute("aria-label", title);
        const heading = document.createElement("h2");
        heading.textContent = title;
        form.append(heading);
        if (description) {
            const copy = document.createElement("p");
            copy.textContent = description;
            form.append(copy);
        }
        const controls = new Map();
        for (const field of fields) {
            const wrapper = document.createElement("label");
            wrapper.className = field.type === "checkbox" ? "semantic-editor-dialog-check" : "";
            const label = document.createElement("span");
            label.textContent = field.label;
            let control;
            if (field.type === "select") {
                control = document.createElement("select");
                for (const [value, text] of field.options ?? []) {
                    const option = document.createElement("option");
                    option.value = value;
                    option.textContent = text;
                    control.append(option);
                }
                control.value = field.value ?? "";
            } else if (field.type === "textarea") {
                control = document.createElement("textarea");
                control.rows = field.rows ?? 3;
                control.value = field.value ?? "";
            } else {
                control = document.createElement("input");
                control.type = field.type ?? "text";
                if (field.type === "checkbox") control.checked = !!field.value;
                else control.value = field.value ?? "";
            }
            control.name = field.name;
            if (field.required) control.required = true;
            if (field.type === "checkbox") wrapper.append(control, label);
            else wrapper.append(label, control);
            controls.set(field.name, control);
            form.append(wrapper);
        }
        const error = document.createElement("p");
        error.className = "semantic-editor-dialog-error";
        error.hidden = true;
        error.setAttribute("role", "alert");
        const actions = document.createElement("div");
        actions.className = "semantic-editor-dialog-actions";
        const cancel = button("Cancel", "Cancel", () => close(null));
        const submit = button(submitLabel, submitLabel, () => form.requestSubmit());
        submit.classList.add("semantic-editor-button--primary");
        actions.append(cancel, submit);
        form.append(error, actions);
        backdrop.append(form);
        root.append(backdrop);

        let settled = false;
        const close = value => {
            if (settled) return;
            settled = true;
            backdrop.remove();
            if (previousFocus?.isConnected) previousFocus.focus();
            resolve(value);
        };
        form.addEventListener("submit", event => {
            event.preventDefault();
            const values = Object.fromEntries([...controls].map(([name, control]) => [
                name,
                control.type === "checkbox" ? control.checked : control.value
            ]));
            const validationError = validate?.(values);
            if (validationError) {
                error.textContent = validationError;
                error.hidden = false;
                return;
            }
            close(values);
        });
        backdrop.addEventListener("mousedown", event => {
            if (event.target === backdrop) close(null);
        });
        form.addEventListener("keydown", event => {
            if (event.key === "Escape") {
                event.preventDefault();
                close(null);
            } else if (event.key === "Tab") {
                const focusable = [...form.querySelectorAll("input, select, textarea, button")].filter(control => !control.disabled);
                const first = focusable[0];
                const last = focusable.at(-1);
                if (event.shiftKey && document.activeElement === first) {
                    event.preventDefault();
                    last?.focus();
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault();
                    first?.focus();
                }
            }
        });
        queueMicrotask(() => controls.values().next().value?.focus());
    });
}

function applyBlock(view, nodeName, styleRole, level = 2) {
    const type = schema.nodes[nodeName];
    if (!type) return;
    const {from, to} = view.state.selection;
    let transaction = view.state.tr;
    let changed = false;
    view.state.doc.nodesBetween(from, to, (node, position) => {
        if (!node.isTextblock || !type.validContent(node.content))
            return true;
        transaction = transaction.setNodeMarkup(
            position,
            type,
            {
                ...node.attrs,
                id: node.attrs.id || newBlockId(),
                styleRole,
                list: nodeName === "list_item" ? node.attrs.list : null,
                ...(nodeName === "heading" ? {level} : {})
            },
            node.marks);
        changed = true;
        return false;
    });
    if (changed)
        view.dispatch(transaction.scrollIntoView());
    view.focus();
}

function applyBlockRole(view, styleRole) {
    if (!styleRole) return;
    const {from, to} = view.state.selection;
    let transaction = view.state.tr;
    let changed = false;
    view.state.doc.nodesBetween(from, to, (node, position) => {
        if (!node.isTextblock && node.type.name !== "scene_break") return true;
        transaction = transaction.setNodeMarkup(
            position,
            undefined,
            {...node.attrs, id: node.attrs.id || newBlockId(), styleRole},
            node.marks);
        changed = true;
        return false;
    });
    if (changed) view.dispatch(transaction.scrollIntoView());
    view.focus();
}

function applyHeadingLevel(view, level) {
    if (!Number.isInteger(level) || level < 1 || level > 6) return;
    const {from, to} = view.state.selection;
    let transaction = view.state.tr;
    let changed = false;
    view.state.doc.nodesBetween(from, to, (node, position) => {
        if (!node.isTextblock || !schema.nodes.heading.validContent(node.content))
            return true;
        transaction = transaction.setNodeMarkup(
            position,
            schema.nodes.heading,
            {
                ...node.attrs,
                id: node.attrs.id || newBlockId(),
                styleRole: node.attrs.styleRole || "subheading",
                level
            },
            node.marks);
        changed = true;
        return false;
    });
    if (changed) view.dispatch(transaction.scrollIntoView());
    view.focus();
}

function resetBlockRole(view) {
    const defaults = {
        paragraph: "body",
        heading: "subheading",
        blockquote: "block-quote",
        list_item: "list-item",
        figure: "figure-caption",
        scene_break: "scene-break"
    };
    const {from, to} = view.state.selection;
    let transaction = view.state.tr;
    let changed = false;
    view.state.doc.nodesBetween(from, to, (node, position) => {
        if ((!node.isTextblock && node.type.name !== "scene_break") || !defaults[node.type.name])
            return true;
        const role = node.type.name === "heading" && node.attrs.level === 1
            ? "chapter-heading"
            : defaults[node.type.name];
        transaction = transaction.setNodeMarkup(
            position,
            undefined,
            {...node.attrs, id: node.attrs.id || newBlockId(), styleRole: role, paragraphPresentation: null},
            node.marks);
        changed = true;
        return false;
    });
    if (changed) view.dispatch(transaction.scrollIntoView());
    view.focus();
}

const paragraphStyleNodeNames = new Set(["paragraph", "heading", "blockquote", "list_item", "figure"]);

function selectedParagraphPositions(view) {
    const {selection, doc} = view.state;
    if (selection.empty) {
        for (let depth = selection.$from.depth; depth > 0; depth--) {
            const node = selection.$from.node(depth);
            if (paragraphStyleNodeNames.has(node.type.name))
                return [selection.$from.before(depth)];
        }
        return [];
    }

    const positions = [];
    doc.nodesBetween(selection.from, selection.to, (node, position) => {
        if (!paragraphStyleNodeNames.has(node.type.name)) return true;
        positions.push(position);
        return false;
    });
    return positions;
}

function chapterParagraphPositions(view) {
    const positions = [];
    view.state.doc.descendants((node, position) => {
        if (!paragraphStyleNodeNames.has(node.type.name)) return true;
        positions.push(position);
        return false;
    });
    return positions;
}

function applyParagraphStyleAtPositions(view, positions, styleRole) {
    if (!styleRole || positions.length === 0) return false;
    let transaction = view.state.tr;
    for (const position of positions) {
        const node = transaction.doc.nodeAt(position);
        if (!node || !paragraphStyleNodeNames.has(node.type.name)) continue;
        transaction = transaction.setNodeMarkup(position, undefined, {
            ...node.attrs,
            id: node.attrs.id || newBlockId(),
            styleRole,
            paragraphPresentation: null
        }, node.marks);
    }
    if (!transaction.docChanged) return false;
    view.dispatch(transaction.scrollIntoView());
    view.focus();
    return true;
}

function applyParagraphStyle(view, styleRole, wholeChapter = false) {
    const positions = wholeChapter ? chapterParagraphPositions(view) : selectedParagraphPositions(view);
    return applyParagraphStyleAtPositions(view, positions, styleRole);
}

function selectedParagraphIdentity(view) {
    const position = selectedParagraphPositions(view)[0];
    if (!Number.isInteger(position)) return null;
    const node = view.state.doc.nodeAt(position);
    return node?.attrs?.id ? {blockId: node.attrs.id, position} : null;
}

function blockPositionById(doc, blockId) {
    let found = null;
    doc.descendants((node, position) => {
        if (node.attrs?.id !== blockId) return true;
        found = position;
        return false;
    });
    return found;
}

function annotationRangeFromSelection(view) {
    const {selection, doc} = view.state;
    if (selection.empty || selection instanceof NodeSelection)
        throw new Error("Select manuscript text before adding a review annotation.");
    const pieces = [];
    doc.forEach((node, position) => {
        const nodeEnd = position + node.nodeSize;
        if (!["paragraph", "heading", "blockquote", "list_item", "figure"].includes(node.type.name)
            && selection.from < nodeEnd && selection.to > position)
            throw new Error("Review annotations cannot cross Scene Breaks, Designed Pages, or non-flowing content.");
        const contentStart = position + 1;
        const contentEnd = contentStart + node.content.size;
        const from = Math.max(selection.from, contentStart);
        const to = Math.min(selection.to, contentEnd);
        if (to <= from) return;
        if (!["paragraph", "heading", "blockquote", "list_item", "figure"].includes(node.type.name))
            throw new Error("Review annotations can only cover flowing manuscript text and Figure captions.");
        pieces.push({
            blockId: node.attrs.id,
            startOffset: from - contentStart,
            endOffset: to - contentStart
        });
    });
    if (pieces.length === 0)
        throw new Error("Select manuscript text before adding a review annotation.");
    return {
        startBlockId: pieces[0].blockId,
        startOffset: pieces[0].startOffset,
        endBlockId: pieces.at(-1).blockId,
        endOffset: pieces.at(-1).endOffset
    };
}

function annotationDecorations(doc, annotations) {
    const decorations = [];
    for (const annotation of annotations || []) {
        if (String(annotation.anchorState).toLowerCase() !== "current") continue;
        const range = annotation.range;
        const startPosition = blockPositionById(doc, range.startBlockId);
        const endPosition = blockPositionById(doc, range.endBlockId);
        if (!Number.isInteger(startPosition) || !Number.isInteger(endPosition)) continue;
        let active = false;
        doc.forEach((node, position) => {
            if (position === startPosition) active = true;
            if (!active || !node.inlineContent) return;
            const fromOffset = position === startPosition
                ? Math.max(0, Math.min(range.startOffset, node.content.size))
                : 0;
            const toOffset = position === endPosition
                ? Math.max(0, Math.min(range.endOffset, node.content.size))
                : node.content.size;
            const from = position + 1 + fromOffset;
            const to = position + 1 + toOffset;
            if (to > from) {
                decorations.push(Decoration.inline(from, to, {
                    class: "manuscript-review-highlight",
                    "data-annotation-id": annotation.id,
                    title: annotation.kind === "note" ? "Review note" : "Review highlight"
                }));
            }
            if (position === endPosition) active = false;
        });
    }
    return DecorationSet.create(doc, decorations);
}

function captureStableSelection(view) {
    const {selection, doc} = view.state;
    if (selection instanceof NodeSelection) {
        const node = doc.nodeAt(selection.from);
        return node?.attrs?.id ? {blockId: node.attrs.id, node: true, anchorOffset: 0, headOffset: 0} : {};
    }
    let selected = null;
    doc.descendants((node, position) => {
        if (!node.attrs?.id || selection.head < position || selection.head > position + node.nodeSize)
            return true;
        selected = {node, position};
        return true;
    });
    if (!selected) return {};
    const contentStart = selected.position + 1;
    return {
        blockId: selected.node.attrs.id,
        node: false,
        anchorOffset: Math.max(0, selection.anchor - contentStart),
        headOffset: Math.max(0, selection.head - contentStart)
    };
}

function topLevelBlockEntries(doc) {
    const entries = [];
    doc.forEach((node, position) => {
        if (!node.attrs?.id) return;
        entries.push({
            id: node.attrs.id,
            node,
            position
        });
    });
    return entries;
}

function selectionLocation(view) {
    const stable = captureStableSelection(view);
    const entries = topLevelBlockEntries(view.state.doc);
    const entryIndex = stable.blockId
        ? entries.findIndex(entry => entry.id === stable.blockId)
        : -1;
    const logicalProgress = entries.length <= 1 || entryIndex < 0
        ? 0
        : entryIndex / (entries.length - 1);
    return {
        ...stable,
        logicalProgress,
        fallbackLine: entryIndex >= 0 ? entryIndex + 1 : 1
    };
}

function fallbackBlockEntry(entries, logicalProgress) {
    if (entries.length === 0) return null;
    const progress = Number.isFinite(Number(logicalProgress))
        ? Math.max(0, Math.min(1, Number(logicalProgress)))
        : 0;
    return entries[Math.min(entries.length - 1, Math.round(progress * (entries.length - 1)))];
}

function restoreLocation(view, locationJson, collapseToHead = true) {
    if (!locationJson) return false;
    try {
        const saved = typeof locationJson === "string" ? JSON.parse(locationJson) : locationJson;
        const entries = topLevelBlockEntries(view.state.doc);
        let position = saved?.blockId ? blockPositionById(view.state.doc, saved.blockId) : null;
        let node = Number.isInteger(position) ? view.state.doc.nodeAt(position) : null;
        const exact = !!node;
        if (!node) {
            const fallback = fallbackBlockEntry(entries, saved?.logicalProgress);
            if (!fallback) return false;
            position = fallback.position;
            node = fallback.node;
        }
        if (!Number.isInteger(position) || !node) return false;
        if (saved.node || !node.inlineContent) {
            view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, position)).scrollIntoView());
            return true;
        }
        const contentStart = position + 1;
        const maxOffset = node.content.size;
        const fallbackOffset = exact ? null : Number(saved.headOffset) || 0;
        const savedHeadOffset = Number(saved.headOffset) || 0;
        const anchorOffset = Math.max(0, Math.min(Number.isFinite(fallbackOffset)
            ? fallbackOffset
            : collapseToHead ? savedHeadOffset : Number(saved.anchorOffset) || 0, maxOffset));
        const headOffset = Math.max(0, Math.min(Number.isFinite(fallbackOffset) ? fallbackOffset : Number(saved.headOffset) || 0, maxOffset));
        view.dispatch(view.state.tr.setSelection(TextSelection.create(
            view.state.doc,
            contentStart + Math.min(anchorOffset, headOffset),
            contentStart + Math.max(anchorOffset, headOffset))).scrollIntoView());
        return true;
    } catch {
        // Location restoration is best-effort; the document remains authoritative.
        return false;
    }
}

function restoreStableSelection(view, selectionJson) {
    if (!selectionJson) return;
    restoreLocation(view, selectionJson, false);
}

function applyMark(view, markName, value = null) {
    const type = schema.marks[markName];
    if (!type) return;
    if (value === null && ["link", "language", "character_style"].includes(markName)) {
        const {from, to} = view.state.selection;
        const transaction = from === to
            ? view.state.tr.removeStoredMark(type)
            : view.state.tr.removeMark(from, to, type);
        view.dispatch(transaction);
        view.focus();
        return;
    }
    toggleMark(type, value === null ? null : {value})(view.state, view.dispatch, view);
    view.focus();
}

function selectedMarkValue(view, markName) {
    const type = schema.marks[markName];
    if (!type) return "";
    const marks = view.state.storedMarks || view.state.selection.$from.marks();
    return marks.find(mark => mark.type === type)?.attrs?.value || "";
}

async function editLink(view, root) {
    const values = await showEditorForm(root, {
        title: "Link",
        description: "Enter a web, email, phone, or document-fragment link. Leave it blank to remove the link.",
        submitLabel: "Apply",
        fields: [{name: "url", label: "Link URL", type: "text", value: selectedMarkValue(view, "link")}],
        validate: value => value.url.trim() && !safeLink(value.url)
            ? "Use an http, https, mailto, tel, or document-fragment link."
            : null
    });
    if (!values) return;
    applyMark(view, "link", values.url.trim() ? safeLink(values.url) : null);
}

async function editLanguage(view, root) {
    const values = await showEditorForm(root, {
        title: "Text language",
        description: "Set a BCP 47 language tag for the selection. Leave it blank to use the surrounding language.",
        submitLabel: "Apply",
        fields: [{name: "language", label: "Language tag", type: "text", value: selectedMarkValue(view, "language")}],
        validate: value => value.language.trim() && !safeLanguage(value.language)
            ? "Use a valid language tag such as en, en-US, or fr-CA."
            : null
    });
    if (!values) return;
    applyMark(view, "language", values.language.trim() ? safeLanguage(values.language) : null);
}

function updateParagraphPresentation(view, update) {
    const positions = selectedParagraphPositions(view);
    if (positions.length === 0) return false;
    let transaction = view.state.tr;
    let changed = false;
    for (const position of positions) {
        const node = transaction.doc.nodeAt(position);
        if (!node || !paragraphStyleNodeNames.has(node.type.name)) continue;
        const current = node.attrs.paragraphPresentation || {};
        const next = Object.fromEntries(Object.entries(update({...current}))
            .filter(([, value]) => value !== null && value !== undefined));
        transaction = transaction.setNodeMarkup(position, undefined, {
            ...node.attrs,
            paragraphPresentation: Object.keys(next).length ? next : null
        }, node.marks);
        changed = true;
    }
    if (changed) view.dispatch(transaction.scrollIntoView());
    view.focus();
    return changed;
}

function setParagraphTypography(view, property, value) {
    return updateParagraphPresentation(view, current => ({...current, [property]: value}));
}

function selectedParagraphTypography(view, namedStyles) {
    const position = selectedParagraphPositions(view)[0];
    const node = Number.isInteger(position) ? view.state.doc.nodeAt(position) : null;
    if (!node) return {};
    const definition = namedStyles.find(style =>
        style.kind === "paragraph" && style.semanticRole === node.attrs.styleRole)?.definition || {};
    const presentation = node.attrs.paragraphPresentation || {};
    return {
        fontFamilyKey: presentation.fontFamilyKey ?? definition.fontFamilyKey ?? "",
        fontSizePoints: presentation.fontSizePoints ?? definition.fontSizePoints ?? "",
        lineHeight: presentation.lineHeight ?? definition.lineHeight ?? ""
    };
}

function buildTypographyControls(view, namedStyles, fontFamilies, root) {
    const group = document.createElement("div");
    group.className = "semantic-editor-typography-controls";
    const fontOptions = [
        ["", "Font"],
        ["__clear__", "Default font"],
        ["serif", "Classic serif"],
        ["sans", "Clean sans serif"],
        ["mono", "Monospace"]
    ].concat(
        fontFamilies.map(family => [family.key, family.name]));
    const fontControl = selectControl("Font family", fontOptions, value => {
        if (!value) return;
        if (!setParagraphTypography(view, "fontFamilyKey", value === "__clear__" ? null : value))
            showEditorNotice(root, "Place the cursor in a paragraph first.");
    }, false);
    fontControl.classList.add("semantic-editor-font-family");

    const sizeGroup = document.createElement("div");
    sizeGroup.className = "semantic-editor-size-control";
    const sizeInput = document.createElement("input");
    sizeInput.type = "number";
    sizeInput.min = "1";
    sizeInput.max = "288";
    sizeInput.step = "0.5";
    sizeInput.placeholder = "Size";
    sizeInput.setAttribute("aria-label", "Font size in points");
    sizeInput.title = "Font size in points";
    const applySize = () => {
        if (!sizeInput.value.trim()) {
            if (!setParagraphTypography(view, "fontSizePoints", null))
                showEditorNotice(root, "Place the cursor in a paragraph first.");
            return;
        }
        const value = Number(sizeInput.value);
        if (!Number.isFinite(value) || value <= 0 || value > 288) {
            showEditorNotice(root, "Choose a font size from 1 through 288 points.");
            return;
        }
        if (!setParagraphTypography(view, "fontSizePoints", value))
            showEditorNotice(root, "Place the cursor in a paragraph first.");
    };
    sizeInput.addEventListener("change", applySize);
    sizeInput.addEventListener("keydown", event => {
        if (event.key !== "Enter") return;
        event.preventDefault();
        applySize();
    });
    const stepSize = delta => {
        const current = Number(sizeInput.value) || 12;
        sizeInput.value = String(Math.max(1, Math.min(288, current + delta)));
        applySize();
    };
    sizeGroup.append(
        iconButton("−", "Decrease paragraph font size", () => stepSize(-1)),
        sizeInput,
        iconButton("+", "Increase paragraph font size", () => stepSize(1)));

    const lineHeightControl = selectControl("Line spacing", [
        ["", "Line spacing"],
        ["__clear__", "Default spacing"],
        ["1", "Single"],
        ["1.15", "1.15"],
        ["1.5", "1.5"],
        ["2", "Double"]
    ], value => {
        if (!value) return;
        const lineHeight = value === "__clear__" ? null : Number(value);
        if (!setParagraphTypography(view, "lineHeight", lineHeight))
            showEditorNotice(root, "Place the cursor in a paragraph first.");
    }, false);

    group.append(fontControl, sizeGroup, lineHeightControl);
    return {
        group,
        update() {
            const current = selectedParagraphTypography(view, namedStyles);
            const familySelect = fontControl.querySelector("select");
            familySelect.value = fontOptions.some(([value]) => value === current.fontFamilyKey)
                ? current.fontFamilyKey
                : "";
            sizeInput.value = current.fontSizePoints === "" ? "" : String(current.fontSizePoints);
            const lineHeightSelect = lineHeightControl.querySelector("select");
            const lineHeightValue = current.lineHeight === "" ? "" : String(current.lineHeight);
            lineHeightSelect.value = [...lineHeightSelect.options].some(option => option.value === lineHeightValue)
                ? lineHeightValue
                : "";
        }
    };
}

function setParagraphAlignment(view, alignment) {
    updateParagraphPresentation(view, current => ({...current, alignment}));
}

function changeParagraphIndent(view, delta) {
    updateParagraphPresentation(view, current => ({
        ...current,
        leftIndentEm: Math.max(0, Math.min(12, Number(current.leftIndentEm || 0) + delta))
    }));
}

async function editParagraphPresentation(view, root) {
    const node = view.state.selection.$from.parent;
    const current = node.attrs.paragraphPresentation || {};
    const values = await showEditorForm(root, {
        title: "Paragraph layout",
        description: "Fine-tune indentation, spacing, and pagination for the selected paragraph.",
        submitLabel: "Apply",
        fields: [
            {name: "rightIndent", label: "Right indent (em)", type: "number", value: current.rightIndentEm || 0},
            {name: "firstLine", label: "First-line indent (em; negative creates a hanging indent)", type: "number", value: current.firstLineIndentEm || 0},
            {name: "before", label: "Space before (pt)", type: "number", value: current.spacingBeforePoints || 0},
            {name: "after", label: "Space after (pt)", type: "number", value: current.spacingAfterPoints || 0},
            {name: "keepWithNext", label: "Keep with next block", type: "checkbox", value: current.keepWithNext},
            {name: "startOnNewPage", label: "Start on a new page", type: "checkbox", value: current.startOnNewPage}
        ],
        validate: value => {
            const rightIndent = Number(value.rightIndent);
            const firstLine = Number(value.firstLine);
            const before = Number(value.before);
            const after = Number(value.after);
            if (!Number.isFinite(rightIndent) || rightIndent < 0 || rightIndent > 12)
                return "Right indent must be between 0 and 12 em.";
            if (!Number.isFinite(firstLine) || firstLine < -12 || firstLine > 12)
                return "First-line indent must be between -12 and 12 em.";
            if (![before, after].every(number => Number.isFinite(number) && number >= 0 && number <= 288))
                return "Paragraph spacing must be between 0 and 288 points.";
            return null;
        }
    });
    if (!values) return;
    updateParagraphPresentation(view, value => ({
        ...value,
        rightIndentEm: Number(values.rightIndent),
        firstLineIndentEm: Number(values.firstLine),
        spacingBeforePoints: Number(values.before),
        spacingAfterPoints: Number(values.after),
        keepWithNext: values.keepWithNext,
        startOnNewPage: values.startOnNewPage
    }));
}

function clearParagraphPresentation(view) {
    updateParagraphPresentation(view, () => ({}));
}

function toggleListFormatting(view) {
    const selectedBlock = view.state.selection.$from.parent;
    const currentRole = selectedBlock.attrs.styleRole;
    if (selectedBlock.type.name === "list_item") {
        applyBlock(
            view,
            "paragraph",
            !currentRole || currentRole === "list-item" ? "body" : currentRole,
            2);
    } else {
        applyBlock(view, "list_item", currentRole || "list-item", 2);
    }
}

function insertSceneBreak(view) {
    const node = schema.nodes.scene_break.create({id: newBlockId(), styleRole: "scene-break"});
    view.dispatch(view.state.tr.replaceSelectionWith(node).scrollIntoView());
    view.focus();
}

function insertHardBreak(state, dispatch) {
    if (!state.selection.$from.parent.inlineContent)
        return false;
    if (dispatch)
        dispatch(state.tr.replaceSelectionWith(schema.nodes.hard_break.create()).scrollIntoView());
    return true;
}

const defaultFigurePresentation = Object.freeze({
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
});

function selectedFigure(view) {
    const {$from} = view.state.selection;
    if (view.state.selection.node?.type?.name === "figure")
        return {node: view.state.selection.node, position: view.state.selection.from};
    return $from.parent.type.name === "figure"
        ? {node: $from.parent, position: $from.before()}
        : null;
}

function selectFigureFromElement(view, element) {
    const figure = element.closest("figure[data-block-id]");
    const blockId = figure?.dataset.blockId;
    if (!blockId) return false;
    let position = null;
    view.state.doc.descendants((node, nodePosition) => {
        if (node.type.name !== "figure" || node.attrs.id !== blockId) return true;
        position = nodePosition;
        return false;
    });
    if (position === null) return false;
    view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, position)));
    view.focus();
    return true;
}

function buildFigureInspector(view, projectImages) {
    const panel = document.createElement("section");
    panel.className = "semantic-figure-inspector";
    panel.hidden = true;
    const heading = document.createElement("strong");
    heading.textContent = "Figure layout and accessibility";
    const controls = document.createElement("div");
    controls.className = "semantic-figure-controls";
    const fields = new Map();
    const field = (name, label, kind, options = []) => {
        const wrapper = document.createElement("label");
        wrapper.className = "semantic-figure-field";
        if (kind === "checkbox")
            wrapper.classList.add("semantic-figure-field--check");
        const labelText = document.createElement("span");
        labelText.textContent = label;
        const input = kind === "select" ? document.createElement("select") : document.createElement("input");
        if (kind !== "select") input.type = kind;
        for (const [value, text] of options) {
            const option = document.createElement("option");
            option.value = value; option.textContent = text; input.append(option);
        }
        wrapper.append(...(kind === "checkbox" ? [input, labelText] : [labelText, input]));
        controls.append(wrapper); fields.set(name, input); return input;
    };
    field("imageId", "Project image", "select", projectImages.map(image => [image.id, image.fileName]));
    field("placement", "Placement", "select", [["inline", "Inline"], ["centered", "Centered"], ["float", "Floated"], ["fullWidth", "Full width"], ["fullBleed", "Full bleed"], ["dedicatedPage", "Dedicated page"]]);
    const width = field("widthPercent", "Width %", "number"); width.min = "1"; width.max = "100";
    field("alignment", "Alignment", "select", [["start", "Start"], ["center", "Center"], ["end", "End"]]);
    field("textWrap", "Text wrap", "select", [["none", "None"], ["start", "Text on start side"], ["end", "Text on end side"]]);
    field("fit", "Crop / fit", "select", [["contain", "Show whole image"], ["cover", "Fill frame"]]);
    for (const [name, label] of [["cropXPercent", "Crop position X %"], ["cropYPercent", "Crop position Y %"], ["spacingBeforePoints", "Space before pt"], ["spacingAfterPoints", "Space after pt"]]) {
        const input = field(name, label, "number"); input.step = ".5";
    }
    field("captionPlacement", "Caption", "select", [["below", "Below"], ["above", "Above"], ["overlay", "Overlay"], ["hidden", "Hidden"]]);
    field("startOnNewPage", "Start on new page", "checkbox");
    field("keepWithCaption", "Keep with caption", "checkbox");
    const alt = document.createElement("label");
    alt.className = "semantic-figure-field semantic-figure-field--wide";
    const altLabel = document.createElement("span"); altLabel.textContent = "Alternative text";
    const altInput = document.createElement("textarea"); altInput.rows = 2; alt.append(altLabel, altInput); controls.append(alt); fields.set("altText", altInput);
    field("decorative", "Decorative image", "checkbox");
    field("language", "Language", "text");
    field("accessibilityRole", "Semantic role", "select", [["figure", "Figure"], ["illustration", "Illustration"], ["diagram", "Diagram"], ["map", "Map"], ["photograph", "Photograph"], ["ornament", "Ornament"]]);
    const apply = () => {
        const selected = selectedFigure(view); if (!selected) return;
        const current = {...defaultFigurePresentation, ...(selected.node.attrs.presentation || {})};
        const number = name => Number(fields.get(name).value);
        const decorative = fields.get("decorative").checked;
        const altText = fields.get("altText").value.trim();
        if (!decorative && !altText) {
            fields.get("altText").setCustomValidity("Describe the image or mark it decorative.");
            fields.get("altText").reportValidity();
            return;
        }
        fields.get("altText").setCustomValidity("");
        const imageId = fields.get("imageId").value;
        const image = projectImages.find(candidate => candidate.id === imageId);
        const presentation = {
            ...current,
            placement: fields.get("placement").value,
            widthPercent: Math.min(100, Math.max(1, number("widthPercent"))),
            alignment: fields.get("alignment").value,
            textWrap: fields.get("textWrap").value,
            fit: fields.get("fit").value,
            cropXPercent: Math.min(100, Math.max(0, number("cropXPercent"))),
            cropYPercent: Math.min(100, Math.max(0, number("cropYPercent"))),
            spacingBeforePoints: Math.max(0, number("spacingBeforePoints")),
            spacingAfterPoints: Math.max(0, number("spacingAfterPoints")),
            startOnNewPage: fields.get("startOnNewPage").checked,
            keepWithCaption: fields.get("keepWithCaption").checked,
            captionPlacement: fields.get("captionPlacement").value
        };
        view.dispatch(view.state.tr.setNodeMarkup(selected.position, undefined, {
            ...selected.node.attrs,
            imageId,
            imageUrl: image?.previewUrl || selected.node.attrs.imageUrl,
            altText: decorative ? null : altText,
            decorative,
            language: fields.get("language").value.trim() || null,
            accessibilityRole: fields.get("accessibilityRole").value,
            presentation
        }));
    };
    for (const input of fields.values()) input.addEventListener("change", apply);
    panel.append(heading, controls);
    return {
        panel,
        update() {
            const selected = selectedFigure(view); panel.hidden = !selected; if (!selected) return;
            const attrs = selected.node.attrs;
            const presentation = {...defaultFigurePresentation, ...(attrs.presentation || {})};
            for (const name of ["placement", "widthPercent", "alignment", "textWrap", "fit", "cropXPercent", "cropYPercent", "spacingBeforePoints", "spacingAfterPoints", "captionPlacement"])
                fields.get(name).value = presentation[name];
            fields.get("startOnNewPage").checked = presentation.startOnNewPage;
            fields.get("keepWithCaption").checked = presentation.keepWithCaption;
            fields.get("imageId").value = attrs.imageId || "";
            fields.get("altText").value = attrs.altText || "";
            fields.get("decorative").checked = attrs.decorative;
            fields.get("language").value = attrs.language || "";
            fields.get("accessibilityRole").value = attrs.accessibilityRole || "figure";
        }
    };
}

async function setFigureImage(view, image, root) {
    if (!image?.id) return;
    const selected = selectedFigure(view);
    const currentPresentation = {
        ...defaultFigurePresentation,
        ...(selected?.node.attrs.presentation || {})
    };
    const fields = [];
    if (!selected) {
        fields.push({name: "caption", label: "Caption", type: "textarea", rows: 2, value: ""});
    }
    fields.push(
        {
            name: "altText",
            label: "Alternative text",
            type: "textarea",
            value: selected?.node.attrs.altText || image.altText || ""
        },
        {name: "decorative", label: "This image is decorative", type: "checkbox", value: selected?.node.attrs.decorative || false},
        {
            name: "fit",
            label: "Image fit",
            type: "select",
            value: currentPresentation.fit,
            options: [["contain", "Show the whole image"], ["cover", "Fill the frame"]]
        }
    );
    const values = await showEditorForm(root, {
        title: selected ? "Replace figure image" : "Insert figure",
        description: "Choose how the image is placed. You can change its layout and caption later.",
        submitLabel: selected ? "Replace image" : "Insert figure",
        fields,
        validate: value => !value.decorative && !value.altText.trim()
            ? "Add alternative text or mark the image decorative."
            : null
    });
    if (!values) return;
    const altText = values.altText.trim();
    const presentation = {...currentPresentation, fit: values.fit};
    if (!selected) {
        const content = values.caption.trim() ? schema.text(values.caption.trim()) : null;
        const node = schema.nodes.figure.create({
            id: newBlockId(),
            styleRole: "figure-caption",
            imageId: image.id,
            altText: values.decorative ? null : altText,
            imageUrl: image.previewUrl,
            decorative: values.decorative,
            accessibilityRole: "figure",
            presentation
        }, content);
        view.dispatch(view.state.tr.replaceSelectionWith(node).scrollIntoView());
    } else {
        view.dispatch(view.state.tr.setNodeMarkup(
            selected.position,
            undefined,
            {
                ...selected.node.attrs,
                imageId: image.id,
                imageUrl: image.previewUrl,
                altText: values.decorative ? null : altText,
                decorative: values.decorative,
                presentation
            }).scrollIntoView());
    }
    view.focus();
}

async function editFigureAltText(view, root) {
    const selected = selectedFigure(view);
    if (!selected) {
        showEditorNotice(root, "Select a figure image or place the cursor in its caption first.");
        return;
    }
    const values = await showEditorForm(root, {
        title: "Figure accessibility",
        description: "Describe meaningful images. Mark purely ornamental images as decorative.",
        submitLabel: "Apply",
        fields: [
            {name: "altText", label: "Alternative text", type: "textarea", value: selected.node.attrs.altText || ""},
            {name: "decorative", label: "Decorative image", type: "checkbox", value: selected.node.attrs.decorative}
        ],
        validate: value => !value.decorative && !value.altText.trim()
            ? "Enter alternative text or mark the image decorative."
            : null
    });
    if (!values) return;
    const altText = values.altText.trim();
    view.dispatch(view.state.tr.setNodeMarkup(
        selected.position,
        undefined,
        {...selected.node.attrs, altText: values.decorative ? null : altText, decorative: values.decorative}).scrollIntoView());
    view.focus();
}

async function editFigurePresentation(view, root) {
    const selected = selectedFigure(view);
    if (!selected) {
        showEditorNotice(root, "Select a figure image or place the cursor in its caption first.");
        return;
    }
    const current = {...defaultFigurePresentation, ...(selected.node.attrs.presentation || {})};
    const values = await showEditorForm(root, {
        title: "Figure layout",
        description: "Choose how the selected image participates in the manuscript flow.",
        submitLabel: "Apply",
        fields: [
            {
                name: "placement", label: "Placement", type: "select", value: current.placement,
                options: [
                    ["inline", "Inline"], ["centered", "Centered"], ["float", "Float with text"],
                    ["fullWidth", "Full width"], ["fullBleed", "Full bleed"], ["dedicatedPage", "Dedicated page"]
                ]
            },
            {name: "width", label: "Width (%)", type: "number", value: current.widthPercent},
            {
                name: "fit", label: "Image fit", type: "select", value: current.fit,
                options: [["contain", "Show whole image"], ["cover", "Crop to fill"]]
            }
        ],
        validate: value => {
            const width = Number(value.width);
            return Number.isFinite(width) && width > 0 && width <= 100
                ? null
                : "Width must be between 1 and 100 percent.";
        }
    });
    if (!values) return;
    const presentation = {
        ...current,
        placement: values.placement,
        widthPercent: Number(values.width),
        fit: values.fit,
        textWrap: values.placement === "float" ? (current.textWrap === "none" ? "end" : current.textWrap) : "none"
    };
    view.dispatch(view.state.tr.setNodeMarkup(
        selected.position,
        undefined,
        {...selected.node.attrs, presentation}).scrollIntoView());
    view.focus();
}


function textBlockMatches(doc, search) {
    if (!search) return [];
    const matches = [];
    doc.descendants((node, position) => {
        if (!node.isTextblock) return;
        let segmentText = "";
        let segmentPositions = [];
        let childOffset = 0;
        const flushSegment = () => {
            let index = segmentText.indexOf(search);
            while (index >= 0) {
                matches.push({
                    from: segmentPositions[index],
                    to: segmentPositions[index + search.length - 1] + 1
                });
                index = segmentText.indexOf(search, index + Math.max(1, search.length));
            }
            segmentText = "";
            segmentPositions = [];
        };
        node.forEach(child => {
            if (child.isText) {
                segmentText += child.text;
                for (let index = 0; index < child.text.length; index++)
                    segmentPositions.push(position + 1 + childOffset + index);
            } else if (child.type.name === "hard_break") {
                flushSegment();
            }
            childOffset += child.nodeSize;
        });
        flushSegment();
        return false;
    });
    return matches;
}

function replaceAll(view, search, replacement) {
    const matches = textBlockMatches(view.state.doc, search);
    if (matches.length === 0) return 0;
    let transaction = view.state.tr;
    for (const match of matches.reverse())
        transaction = transaction.insertText(replacement, match.from, match.to);
    view.dispatch(transaction.scrollIntoView());
    return matches.length;
}

function replaceAllInDocument(doc, search, replacement) {
    let state = EditorState.create({doc});
    const view = {
        get state() { return state; },
        dispatch(transaction) { state = state.apply(transaction); }
    };
    replaceAll(view, search, replacement);
    return state.doc;
}

function countMatches(doc, search) {
    return textBlockMatches(doc, search).length;
}

function buildFindPanel(view, root) {
    const panel = document.createElement("div");
    panel.className = "semantic-find-panel";
    panel.hidden = true;
    const find = document.createElement("input");
    find.type = "search";
    find.placeholder = "Find";
    find.setAttribute("aria-label", "Find text");
    const replacement = document.createElement("input");
    replacement.type = "text";
    replacement.placeholder = "Replace with";
    replacement.setAttribute("aria-label", "Replacement text");
    const result = document.createElement("output");
    const preview = document.createElement("output");
    preview.className = "semantic-find-preview";
    let currentMatch = 0;
    const update = () => {
        const matches = textBlockMatches(view.state.doc, find.value);
        const count = matches.length;
        currentMatch = count === 0 ? 0 : Math.min(currentMatch, count - 1);
        result.value = `${count} match${count === 1 ? "" : "es"}`;
        result.textContent = result.value;
        if (count === 0) {
            preview.textContent = "No replacement preview";
            return;
        }
        const match = matches[currentMatch];
        const context = view.state.doc.textBetween(
            Math.max(0, match.from - 24),
            Math.min(view.state.doc.content.size, match.to + 24),
            " ");
        preview.textContent =
            `${currentMatch + 1}/${count}: ${context} -> ${context.replace(find.value, replacement.value)}`;
    };
    find.addEventListener("input", update);
    const replaceButton = button("Replace all", "Replace all matching text", async () => {
        const count = countMatches(view.state.doc, find.value);
        if (count === 0) return;
        const confirmation = await showEditorForm(root, {
            title: "Replace matching text",
            description: `Replace ${count} match${count === 1 ? "" : "es"} throughout this chapter?`,
            submitLabel: "Replace all",
            fields: []
        });
        if (confirmation)
            replaceAll(view, find.value, replacement.value);
        update();
    });
    const previous = button("Previous", "Previous match", () => {
        const matches = textBlockMatches(view.state.doc, find.value);
        if (matches.length === 0) return;
        currentMatch = (currentMatch - 1 + matches.length) % matches.length;
        const match = matches[currentMatch];
        view.dispatch(view.state.tr.setSelection(
            TextSelection.create(view.state.doc, match.from, match.to)).scrollIntoView());
        update();
        view.focus();
    });
    const next = button("Next", "Next match", () => {
        const matches = textBlockMatches(view.state.doc, find.value);
        if (matches.length === 0) return;
        currentMatch = (currentMatch + 1) % matches.length;
        const match = matches[currentMatch];
        view.dispatch(view.state.tr.setSelection(
            TextSelection.create(view.state.doc, match.from, match.to)).scrollIntoView());
        update();
        view.focus();
    });
    replacement.addEventListener("input", update);
    panel.append(find, replacement, result, previous, next, preview, replaceButton);
    return {
        panel,
        open() {
            const scrollTop = root.scrollTop;
            panel.hidden = !panel.hidden;
            if (!panel.hidden) find.focus({preventScroll: true});
            root.scrollTop = scrollTop;
            if (typeof requestAnimationFrame === "function")
                requestAnimationFrame(() => { root.scrollTop = scrollTop; });
        },
        update
    };
}

function buildOutline(view) {
    const panel = document.createElement("aside");
    panel.className = "semantic-outline";
    panel.hidden = true;
    panel.setAttribute("aria-label", "Document outline");
    const heading = document.createElement("strong");
    heading.textContent = "Document outline";
    const list = document.createElement("ol");
    panel.append(heading, list);
    const update = () => {
        list.replaceChildren();
        view.state.doc.descendants((node, position) => {
            if (node.type.name !== "heading") return;
            const item = document.createElement("li");
            const jump = button(node.textContent || "(untitled heading)", "Move cursor to heading", () => {
                view.dispatch(view.state.tr.setSelection(TextSelection.near(view.state.doc.resolve(position + 1))).scrollIntoView());
                view.focus();
            });
            item.append(jump);
            list.append(item);
        });
        if (!list.hasChildNodes()) {
            const empty = document.createElement("li");
            empty.textContent = "No headings";
            list.append(empty);
        }
    };
    return {panel, open() { panel.hidden = !panel.hidden; update(); }, update};
}

function pasteNormalizationWarnings(
    event,
    paragraphRoles = builtInParagraphRoles,
    characterRoles = new Set(),
    imageById = new Map()) {
    const html = event.clipboardData?.getData("text/html");
    if (!html) return [];
    const document = new DOMParser().parseFromString(html, "text/html");
    const supported = new Set(["P", "BR", "H1", "H2", "H3", "H4", "H5", "H6", "BLOCKQUOTE", "UL", "OL", "LI", "HR", "FIGURE", "FIGCAPTION", "TABLE", "THEAD", "TBODY", "TFOOT", "TR", "TH", "TD", "EM", "I", "STRONG", "B", "U", "S", "DEL", "CODE", "A", "SPAN", "SUP", "SUB"]);
    const warnings = new Set();
    for (const element of document.body.querySelectorAll("*")) {
        if (!supported.has(element.tagName)) {
            warnings.add(`removed <${element.tagName.toLowerCase()}>`);
            continue;
        }
        if (element.tagName === "HR" && !element.hasAttribute("data-scene-break"))
            warnings.add("removed ordinary horizontal rule (only semantic scene breaks are supported)");
        if (/^H[3-6]$/.test(element.tagName))
            warnings.add(`converted <${element.tagName.toLowerCase()}> to the semantic subheading level`);
        if (element.tagName === "OL")
            warnings.add("converted ordered list to unordered list items");
        if (element.hasAttribute("data-style-role")
            && !paragraphRoles.has(element.dataset.styleRole))
            warnings.add(`replaced unknown paragraph style role '${element.dataset.styleRole}'`);
        if (element.tagName === "A" && !safeLink(element.getAttribute("href")))
            warnings.add("removed unsafe or unsupported link");
        if (element.tagName === "SPAN" && element.hasAttribute("lang")
            && !safeLanguage(element.getAttribute("lang")))
            warnings.add("removed invalid language tag");
        if (element.tagName === "SPAN" && element.hasAttribute("data-character-style")
            && !characterRoles.has(element.dataset.characterStyle))
            warnings.add(`removed unknown character style role '${element.dataset.characterStyle}'`);
        if (element.tagName === "SPAN" && element.hasAttribute("class")
            && element.className !== "semantic-small-caps")
            warnings.add("removed unsupported span class");
        if (element.tagName === "FIGURE"
            && !imageById.has(String(element.dataset.imageId || "").toLowerCase()))
            warnings.add("converted a figure that did not reference a project image to text");

        const allowed = element.tagName === "A"
            ? new Set(["href"])
            : element.tagName === "SPAN"
                ? new Set(["lang", "data-character-style", "class"])
                : ["TH", "TD"].includes(element.tagName)
                    ? new Set(["rowspan", "colspan"])
                    : element.tagName === "HR"
                    ? new Set(["data-scene-break", "data-style-role"])
                    : new Set();
        if ([...element.attributes].some(attribute => !allowed.has(attribute.name)))
            warnings.add(`normalized presentation attributes on <${element.tagName.toLowerCase()}>`);
    }
    return [...warnings];
}

function sanitizePastedSlice(slice, paragraphRoles, characterRoles, imageById) {
    const sanitizeMarks = marks => marks.flatMap(mark => {
        if (mark.type.name === "character_style"
            && (!validSemanticRole(mark.attrs.value) || !characterRoles.has(mark.attrs.value)))
            return [];
        if (mark.type.name === "language") {
            const value = safeLanguage(mark.attrs.value);
            return value ? [schema.marks.language.create({value})] : [];
        }
        if (mark.type.name === "link") {
            const value = safeLink(mark.attrs.value);
            return value ? [schema.marks.link.create({value})] : [];
        }
        return [mark];
    });

    const sanitizeNode = node => {
        if (node.isText)
            return schema.text(node.text, sanitizeMarks(node.marks));

        const children = [];
        node.forEach(child => children.push(sanitizeNode(child)));
        const content = Fragment.fromArray(children);
        if (!node.isBlock)
            return node.type.create(node.attrs, content, sanitizeMarks(node.marks));

        const fallbackRole = defaultRoleByNode[node.type.name] || "body";
        const styleRole = validSemanticRole(node.attrs.styleRole)
            && paragraphRoles.has(node.attrs.styleRole)
            ? node.attrs.styleRole
            : fallbackRole;
        if (node.type.name === "figure") {
            const image = imageById.get(String(node.attrs.imageId || "").toLowerCase());
            if (!image) {
                return schema.nodes.paragraph.create(
                    {id: newBlockId(), styleRole: "body"},
                    content);
            }
            const altText = node.attrs.altText?.trim() || image.altText?.trim() || "";
            return schema.nodes.figure.create({
                ...node.attrs,
                id: newBlockId(),
                styleRole,
                imageId: image.id,
                altText,
                imageUrl: image.previewUrl
            }, content);
        }
        const attrs = node.type.name === "table"
            ? {
                ...node.attrs,
                id: newBlockId(),
                tableId: newBlockId(),
                columnWidthWeights: node.attrs.columnWidthWeights?.length > 0
                    ? node.attrs.columnWidthWeights
                    : Array.from({length: Math.max(1, node.firstChild?.childCount || 1)}, () => 1)
            }
            : {...node.attrs, id: newBlockId()};
        return node.type.create({
            ...attrs,
            styleRole
        }, content, sanitizeMarks(node.marks));
    };

    return new Slice(
        Fragment.fromArray(slice.content.content.map(sanitizeNode)),
        slice.openStart,
        slice.openEnd);
}

function sanitizeHtmlForPaste(html, styles = [], images = []) {
    const htmlDocument = new window.DOMParser().parseFromString(html, "text/html");
    const slice = ProseMirrorDOMParser.fromSchema(schema).parseSlice(htmlDocument.body);
    const paragraphRoles = new Set([
        ...builtInParagraphRoles,
        ...styles.filter(style => style.kind === "paragraph").map(style => style.semanticRole)
    ]);
    const characterRoles = new Set(
        styles.filter(style => style.kind === "character").map(style => style.semanticRole));
    const imageById = new Map(images.map(image => [String(image.id).toLowerCase(), image]));
    return sanitizePastedSlice(slice, paragraphRoles, characterRoles, imageById);
}

function installEditorFontRules(root, fontFamilies) {
    const styleElement = document.createElement("style");
    styleElement.dataset.bookFonts = "true";
    const bundledFaces = {
        serif: [
            ["/fonts/lora/Lora-Regular.ttf", 400, false],
            ["/fonts/lora/Lora-Italic.ttf", 400, true],
            ["/fonts/lora/Lora-Bold.ttf", 700, false],
            ["/fonts/lora/Lora-BoldItalic.ttf", 700, true],
        ],
        sans: [
            ["/fonts/nunito/Nunito-Regular.ttf", 400, false],
            ["/fonts/nunito/Nunito-Italic.ttf", 400, true],
            ["/fonts/nunito/Nunito-Bold.ttf", 700, false],
            ["/fonts/nunito/Nunito-BoldItalic.ttf", 700, true],
        ],
        mono: [
            ["/fonts/roboto-mono/RobotoMono-Regular.ttf", 400, false],
            ["/fonts/roboto-mono/RobotoMono-Italic.ttf", 400, true],
            ["/fonts/roboto-mono/RobotoMono-Bold.ttf", 700, false],
            ["/fonts/roboto-mono/RobotoMono-BoldItalic.ttf", 700, true],
        ],
    };
    for (const [key, faces] of Object.entries(bundledFaces)) {
        const family = editorFontFamily(key)?.split(",", 1)[0];
        for (const [url, weight, italic] of faces)
            styleElement.textContent += `@font-face{font-family:${family};src:url(${JSON.stringify(url)}) format("truetype");font-weight:${weight};font-style:${italic ? "italic" : "normal"};font-display:swap}\n`;
    }
    for (const family of fontFamilies) {
        const cssFamily = editorFontFamily(family.key);
        if (!cssFamily) continue;
        for (const face of family.faces || []) {
            if (!face.contentUrl) continue;
            styleElement.textContent += `@font-face{font-family:${cssFamily};src:url(${JSON.stringify(face.contentUrl)});font-weight:${face.weight || 400};font-style:${face.italic === true ? "italic" : "normal"};font-display:swap}\n`;
        }
    }
    root.append(styleElement);
}

function typographyDeclarations(style, extra = {}, includeSpacing = true) {
    if (!style) return [];
    const declarations = [];
    const family = editorFontFamily(style.fontFamilyKey);
    if (family) declarations.push(`font-family:${family}`);
    if (Number.isFinite(style.fontSizePoints)) declarations.push(`font-size:${style.fontSizePoints}pt`);
    if (Number.isInteger(style.fontWeight)) declarations.push(`font-weight:${style.fontWeight}`);
    if (style.italic === true) declarations.push("font-style:italic");
    else if (style.italic === false) declarations.push("font-style:normal");
    const textAlign = typeof style.textAlign === "string" ? style.textAlign.trim().toLowerCase() : "";
    if (["left", "right", "center", "justify"].includes(textAlign)) declarations.push(`text-align:${textAlign}`);
    if (Number.isFinite(style.lineHeight)) declarations.push(`line-height:${style.lineHeight}`);
    const textColor = cssRgb(style.textColorRgb);
    if (textColor) declarations.push(`color:${textColor}`);
    if (includeSpacing) {
        const before = style.spacingBeforePoints ?? style.spaceBeforePoints;
        const after = style.spacingAfterPoints ?? style.spaceAfterPoints;
        if (Number.isFinite(before)) declarations.push(`margin-top:${before}pt`);
        if (Number.isFinite(after)) declarations.push(`margin-bottom:${after}pt`);
    }
    if (Number.isFinite(style.leftIndentEm))
        declarations.push(`margin-left:${style.leftIndentEm}em`, `--lk-blockquote-left-indent:${style.leftIndentEm}em`, `--lk-caption-left-indent:${style.leftIndentEm}em`);
    if (Number.isFinite(style.rightIndentEm))
        declarations.push(`margin-right:${style.rightIndentEm}em`, `--lk-caption-right-indent:${style.rightIndentEm}em`);
    if (Number.isFinite(style.firstLineIndentEm)) declarations.push(`text-indent:${style.firstLineIndentEm}em`);
    for (const [property, value] of Object.entries(extra))
        if (value !== null && value !== undefined) declarations.push(`${property}:${value}`);
    return declarations;
}

function cssRgb(channels, alpha = null) {
    if (!Array.isArray(channels) || channels.length !== 3 || channels.some(channel => !Number.isFinite(channel)))
        return null;
    const bytes = channels.map(channel => Math.round(Math.max(0, Math.min(1, channel)) * 255));
    const opacity = Number.isFinite(alpha) ? ` / ${Math.max(0, Math.min(1, alpha))}` : "";
    return `rgb(${bytes.join(" ")}${opacity})`;
}

function installTypographyRules(root, typography = {}) {
    const styleElement = document.createElement("style");
    styleElement.dataset.bookTypography = "true";
    const selector = value => `.semantic-editor.semantic-editor .semantic-prosemirror ${value}`;
    const update = current => {
        const rules = [];
        const add = (target, declarations) => {
            if (declarations.length > 0)
                rules.push(`${selector(target)}{${declarations.join(";")}}`);
        };
        const body = current.body || {};
        add("> *", typographyDeclarations(body, {}, false));
        add("p", typographyDeclarations(body));
        add("[data-style-role=body]", typographyDeclarations(body));
        const headings = current.headings || {};
        for (let level = 1; level <= 6; level++)
            add(`h${level}`, typographyDeclarations(headings[String(level)]));
        add("blockquote", typographyDeclarations(current.blockquote));
        add("[data-style-role=chapter-heading]", typographyDeclarations(current.chapterHeading));
        add("[data-style-role=block-quote]", typographyDeclarations(current.blockquote));
        const blockquote = current.blockquote || {};
        const decoration = blockquote.decoration || {};
        const ruleColor = cssRgb(decoration.ruleColorRgb) || "transparent";
        const ruleWidth = decoration.ruleWidthEm ?? 0;
        const ruleGap = decoration.ruleGapEm ?? 0;
        const blockquoteDecorationDeclarations = [
            `margin-left:max(0em,calc(var(--lk-blockquote-left-indent,0em) - ${ruleWidth + ruleGap}em))!important`,
            `padding-left:${ruleGap}em`,
            `border-left:${ruleWidth}em solid ${ruleColor}`
        ];
        add("blockquote", blockquoteDecorationDeclarations);
        add("[data-style-role=block-quote]", blockquoteDecorationDeclarations);
        add("figure figcaption", typographyDeclarations(current.caption));
        add(".semantic-list-item", [
            ...typographyDeclarations(body, {}, false),
            ...typographyDeclarations(current.listItem, {}, false),
            "list-style:none",
            "display:block",
            "position:relative",
            `margin-left:${current.listItem?.leftIndentEm ?? 1}em`,
            "padding-left:0",
            `margin-top:${current.listItem?.spaceBeforePoints ?? 0}pt`,
            `margin-bottom:${current.listItem?.spaceAfterPoints ?? 0}pt`
        ]);
        const bullet = JSON.stringify(current.listItem?.bullet || "• ");
        const hangingIndent = current.listItem?.hangingIndentEm ?? 1;
        rules.push(`${selector(".semantic-list-item")}::before{content:${bullet};position:absolute;left:${-hangingIndent}em;}`);
        rules.push(`${selector(".semantic-list-item[data-list-marker]")}::before{content:attr(data-list-marker);}`);
        add("hr[data-scene-break]", [
            ...typographyDeclarations(current.sceneBreak, {}, false),
            `margin-top:${current.sceneBreak?.spaceBeforePoints ?? 0}pt`,
            `margin-bottom:${current.sceneBreak?.spaceAfterPoints ?? 0}pt`
        ]);
        rules.push(`${selector("hr[data-scene-break]")}::after{content:${JSON.stringify(current.sceneBreak?.text || "* * *")}}`);
        const superscript = current.inline?.superscript || {};
        const subscript = current.inline?.subscript || {};
        add("sup", [`font-size:${superscript.sizeScale ?? 0.7}em`, `vertical-align:${superscript.baselineShiftEm ?? 0.35}em`]);
        add("sub", [`font-size:${subscript.sizeScale ?? 0.7}em`, `vertical-align:${subscript.baselineShiftEm ?? -0.2}em`]);
        add("code", ["font-family:'Lorekeeper-builtin-roboto-mono',monospace", "font-size:1em"]);
        add(".semantic-small-caps", ["font-variant-caps:small-caps"]);
        rules.push(`${selector("figure[data-caption-placement=above]")} > figcaption{order:-1;}`);
        const overlay = current.caption?.overlay || {};
        const overlayText = cssRgb(overlay.textColorRgb) || "#fff";
        const overlayBackground = cssRgb(overlay.backgroundColorRgb, overlay.backgroundOpacity ?? 0) || "transparent";
        const overlayVertical = overlay.paddingVerticalEm ?? 0;
        const overlayHorizontal = overlay.paddingHorizontalEm ?? 0;
        rules.push(`${selector("figure[data-caption-placement=overlay]")} > figcaption{position:absolute;inset-inline:0;bottom:0;box-sizing:border-box;margin-left:0!important;margin-right:0!important;padding-block:${overlayVertical}em;padding-inline-start:calc(${overlayHorizontal}em + var(--lk-caption-left-indent,0em));padding-inline-end:calc(${overlayHorizontal}em + var(--lk-caption-right-indent,0em));color:${overlayText};background:${overlayBackground};}`);
        rules.push(`${selector("figure[data-caption-placement=hidden]")} > figcaption{display:block;padding:.25em .5em;border:1px dashed var(--lk-line-strong,#cbd3df);color:var(--lk-text-muted,#647086);background:var(--lk-surface-subtle,transparent);opacity:.9;}`);
        rules.push(`${selector("figure[data-caption-placement=hidden]")} > figcaption::before{content:"Hidden in Read";display:block;margin-bottom:.2em;font-family:'Lorekeeper-builtin-nunito',sans-serif;font-size:.72em;font-style:normal;font-weight:700;letter-spacing:.04em;}`);
        styleElement.textContent = rules.join("\n");
    };
    update(typography);
    root.append(styleElement);
    return {update};
}

function installNamedStyleRules(root, styles) {
    const styleElement = document.createElement("style");
    styleElement.dataset.bookTextStyles = "true";
    const update = currentStyles => {
        styleElement.textContent = "";
        for (const style of currentStyles) {
            const definition = style.definition || {};
            const selector = style.kind === "character"
                ? `.semantic-editor.semantic-editor .semantic-prosemirror span[data-character-style=${JSON.stringify(style.semanticRole)} i]`
                : `.semantic-editor.semantic-editor .semantic-prosemirror [data-style-role=${JSON.stringify(style.semanticRole)} i]:not(figure),`
                    + `.semantic-editor.semantic-editor .semantic-prosemirror figure[data-style-role=${JSON.stringify(style.semanticRole)} i] > figcaption`;
            const declarations = [];
            const family = editorFontFamily(definition.fontFamilyKey);
            if (family) declarations.push(`font-family:${family}`);
            if (definition.fontSizePoints) declarations.push(`font-size:${definition.fontSizePoints}pt`);
            if (definition.fontWeight) declarations.push(`font-weight:${definition.fontWeight}`);
            if (definition.italic !== null && definition.italic !== undefined)
                declarations.push(`font-style:${definition.italic ? "italic" : "normal"}`);
            if (definition.smallCaps !== null && definition.smallCaps !== undefined)
                declarations.push(`font-variant-caps:${definition.smallCaps ? "small-caps" : "normal"}`);
            if (definition.lineHeight) declarations.push(`line-height:${definition.lineHeight}`);
            if (definition.spaceBeforePoints !== null && definition.spaceBeforePoints !== undefined)
                declarations.push(`margin-top:${definition.spaceBeforePoints}pt`);
            if (definition.spaceAfterPoints !== null && definition.spaceAfterPoints !== undefined)
                declarations.push(`margin-bottom:${definition.spaceAfterPoints}pt`);
            if (definition.leftIndentEm !== null && definition.leftIndentEm !== undefined)
                declarations.push(`margin-left:${definition.leftIndentEm}em`, `--lk-blockquote-left-indent:${definition.leftIndentEm}em`, `--lk-caption-left-indent:${definition.leftIndentEm}em`);
            if (definition.rightIndentEm !== null && definition.rightIndentEm !== undefined)
                declarations.push(`margin-right:${definition.rightIndentEm}em`, `--lk-caption-right-indent:${definition.rightIndentEm}em`);
            if (definition.firstLineIndentEm !== null && definition.firstLineIndentEm !== undefined)
                declarations.push(`text-indent:${definition.firstLineIndentEm}em`);
            if (["left", "right", "center", "justify"].includes(definition.textAlign?.toLowerCase()))
                declarations.push(`text-align:${definition.textAlign.toLowerCase()}`);
            if (declarations.length > 0)
                styleElement.textContent += `${selector}{${declarations.join(";")}}\n`;
        }
    };
    update(styles);
    root.append(styleElement);
    return {update};
}

function canonicalPlainText(doc, manuscriptId, revision) {
    const domain = domainFromDocument(doc, manuscriptId, revision);
    return domain.content
        .filter(block => block.type !== "designedPage")
        .map(block => block.type === "sceneBreak"
            ? "***"
            : domainBlockText(block))
        .join("\n\n");
}

function editorialText(doc, manuscriptId, revision) {
    const domain = domainFromDocument(doc, manuscriptId, revision);
    return domain.content
        .filter(block => !["sceneBreak", "designedPage"].includes(block.type))
        .map(domainBlockText)
        .join("\n\n");
}

function changeListLevel(view, delta) {
    if (view.state.selection.$from.parent.type.name !== "list_item") return false;
    const {from, to} = view.state.selection;
    const group = view.state.selection.$from.parent.attrs.list?.id || newBlockId();
    let transaction = view.state.tr;
    view.state.doc.nodesBetween(from, to, (node, position) => {
        if (node.type.name !== "list_item") return true;
        const list = node.attrs.list || {id: group, ordered: false, level: 0, start: null};
        transaction = transaction.setNodeMarkup(position, undefined, {...node.attrs,
            list: {...list, level: Math.max(0, Math.min(8, list.level + delta)), start: null}});
        return false;
    });
    view.dispatch(transaction.scrollIntoView());
    view.focus();
    return true;
}

async function editListFormatting(view, root) {
    const before = view.state.doc;
    const {from, to} = view.state.selection;
    const current = view.state.selection.$from.parent.attrs.list;
    const values = await showEditorForm(root, {
        title: "List formatting", submitLabel: "Apply list",
        fields: [
            {name: "ordered", label: "Numbered list", type: "checkbox", value: current?.ordered ?? false},
            {name: "level", label: "Nesting level (0–8)", type: "number", value: current?.level ?? 0},
            {name: "start", label: "Restart at (leave blank to continue)", type: "number", value: current?.start ?? ""}
        ],
        validate: value => !Number.isInteger(Number(value.level)) || Number(value.level) < 0 || Number(value.level) > 8
            ? "Use a nesting level from 0 through 8."
            : String(value.start).trim() && (!value.ordered || !Number.isInteger(Number(value.start)) || Number(value.start) < 1 || Number(value.start) > 1000000)
                ? "A numbered list can restart at a whole number from 1 through 1,000,000." : null
    });
    if (!values) return;
    if (view.state.doc !== before) {
        showEditorNotice(root, "The manuscript changed. Select the list again before applying formatting.");
        return;
    }
    const group = current?.ordered === values.ordered ? current.id : newBlockId();
    let transaction = view.state.tr;
    let first = true;
    before.nodesBetween(from, to, (node, position) => {
        if (!node.isTextblock || !schema.nodes.list_item.validContent(node.content)) return true;
        const start = first && values.ordered && String(values.start).trim() ? Number(values.start) : null;
        transaction = transaction.setNodeMarkup(position, schema.nodes.list_item, {...node.attrs,
            styleRole: "list-item", list: {id: group, ordered: values.ordered, level: Number(values.level), start}});
        first = false;
        return false;
    });
    view.dispatch(transaction.scrollIntoView());
    view.focus();
}

function domainBlockText(block) {
    if (block.type === "table") {
        return (block.table?.rows || []).map(row =>
            (row.cells || []).map(cell =>
                (cell.content || []).map(domainBlockText).join("\n")).join("\t")).join("\n");
    }
    return (block.content || []).map(inline => inline.text || "").join("");
}

function hydrateFigureImageUrls(document, imageById) {
    for (const block of document.content || []) {
        if (block.type === "table") {
            for (const row of block.table?.rows || [])
                for (const cell of row.cells || []) hydrateFigureImageUrls({content: cell.content || []}, imageById);
        }
        const image = block.type === "figure"
            ? imageById.get(String(block.imageId).toLowerCase())
            : null;
        block.imageUrl = image?.previewUrl ?? null;
    }
    return document;
}

function hydrateDesignedPageSummaries(document, designedPageById) {
    for (const block of document.content || []) {
        const summary = block.type === "designedPage"
            ? designedPageById.get(String(block.designedPageId).toLowerCase())
            : null;
        block.designedPageName = summary?.name ?? null;
        block.designedPageSurfaceLabel = summary?.surfaceLabel ?? null;
        block.designedPageStatus = summary?.status ?? null;
        block.designedPagePreviewUrl = summary?.previewUrl ?? null;
    }
    return document;
}

async function insertRichTable(view, root) {
    const values = await showEditorForm(root, {
        title: "Insert table",
        description: "Create a semantic table. Column widths begin equal and can be refined by imported or future layout tools.",
        submitLabel: "Insert",
        fields: [
            {name: "rows", label: "Rows", type: "number", value: "2", required: true},
            {name: "columns", label: "Columns", type: "number", value: "2", required: true},
            {name: "header", label: "Use first row as headers", type: "checkbox", value: true}
        ],
        validate: value => {
            const rows = Number(value.rows);
            const columns = Number(value.columns);
            return !Number.isInteger(rows) || rows < 1 || rows > 50 || !Number.isInteger(columns) || columns < 1 || columns > 20
                ? "Use 1–50 rows and 1–20 columns."
                : null;
        }
    });
    if (!values) return;
    const rowCount = Number(values.rows);
    const columnCount = Number(values.columns);
    const rows = Array.from({length: rowCount}, (_, rowIndex) => schema.nodes.table_row.create({
        id: newBlockId(),
        header: values.header && rowIndex === 0
    }, Array.from({length: columnCount}, () => schema.nodes.table_cell.create({
        id: newBlockId(),
        rowSpan: 1,
        columnSpan: 1,
        header: values.header && rowIndex === 0
    }, schema.nodes.paragraph.create({id: newBlockId(), styleRole: "body"})))));
    const table = schema.nodes.table.create({
        id: newBlockId(),
        styleRole: "table",
        tableId: newBlockId(),
        columnWidthWeights: Array(columnCount).fill(1),
        headerRowCount: values.header ? 1 : 0
    }, rows);
    const transaction = view.state.tr.replaceSelectionWith(table);
    let tablePosition = null;
    transaction.doc.descendants((node, position) => {
        if (tablePosition !== null) return false;
        if (node.type.name === "table" && node.attrs.id === table.attrs.id) tablePosition = position;
        return tablePosition === null;
    });
    if (tablePosition !== null)
        transaction.setSelection(Selection.near(transaction.doc.resolve(tablePosition + 1)));
    view.dispatch(transaction.scrollIntoView());
    view.focus();
}

// Column weights become percentage widths so empty cells keep their share under fixed table layout.
function tableColumnGroup(weights) {
    const valid = (weights || []).filter(weight => Number.isFinite(weight) && weight > 0);
    const total = valid.reduce((sum, weight) => sum + weight, 0);
    return ["colgroup", {}, ...valid.map(weight => ["col", {style: `width: ${(weight / total * 100).toFixed(3)}%`}])];
}

// Moves the cursor to the next or previous cell of the innermost table; false outside tables.
function moveToAdjacentTableCell(view, direction) {
    const {$from} = view.state.selection;
    let cellDepth = $from.depth;
    while (cellDepth > 0 && $from.node(cellDepth).type.name !== "table_cell") cellDepth--;
    if (cellDepth === 0) return false;
    const tableDepth = cellDepth - 2;
    const tableStart = $from.start(tableDepth);
    const cellPositions = [];
    $from.node(tableDepth).descendants((node, position) => {
        if (node.type.name !== "table_cell") return true;
        cellPositions.push(tableStart + position);
        return false;
    });
    const target = cellPositions[cellPositions.indexOf($from.before(cellDepth)) + direction];
    if (target !== undefined)
        view.dispatch(view.state.tr.setSelection(Selection.near(view.state.doc.resolve(target + 1))).scrollIntoView());
    return true;
}

function insertNote(view, root, kind) {
    if (!view.state.selection.$from.parent.inlineContent) {
        showEditorNotice(root, "Place the cursor in a paragraph, list item, figure caption, or table cell first.");
        return;
    }
    const noteId = newBlockId();
    const referenceId = newBlockId();
    const notes = structuredClone(view.state.doc.attrs.notes || []);
    notes.push({
        id: noteId,
        kind,
        content: [{
            id: newBlockId(),
            type: "paragraph",
            styleRole: "body",
            headingLevel: null,
            imageId: null,
            altText: null,
            content: []
        }]
    });
    const reference = schema.nodes.note_reference.create({id: referenceId, noteId, kind});
    view.dispatch(view.state.tr
        .setSelection(TextSelection.create(view.state.doc, view.state.selection.to))
        .replaceSelectionWith(reference)
        .setDocAttribute("notes", notes)
        .scrollIntoView());
    view.focus();
    return noteId;
}

export function citationItemsFromForm(values, count, originalItems = []) {
    const items = [];
    for (let ordinal = 1; ordinal <= count; ordinal += 1) {
        const bibliographicRecordId = values[`record${ordinal}`];
        if (!bibliographicRecordId) continue;
        const original = originalItems[ordinal - 1];
        items.push({
            bibliographicRecordId,
            prefix: values[`prefix${ordinal}`].trim(),
            suffix: values[`suffix${ordinal}`].trim(),
            locatorLabel: values[`locatorLabel${ordinal}`],
            locatorValue: values[`locatorValue${ordinal}`].trim(),
            sourceLocationId: original?.bibliographicRecordId === bibliographicRecordId ? original.sourceLocationId ?? null : null
        });
    }
    return items;
}

async function insertOrEditCitation(view, root, loadBibliography) {
    const initialDocument = view.state.doc;
    const initialSelection = view.state.selection;
    const existing = initialSelection instanceof NodeSelection && initialSelection.node.type.name === "citation"
        ? initialSelection.node : null;
    const originalItems = existing?.attrs.items || [];
    if (!view.state.selection.$from.parent.inlineContent) {
        showEditorNotice(root, "Place the cursor in a paragraph, list item, figure caption, or table cell first.");
        return;
    }
    let bibliography;
    try {
        bibliography = await loadBibliography();
    } catch (error) {
        showEditorNotice(root, error?.message || "The bibliography could not be loaded. Try again.");
        view.focus();
        return;
    }
    if (view.state.doc !== initialDocument) {
        showEditorNotice(root, "The manuscript changed while the bibliography was loading. Select the citation or insertion point and try again.");
        view.focus();
        return;
    }
    if (!bibliography.length) {
        showEditorNotice(root, "Add a bibliographic record in Sources before inserting a citation.");
        return;
    }
    const options = [["", "Choose a record"]].concat(bibliography.map(record => [
        String(record.id),
        `${record.title}${record.issuedYear ? ` (${record.issuedYear})` : ""}`
    ]));
    const fields = [];
    const count = Math.max(3, originalItems.length + 1);
    for (let ordinal = 1; ordinal <= count; ordinal += 1) {
        const item = originalItems[ordinal - 1];
        fields.push(
            {name: `record${ordinal}`, label: ordinal === 1 ? "Source" : `Additional source ${ordinal}`, type: "select", value: item?.bibliographicRecordId || "", options},
            {name: `prefix${ordinal}`, label: `Prefix ${ordinal}`, type: "text", value: item?.prefix || ""},
            {name: `locatorLabel${ordinal}`, label: `Locator label ${ordinal}`, type: "text", value: item?.locatorLabel ?? "page"},
            {name: `locatorValue${ordinal}`, label: `Locator ${ordinal}`, type: "text", value: item?.locatorValue || ""},
            {name: `suffix${ordinal}`, label: `Suffix ${ordinal}`, type: "text", value: item?.suffix || ""}
        );
    }
    const values = await showEditorForm(root, {
        title: existing ? "Edit citation" : "Insert citation",
        description: "Choose bibliography items for this citation cluster. Prefixes, suffixes, and locators stay attached to their individual items. Leave an additional source blank to remove it.",
        submitLabel: existing ? "Save citation" : "Insert citation",
        fields,
        validate: value => !value.record1 ? "Choose at least one bibliographic record." : null
    });
    if (!values) { view.focus(); return; }
    if (view.state.doc !== initialDocument) {
        showEditorNotice(root, "The manuscript changed while the citation was open. Select the citation or insertion point and try again.");
        view.focus();
        return;
    }
    const items = citationItemsFromForm(values, count, originalItems);
    // Like notes, a new citation goes after the selection; replacing it would delete a selected note marker and its note.
    view.dispatch(view.state.tr
        .setSelection(existing ? initialSelection : TextSelection.create(initialDocument, initialSelection.to))
        .replaceSelectionWith(schema.nodes.citation.create({id: existing?.attrs.id || newBlockId(), items}))
        .scrollIntoView());
    view.focus();
}

const authoringJournalDatabase = "LorekeeperAuthoringJournalV1";
const authoringJournalStore = "batches";

function authoringId() {
    return crypto.randomUUID();
}

function canonicalJson(value) {
    if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
    if (!value || typeof value !== "object") return JSON.stringify(value);
    return `{${Object.keys(value).filter(key => value[key] !== null && value[key] !== undefined)
        .sort().map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(",")}}`;
}

async function sha256(value) {
    const bytes = new TextEncoder().encode(value);
    const hash = await crypto.subtle.digest("SHA-256", bytes);
    return `sha256:${[...new Uint8Array(hash)].map(byte => byte.toString(16).padStart(2, "0")).join("")}`;
}

// A rejected batch never advances the server session. Once its local journal
// entry is explicitly discarded, all client watermarks must return to the
// server's next value; otherwise the next local batch skips a sequence and the
// fence can wait forever for a receipt that cannot exist.
export function authoringSequenceWatermarks(nextSequence) {
    if (!Number.isSafeInteger(nextSequence) || nextSequence <= 0)
        throw new Error("The authoring session did not provide a valid next sequence.");
    return {
        nextSequence,
        highestLocalSequence: nextSequence - 1,
        lastDispatchedSequence: nextSequence - 1,
        lastAcknowledgedSequence: nextSequence - 1
    };
}

// The editor is made read-only while a persistent Undo/Redo move waits for
// its receipt. Its already-authorized local inverse is the one exception: it
// is dispatched synchronously under applyingHistory, whereas every external
// document-changing transaction remains blocked.
export function shouldApplyAuthoringTransaction(transaction, {readOnly, hasUnresolvedHistory, applyingHistory}) {
    return !transaction.docChanged || applyingHistory || (!readOnly && !hasUnresolvedHistory);
}

class AuthoringJournalV1 {
    constructor(projectId, targetId, sessionId, onFailure) {
        this.projectId = projectId;
        this.targetId = targetId;
        this.sessionId = sessionId;
        this.onFailure = onFailure;
        this.recoverable = true;
    }

    async database() {
        if (this.db) return this.db;
        this.db = await new Promise((resolve, reject) => {
            const request = indexedDB.open(authoringJournalDatabase, 1);
            request.onupgradeneeded = () => {
                const db = request.result;
                const store = db.createObjectStore(authoringJournalStore, {keyPath: "key"});
                store.createIndex("byTarget", ["projectId", "targetId", "sequence"]);
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error || new Error("The local authoring journal could not open."));
        });
        return this.db;
    }

    async write(entry) {
        try {
            const db = await this.database();
            await new Promise((resolve, reject) => {
                const transaction = db.transaction(authoringJournalStore, "readwrite");
                transaction.objectStore(authoringJournalStore).put(entry);
                transaction.oncomplete = resolve;
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
            return true;
        } catch (error) {
            this.recoverable = false;
            this.onFailure(error);
            return false;
        }
    }

    async remove(key) {
        try {
            const db = await this.database();
            await new Promise((resolve, reject) => {
                const transaction = db.transaction(authoringJournalStore, "readwrite");
                transaction.objectStore(authoringJournalStore).delete(key);
                transaction.oncomplete = resolve;
                transaction.onerror = () => reject(transaction.error);
                transaction.onabort = () => reject(transaction.error);
            });
            return true;
        } catch (error) {
            this.recoverable = false;
            this.onFailure(error);
            return false;
        }
    }

    async pending() {
        try {
            const db = await this.database();
            return await new Promise((resolve, reject) => {
                const request = db.transaction(authoringJournalStore, "readonly")
                    .objectStore(authoringJournalStore)
                    .index("byTarget")
                    .getAll(IDBKeyRange.bound(
                        [this.projectId, this.targetId, Number.MIN_SAFE_INTEGER],
                        [this.projectId, this.targetId, Number.MAX_SAFE_INTEGER]));
                request.onsuccess = () => resolve(request.result || []);
                request.onerror = () => reject(request.error);
            });
        } catch (error) {
            this.recoverable = false;
            this.onFailure(error);
            return [];
        }
    }
}

function blockText(block) {
    return domainBlockText(block);
}

function sameJson(left, right) {
    return canonicalJson(left ?? null) === canonicalJson(right ?? null);
}

function markOperations(block) {
    const operations = [];
    let offset = 0;
    for (const inline of block.content || []) {
        const endOffset = offset + (inline.text || "").length;
        for (const mark of inline.marks || []) {
            operations.push({
                kind: "setInlineMark",
                blockId: block.id,
                startOffset: offset,
                endOffset,
                mark: mark.type,
                enabled: true,
                value: mark.value ?? null
            });
        }
        offset = endOffset;
    }
    return operations;
}

function wireBlockProperties(block) {
    return {
        styleRole: block.styleRole,
        imageId: block.imageId ?? null,
        altText: block.altText ?? null,
        headingLevel: block.headingLevel ?? null,
        decorative: block.decorative === true,
        language: block.language ?? null,
        accessibilityRole: block.accessibilityRole ?? null,
        figurePresentation: block.figurePresentation ?? null,
        designedPageId: block.designedPageId ?? null
    };
}

function sameInlineContent(left, right) {
    return sameJson(left?.content || [], right?.content || []);
}

function sameFigureProperties(left, right) {
    return left.imageId === right.imageId
        && left.altText === right.altText
        && left.decorative === right.decorative
        && left.language === right.language
        && left.accessibilityRole === right.accessibilityRole
        && sameJson(left.figurePresentation, right.figurePresentation);
}

// Translate the ProseMirror semantic document, never its DOM or HTML. The
// server validates these operations against its pre-state and derives inverses.
function manuscriptTextBlocks(document) {
    const result = [];
    const visit = (blocks, path) => {
        for (const block of blocks || []) {
            if (block.table) {
                for (const row of block.table.rows || [])
                    for (const cell of row.cells || [])
                        visit(cell.content, [...path, `table:${block.table.id}`, `row:${row.id}`, `cell:${cell.id}`]);
            } else result.push({block, path});
        }
    };
    visit(document.content, ["document"]);
    for (const note of document.notes || []) visit(note.content, ["notes", note.id]);
    return result;
}

function inlineAuthoringOperations(before, after) {
    const structure = document => {
        const copy = structuredClone(document);
        copy.revision = 0;
        for (const {block} of manuscriptTextBlocks(copy)) block.content = [];
        return copy;
    };
    if (!sameJson(structure(before), structure(after))) return null;
    const previous = new Map(manuscriptTextBlocks(before).map(item => [item.block.id, item]));
    return manuscriptTextBlocks(after).filter(({block}) => !sameInlineContent(previous.get(block.id).block, block))
        .map(({block, path}) => ({
            kind: "replaceInlineContent", blockId: block.id,
            position: {documentId: before.manuscriptId, containerPath: path, blockOrAtomId: block.id, offset: 0, affinity: "after"},
            inlineContent: structuredClone(block.content || [])
        }));
}

export function authoringOperations(before, after) {
    const hasRichContent = value => (value.notes || []).length > 0
        || (value.content || []).some(block => block.type === "table" || block.list
            || (block.content || []).some(inline => inline.type === "citation"));
    if (hasRichContent(before) || hasRichContent(after)) {
        const inline = inlineAuthoringOperations(before, after);
        if (inline !== null) return inline;
        return sameJson(before.content || [], after.content || []) && sameJson(before.notes || [], after.notes || [])
            ? []
            : [{kind: "replaceRichDocument", richDocument: after}];
    }
    const operations = [];
    const beforeById = new Map((before.content || []).map((block, index) => [block.id, {block, index}]));
    const afterById = new Map((after.content || []).map((block, index) => [block.id, {block, index}]));

    for (const block of before.content || []) {
        if (!afterById.has(block.id)) operations.push({kind: "deleteBlock", blockId: block.id});
    }
    for (let index = 0; index < (after.content || []).length; index++) {
        const afterBlock = after.content[index];
        const existing = beforeById.get(afterBlock.id)?.block;
        if (!existing) {
            operations.push({
                kind: "insertBlock",
                blockId: afterBlock.id,
                index,
                blockType: afterBlock.type,
                text: blockText(afterBlock),
                ...wireBlockProperties(afterBlock)
            });
            if (afterBlock.paragraphPresentation)
                operations.push({kind: "setParagraphPresentation", blockId: afterBlock.id, paragraphPresentation: afterBlock.paragraphPresentation});
            operations.push(...markOperations(afterBlock));
            continue;
        }
        if (existing.type !== afterBlock.type) operations.push({
            kind: "setBlockType", blockId: afterBlock.id, blockType: afterBlock.type,
            ...wireBlockProperties(afterBlock)
        });
        else if (existing.styleRole !== afterBlock.styleRole)
            operations.push({kind: "setBlockStyle", blockId: afterBlock.id, styleRole: afterBlock.styleRole});
        // Replace before reapplying every desired mark run. This deliberately
        // handles mark-only edits and removed marks: AddMark-only deltas cannot
        // express a deterministic removal when a valued mark changed.
        if (!sameInlineContent(existing, afterBlock)) {
            operations.push({kind: "replaceBlockText", blockId: afterBlock.id, text: blockText(afterBlock)});
            operations.push(...markOperations(afterBlock));
        }
        if (afterBlock.type === "figure" && existing.type === "figure"
            && !sameFigureProperties(existing, afterBlock))
            operations.push({
                kind: "setFigurePresentation",
                blockId: afterBlock.id,
                imageId: afterBlock.imageId ?? null,
                altText: afterBlock.altText ?? null,
                decorative: afterBlock.decorative === true,
                language: afterBlock.language ?? null,
                accessibilityRole: afterBlock.accessibilityRole ?? "figure",
                figurePresentation: afterBlock.figurePresentation ?? {...defaultFigurePresentation}
            });
        if (!sameJson(existing.paragraphPresentation, afterBlock.paragraphPresentation))
            operations.push({kind: "setParagraphPresentation", blockId: afterBlock.id, paragraphPresentation: afterBlock.paragraphPresentation ?? null});
        if (beforeById.get(afterBlock.id).index !== index)
            operations.push({kind: "moveBlock", blockId: afterBlock.id, index});
    }
    return operations;
}

function baseOrderPrecondition(beforeById, afterBlocks, blockId) {
    const index = afterBlocks.findIndex(block => block.id === blockId);
    if (index < 0) return null;
    let previous = null;
    for (let cursor = index - 1; cursor >= 0; cursor--) {
        const candidate = afterBlocks[cursor];
        if (candidate.id !== blockId && beforeById.has(candidate.id)) {
            previous = candidate;
            break;
        }
    }
    let next = null;
    for (let cursor = index + 1; cursor < afterBlocks.length; cursor++) {
        const candidate = afterBlocks[cursor];
        if (candidate.id !== blockId && beforeById.has(candidate.id)) {
            next = candidate;
            break;
        }
    }
    return {
        previousBlockId: previous?.id ?? null,
        previousBlockFingerprint: previous ? beforeById.get(previous.id).fingerprint : null,
        nextBlockId: next?.id ?? null,
        nextBlockFingerprint: next ? beforeById.get(next.id).fingerprint : null
    };
}

export function addAuthoringPreconditions(operations, before, after, fingerprints, documentFingerprint = null) {
    const beforeBlocks = before.content || [];
    const afterBlocks = after.content || [];
    const beforeById = new Map(beforeBlocks.map(block => [block.id, {
        block,
        fingerprint: fingerprints.get(block.id) || null
    }]));
    for (const operation of operations) {
        const kind = String(operation.kind || "").toLowerCase();
        if (kind === "replacerichdocument") {
            operation.expectedDocumentFingerprint = documentFingerprint;
            continue;
        }
        if (kind === "replaceinlinecontent") {
            operation.expectedElementFingerprint = fingerprints.get(operation.blockId) || null;
            continue;
        }
        const blockId = operation.blockId || operation.placementBlockId;
        if (blockId && beforeById.has(blockId))
            operation.expectedElementFingerprint = beforeById.get(blockId).fingerprint;
        if (kind === "mergeblocks" && operation.secondBlockId && beforeById.has(operation.secondBlockId))
            operation.expectedSecondElementFingerprint = beforeById.get(operation.secondBlockId).fingerprint;
        if (kind !== "insertblock" && kind !== "insertdesignedpageplacement" && kind !== "moveblock")
            continue;
        const targetId = kind === "insertdesignedpageplacement" ? operation.placementBlockId : operation.blockId;
        const order = baseOrderPrecondition(beforeById, afterBlocks, targetId);
        if (!order)
            continue;
        operation.expectedOrder = order;
        const anchorId = order.nextBlockId || order.previousBlockId;
        if (anchorId) {
            operation.insertAt = order.nextBlockId
                ? {beforeBlockId: order.nextBlockId}
                : {afterBlockId: order.previousBlockId};
            operation.expectedAnchorFingerprint = beforeById.get(anchorId).fingerprint;
            if (kind === "insertblock" || kind === "insertdesignedpageplacement")
                delete operation.index;
        }
    }
}

export function operationsForTarget(operations, ordinal = 0) {
    return (operations || []).filter(operation => Number(
        operation.targetOrdinal ?? operation.TargetOrdinal ?? 0) === ordinal);
}

function applyAuthoringOperations(view, operations, imageById = new Map(), designedPageById = new Map()) {
    const positionForIndex = (doc, index) => {
        let position = 0;
        for (let current = 0; current < Math.max(0, Math.min(index, doc.childCount)); current++)
            position += doc.child(current).nodeSize;
        return position;
    };
    const hydratedFromWire = domain => documentFromDomain(hydrateDesignedPageSummaries(
        hydrateFigureImageUrls(structuredClone(domain), imageById), designedPageById));
    const nodeFromWire = operation => hydratedFromWire({content: [operation.canonicalBlock]}).firstChild;
    const insertNodeFromWire = operation => hydratedFromWire({content: [
        String(operation.kind).toLowerCase() === "insertdesignedpageplacement"
            ? {id: operation.placementBlockId, type: "designedPage", styleRole: "designed-page", designedPageId: operation.pageId, content: []}
            : {
                id: operation.blockId,
                type: operation.blockType,
                ...wireBlockProperties(operation),
                content: operation.text ? [{text: operation.text, marks: []}] : []
            }
    ]}).firstChild;
    for (const operation of operations || []) {
        const kind = String(operation.kind || "").toLowerCase();
        const blockId = operation.blockId || operation.placementBlockId;
        const position = blockId ? blockPositionById(view.state.doc, blockId) : null;
        let transaction = view.state.tr;
        if (kind === "restoreblock") {
            const existing = blockPositionById(view.state.doc, operation.canonicalBlock.id);
            const replacement = nodeFromWire(operation);
            transaction = Number.isInteger(existing)
                ? transaction.replaceWith(existing, existing + view.state.doc.nodeAt(existing).nodeSize, replacement)
                : transaction.insert(positionForIndex(view.state.doc, operation.index), replacement);
        } else if (kind === "replacerichdocument" || kind === "insertsemanticfragment") {
            const replacement = hydratedFromWire(kind === "insertsemanticfragment"
                ? insertSemanticFragment(domainFromDocument(view.state.doc, operation.position.documentId, 0),
                    operation.position, operation.richDocument, operation.secondBlockId)
                : operation.richDocument);
            transaction = transaction
                .replaceWith(0, view.state.doc.content.size, replacement.content)
                .setDocAttribute("notes", replacement.attrs.notes || []);
        } else if (kind === "replaceinlinecontent") {
            if (operation.position?.containerPath?.[0] === "notes") {
                const notes = structuredClone(view.state.doc.attrs.notes || []);
                const note = notes.find(item => item.id === operation.position.containerPath[1]);
                const block = note?.content.find(item => item.id === operation.blockId);
                if (!block) throw new Error("The saved note edit no longer matches this manuscript.");
                block.content = structuredClone(operation.inlineContent);
                transaction = transaction.setDocAttribute("notes", notes);
            } else {
                if (!Number.isInteger(position)) throw new Error("The saved inline edit no longer matches this manuscript.");
                const node = view.state.doc.nodeAt(position);
                const noteKinds = new Map((view.state.doc.attrs.notes || []).map(note => [note.id, note.kind]));
                const content = operation.inlineContent.flatMap(inline => inlineFromDomain({
                    ...inline, kind: inline.type === "noteReference" ? noteKinds.get(inline.noteId) : inline.kind
                }));
                transaction = transaction.replaceWith(position + 1, position + 1 + node.content.size, content);
            }
        } else if (kind === "deleteblock" || kind === "removedesignedpageplacement") {
            if (!Number.isInteger(position)) continue;
            transaction = transaction.delete(position, position + view.state.doc.nodeAt(position).nodeSize);
        } else if (kind === "insertblock" || kind === "insertdesignedpageplacement") {
            transaction = transaction.insert(positionForIndex(view.state.doc, operation.index), insertNodeFromWire(operation));
        } else if (!Number.isInteger(position)) {
            continue;
        } else if (kind === "replaceblocktext") {
            const node = view.state.doc.nodeAt(position);
            if (!node.inlineContent) continue;
            const replacement = documentFromDomain({content: [{id: blockId, type: nodeToBlockType[node.type.name], styleRole: node.attrs.styleRole, headingLevel: node.attrs.level, content: operation.text ? [{text: operation.text, marks: []}] : []}]}).firstChild.content;
            transaction = transaction.replaceWith(position + 1, position + 1 + node.content.size, replacement);
        } else if (kind === "moveblock") {
            const node = view.state.doc.nodeAt(position);
            transaction = transaction.delete(position, position + node.nodeSize);
            const destination = positionForIndex(transaction.doc, operation.index);
            transaction = transaction.insert(destination, node);
        } else if (kind === "setblockstyle" || kind === "setparagraphpresentation" || kind === "setblocktype") {
            const node = view.state.doc.nodeAt(position);
            const attrs = {...node.attrs};
            if (kind === "setblockstyle") attrs.styleRole = operation.styleRole;
            if (kind === "setparagraphpresentation") attrs.paragraphPresentation = operation.paragraphPresentation;
            if (kind === "setblocktype") {
                attrs.styleRole = operation.styleRole;
                attrs.imageId = operation.imageId ?? null;
                attrs.altText = operation.altText ?? null;
                attrs.level = operation.headingLevel ?? attrs.level;
                attrs.decorative = operation.decorative === true;
                attrs.language = operation.language ?? null;
                attrs.accessibilityRole = operation.accessibilityRole ?? null;
                attrs.presentation = operation.figurePresentation ?? null;
                attrs.designedPageId = operation.designedPageId ?? null;
            }
            const type = kind === "setblocktype"
                ? schema.nodes[blockTypeToNode[operation.blockType]]
                : node.type;
            transaction = transaction.setNodeMarkup(position, type, attrs);
        } else if (kind === "setinlinemark") {
            const node = view.state.doc.nodeAt(position);
            const markType = schema.marks[markTypeToName[operation.mark]];
            if (!node?.inlineContent || !markType) continue;
            const from = position + 1 + Math.max(0, Math.min(operation.startOffset, node.content.size));
            const to = position + 1 + Math.max(0, Math.min(operation.endOffset, node.content.size));
            const mark = markType.create(operation.value ? {value: operation.value} : null);
            transaction = operation.enabled ? transaction.addMark(from, to, mark) : transaction.removeMark(from, to, markType);
        } else if (kind === "setfigurepresentation") {
            const node = view.state.doc.nodeAt(position);
            if (node?.type.name !== "figure") continue;
            transaction = transaction.setNodeMarkup(position, node.type, {
                ...node.attrs,
                imageId: operation.imageId ?? null,
                altText: operation.altText ?? null,
                decorative: operation.decorative === true,
                language: operation.language ?? null,
                accessibilityRole: operation.accessibilityRole ?? "figure",
                presentation: operation.figurePresentation ?? {...defaultFigurePresentation}
            });
        } else {
            continue;
        }
        view.dispatch(transaction);
    }
}

export async function attach(root, dotNetRef, debounceMs, initialJson, stylesJson = "[]", imagesJson = "[]", editionsJson = "[]", compositionsJson = "[]", fontFamiliesJson = "[]", allowDesignedPages = true, annotationsJson = "[]", typographyJson = "{}", allowAnnotations = true, performanceTraceEnabled = false, authoringTargetJson = "{}") {
    if (!root || typeof root.replaceChildren !== "function" || root.isConnected === false)
        return null;

    const attachmentStartedAt = performance.now();
    let initial = JSON.parse(initialJson);
    const authoringTarget = JSON.parse(authoringTargetJson);
    const namedStyles = JSON.parse(stylesJson);
    const projectImages = JSON.parse(imagesJson);
    JSON.parse(editionsJson);
    const designedPages = JSON.parse(compositionsJson);
    const fontFamilies = JSON.parse(fontFamiliesJson);
    let reviewAnnotations = allowAnnotations ? JSON.parse(annotationsJson) : [];
    let typography = JSON.parse(typographyJson);
    const imageById = new Map(projectImages.map(image => [String(image.id).toLowerCase(), image]));
    const designedPageById = new Map(designedPages.map(page => [String(page.id).toLowerCase(), page]));
    const paragraphRoles = new Set([
        ...builtInParagraphRoles,
        ...namedStyles
            .filter(style => style.kind === "paragraph")
            .map(style => style.semanticRole)
    ]);
    const characterRoles = new Set(
        namedStyles
            .filter(style => style.kind === "character")
            .map(style => style.semanticRole));
    const stageImportResources = resources => {
        if (!resources) return;
        for (const image of resources.images)
            imageById.set(String(image.id).toLowerCase(), {id: image.id, previewUrl: `data:${image.contentType};base64,${image.data}`});
        for (const style of resources.styles) {
            if (!namedStyles.some(existing => existing.id === style.id)) namedStyles.push({...style, semanticRole: style.role});
            (String(style.kind).toLowerCase() === "character" ? characterRoles : paragraphRoles).add(style.role);
        }
    };
    const discardImportResources = resources => {
        if (!resources) return;
        for (const image of resources.images) imageById.delete(String(image.id).toLowerCase());
        for (const style of resources.styles) {
            const index = namedStyles.findIndex(existing => existing.id === style.id);
            if (index >= 0) namedStyles.splice(index, 1);
            (String(style.kind).toLowerCase() === "character" ? characterRoles : paragraphRoles).delete(style.role);
        }
    };
    hydrateFigureImageUrls(initial, imageById);
    hydrateDesignedPageSummaries(initial, designedPageById);
    let manuscriptId = initial.manuscriptId;
    let revision = initial.revision;
    const sessionStorageKey = `lorekeeper-authoring-session-v1:${authoringTarget.projectId}:${authoringTarget.targetId}`;
    let sessionId = sessionStorage.getItem(sessionStorageKey);
    if (!sessionId) {
        sessionId = authoringId();
        sessionStorage.setItem(sessionStorageKey, sessionId);
    }
    let authoringSession = null;
    let nextSequence = 0;
    let highestLocalSequence = -1;
    let lastDispatchedSequence = -1;
    let lastAcknowledgedSequence = -1;
    let requestedReadOnly = false;
    let targetVersion = {generation: 0, fingerprint: ""};
    let elementFingerprints = new Map();
    let pendingActionLabel = "Edit manuscript";
    let releaseWriterLease = null;
    let applyingAuthoringHistory = false;
    let pendingLocalTransition = null;
    // A locally visible Undo/Redo may be waiting for the batch it reverses to
    // receive its receipt. It is deliberately distinct from an unresolved
    // history RPC: this keeps the writer dirty during the short compensation
    // window between those two durable operations.
    let pendingVisibleHistoryMove = false;
    let historyMoveInFlight = false;
    let fenceFrozen = false;
    // This is a view cache of the process-owned cursor, never an authority or
    // persistence mechanism. It is rebuilt from the server cursor on remount.
    let confirmedHistory = [];
    let confirmedHistoryCursor = 0;
    let confirmedDocument = structuredClone(initial);
    let queuedDocument = structuredClone(initial);
    let dispatchPaused = false;
    let journalFailure = null;
    let pendingJournalCount = 0;
    const journal = new AuthoringJournalV1(authoringTarget.projectId, authoringTarget.targetId, sessionId, error => {
        dispatchPaused = true;
        journalFailure = error?.message || "The browser could not safely store this edit for recovery.";
        requestedReadOnly = true;
        void dotNetRef.invokeMethodAsync("OnAuthoringJournalState", false, journalFailure).catch(() => {});
    });
    try {
        authoringSession = await dotNetRef.invokeMethodAsync("InitializeAuthoringSession", {
            protocolId: "AuthoringBatchProtocolV1",
            projectId: authoringTarget.projectId,
            sessionId,
            targets: [{targetId: authoringTarget.targetId}]
        });
        ({nextSequence, highestLocalSequence, lastDispatchedSequence, lastAcknowledgedSequence}
            = authoringSequenceWatermarks(Number(authoringSession?.nextSequence || 0)));
        const target = authoringSession?.targets?.find(item => item.targetId === authoringTarget.targetId);
        if (target?.manuscriptJson) {
            initial = hydrateDesignedPageSummaries(
                hydrateFigureImageUrls(JSON.parse(target.manuscriptJson), imageById),
                designedPageById);
            manuscriptId = initial.manuscriptId;
            revision = initial.revision;
            confirmedDocument = structuredClone(initial);
            queuedDocument = structuredClone(initial);
            targetVersion = {generation: target.generation, fingerprint: target.fingerprint};
            elementFingerprints = new Map(Object.entries(target.elementFingerprints || {}));
        }
        const pending = await journal.pending();
        pendingJournalCount = pending.length;
        for (const entry of pending) stageImportResources(entry.batch?.importResources);
        if (pending.length > 0) {
            // The server's next sequence reflects only acknowledged/replayed
            // work. Reserve every recovered journal sequence before a new
            // local edit can enter the serialized queue.
            nextSequence = Math.max(nextSequence, ...pending.map(entry => Number(entry.sequence) + 1));
            highestLocalSequence = Math.max(highestLocalSequence, nextSequence - 1);
            const recovery = pending[pending.length - 1];
            if (recovery.afterJson) {
                initial = JSON.parse(recovery.afterJson);
                manuscriptId = initial.manuscriptId;
                revision = initial.revision;
                queuedDocument = structuredClone(initial);
            }
        }
    } catch (error) {
        dispatchPaused = true;
        journalFailure = error?.message || "The authoring session could not open.";
    }
    if (navigator.locks?.request && authoringTarget.targetId) {
        let acquired;
        const acquiredPromise = new Promise(resolve => { acquired = resolve; });
        void navigator.locks.request(
            `lorekeeper-authoring-writer:${authoringTarget.projectId}:${authoringTarget.targetId}`,
            {mode: "exclusive", ifAvailable: true},
            async lock => {
                acquired(!!lock);
                if (!lock) return;
                await new Promise(resolve => { releaseWriterLease = resolve; });
            });
        if (!await acquiredPromise) {
            requestedReadOnly = true;
            journalFailure = "This manuscript is being edited in another window. Flush or close that editor to take over.";
        }
    }
    let timer = null;
    hydrateFigureImageUrls(initial, imageById);
    let saveChain = Promise.resolve(true);
    let changeGeneration = 0;
    let savedGeneration = 0;
    let readOnly = false;
    let importBusy = false;
    let attachmentDisposed = false;
    let importCancelled = false;
    let pendingImport = null;
    let noteEditor = null;
    let updateFormattingControls = () => {};
    let persistentHistoryState = {canUndo: false, canRedo: false, undoLabel: null, redoLabel: null};
    let performPersistentHistory = async () => false;
    // A history RPC can commit before its response is lost. Hold its exact
    // idempotency identity until the canonical replay response is available;
    // no later mutation may use pre-history revision/generation metadata.
    let unresolvedHistoryRequest = null;
    let reconcileUnresolvedHistory = async () => true;
    let traceSequence = 0;
    const recordVisibleFrame = (metric, startedAt) => {
        if (!performanceTraceEnabled || !Number.isFinite(startedAt)) return;
        requestAnimationFrame(() => {
            const durationMilliseconds = performance.now() - startedAt;
            if (!Number.isFinite(durationMilliseconds) || durationMilliseconds < 0) return;
            void dotNetRef.invokeMethodAsync(
                "RecordPerformanceTrace",
                metric,
                durationMilliseconds,
                ++traceSequence).catch(() => {});
        });
    };
    const applyEffectiveReadOnly = () => {
        readOnly = requestedReadOnly || fenceFrozen || historyMoveInFlight || importBusy;
        if (!view) return;
        view.setProps({editable: () => !readOnly});
        for (const control of root.querySelectorAll("button, select, input"))
            control.disabled = readOnly;
        root.classList.toggle("semantic-editor--readonly", readOnly);
        noteEditor?.update();
    };
    const notifyWriterState = () => {
        // A failed IndexedDB operation makes even an otherwise acknowledged
        // view unsafe to claim as flushable. Keep the fence conservative until
        // the author resolves the local recovery failure.
        const dirty = savedGeneration < changeGeneration
            || pendingJournalCount > 0
            || pendingVisibleHistoryMove
            || unresolvedHistoryRequest !== null
            || !journal.recoverable
            || !!journalFailure;
        return dotNetRef.invokeMethodAsync(
            "OnAuthoringWriterState",
            targetVersion.generation || 0,
            highestLocalSequence,
            lastDispatchedSequence,
            lastAcknowledgedSequence,
            dirty,
            journal.recoverable && !journalFailure,
            // Paused dispatch (conflict, receipt uncertainty, or quota) is a
            // recoverability state, not a disconnected browser. Disposal is
            // the only path that reports this writer as unreachable.
            true).catch(() => {});
    };

    const toolbar = document.createElement("div");
    toolbar.className = "semantic-editor-toolbar";
    toolbar.setAttribute("role", "toolbar");
    toolbar.setAttribute("aria-label", "Manuscript formatting");
    const editorChrome = document.createElement("div");
    editorChrome.className = "semantic-editor-chrome";
    editorChrome.append(toolbar);
    const surface = document.createElement("div");
    surface.className = "semantic-editor-surface";
    const persistentCaret = document.createElement("span");
    persistentCaret.className = "semantic-editor-persistent-caret";
    persistentCaret.setAttribute("aria-hidden", "true");
    const status = document.createElement("div");
    status.className = "semantic-editor-status";
    status.setAttribute("aria-live", "polite");
    const conflictPanel = document.createElement("section");
    conflictPanel.className = "semantic-editor-conflict";
    conflictPanel.hidden = true;
    root.classList.add("semantic-editor-root");
    root.replaceChildren(editorChrome, surface, status, conflictPanel);
    installEditorFontRules(root, fontFamilies);
    const typographyRules = installTypographyRules(root, typography);
    const namedStyleRules = installNamedStyleRules(root, namedStyles);

    let view;
    let caretFrame = null;
    const updatePersistentCaret = () => {
        caretFrame = null;
        if (!view || readOnly || view.hasFocus() || !root.isConnected) {
            persistentCaret.hidden = true;
            return;
        }

        const selection = view.state.selection;
        const position = selection instanceof NodeSelection
            ? selection.to
            : selection.head ?? selection.to;
        try {
            const coordinates = view.coordsAtPos(position, -1);
            const surfaceBounds = surface.getBoundingClientRect();
            const computedSize = Number.parseFloat(getComputedStyle(view.dom).fontSize) || 17;
            const caretHeight = Math.min(32, Math.max(18, coordinates.bottom - coordinates.top || computedSize * 1.35));
            persistentCaret.style.left = `${coordinates.left - surfaceBounds.left}px`;
            persistentCaret.style.top = `${coordinates.top - surfaceBounds.top}px`;
            persistentCaret.style.height = `${caretHeight}px`;
            persistentCaret.hidden = false;
        } catch {
            persistentCaret.hidden = true;
        }
    };
    const schedulePersistentCaret = () => {
        if (caretFrame !== null) cancelAnimationFrame(caretFrame);
        caretFrame = requestAnimationFrame(updatePersistentCaret);
    };
    const refreshReviewAnnotations = async () => {
        if (!allowAnnotations) return;
        try {
            reviewAnnotations = await dotNetRef.invokeMethodAsync("GetReviewAnnotations");
            view.dispatch(view.state.tr.setMeta(annotationsKey, true));
        } catch {}
    };
    const authoringSelection = stable => ({
        kind: "manuscript",
        targets: [{
            targetId: authoringTarget.targetId,
            blockId: stable?.blockId ?? null,
            offset: stable?.headOffset ?? 0,
            affinity: "forward"
        }]
    });
    const showConflict = (result, entry) => {
        const conflict = result?.conflicts?.[0];
        const canonical = conflict?.canonical?.manuscriptJson;
        conflictPanel.hidden = false;
        conflictPanel.replaceChildren();
        const heading = document.createElement("strong");
        heading.textContent = "This edit conflicts with a newer manuscript change";
        const details = document.createElement("p");
        details.textContent = `${conflict?.message || "Both versions were retained."} Local batch ${entry.batch.batchId}.`;
        const summary = document.createElement("p");
        try {
            const localText = editorialText(documentFromDomain(JSON.parse(entry.afterJson)), manuscriptId, revision);
            const canonicalText = canonical
                ? editorialText(documentFromDomain(JSON.parse(canonical)), manuscriptId, revision)
                : "";
            summary.textContent = `Local version: ${localText.length.toLocaleString()} characters. Newer version: ${canonicalText.length.toLocaleString()} characters.`;
        } catch {
            summary.textContent = "Both the local and newer manuscript variants are retained for your decision.";
        }
        const retry = button("Retry", "Retry this unchanged local batch", async () => {
            try {
                const retryResult = await dotNetRef.invokeMethodAsync("DispatchAuthoringBatch", entry.batch);
                const retryCanonical = retryResult?.canonicalVersions?.find(item => item.targetId === authoringTarget.targetId);
                if (!["committed", "replayed"].includes(String(retryResult?.status || "").toLowerCase()) || !retryCanonical) {
                    showConflict(retryResult, entry);
                    return;
                }
                await dotNetRef.invokeMethodAsync("AcknowledgeAuthoringReceipt", {
                    projectId: authoringTarget.projectId, sessionId, receiptId: retryResult.receiptId,
                    batchId: entry.batch.batchId, requestHash: entry.batch.requestHash
                });
                if (!await journal.remove(entry.key))
                    throw new Error("The saved edit could not be cleared from the local recovery journal.");
                pendingJournalCount = Math.max(0, pendingJournalCount - 1);
                revision = retryCanonical.afterRevision;
                targetVersion = {generation: retryCanonical.generation, fingerprint: retryCanonical.fingerprint};
                const retryTarget = retryResult.targets?.find(item => item.targetId === authoringTarget.targetId);
                if (retryTarget?.elementFingerprints)
                    elementFingerprints = new Map(Object.entries(retryTarget.elementFingerprints));
                confirmedDocument = JSON.parse(entry.afterJson);
                pendingImport = null;
                confirmedHistory.splice(confirmedHistoryCursor);
                confirmedHistory.push({
                    inverse: retryResult.inverse?.operations || [],
                    forward: entry.batch.operations,
                    beforeSelection: entry.batch.selection?.before || null,
                    afterSelection: entry.batch.selection?.after || null
                });
                confirmedHistoryCursor = confirmedHistory.length;
                lastAcknowledgedSequence = Math.max(lastAcknowledgedSequence, Number(entry.batch.sequence));
                if (sameJson(
                    domainFromDocument(view.state.doc, manuscriptId, confirmedDocument.revision),
                    confirmedDocument)) {
                    queuedDocument = structuredClone(confirmedDocument);
                    pendingLocalTransition = null;
                    savedGeneration = changeGeneration;
                }
                conflictPanel.hidden = true;
                dispatchPaused = false;
                notifyWriterState();
                status.textContent = "The local edit was saved.";
            } catch (error) {
                dispatchPaused = true;
                notifyWriterState();
                status.textContent = error?.message || "The local batch could not be retried.";
            }
        });
        const discard = button("Use newer version", "Discard this local batch and use the newer canonical manuscript", async () => {
            if (!canonical) return;
            // This is an explicit author decision, never a conflict-side effect.
            try {
                // A rejected dispatch rolls back on the server. Re-open the
                // same durable session before dropping its journal entry so
                // the next batch reuses the server's expected sequence rather
                // than skipping the rejected one.
                const reopened = await dotNetRef.invokeMethodAsync("InitializeAuthoringSession", {
                    protocolId: "AuthoringBatchProtocolV1",
                    projectId: authoringTarget.projectId,
                    sessionId,
                    targets: [{targetId: authoringTarget.targetId}]
                });
                const reopenedTarget = reopened?.targets?.find(item => item.targetId === authoringTarget.targetId);
                if (!reopenedTarget?.manuscriptJson)
                    throw new Error("The current canonical manuscript could not be loaded for this conflict.");
                const watermarks = authoringSequenceWatermarks(Number(reopened.nextSequence));
                if (!await journal.remove(entry.key))
                    throw new Error("The discarded local batch could not be cleared from the recovery journal.");
                pendingJournalCount = Math.max(0, pendingJournalCount - 1);
                authoringSession = reopened;
                ({nextSequence, highestLocalSequence, lastDispatchedSequence, lastAcknowledgedSequence} = watermarks);
                replaceDocument(reopenedTarget.manuscriptJson);
                discardImportResources(entry.batch.importResources);
                namedStyleRules.update(namedStyles);
                pendingImport = null;
                confirmedDocument = JSON.parse(reopenedTarget.manuscriptJson);
                queuedDocument = structuredClone(confirmedDocument);
                revision = reopenedTarget.revision;
                targetVersion = {generation: reopenedTarget.generation, fingerprint: reopenedTarget.fingerprint};
                elementFingerprints = new Map(Object.entries(reopenedTarget.elementFingerprints || {}));
                pendingLocalTransition = null;
                savedGeneration = changeGeneration;
                dispatchPaused = false;
                conflictPanel.hidden = true;
                notifyWriterState();
                status.textContent = "The newer manuscript version is now shown.";
            } catch (error) {
                dispatchPaused = true;
                notifyWriterState();
                status.textContent = error?.message || "The local batch could not be discarded.";
            }
        });
        conflictPanel.append(heading, details, summary, retry, discard);
    };
    const saveNow = () => {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
        const targetGeneration = changeGeneration;
        const saveStartedAt = performance.now();
        const after = domainFromDocument(view.state.doc, manuscriptId, revision);
        const selection = captureStableSelection(view);
        saveChain = saveChain.catch(() => false).then(async () => {
            if (!await reconcileUnresolvedHistory()) return false;
            if (savedGeneration >= targetGeneration) return !dispatchPaused;
            if (dispatchPaused || !journal.recoverable || !authoringSession) return false;
            const before = queuedDocument;
            const imported = pendingImport;
            const operations = imported ? [imported.operation] : authoringOperations(before, after);
            addAuthoringPreconditions(operations, before, after, elementFingerprints, targetVersion.fingerprint);
            if (operations.length === 0) {
                savedGeneration = Math.max(savedGeneration, targetGeneration);
                return true;
            }
            const batch = {
                protocolId: "AuthoringBatchProtocolV1",
                projectId: authoringTarget.projectId,
                sessionId,
                batchId: authoringId(),
                sequence: nextSequence++,
                actionLabel: pendingActionLabel,
                targets: [{
                    ordinal: 0,
                    targetId: authoringTarget.targetId,
                    expectedRevision: revision,
                    expectedGeneration: targetVersion.generation,
                    baseFingerprint: targetVersion.fingerprint
                }],
                operations: operations.map(operation => ({targetOrdinal: 0, ...operation})),
                selection: {
                    before: authoringSelection(pendingLocalTransition?.beforeSelection),
                    after: authoringSelection(selection)
                }
            };
            if (imported) batch.importResources = imported.resources;
            batch.requestHash = await sha256(canonicalJson({
                protocolId: batch.protocolId, projectId: batch.projectId, sessionId: batch.sessionId,
                batchId: batch.batchId, sequence: batch.sequence, actionLabel: batch.actionLabel,
                targets: batch.targets, operations: batch.operations, selection: batch.selection,
                ...(batch.importResources ? {importResources: batch.importResources} : {})
            }));
            const entry = {
                key: `${authoringTarget.projectId}|${authoringTarget.targetId}|${batch.batchId}`,
                projectId: authoringTarget.projectId,
                targetId: authoringTarget.targetId,
                sequence: batch.sequence,
                batch,
                beforeJson: JSON.stringify(before),
                afterJson: JSON.stringify(after),
                createdAt: Date.now()
            };
            if (!await journal.write(entry)) return false;
            pendingJournalCount++;
            queuedDocument = structuredClone(after);
            lastDispatchedSequence = batch.sequence;
            highestLocalSequence = Math.max(highestLocalSequence, batch.sequence);
            notifyWriterState();
            try {
                const result = await dotNetRef.invokeMethodAsync("DispatchAuthoringBatch", batch);
                const applied = ["committed", "replayed"].includes(String(result?.status || "").toLowerCase());
                const canonical = result?.canonicalVersions?.find(item => item.targetId === authoringTarget.targetId);
                if (!applied || !canonical) {
                    dispatchPaused = true;
                    status.textContent = result?.conflicts?.[0]?.message || "The edit needs resolution before it can be saved.";
                    showConflict(result, entry);
                    return false;
                }
                revision = canonical.afterRevision;
                targetVersion = {generation: canonical.generation, fingerprint: canonical.fingerprint};
                const canonicalTarget = result.targets?.find(item => item.targetId === authoringTarget.targetId);
                if (canonicalTarget?.elementFingerprints)
                    elementFingerprints = new Map(Object.entries(canonicalTarget.elementFingerprints));
                await dotNetRef.invokeMethodAsync("AcknowledgeAuthoringReceipt", {
                    projectId: authoringTarget.projectId,
                    sessionId,
                    receiptId: result.receiptId,
                    batchId: batch.batchId,
                    requestHash: batch.requestHash
                });
                if (!await journal.remove(entry.key))
                    throw new Error("The saved edit could not be cleared from the local recovery journal.");
                pendingJournalCount = Math.max(0, pendingJournalCount - 1);
                confirmedDocument = structuredClone(after);
                confirmedHistory.splice(confirmedHistoryCursor);
                confirmedHistory.push({
                    inverse: result.inverse?.operations || [],
                    forward: batch.operations,
                    beforeSelection: entry.batch.selection.before,
                    afterSelection: batch.selection.after,
                });
                confirmedHistoryCursor = confirmedHistory.length;
                // Typing can continue while the batch is in flight. Only clear
                // the coalesced local transition if this acknowledgement is
                // still describing the visible document.
                if (changeGeneration === targetGeneration) {
                    pendingLocalTransition = null;
                }
                savedGeneration = Math.max(savedGeneration, targetGeneration);
                persistentHistoryState = result.history?.state || persistentHistoryState;
                if (pendingImport === imported) pendingImport = null;
                lastAcknowledgedSequence = batch.sequence;
                notifyWriterState();
                await refreshReviewAnnotations();
                updateFormattingControls();
                updateStatus();
                recordVisibleFrame("save-acknowledgment", saveStartedAt);
                return true;
            } catch (error) {
                // The batch/receipt remains journaled. Do not report a clean
                // save or fabricate a new batch over an uncertain receipt;
                // remount recovery will replay the idempotent batch and retry
                // the acknowledgement.
                dispatchPaused = true;
                notifyWriterState();
                status.textContent = error?.message || "The edit is waiting to be saved.";
                return false;
            }
        });
        return saveChain;
    };

    const scheduleSave = () => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { void saveNow(); }, debounceMs);
    };

    const replaceDocument = json => {
        const incoming = hydrateDesignedPageSummaries(
            hydrateFigureImageUrls(JSON.parse(json), imageById),
            designedPageById);
        manuscriptId = incoming.manuscriptId;
        revision = incoming.revision;
        changeGeneration = 0;
        savedGeneration = 0;
        const incomingDocument = documentFromDomain(incoming);
        view.updateState(EditorState.create({
            doc: incomingDocument,
            selection: initialEditorSelection(incomingDocument),
            plugins: view.state.plugins
        }));
        updateStatus();
        outline.update();
        figureInspector.update();
    };

    const importWord = async (read, insertionView = view) => {
        if (readOnly || importBusy) return;
        if (!insertionView.state.selection.empty || !insertionView.state.selection.$from.parent.inlineContent) {
            showEditorNotice(root, "Place a single cursor in a text paragraph. Word import inserts there without replacing selected content.");
            return;
        }
        const selected = {blockId: insertionView.state.selection.$from.parent.attrs.id, headOffset: insertionView.state.selection.$from.parentOffset};
        importBusy = true; importCancelled = false; applyEffectiveReadOnly();
        status.textContent = "Reading Word content…";
        const cancel = document.createElement("button"); cancel.type = "button"; cancel.textContent = "Cancel Word import";
        cancel.addEventListener("click", () => { importCancelled = true; void dotNetRef.invokeMethodAsync("CancelWordImport"); });
        root.append(cancel);
        let stagedResources = null;
        let applied = false;
        try {
            if (!await saveNow()) throw new Error("Save or resolve pending edits before importing Word content.");
            if (importCancelled || attachmentDisposed) return;
            const before = domainFromDocument(view.state.doc, manuscriptId, revision);
            const position = findImportPosition(before, selected.blockId, selected.headOffset);
            const importVersion = {revision, ...targetVersion};
            if (!position) throw new Error("The selected import paragraph is no longer available.");
            const fragment = JSON.parse(await read());
            if (importCancelled || attachmentDisposed || !root.isConnected) return;
            if (revision !== importVersion.revision || targetVersion.fingerprint !== importVersion.fingerprint
                || targetVersion.generation !== importVersion.generation || fenceFrozen || requestedReadOnly)
                throw new Error("The manuscript changed while Word content was being read. Choose the insertion point again and retry.");
            const secondBlockId = newBlockId();
            const after = insertSemanticFragment(before, position, fragment.document, secondBlockId);
            const resources = journalImportResources(fragment.resources);
            stageImportResources(resources);
            stagedResources = resources;
            namedStyleRules.update(namedStyles);
            refreshStylePickers();
            pendingImport = {resources, operation: {kind: "insertSemanticFragment", position,
                richDocument: fragment.document, secondBlockId, expectedDocumentFingerprint: targetVersion.fingerprint}};
            const replacement = documentFromDomain(hydrateFigureImageUrls(after, imageById));
            importBusy = false; applyEffectiveReadOnly();
            forceAuthoringBoundary = true;
            view.dispatch(view.state.tr.replaceWith(0, view.state.doc.content.size, replacement.content)
                .setDocAttribute("notes", replacement.attrs.notes || []).setMeta("uiEvent", "paste"));
            applied = true;
            importBusy = true; applyEffectiveReadOnly(); cancel.remove();
            pendingActionLabel = "Import Word content";
            if (await saveNow()) {
                for (const image of resources.images)
                    imageById.set(String(image.id).toLowerCase(), {id: image.id,
                        previewUrl: `/projects/${authoringTarget.projectId.replaceAll("-", "")}/images/${image.id.replaceAll("-", "")}/content?maxEdge=640`});
                if (fragment.report.length) await dotNetRef.invokeMethodAsync("OnPasteNormalized", fragment.report);
            }
        } catch (error) {
            if (!importCancelled) showEditorNotice(root, error?.message || "Word content could not be imported.");
        } finally {
            if (stagedResources && !applied) {
                discardImportResources(stagedResources); pendingImport = null;
                namedStyleRules.update(namedStyles); refreshStylePickers();
            }
            cancel.remove(); importBusy = false;
            if (!attachmentDisposed && root.isConnected) {
                applyEffectiveReadOnly(); updateStatus();
                (insertionView.isDestroyed ? view : insertionView).focus();
            }
        }
    };

    const chooseWordFile = (insertionView = view) => {
        if (readOnly) return;
        const input = document.createElement("input"); input.type = "file"; input.accept = ".docx";
        input.addEventListener("change", () => {
            const file = input.files?.[0];
            if (!file) return;
            if (file.size > 32 * 1024 * 1024) { showEditorNotice(root, "Choose a DOCX file no larger than 32 MiB."); return; }
            void importWord(async () => dotNetRef.invokeMethodAsync("ReadWordDocx", new Uint8Array(await file.arrayBuffer())), insertionView);
        }, {once: true});
        input.click();
    };

    const handleWordPaste = (insertionView, event) => {
        const html = event.clipboardData?.getData("text/html") || "";
        if (!/class=["']?Mso|mso-|urn:schemas-microsoft-com:office|Microsoft Word/i.test(html)) return false;
        event.preventDefault();
        const files = [...(event.clipboardData?.files || [])].filter(file => file.type.startsWith("image/"));
        void importWord(async () => {
            if (html.length > 4 * 1024 * 1024 || files.reduce((size, file) => size + file.size, 0) > 32 * 1024 * 1024)
                throw new Error("Word clipboard content exceeds the bounded import limit. Use Import DOCX.");
            const images = await Promise.all(files.map(async file => ({id: authoringId(), fileName: file.name,
                contentType: file.type, data: new Uint8Array(await file.arrayBuffer())})));
            return dotNetRef.invokeMethodAsync("ReadWordClipboard", html, images);
        }, insertionView);
        return true;
    };

    const initialDocument = documentFromDomain(initial);
    let forceAuthoringBoundary = false;
    const authoringHistoryAdapter = new Plugin({
        key: authoringHistoryAdapterKey,
        state: {
            init: () => 0,
            apply: (transaction, value) => transaction.getMeta(authoringHistoryAdapterKey)?.boundary ? value + 1 : value
        }
    });
    const annotationsPlugin = new Plugin({
        key: annotationsKey,
        state: {
            init: () => 0,
            apply: (transaction, value) => transaction.getMeta(annotationsKey) ? value + 1 : value
        },
        props: {
            decorations: state => annotationDecorations(state.doc, reviewAnnotations)
        }
    });
    const state = EditorState.create({
        doc: initialDocument,
        selection: initialEditorSelection(initialDocument),
        plugins: [
            blockIdPlugin(),
            listNumberingPlugin(),
            authoringHistoryAdapter,
            annotationsPlugin,
            gapCursor(),
            keymap({
                "Mod-Enter": state => {
                    if (!(state.selection instanceof NodeSelection) || state.selection.node.type.name !== "note_reference") return false;
                    noteEditor.open(state.selection.node.attrs.noteId); return true;
                },
                "Mod-z": () => { void performPersistentHistory(false); return true; },
                "Shift-Mod-z": () => { void performPersistentHistory(true); return true; },
                "Mod-y": () => { void performPersistentHistory(true); return true; },
                "Mod-b": toggleMark(schema.marks.strong),
                "Mod-i": toggleMark(schema.marks.em),
                "Tab": (_state, _dispatch, editorView) => { if (!moveToAdjacentTableCell(editorView, 1) && !changeListLevel(editorView, 1)) changeParagraphIndent(editorView, 1.5); return true; },
                "Shift-Tab": (_state, _dispatch, editorView) => { if (!moveToAdjacentTableCell(editorView, -1) && !changeListLevel(editorView, -1)) changeParagraphIndent(editorView, -1.5); return true; },
                "Shift-Enter": insertHardBreak,
                "Enter": chainCommands(newlineInCode, createParagraphNear, liftEmptyBlock, baseKeymap.Enter)
            }),
            keymap(baseKeymap)
        ]
    });
    view = new EditorView(surface, {
        state,
        editable: () => !readOnly,
        dispatchTransaction(transaction) {
            if (!shouldApplyAuthoringTransaction(transaction, {
                readOnly,
                hasUnresolvedHistory: unresolvedHistoryRequest !== null,
                applyingHistory: applyingAuthoringHistory
            })) {
                // Reconcile the exact idempotent history request before a new
                // local mutation can form a batch against stale metadata.
                if (unresolvedHistoryRequest)
                    void reconcileUnresolvedHistory();
                return;
            }
            const beforeDocument = view.state.doc;
            const isPaste = transaction.getMeta("uiEvent") === "paste";
            const structural = transaction.docChanged
                && beforeDocument.childCount !== transaction.doc.childCount;
            const boundary = forceAuthoringBoundary || isPaste || structural;
            forceAuthoringBoundary = false;
            if (boundary) {
                // closeHistory is only a boundary marker for the adapter; we do
                // not install history(), so ProseMirror cannot own Undo/Redo.
                transaction = closeHistory(transaction);
                transaction.setMeta(authoringHistoryAdapterKey, {boundary: true});
            }
            const selectionBeforeTransaction = captureStableSelection(view);
            const inputStartedAt = transaction.docChanged ? performance.now() : null;
            const next = view.state.apply(transaction);
            view.updateState(next);
            if (transaction.docChanged && !applyingAuthoringHistory) {
                changeGeneration++;
                highestLocalSequence = Math.max(highestLocalSequence, nextSequence);
                const base = pendingLocalTransition?.base
                    || domainFromDocument(beforeDocument, manuscriptId, revision);
                const current = domainFromDocument(next.doc, manuscriptId, revision);
                pendingLocalTransition = {
                    base,
                    forward: authoringOperations(base, current),
                    inverse: authoringOperations(current, base),
                    beforeSelection: pendingLocalTransition?.beforeSelection || selectionBeforeTransaction,
                    afterSelection: captureStableSelection(view)
                };
                pendingActionLabel = isPaste ? "Paste" : structural ? "Change manuscript structure" : "Edit manuscript";
                scheduleSave();
                notifyWriterState();
            }
            findPanel.update();
            outline.update();
            figureInspector.update();
            updateFormattingControls();
            updateStatus();
            schedulePersistentCaret();
            if (inputStartedAt !== null)
                recordVisibleFrame("input-to-visible-frame", inputStartedAt);
        },
        handleDOMEvents: {
            focus() {
                persistentCaret.hidden = true;
                return false;
            },
            blur() {
                schedulePersistentCaret();
                return false;
            },
            click(_view, event) {
                if (!(event.target instanceof Element)) return false;
                const annotation = event.target.closest("[data-annotation-id]");
                if (annotation?.dataset.annotationId) {
                    void dotNetRef.invokeMethodAsync("OnAnnotationSelected", annotation.dataset.annotationId);
                    return false;
                }
                const figureImage = event.target.closest("figure[data-block-id] img");
                if (figureImage) {
                    event.preventDefault();
                    return selectFigureFromElement(view, figureImage);
                }
                const button = event.target.closest("[data-open-designed-page]");
                if (!button?.dataset.openDesignedPage) return false;
                event.preventDefault();
                void dotNetRef.invokeMethodAsync("OnOpenDesignedPage", button.dataset.openDesignedPage);
                return true;
            },
            dblclick(_view, event) {
                const note = event.target instanceof Element ? event.target.closest("[data-note-id]") : null;
                if (note?.dataset.noteId) { noteEditor.open(note.dataset.noteId); return true; }
                const element = event.target instanceof Element
                    ? event.target.closest("[data-designed-page-id]")
                    : null;
                if (!element?.dataset.designedPageId) return false;
                void dotNetRef.invokeMethodAsync("OnOpenDesignedPage", element.dataset.designedPageId);
                return true;
            }
        },
        handlePaste(_view, event) {
            if (handleWordPaste(_view, event)) return true;
            forceAuthoringBoundary = true;
            void saveNow();
            // The first save closes any typing group before the paste. The queued save
            // records the pasted document before later typing can join that action.
            setTimeout(() => { void saveNow(); }, 0);
            const warnings = pasteNormalizationWarnings(
                event,
                paragraphRoles,
                characterRoles,
                imageById);
            if (warnings.length > 0)
                void dotNetRef.invokeMethodAsync("OnPasteNormalized", warnings);
            return false;
        },
        transformPasted(slice) {
            return sanitizePastedSlice(slice, paragraphRoles, characterRoles, imageById);
        },
        attributes: {
            class: "semantic-prosemirror",
            role: "textbox",
            "aria-label": "Manuscript text editor",
            "aria-multiline": "true",
            spellcheck: "true"
        }
    });
    surface.append(persistentCaret);
    const performNoteHistory = async redo => {
        const active = noteEditor?.view;
        await performPersistentHistory(redo);
        if (active && active === noteEditor?.view && !active.isDestroyed) active.focus();
    };
    noteEditor = createNoteEditor(root, view, {
        toDocument: content => documentFromDomain(hydrateFigureImageUrls({manuscriptId, revision,
            content: structuredClone(content), notes: []}, imageById)),
        fromDocument: doc => domainFromDocument(doc, manuscriptId, revision).content,
        readOnly: () => readOnly,
        notice: message => showEditorNotice(root, message),
        click: (editor, event) => {
            const image = event.target instanceof Element ? event.target.closest("figure[data-block-id] img") : null;
            if (!image) return false;
            event.preventDefault(); return selectFigureFromElement(editor, image);
        },
        boundary: () => { forceAuthoringBoundary = true; void saveNow(); },
        transformPasted: slice => sanitizePastedSlice(slice, paragraphRoles, characterRoles, imageById),
        handlePaste: (editor, event) => {
            if (handleWordPaste(editor, event)) return true;
            forceAuthoringBoundary = true; void saveNow();
            setTimeout(() => { void saveNow(); }, 0);
            return false;
        },
        plugins: () => [blockIdPlugin(), listNumberingPlugin(), gapCursor(), keymap({
            "Mod-z": () => { void performNoteHistory(false); return true; },
            "Shift-Mod-z": () => { void performNoteHistory(true); return true; },
            "Mod-y": () => { void performNoteHistory(true); return true; },
            "Mod-b": toggleMark(schema.marks.strong), "Mod-i": toggleMark(schema.marks.em),
            "Shift-Enter": insertHardBreak,
            "Tab": (_state, _dispatch, editor) => changeListLevel(editor, 1),
            "Shift-Tab": (_state, _dispatch, editor) => changeListLevel(editor, -1)
        }), keymap(baseKeymap)],
        controls: editor => {
            const typography = buildTypographyControls(editor, namedStyles, fontFamilies, root);
            const historyButton = (label, redo) => {
                const control = button(label, `${label} note or manuscript edit`, () => { void performNoteHistory(redo); });
                control.dataset.historyDirection = redo ? "redo" : "undo";
                return control;
            };
            return {update: () => typography.update(), elements: [
            historyButton("Undo", false), historyButton("Redo", true),
            typography.group,
            button("B", "Bold", () => applyMark(editor, "strong")),
            button("I", "Italic", () => applyMark(editor, "em")),
            button("U", "Underline", () => applyMark(editor, "underline")),
            selectControl("Note inline formatting", [["", "More formatting"], ["strikethrough", "Strikethrough"],
                ["code", "Inline code"], ["small_caps", "Small caps"], ["superscript", "Superscript"], ["subscript", "Subscript"]],
                value => { if (value) applyMark(editor, value); }),
            selectControl("Note paragraph style", [["", "Paragraph style"], ["__reset__", "Built-in style"]].concat(
                namedStyles.filter(style => style.kind === "paragraph").map(style => [style.semanticRole, style.name])),
                value => { if (value === "__reset__") resetBlockRole(editor); else if (value) applyParagraphStyle(editor, value); }),
            selectControl("Note character style", [["", "Character style"], ["__remove__", "Remove character style"]].concat(
                namedStyles.filter(style => style.kind === "character").map(style => [style.semanticRole, style.name])),
                value => { if (value) applyMark(editor, "character_style", value === "__remove__" ? null : value); }),
            button("Lang", "Edit note language", () => { void editLanguage(editor, root); }),
            button("Link", "Edit note hyperlink", () => { void editLink(editor, root); }),
            button("List", "Format note list", () => toggleListFormatting(editor)),
            button("Paragraph", "Convert note block to paragraph", () => applyBlock(editor, "paragraph", "body")),
            button("Format", "Format note paragraph", () => { void editParagraphPresentation(editor, root); }),
            button("Cite", "Insert or edit note citation", () => { void insertOrEditCitation(editor, root,
                () => dotNetRef.invokeMethodAsync("ListCitationBibliography")); }),
            button("Image", "Insert or replace note Figure", async () => {
                const images = [...imageById.values()];
                if (!images.length) { showEditorNotice(root, "Add an image to the project library or import a Word picture first."); return; }
                const values = await showEditorForm(root, {title: "Note Figure", submitLabel: "Insert",
                    fields: [{name: "image", label: "Project image", type: "select", options: images.map(image =>
                        [image.id, image.title || image.fileName || `Image ${image.id.slice(0, 8)}`])}]});
                if (values && !editor.isDestroyed && !readOnly) await setFigureImage(editor, images.find(image => image.id === values.image), root);
            }),
            button("Alt", "Edit note Figure alternative text", () => { void editFigureAltText(editor, root); }),
            button("Figure", "Edit note Figure presentation", () => { void editFigurePresentation(editor, root); }),
            button("Import DOCX", "Import Word content into this note", () => chooseWordFile(editor))
            ]};
        }
    });
    const caretResizeObserver = new ResizeObserver(schedulePersistentCaret);
    caretResizeObserver.observe(surface);
    caretResizeObserver.observe(view.dom);
    surface.addEventListener("mousedown", event => {
        if (event.target !== surface || readOnly) return;
        event.preventDefault();
        view.focus();
    });

    const findPanel = buildFindPanel(view, root);
    const outline = buildOutline(view);
    const figureInspector = buildFigureInspector(view, projectImages);
    editorChrome.append(findPanel.panel, outline.panel, figureInspector.panel);
    figureInspector.update();
    const updateStatus = () => {
        noteEditor?.update();
        const text = editorialText(view.state.doc, manuscriptId, revision);
        const words = text.trim() ? text.trim().split(/\s+/u).length : 0;
        const saveState = journalFailure
            ? "not locally recoverable"
            : dispatchPaused
                ? "save paused"
                : savedGeneration < changeGeneration || pendingVisibleHistoryMove
                    ? "saving"
                    : "saved";
        status.textContent = `${words.toLocaleString()} words · ${text.length.toLocaleString()} characters · revision ${revision} · ${saveState}`;
    };

    const typographyControls = buildTypographyControls(view, namedStyles, fontFamilies, root);
    updateFormattingControls = () => typographyControls.update();

    let selectedParagraphStyleRole = "";
    const paragraphStyles = () => namedStyles.filter(style => style.kind === "paragraph");
    const styleOptions = () => [["", "Choose a saved style"], ["__reset__", "Reset paragraph to built-in style"]].concat(
        paragraphStyles().map(style => [style.semanticRole, style.name]));
    const stylePicker = selectControl(
        "Book Text Style",
        styleOptions(),
        value => {
            if (value === "__reset__") {
                selectedParagraphStyleRole = "";
                resetBlockRole(view);
                return true;
            }
            selectedParagraphStyleRole = value;
        },
        false);
    const refreshStylePickers = () => {
        for (const [select, options] of [
            [stylePicker.querySelector("select"), styleOptions()],
            [toolbar.querySelector('select[aria-label="Book Text character style"]'),
                [["", "Character"], ["__remove__", "Remove character style"]].concat(
                    namedStyles.filter(style => style.kind === "character").map(style => [style.semanticRole, style.name]))]
        ]) {
            if (!select) continue;
            const selected = select.value;
            select.replaceChildren(...options.map(([value, label]) => {
                const option = document.createElement("option"); option.value = value; option.textContent = label; return option;
            }));
            select.value = options.some(([value]) => value === selected) ? selected : "";
        }
    };
    const styleControls = document.createElement("div");
    styleControls.className = "semantic-editor-style-controls";
    styleControls.append(
        stylePicker,
        iconButton("✓", "Apply the chosen Book Text Style to the current paragraph or selected paragraphs", () => {
            if (!selectedParagraphStyleRole) {
                showEditorNotice(root, "Choose a saved Book Text Style first.");
                return;
            }
            if (!applyParagraphStyle(view, selectedParagraphStyleRole))
                showEditorNotice(root, "Place the cursor in a paragraph, heading, block quote, or list item first.");
        }),
        button("All", "Apply the chosen Book Text Style to every text paragraph in this chapter", () => {
            if (!selectedParagraphStyleRole) {
                showEditorNotice(root, "Choose a saved Book Text Style first.");
                return;
            }
            if (!applyParagraphStyle(view, selectedParagraphStyleRole, true))
                showEditorNotice(root, "This chapter has no compatible text paragraphs.");
        }),
        button("+ Style", "Capture the current paragraph formatting as a reusable Book Text Style", async () => {
            const selected = selectedParagraphIdentity(view);
            if (!selected) {
                showEditorNotice(root, "Place the cursor in a paragraph, heading, block quote, or list item first.");
                return;
            }
            const values = await showEditorForm(root, {
                title: "Save as Book Text Style",
                description: "This captures the paragraph's spacing, alignment, indentation, pagination, inherited font settings, and whole-paragraph bold, italic, or small-caps formatting. Inline emphasis remains content formatting.",
                submitLabel: "Save style",
                fields: [{name: "name", label: "Style name", required: true}],
                validate: value => value.name.trim().length > 80 ? "Use a name of 80 characters or fewer." : null
            });
            if (!values) return;
            if (!await saveNow()) {
                showEditorNotice(root, "Save the paragraph before creating its style.");
                return;
            }
            try {
                const style = await dotNetRef.invokeMethodAsync(
                    "OnCreateParagraphStyleFromBlock",
                    values.name.trim(),
                    selected.blockId,
                    revision);
                namedStyles.push(style);
                namedStyles.sort((left, right) => left.name.localeCompare(right.name));
                paragraphRoles.add(style.semanticRole);
                namedStyleRules.update(namedStyles);
                const select = stylePicker.querySelector("select");
                select.replaceChildren();
                for (const [value, label] of styleOptions()) {
                    const option = document.createElement("option");
                    option.value = value;
                    option.textContent = label;
                    select.append(option);
                }
                select.value = style.semanticRole;
                selectedParagraphStyleRole = style.semanticRole;
                const position = blockPositionById(view.state.doc, selected.blockId);
                if (Number.isInteger(position))
                    applyParagraphStyleAtPositions(view, [position], style.semanticRole);
                showEditorNotice(root, `Saved and applied “${style.name}”.`);
            } catch (error) {
                showEditorNotice(root, error?.message || "The Book Text Style could not be saved.");
            }
        }),
        button("Manage", "Manage Book Text Styles", () =>
            void dotNetRef.invokeMethodAsync("OnOpenBookTextStyles"))
    );

    const designedPageControls = allowDesignedPages
        ? [iconButton("▣", "Insert a designed page at the current manuscript position", () =>
            void dotNetRef.invokeMethodAsync("OnOpenPageLibrary"))]
        : [];

    const createAnnotation = async kind => {
        if (!allowAnnotations) return;
        let range;
        try {
            range = annotationRangeFromSelection(view);
        } catch (error) {
            showEditorNotice(root, error?.message || "Select flowing manuscript text first.");
            return;
        }
        let noteText = "";
        if (kind === "note") {
            const values = await showEditorForm(root, {
                title: "Add review note",
                description: "The note stays with this selected manuscript range and is available to the assistant.",
                submitLabel: "Add note",
                fields: [{name: "noteText", label: "Note", type: "textarea", required: true}],
                validate: value => value.noteText.trim().length > 8000 ? "Use 8,000 characters or fewer." : null
            });
            if (!values) return;
            noteText = values.noteText.trim();
        }
        if (!await saveNow()) {
            showEditorNotice(root, "Save the manuscript before adding the annotation.");
            return;
        }
        try {
            const annotation = await dotNetRef.invokeMethodAsync("OnCreateAnnotation", kind, noteText, range, revision);
            reviewAnnotations = reviewAnnotations.concat(annotation);
            view.dispatch(view.state.tr.setMeta(annotationsKey, true));
        } catch (error) {
            showEditorNotice(root, error?.message || "The review annotation could not be created.");
        }
    };

    toolbar.append(
        ...(allowAnnotations
            ? [
                button("Highlight", "Highlight the selected text for review", () => void createAnnotation("highlight")),
                button("Note", "Add a review note to the selected text", () => void createAnnotation("note")),
            ]
            : []),
        selectControl("Block style", [
            ["", "Book text"],
            ["paragraph|body|2", "Body text"],
            ["heading|chapter-heading|1", "Chapter title"],
            ["heading|heading|2", "Heading"],
            ["heading|subheading|3", "Subheading"],
            ["blockquote|block-quote|2", "Block quote"],
            ["list_item|list-item|2", "List"],
            ["paragraph|figure-caption|2", "Caption"],
        ], value => {
            if (!value) return;
            const [node, role, level] = value.split("|");
            applyBlock(view, node, role, Number(level));
        }),
        selectControl("Heading level", [
            ["", "H"],
            ["1", "Heading level 1"],
            ["2", "Heading level 2"],
            ["3", "Heading level 3"],
            ["4", "Heading level 4"],
            ["5", "Heading level 5"],
            ["6", "Heading level 6"],
        ], value => {
            if (value) applyHeadingLevel(view, Number(value));
        }),
        typographyControls.group,
        styleControls,
        button("Images", "Choose a project image to insert or replace a Figure", () =>
            void dotNetRef.invokeMethodAsync("OnOpenProjectImagePicker")),
        button("Alt", "Edit selected figure alternative text", () => void editFigureAltText(view, root)),
        iconButton("◩", "Edit selected figure placement, width, and crop behavior", () =>
            void editFigurePresentation(view, root)),
        ...designedPageControls,
        iconButton("¶", "Convert selected figure to a paragraph", () =>
            applyBlock(view, "paragraph", "body", 2)),
        iconButton("B", "Bold (Ctrl+B)", () => applyMark(view, "strong")),
        iconButton("I", "Italic (Ctrl+I)", () => applyMark(view, "em")),
        iconButton("U", "Underline", () => applyMark(view, "underline")),
        iconButton("S", "Strikethrough", () => applyMark(view, "strikethrough")),
        button("</>", "Inline code", () => applyMark(view, "code")),
        button("Aᴀ", "Small caps intent", () => applyMark(view, "small_caps")),
        iconButton("x²", "Superscript", () => applyMark(view, "superscript")),
        iconButton("x₂", "Subscript", () => applyMark(view, "subscript")),
        (() => {
            const control = iconButton("", "Add or remove link", () => void editLink(view, root));
            control.classList.add("semantic-editor-link-button");
            return control;
        })(),
        button("Lang", "Set or remove language", () => void editLanguage(view, root)),
        selectControl(
            "Book Text character style",
            [["", "Character"], ["__remove__", "Remove character style"]].concat(
                namedStyles
                    .filter(style => style.kind === "character")
                    .map(style => [style.semanticRole, style.name])),
            value => applyMark(
                view,
                "character_style",
                value === "__remove__" ? null : value || null)),
        iconButton("⁂", "Insert scene break", () => insertSceneBreak(view)),
        button("Table", "Insert semantic table", () => void insertRichTable(view, root)),
        button("Import DOCX", "Insert Word content at the cursor", () => chooseWordFile()),
        button("Fn", "Insert footnote", () => noteEditor.open(insertNote(view, root, "footnote"))),
        button("En", "Insert endnote", () => noteEditor.open(insertNote(view, root, "endnote"))),
        button("Notes", "Edit manuscript notes", async () => {
            const selected = view.state.selection instanceof NodeSelection && view.state.selection.node.type.name === "note_reference"
                ? view.state.selection.node.attrs.noteId : null;
            if (selected) { noteEditor.open(selected); return; }
            const notes = view.state.doc.attrs.notes || [];
            if (!notes.length) { showEditorNotice(root, "Insert a footnote or endnote at its manuscript reference first."); return; }
            const values = await showEditorForm(root, {title: "Edit manuscript note", submitLabel: "Edit",
                fields: [{name: "note", label: "Note", type: "select", options: notes.map((note, index) =>
                    [note.id, `${index + 1}. ${note.kind === "endnote" ? "Endnote" : "Footnote"}: ${note.content.map(domainBlockText).join(" ").slice(0, 80)}`])}]});
            if (values) noteEditor.open(values.note);
        }),
        button("Cite", "Insert or edit citation", () => void insertOrEditCitation(view, root,
            () => dotNetRef.invokeMethodAsync("ListCitationBibliography"))),
        selectControl("Insert special character", [
            ["", "Ω"],
            ["—", "Em dash —"],
            ["–", "En dash –"],
            ["…", "Ellipsis …"],
            ["“", "Opening quote “"],
            ["”", "Closing quote ”"],
            ["‘", "Opening apostrophe ‘"],
            ["’", "Closing apostrophe ’"],
            ["©", "Copyright ©"]
        ], value => {
            if (!value) return;
            view.dispatch(view.state.tr.insertText(value).scrollIntoView());
            view.focus();
        }),
        iconButton("↶", "Undo (Ctrl+Z)", () => void performPersistentHistory(false)),
        iconButton("↷", "Redo (Ctrl+Y)", () => void performPersistentHistory(true)),
        iconButton("⌕", "Find and replace", () => findPanel.open()),
        iconButton("☷", "Toggle document outline", () => outline.open())
    );
    toolbar.append(
        alignmentButton("left", "Align paragraph left", () => setParagraphAlignment(view, "start")),
        alignmentButton("center", "Center paragraph", () => setParagraphAlignment(view, "center")),
        alignmentButton("right", "Align paragraph right", () => setParagraphAlignment(view, "end")),
        alignmentButton("justify", "Justify paragraph", () => setParagraphAlignment(view, "justify")),
        iconButton("⇥", "Increase paragraph indent (Tab)", () => changeParagraphIndent(view, 1.5)),
        iconButton("⇤", "Decrease paragraph indent (Shift+Tab)", () => changeParagraphIndent(view, -1.5)),
        iconButton("•≡", "Toggle list formatting", () => toggleListFormatting(view)),
        button("List…", "Set list numbering, nesting, or restart", () => void editListFormatting(view, root))
    );
    toolbar.append(
        iconButton("¶…", "Right, first-line, and hanging indents, spacing, and pagination controls", () =>
            void editParagraphPresentation(view, root)),
        button("Tx×", "Clear direct paragraph formatting", () => clearParagraphPresentation(view))
    );
    const controlByTitle = title => [...toolbar.children].find(control => control.title === title);
    const controlBySelect = label => [...toolbar.children].find(control =>
        control.querySelector?.("select")?.getAttribute("aria-label") === label);
    toolbar.replaceChildren(
        toolGroup("History and navigation", [
            controlByTitle("Undo (Ctrl+Z)"),
            controlByTitle("Redo (Ctrl+Y)"),
            controlByTitle("Find and replace"),
            controlByTitle("Toggle document outline"),
        ]),
        ...(allowAnnotations
            ? [toolGroup("Review", [
                controlByTitle("Highlight the selected text for review"),
                controlByTitle("Add a review note to the selected text"),
            ])]
            : []),
        toolGroup("Text and typography", [
            controlBySelect("Block style"),
            controlBySelect("Heading level"),
            typographyControls.group,
        ]),
        toolGroup("Inline formatting", [
            controlByTitle("Bold (Ctrl+B)"),
            controlByTitle("Italic (Ctrl+I)"),
            controlByTitle("Underline"),
            controlByTitle("Strikethrough"),
            controlByTitle("Inline code"),
            controlByTitle("Small caps intent"),
            controlByTitle("Superscript"),
            controlByTitle("Subscript"),
            controlByTitle("Add or remove link"),
            controlByTitle("Set or remove language"),
            controlBySelect("Book Text character style"),
        ]),
        toolGroup("Images and structure", [
            controlByTitle("Choose a project image to insert or replace a Figure"),
            controlByTitle("Edit selected figure alternative text"),
            controlByTitle("Edit selected figure placement, width, and crop behavior"),
            controlByTitle("Convert selected figure to a paragraph"),
            controlByTitle("Insert a designed page at the current manuscript position"),
            controlByTitle("Insert scene break"),
            controlByTitle("Insert semantic table"),
            controlByTitle("Insert Word content at the cursor"),
            controlByTitle("Insert footnote"),
            controlByTitle("Insert endnote"),
            controlByTitle("Edit manuscript notes"),
            controlByTitle("Insert or edit citation"),
            controlBySelect("Insert special character"),
        ]),
        toolGroup("Paragraph formatting", [
            controlByTitle("Align paragraph left"),
            controlByTitle("Center paragraph"),
            controlByTitle("Align paragraph right"),
            controlByTitle("Justify paragraph"),
            controlByTitle("Increase paragraph indent (Tab)"),
            controlByTitle("Decrease paragraph indent (Shift+Tab)"),
            controlByTitle("Toggle list formatting"),
            controlByTitle("Set list numbering, nesting, or restart"),
            controlByTitle("Right, first-line, and hanging indents, spacing, and pagination controls"),
            controlByTitle("Clear direct paragraph formatting"),
        ]),
        toolGroup("Reusable styles", [styleControls])
    );
    const markControls = new Map([
        ["Bold (Ctrl+B)", "strong"],
        ["Italic (Ctrl+I)", "em"],
        ["Underline", "underline"],
        ["Strikethrough", "strikethrough"],
        ["Inline code", "code"],
        ["Small caps intent", "small_caps"],
        ["Superscript", "superscript"],
        ["Subscript", "subscript"],
    ]);
    const alignmentControls = new Map([
        ["Align paragraph left", "start"],
        ["Center paragraph", "center"],
        ["Align paragraph right", "end"],
        ["Justify paragraph", "justify"],
    ]);
    const setControlPressed = (control, pressed) => {
        if (!control) return;
        control.classList.toggle("semantic-editor-button--active", pressed);
        control.setAttribute("aria-pressed", String(pressed));
    };
    const updateToolbarState = () => {
        const {from, to, empty, $from} = view.state.selection;
        const currentMarks = view.state.storedMarks || $from.marks();
        for (const [title, markName] of markControls) {
            const markType = view.state.schema.marks[markName];
            const pressed = !!markType && (empty
                ? currentMarks.some(mark => mark.type === markType)
                : view.state.doc.rangeHasMark(from, to, markType));
            setControlPressed(toolbar.querySelector(`[title=${JSON.stringify(title)}]`), pressed);
        }
        const paragraphPosition = selectedParagraphPositions(view)[0];
        const paragraph = Number.isInteger(paragraphPosition)
            ? view.state.doc.nodeAt(paragraphPosition)
            : null;
        const alignment = paragraph?.attrs.paragraphPresentation?.alignment || null;
        for (const [title, value] of alignmentControls) {
            setControlPressed(toolbar.querySelector(`[title=${JSON.stringify(title)}]`), alignment === value);
        }
        setControlPressed(
            toolbar.querySelector('[title="Toggle list formatting"]'),
            paragraph?.type.name === "list_item");

        const blockSelect = toolbar.querySelector('select[aria-label="Block style"]');
        const headingSelect = toolbar.querySelector('select[aria-label="Heading level"]');
        if (blockSelect) {
            const blockValue = paragraph && ["paragraph", "heading", "blockquote", "list_item"].includes(paragraph.type.name)
                ? `${paragraph.type.name}|${paragraph.attrs.styleRole}|${paragraph.attrs.headingLevel || 2}`
                : "";
            blockSelect.value = [...blockSelect.options].some(option => option.value === blockValue)
                ? blockValue
                : "";
        }
        if (headingSelect) headingSelect.value = paragraph?.type.name === "heading"
            ? String(paragraph.attrs.headingLevel || 2)
            : "";
        const undoControl = toolbar.querySelector('[data-history-direction="undo"]');
        const redoControl = toolbar.querySelector('[data-history-direction="redo"]');
        if (undoControl) {
            undoControl.disabled = readOnly || !persistentHistoryState.canUndo;
            undoControl.title = persistentHistoryState.undoLabel
                ? `Undo ${persistentHistoryState.undoLabel} (Ctrl+Z)`
                : "Nothing to undo";
        }
        if (redoControl) {
            redoControl.disabled = readOnly || !persistentHistoryState.canRedo;
            redoControl.title = persistentHistoryState.redoLabel
                ? `Redo ${persistentHistoryState.redoLabel} (Ctrl+Y)`
                : "Nothing to redo";
        }
    };
    updateFormattingControls = () => {
        typographyControls.update();
        updateToolbarState();
    };
    const undoControl = toolbar.querySelector('[title="Undo (Ctrl+Z)"]');
    const redoControl = toolbar.querySelector('[title="Redo (Ctrl+Y)"]');
    if (undoControl) undoControl.dataset.historyDirection = "undo";
    if (redoControl) redoControl.dataset.historyDirection = "redo";
    updateFormattingControls();
    performPersistentHistory = async (redoDirection, visibleAlreadyApplied = false) => {
        if (readOnly && !visibleAlreadyApplied) return false;
        if (!await reconcileUnresolvedHistory()) return false;
        const historyStartedAt = performance.now();
        // Undo is client-first even while its edit is in flight. Capture and
        // dispatch that exact transition, immediately show its inverse, then
        // advance the server-owned cursor after the receipt. Keeping the flag
        // set prevents the pre-undo acknowledgement from claiming a clean
        // writer in the compensation window.
        if (!visibleAlreadyApplied && savedGeneration < changeGeneration) {
            if (redoDirection || !pendingLocalTransition) {
                status.textContent = "Undo is waiting for the current edit to establish a durable history entry.";
                return false;
            }
            const transition = pendingLocalTransition;
            const dispatch = saveNow();
            pendingVisibleHistoryMove = true;
            historyMoveInFlight = true;
            applyEffectiveReadOnly();
            applyingAuthoringHistory = true;
            try { applyAuthoringOperations(view, transition.inverse, imageById, designedPageById); }
            finally { applyingAuthoringHistory = false; }
            const selectionPoint = transition.beforeSelection?.targets?.[0];
            if (selectionPoint?.blockId)
                restoreStableSelection(view, {blockId: selectionPoint.blockId, anchorOffset: selectionPoint.offset, headOffset: selectionPoint.offset});
            updateFormattingControls();
            recordVisibleFrame("undo-to-visible-frame", historyStartedAt);
            notifyWriterState();
            if (!await dispatch || savedGeneration < changeGeneration) {
                status.textContent = "Undo is visible locally and will be reconciled after the current edit saves.";
                historyMoveInFlight = false;
                applyEffectiveReadOnly();
                notifyWriterState();
                return false;
            }
            return await performPersistentHistory(false, true);
        }
        const nextCursor = redoDirection ? confirmedHistoryCursor + 1 : confirmedHistoryCursor - 1;
        const transition = confirmedHistory[redoDirection ? confirmedHistoryCursor : nextCursor];
        if (!transition) {
            status.textContent = redoDirection ? "Nothing to redo." : "Nothing to undo.";
            return false;
        }
        // Apply the authoritative cursor mirror immediately. The following
        // request validates and advances the process cursor; it is a
        // reconciliation, not the source of visible Undo/Redo.
        const visibleOperations = redoDirection ? transition.forward : transition.inverse;
        if (!historyMoveInFlight) {
            historyMoveInFlight = true;
            applyEffectiveReadOnly();
        }
        if (!visibleAlreadyApplied) {
            applyingAuthoringHistory = true;
            try { applyAuthoringOperations(view, visibleOperations, imageById, designedPageById); }
            finally { applyingAuthoringHistory = false; }
        }
        confirmedHistoryCursor = nextCursor;
        const selection = redoDirection ? transition.afterSelection : transition.beforeSelection;
        const selectionPoint = selection?.targets?.[0];
        if (selectionPoint?.blockId)
            restoreStableSelection(view, {blockId: selectionPoint.blockId, anchorOffset: selectionPoint.offset, headOffset: selectionPoint.offset});
        updateFormattingControls();
        if (!visibleAlreadyApplied) {
            recordVisibleFrame(
                redoDirection ? "redo-to-visible-frame" : "undo-to-visible-frame",
                historyStartedAt);
        }
        try {
            // A history move is visible before its RPC returns. Reuse one
            // identity if that response is lost so the process cursor cannot
            // advance twice on retry.
            const historyRequest = {
                projectId: authoringTarget.projectId,
                sessionId,
                historyRequestId: authoringId(),
                targetId: authoringTarget.targetId,
                expectedGeneration: targetVersion.generation
            };
            const invokeHistory = () => dotNetRef.invokeMethodAsync(
                redoDirection ? "OnRedoAuthoringBatch" : "OnUndoAuthoringBatch",
                historyRequest);
            let historyResult;
            try {
                historyResult = await invokeHistory();
            } catch {
                try {
                    historyResult = await invokeHistory();
                } catch (retryError) {
                    // Do not blindly reverse a local action after transport
                    // loss: first observe the process-owned cursor. If it
                    // reached our target position, retain the already-visible
                    // local delta and wait for normal metadata refresh.
                    try {
                        const observed = await dotNetRef.invokeMethodAsync("GetAuthoringHistoryState");
                        if (Number(observed?.cursor?.position) === nextCursor) {
                            unresolvedHistoryRequest = {
                                request: historyRequest,
                                redoDirection,
                                nextCursor
                            };
                            historyMoveInFlight = false;
                            applyEffectiveReadOnly();
                            adoptHistoryCursor(observed);
                            updateFormattingControls();
                            notifyWriterState();
                            status.textContent = "History was applied; reconnecting to refresh its canonical state before the next edit.";
                            return true;
                        }
                    } catch {}
                    throw retryError;
                }
            }
            const result = historyResult?.batch;
            const canonical = result?.canonicalVersions?.find(item => item.targetId === authoringTarget.targetId);
            if (!result || !["committed", "replayed"].includes(String(result.status || "").toLowerCase()) || !canonical) {
                // The server cursor rejected the request. Restore the pre-click
                // visible state instead of silently adopting another document.
                applyingAuthoringHistory = true;
                try { applyAuthoringOperations(view, redoDirection ? transition.inverse : transition.forward, imageById, designedPageById); }
                finally { applyingAuthoringHistory = false; }
                confirmedHistoryCursor = redoDirection ? confirmedHistoryCursor - 1 : confirmedHistoryCursor + 1;
                pendingVisibleHistoryMove = false;
                historyMoveInFlight = false;
                applyEffectiveReadOnly();
                adoptHistoryCursor(historyResult);
                updateFormattingControls();
                status.textContent = result?.conflicts?.[0]?.message || (redoDirection ? "Nothing to redo." : "Nothing to undo.");
                return false;
            }
            const target = result.targets?.find(item => item.targetId === authoringTarget.targetId);
            if (!target?.manuscriptJson)
                throw new Error("The authoring service did not return its canonical target state.");
            await adoptCanonicalHistoryBatch(historyResult, historyRequest, redoDirection);
            view.focus();
            return true;
        } catch (error) {
            applyingAuthoringHistory = true;
            try { applyAuthoringOperations(view, redoDirection ? transition.inverse : transition.forward, imageById, designedPageById); }
            finally { applyingAuthoringHistory = false; }
            confirmedHistoryCursor = redoDirection ? confirmedHistoryCursor - 1 : confirmedHistoryCursor + 1;
            pendingVisibleHistoryMove = false;
            historyMoveInFlight = false;
            applyEffectiveReadOnly();
            status.textContent = error?.message || "History could not be applied.";
            notifyWriterState();
            return false;
        }
    };
    toolbar.addEventListener("pointerdown", event => {
        const control = event.target instanceof Element ? event.target.closest("button, select, input") : null;
        if (!control || control.dataset.historyDirection) return;
        forceAuthoringBoundary = true;
        pendingActionLabel = "Format manuscript";
        void saveNow();
    }, true);
    const adoptHistoryCursor = projection => {
        if (!projection) return;
        persistentHistoryState = projection.state || projection;
        const cursor = projection.cursor;
        if (!cursor) return;
        confirmedHistory = (cursor.actions || [])
            .filter(action => !(action.targetIds || []).length || action.targetIds.includes(authoringTarget.targetId))
            .map(action => {
            const ordinal = (action.targetIds || []).findIndex(targetId => targetId === authoringTarget.targetId);
            return {
                inverse: operationsForTarget(action.inverse, ordinal < 0 ? 0 : ordinal),
                forward: operationsForTarget(action.forward, ordinal < 0 ? 0 : ordinal),
                beforeSelection: action.beforeSelection || null,
                afterSelection: action.afterSelection || null
            };
        });
        confirmedHistoryCursor = Number(cursor.position || 0);
    };
    const adoptCanonicalHistoryBatch = async (historyResult, historyRequest, redoDirection) => {
        const result = historyResult?.batch;
        const canonical = result?.canonicalVersions?.find(item => item.targetId === authoringTarget.targetId);
        const target = result?.targets?.find(item => item.targetId === authoringTarget.targetId);
        const sequence = Number(result?.sequence);
        if (!result
            || !["committed", "replayed"].includes(String(result.status || "").toLowerCase())
            || !canonical
            || !target?.manuscriptJson
            || !Number.isSafeInteger(sequence)
            || sequence < 0
            || !result.receiptId) {
            throw new Error("The authoring service did not return a complete canonical history result.");
        }

        // A history move consumes a normal session sequence. Reserve it before
        // acknowledgement so a subsequent save cannot reuse the server batch.
        nextSequence = Math.max(nextSequence, sequence + 1);
        highestLocalSequence = Math.max(highestLocalSequence, sequence);
        lastDispatchedSequence = Math.max(lastDispatchedSequence, sequence);
        revision = canonical.afterRevision;
        targetVersion = {generation: canonical.generation, fingerprint: canonical.fingerprint};
        elementFingerprints = new Map(Object.entries(target.elementFingerprints || {}));
        confirmedDocument = JSON.parse(target.manuscriptJson);
        queuedDocument = structuredClone(confirmedDocument);
        replaceDocument(target.manuscriptJson);
        if (target.selectionJson)
            restoreStableSelection(view, target.selectionJson);
        adoptHistoryCursor(historyResult);
        unresolvedHistoryRequest = {request: historyRequest, redoDirection};
        notifyWriterState();
        try {
            await dotNetRef.invokeMethodAsync("AcknowledgeAuthoringReceipt", {
                projectId: authoringTarget.projectId,
                sessionId,
                receiptId: result.receiptId,
                batchId: result.batchId,
                requestHash: result.requestHash
            });
            lastAcknowledgedSequence = Math.max(lastAcknowledgedSequence, sequence);
            unresolvedHistoryRequest = null;
            pendingVisibleHistoryMove = false;
            historyMoveInFlight = false;
            applyEffectiveReadOnly();
            savedGeneration = changeGeneration;
        } catch (error) {
            // The committed move stays visible but blocks all later dispatch
            // until the same idempotent request can acknowledge its receipt.
            status.textContent = error?.message || "History was applied and is waiting for receipt acknowledgement.";
        }
        await refreshReviewAnnotations();
        notifyWriterState();
        updateFormattingControls();
        updateStatus();
        return unresolvedHistoryRequest === null;
    };
    reconcileUnresolvedHistory = async () => {
        const unresolved = unresolvedHistoryRequest;
        if (!unresolved) return true;
        try {
            const historyResult = await dotNetRef.invokeMethodAsync(
                unresolved.redoDirection ? "OnRedoAuthoringBatch" : "OnUndoAuthoringBatch",
                unresolved.request);
            return await adoptCanonicalHistoryBatch(historyResult, unresolved.request, unresolved.redoDirection);
        } catch (error) {
            status.textContent = error?.message || "History is waiting to reconcile before the next edit can be saved.";
            notifyWriterState();
            return false;
        }
    };
    notifyWriterState();
    void dotNetRef.invokeMethodAsync("GetAuthoringHistoryState").then(state => {
        adoptHistoryCursor(state);
        updateFormattingControls();
    }).catch(() => {});
    // A reload first renders the server-confirmed base and replays only
    // unacknowledged journal batches. Confirmed history remains process-owned.
    saveChain = saveChain.then(async () => {
        if (dispatchPaused || !journal.recoverable) return;
        for (const entry of await journal.pending()) {
            try {
                const result = await dotNetRef.invokeMethodAsync("DispatchAuthoringBatch", entry.batch);
                const canonical = result?.canonicalVersions?.find(item => item.targetId === authoringTarget.targetId);
                if (!["committed", "replayed"].includes(String(result?.status || "").toLowerCase()) || !canonical) {
                    dispatchPaused = true;
                    status.textContent = result?.conflicts?.[0]?.message || "Recovered edits need resolution before they can be saved.";
                    showConflict(result, entry);
                    notifyWriterState();
                    return;
                }
                revision = canonical.afterRevision;
                targetVersion = {generation: canonical.generation, fingerprint: canonical.fingerprint};
                const canonicalTarget = result.targets?.find(item => item.targetId === authoringTarget.targetId);
                if (canonicalTarget?.elementFingerprints)
                    elementFingerprints = new Map(Object.entries(canonicalTarget.elementFingerprints));
                confirmedDocument = JSON.parse(entry.afterJson);
                queuedDocument = structuredClone(confirmedDocument);
                await dotNetRef.invokeMethodAsync("AcknowledgeAuthoringReceipt", {
                    projectId: authoringTarget.projectId,
                    sessionId, receiptId: result.receiptId, batchId: entry.batch.batchId,
                    requestHash: entry.batch.requestHash
                });
                if (!await journal.remove(entry.key))
                    throw new Error("The recovered edit could not be cleared from the local recovery journal.");
                pendingJournalCount = Math.max(0, pendingJournalCount - 1);
                confirmedHistory.splice(confirmedHistoryCursor);
                confirmedHistory.push({
                    inverse: result.inverse?.operations || [],
                    forward: entry.batch.operations,
                    beforeSelection: entry.batch.selection?.before || null,
                    afterSelection: entry.batch.selection?.after || null
                });
                confirmedHistoryCursor = confirmedHistory.length;
                lastAcknowledgedSequence = Math.max(lastAcknowledgedSequence, Number(entry.batch.sequence));
                lastDispatchedSequence = Math.max(lastDispatchedSequence, Number(entry.batch.sequence));
                highestLocalSequence = Math.max(highestLocalSequence, Number(entry.batch.sequence));
            } catch (error) {
                dispatchPaused = true;
                status.textContent = error?.message || "The recovered edit is waiting to be acknowledged.";
                notifyWriterState();
                return;
            }
        }
        notifyWriterState();
        updateStatus();
    });
    updateStatus();
    outline.update();
    recordVisibleFrame("editor-ready-after-navigation", attachmentStartedAt);

    return {
        flush: saveNow,
        capturePageInsertion() {
            const resolved = view.state.selection.$from;
            return { index: resolved.index(0) + (resolved.parentOffset > 0 ? 1 : 0), revision };
        },
        waitForSaves() { return saveChain.catch(() => false); },
        async freezeAndFlush(sequence) {
            fenceFrozen = true;
            applyEffectiveReadOnly();
            const flushed = await saveNow();
            const dirty = savedGeneration < changeGeneration
                || pendingJournalCount > 0
                || pendingVisibleHistoryMove
                || unresolvedHistoryRequest !== null
                || !journal.recoverable
                || !!journalFailure;
            notifyWriterState();
            return {
                // This is always the actual acknowledgement watermark; the
                // fence compares it with its captured highest-local sequence.
                flushedSequence: lastAcknowledgedSequence,
                isDirty: dirty,
                isRecoverable: journal.recoverable && !journalFailure,
                isReachable: true,
                errorCode: flushed ? null : "AUTHORING_FLUSH_INCOMPLETE",
                errorMessage: flushed ? null : status.textContent
            };
        },
        async resumeAfterFence() {
            fenceFrozen = false;
            applyEffectiveReadOnly();
            await reconcileUnresolvedHistory();
            notifyWriterState();
        },
        // Registration awaits this call. That matters for a same-session
        // reattach: the fence retains the unreachable dirty state until this
        // recovered handle has supplied its actual queue/journal state.
        reportWriterState() { return notifyWriterState(); },
        setReadOnly(value) {
            requestedReadOnly = !!value;
            applyEffectiveReadOnly();
            schedulePersistentCaret();
        },
        setDocument(json) {
            replaceDocument(json);
            return dotNetRef.invokeMethodAsync("GetAuthoringHistoryState").then(state => {
                adoptHistoryCursor(state);
                updateFormattingControls();
            }).catch(() => {});
        },
        setAnnotations(json) {
            if (!allowAnnotations) return;
            reviewAnnotations = typeof json === "string" ? JSON.parse(json) : json;
            view.dispatch(view.state.tr.setMeta(annotationsKey, true));
        },
        updateTypography(json) {
            typography = typeof json === "string" ? JSON.parse(json) : json;
            typographyRules.update(typography || {});
        },
        getAnnotationRange() {
            if (!allowAnnotations) return null;
            return annotationRangeFromSelection(view);
        },
        getLocation() {
            return selectionLocation(view);
        },
        setLocation(location) {
            return restoreLocation(view, location);
        },
        selectAnnotation(annotationId) {
            if (!allowAnnotations) return false;
            const annotation = reviewAnnotations.find(item => item.id === annotationId);
            if (!annotation || String(annotation.anchorState).toLowerCase() !== "current") return false;
            const start = blockPositionById(view.state.doc, annotation.range.startBlockId);
            const end = blockPositionById(view.state.doc, annotation.range.endBlockId);
            if (!Number.isInteger(start) || !Number.isInteger(end)) return false;
            const startNode = view.state.doc.nodeAt(start);
            const endNode = view.state.doc.nodeAt(end);
            if (!startNode?.inlineContent || !endNode?.inlineContent) return false;
            view.dispatch(view.state.tr.setSelection(TextSelection.create(
                view.state.doc,
                start + 1 + Math.max(0, Math.min(annotation.range.startOffset, startNode.content.size)),
                end + 1 + Math.max(0, Math.min(annotation.range.endOffset, endNode.content.size)))).scrollIntoView());
            view.focus();
            return true;
        },
        selectProjectImage(image) {
            const imageId = String(image?.id || "").toLowerCase();
            if (!imageId) return false;
            imageById.set(imageId, image);
            const existingIndex = projectImages.findIndex(candidate =>
                String(candidate.id).toLowerCase() === imageId);
            if (existingIndex >= 0) projectImages[existingIndex] = image;
            else projectImages.push(image);
            void setFigureImage(view, image, root);
            return true;
        },
        focus() {
            if (!readOnly) view.focus();
        },
        dispose() {
            attachmentDisposed = true;
            importCancelled = true;
            if (timer) clearTimeout(timer);
            if (releaseWriterLease) releaseWriterLease();
            if (caretFrame !== null) cancelAnimationFrame(caretFrame);
            caretResizeObserver.disconnect();
            noteEditor?.dispose();
            view.destroy();
            root.replaceChildren();
            root.classList.remove("semantic-editor-root");
        }
    };
}

export const semanticEditorTesting = {
    schema,
    documentFromDomain,
    domainFromDocument,
    applyBlock,
    applyBlockRole,
    applyHeadingLevel,
    resetBlockRole,
    safeLink,
    safeLanguage,
    validSemanticRole,
    sanitizePastedSlice,
    sanitizeHtmlForPaste,
    pasteNormalizationWarnings,
    countMatches,
    replaceAll,
    replaceAllInDocument,
    canonicalPlainText,
    editorialText,
    selectedFigure,
    toggleListFormatting
};
