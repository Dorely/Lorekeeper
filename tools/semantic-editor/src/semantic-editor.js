import {DOMParser as ProseMirrorDOMParser, Fragment, Schema, Slice} from "prosemirror-model";
import {EditorState, NodeSelection, Plugin, PluginKey, TextSelection} from "prosemirror-state";
import {Decoration, DecorationSet, EditorView} from "prosemirror-view";
import {baseKeymap, chainCommands, createParagraphNear, liftEmptyBlock, newlineInCode, toggleMark} from "prosemirror-commands";
import {GapCursor, gapCursor} from "prosemirror-gapcursor";
import {keymap} from "prosemirror-keymap";

const idsKey = new PluginKey("lorekeeper-block-ids");
const annotationsKey = new PluginKey("lorekeeper-review-annotations");
const blockTypeToNode = {
    paragraph: "paragraph",
    heading: "heading",
    sceneBreak: "scene_break",
    blockQuote: "blockquote",
    listItem: "list_item",
    figure: "figure",
    designedPage: "designed_page"
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
    designed_page: "designed-page"
};

function newBlockId() {
    return crypto.randomUUID().replaceAll("-", "");
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
    pageCompositionId: {default: null},
    compositionName: {default: null},
    compositionSurfaceLabel: {default: null},
    compositionStatus: {default: null},
    compositionPreviewUrl: {default: null}
};

function paragraphStyle(presentation) {
    if (!presentation) return null;
    const styles = [];
    const family = editorFontFamily(presentation.fontFamilyKey);
    if (family) styles.push(`font-family:${family}`);
    if (Number.isFinite(presentation.fontSizePoints)) styles.push(`font-size:${presentation.fontSizePoints}pt`);
    if (Number.isInteger(presentation.fontWeight)) styles.push(`font-weight:${presentation.fontWeight}`);
    if (presentation.italic === true) styles.push("font-style:italic");
    if (presentation.smallCaps === true) styles.push("font-variant-caps:small-caps");
    if (Number.isFinite(presentation.lineHeight)) styles.push(`line-height:${presentation.lineHeight}`);
    const alignment = {start: "left", center: "center", end: "right", justify: "justify"}[presentation.alignment];
    if (alignment) styles.push(`text-align:${alignment}`);
    if (Number.isFinite(presentation.leftIndentEm)) styles.push(`margin-left:${presentation.leftIndentEm}em`);
    if (Number.isFinite(presentation.rightIndentEm)) styles.push(`margin-right:${presentation.rightIndentEm}em`);
    if (Number.isFinite(presentation.firstLineIndentEm)) styles.push(`text-indent:${presentation.firstLineIndentEm}em`);
    if (Number.isFinite(presentation.spacingBeforePoints)) styles.push(`margin-top:${presentation.spacingBeforePoints}pt`);
    if (Number.isFinite(presentation.spacingAfterPoints)) styles.push(`margin-bottom:${presentation.spacingAfterPoints}pt`);
    return styles.join(";") || null;
}

function editorFontFamily(key) {
    const normalized = key?.trim().toLowerCase();
    if (!normalized) return null;
    if (normalized === "serif") return "Georgia, 'Times New Roman', serif";
    if (normalized === "sans") return "Arial, Helvetica, sans-serif";
    if (normalized === "mono") return "'Courier New', Courier, monospace";
    const safeName = normalized.replaceAll(/[^a-z0-9]+/gu, "-").replaceAll(/^-|-$/gu, "");
    return safeName ? `'Lorekeeper-${safeName}'` : null;
}

function textBlockDom(tag, node, extra = {}) {
    return [tag, {
        ...extra,
        class: extra.class || null,
        "data-block-id": node.attrs.id,
        "data-style-role": node.attrs.styleRole,
        style: paragraphStyle(node.attrs.paragraphPresentation)
    }, 0];
}

function textBlockAttrs(element, defaultRole) {
    return {
        id: element.dataset.blockId,
        styleRole: element.dataset.styleRole || defaultRole,
    };
}

function figureDomStyle(presentation) {
    const value = {...defaultFigurePresentation, ...(presentation || {})};
    const styles = [`width:${Math.max(5, Math.min(100, Number(value.widthPercent || 100)))}%`];
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
    const compositionId = node.attrs.pageCompositionId;
    const preview = node.attrs.compositionPreviewUrl
        ? ["img", {
            class: "semantic-designed-page-preview",
            src: node.attrs.compositionPreviewUrl,
            alt: "",
            draggable: "false"
        }]
        : ["span", {class: "semantic-designed-page-preview semantic-designed-page-preview--empty"}, "No preview yet"];
    return ["section", {
        class: "semantic-designed-page",
        "data-block-id": node.attrs.id,
        "data-style-role": "designed-page",
        "data-page-composition-id": compositionId
    },
    preview,
    ["span", {class: "semantic-designed-page-details"},
        ["strong", node.attrs.compositionName || "Designed page"],
        ["span", {class: "semantic-designed-page-meta"},
            `${node.attrs.compositionSurfaceLabel || "Geometry not configured"} · ${node.attrs.compositionStatus || "Open to configure"}`]],
    ["button", {
        type: "button",
        class: "semantic-designed-page-open",
        "data-open-page-composition": compositionId,
        "aria-label": `Open ${node.attrs.compositionName || "Designed page"} editor`
    }, "Edit page"]];
}

const schema = new Schema({
    nodes: {
        doc: {content: "block*"},
        text: {group: "inline"},
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
                ["figcaption", 0]
            ]
        },
        designed_page: {
            group: "block",
            atom: true,
            selectable: true,
            attrs: {...blockAttrs, styleRole: {default: "designed-page"}},
            parseDOM: [{
                tag: "section[data-page-composition-id]",
                getAttrs: element => ({
                    id: element.dataset.blockId,
                    styleRole: "designed-page",
                    pageCompositionId: element.dataset.pageCompositionId
                })
            }],
            toDOM: designedPageDom
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

function documentFromDomain(document) {
    const blocks = (document.content || []).map(block => {
        const nodeName = blockTypeToNode[block.type] || "paragraph";
        const nodeType = schema.nodes[nodeName];
        const attrs = {
            id: block.id || newBlockId(),
            styleRole: block.styleRole || "body",
            imageId: block.imageId || null,
            altText: block.altText || null,
            imageUrl: block.imageUrl || null
            ,decorative: block.decorative === true
            ,language: block.language || null
            ,accessibilityRole: block.accessibilityRole || (nodeName === "figure" ? "figure" : null)
            ,presentation: block.figurePresentation || null
            ,paragraphPresentation: block.paragraphPresentation || null
            ,pageCompositionId: block.pageCompositionId || null
            ,compositionName: block.compositionName || null
            ,compositionSurfaceLabel: block.compositionSurfaceLabel || null
            ,compositionStatus: block.compositionStatus || null
            ,compositionPreviewUrl: block.compositionPreviewUrl || null
        };
        if (nodeName === "heading")
            attrs.level = block.headingLevel || 2;
        const content = ["scene_break", "designed_page"].includes(nodeName)
            ? null
            : (block.content || []).flatMap(inlineFromDomain);
        return nodeType.create(attrs, content);
    });
    if (blocks.length === 0) {
        blocks.push(schema.nodes.paragraph.create({
            id: newBlockId(),
            styleRole: "body"
        }));
    }
    return schema.nodes.doc.create(null, blocks);
}

function domainFromDocument(doc, manuscriptId, revision) {
    const content = [];
    doc.forEach(node => {
        const inlines = [];
        if (node.isTextblock) {
            node.forEach(child => {
                const marks = child.marks.flatMap(mark => {
                    const type = markNameToType[mark.type.name];
                    if (!type) return [];
                    const value = mark.attrs?.value ?? null;
                    return [{type, value}];
                });
                const text = child.isText
                    ? child.text
                    : child.type.name === "hard_break"
                        ? "\n"
                        : null;
                if (!text) return;
                const previous = inlines.at(-1);
                if (previous && JSON.stringify(previous.marks) === JSON.stringify(marks))
                    previous.text += text;
                else
                    inlines.push({type: "text", text, marks});
            });
        }
        const block = {
            id: node.attrs.id || newBlockId(),
            type: nodeToBlockType[node.type.name] || "paragraph",
            styleRole: node.attrs.styleRole || "body",
            headingLevel: node.type.name === "heading" ? node.attrs.level : null,
            imageId: node.type.name === "figure" ? node.attrs.imageId : null,
            altText: node.type.name === "figure" ? node.attrs.altText : null,
            language: node.attrs.language || null,
            paragraphPresentation: ["paragraph", "heading", "blockquote", "list_item"].includes(node.type.name)
                ? node.attrs.paragraphPresentation || null
                : null,
            content: inlines
        };
        if (node.type.name === "figure") {
            block.decorative = node.attrs.decorative === true;
            block.accessibilityRole = node.attrs.accessibilityRole || "figure";
            block.figurePresentation = node.attrs.presentation || {...defaultFigurePresentation};
        }
        if (node.type.name === "designed_page")
            block.pageCompositionId = node.attrs.pageCompositionId;
        content.push(block);
    });
    return {schemaVersion: 4, manuscriptId, revision, content};
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
            newState.doc.descendants((node, position) => {
                if (!node.isBlock || node.type.name === "doc") return true;
                const id = node.attrs.id;
                if (!id || seen.has(id)) {
                    transaction = transaction.setNodeMarkup(position, undefined, {...node.attrs, id: newBlockId()});
                    changed = true;
                } else {
                    seen.add(id);
                }
                return false;
            });
            return changed ? transaction : null;
        }
    });
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

const paragraphStyleNodeNames = new Set(["paragraph", "heading", "blockquote", "list_item"]);

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

function restoreStableSelection(view, selectionJson) {
    if (!selectionJson) return;
    try {
        const saved = typeof selectionJson === "string" ? JSON.parse(selectionJson) : selectionJson;
        if (!saved?.blockId) return;
        const position = blockPositionById(view.state.doc, saved.blockId);
        if (!Number.isInteger(position)) {
            view.dispatch(view.state.tr.setSelection(TextSelection.atStart(view.state.doc)));
            return;
        }
        const node = view.state.doc.nodeAt(position);
        if (!node) return;
        const selection = saved.node
            ? NodeSelection.create(view.state.doc, position)
            : TextSelection.create(
                view.state.doc,
                position + 1 + Math.min(Number(saved.anchorOffset) || 0, node.content.size),
                position + 1 + Math.min(Number(saved.headOffset) || 0, node.content.size));
        view.dispatch(view.state.tr.setSelection(selection).scrollIntoView());
    } catch {
        // Selection restoration is best-effort; the restored document remains authoritative.
    }
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

async function insertDesignedPage(view, dotNetRef, getRevision, flush, replaceDocument, root) {
    const values = await showEditorForm(root, {
        title: "Insert Designed Page",
        description: "Choose the authoring layout. Publication compatibility is checked later in Publish.",
        submitLabel: "Create page",
        fields: [
            {name: "name", label: "Page name", type: "text", value: "Designed page", required: true},
            {
                name: "layoutMode",
                label: "Layout",
                type: "select",
                value: "SinglePage",
                options: [["SinglePage", "Single page"], ["FacingSpread", "Facing spread"]]
            }
        ],
        validate: value => !value.name.trim() ? "Enter a page name." : null
    });
    if (!values) return;
    if (!await flush()) {
        showEditorNotice(root, "The current chapter could not be saved, so the Designed Page was not created.");
        return;
    }
    const resolved = view.state.selection.$from;
    const blockIndex = resolved.index(0) + (resolved.parentOffset > 0 ? 1 : 0);
    try {
        const composition = await dotNetRef.invokeMethodAsync(
            "OnCreateDesignedPage",
            values.name.trim(),
            values.layoutMode,
            blockIndex,
            getRevision());
        if (!composition?.id || !composition?.manuscriptJson)
            throw new Error("The application returned no Designed Page.");
        replaceDocument(composition.manuscriptJson, composition.summary);
        await dotNetRef.invokeMethodAsync("OnOpenDesignedPage", composition.id);
    } catch (error) {
        showEditorNotice(
            root,
            error?.message || "The Designed Page could not be created. Reload the chapter and try again.");
        view.focus();
    }
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
    const supported = new Set(["P", "BR", "H1", "H2", "H3", "H4", "H5", "H6", "BLOCKQUOTE", "UL", "OL", "LI", "HR", "FIGURE", "FIGCAPTION", "EM", "I", "STRONG", "B", "U", "S", "DEL", "CODE", "A", "SPAN", "SUP", "SUB"]);
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
        return node.type.create({
            ...node.attrs,
            id: newBlockId(),
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

function installNamedStyleRules(root, styles) {
    const styleElement = document.createElement("style");
    styleElement.dataset.bookTextStyles = "true";
    const update = currentStyles => {
        styleElement.textContent = "";
        for (const style of currentStyles) {
            const definition = style.definition || {};
            const selector = style.kind === "character"
                ? `.semantic-editor.semantic-editor .semantic-prosemirror span[data-character-style=${JSON.stringify(style.semanticRole)} i]`
                : `.semantic-editor.semantic-editor .semantic-prosemirror [data-style-role=${JSON.stringify(style.semanticRole)} i]`;
            const declarations = [];
            const family = editorFontFamily(definition.fontFamilyKey);
            if (family) declarations.push(`font-family:${family}`);
            if (definition.fontSizePoints) declarations.push(`font-size:${definition.fontSizePoints}pt`);
            if (definition.fontWeight) declarations.push(`font-weight:${definition.fontWeight}`);
            if (definition.italic === true) declarations.push("font-style:italic");
            if (definition.smallCaps === true) declarations.push("font-variant-caps:small-caps");
            if (definition.lineHeight) declarations.push(`line-height:${definition.lineHeight}`);
            if (definition.spaceBeforePoints !== null && definition.spaceBeforePoints !== undefined)
                declarations.push(`margin-top:${definition.spaceBeforePoints}pt`);
            if (definition.spaceAfterPoints !== null && definition.spaceAfterPoints !== undefined)
                declarations.push(`margin-bottom:${definition.spaceAfterPoints}pt`);
            if (definition.leftIndentEm !== null && definition.leftIndentEm !== undefined)
                declarations.push(`margin-left:${definition.leftIndentEm}em`);
            if (definition.rightIndentEm !== null && definition.rightIndentEm !== undefined)
                declarations.push(`margin-right:${definition.rightIndentEm}em`);
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
            : block.content.map(inline => inline.text).join(""))
        .join("\n\n");
}

function editorialText(doc, manuscriptId, revision) {
    const domain = domainFromDocument(doc, manuscriptId, revision);
    return domain.content
        .filter(block => !["sceneBreak", "designedPage"].includes(block.type))
        .map(block => block.content.map(inline => inline.text).join(""))
        .join("\n\n");
}

function hydrateFigureImageUrls(document, imageById) {
    for (const block of document.content || []) {
        const image = block.type === "figure"
            ? imageById.get(String(block.imageId).toLowerCase())
            : null;
        block.imageUrl = image?.previewUrl ?? null;
    }
    return document;
}

function hydrateDesignedPageSummaries(document, compositionById) {
    for (const block of document.content || []) {
        const summary = block.type === "designedPage"
            ? compositionById.get(String(block.pageCompositionId).toLowerCase())
            : null;
        block.compositionName = summary?.name ?? null;
        block.compositionSurfaceLabel = summary?.surfaceLabel ?? null;
        block.compositionStatus = summary?.status ?? null;
        block.compositionPreviewUrl = summary?.previewUrl ?? null;
    }
    return document;
}

export function attach(root, dotNetRef, debounceMs, initialJson, stylesJson = "[]", imagesJson = "[]", editionsJson = "[]", compositionsJson = "[]", fontFamiliesJson = "[]", allowDesignedPages = true, annotationsJson = "[]") {
    if (!root || typeof root.replaceChildren !== "function" || root.isConnected === false)
        return null;

    const initial = JSON.parse(initialJson);
    const namedStyles = JSON.parse(stylesJson);
    const projectImages = JSON.parse(imagesJson);
    JSON.parse(editionsJson);
    const pageCompositions = JSON.parse(compositionsJson);
    const fontFamilies = JSON.parse(fontFamiliesJson);
    let reviewAnnotations = JSON.parse(annotationsJson);
    const imageById = new Map(projectImages.map(image => [String(image.id).toLowerCase(), image]));
    const compositionById = new Map(pageCompositions.map(composition => [String(composition.id).toLowerCase(), composition]));
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
    hydrateFigureImageUrls(initial, imageById);
    hydrateDesignedPageSummaries(initial, compositionById);
    let manuscriptId = initial.manuscriptId;
    let revision = initial.revision;
    let timer = null;
    let saveChain = Promise.resolve(true);
    let changeGeneration = 0;
    let savedGeneration = 0;
    let requestedReadOnly = false;
    let readOnly = false;
    let updateFormattingControls = () => {};
    let persistentHistoryState = {canUndo: false, canRedo: false, undoLabel: null, redoLabel: null};
    let performPersistentHistory = async () => false;
    const applyEffectiveReadOnly = () => {
        readOnly = requestedReadOnly;
        if (!view) return;
        view.setProps({editable: () => !readOnly});
        for (const control of root.querySelectorAll("button, select, input"))
            control.disabled = readOnly;
        root.classList.toggle("semantic-editor--readonly", readOnly);
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
    root.classList.add("semantic-editor-root");
    root.replaceChildren(editorChrome, surface, status);
    installEditorFontRules(root, fontFamilies);
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
        try {
            reviewAnnotations = await dotNetRef.invokeMethodAsync("GetReviewAnnotations");
            view.dispatch(view.state.tr.setMeta(annotationsKey, true));
        } catch {}
    };
    const saveNow = () => {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
        const targetGeneration = changeGeneration;
        const snapshotJson = JSON.stringify(
            domainFromDocument(view.state.doc, manuscriptId, revision));
        const selectionJson = JSON.stringify(captureStableSelection(view));
        saveChain = saveChain.catch(() => false).then(async () => {
            if (savedGeneration >= targetGeneration)
                return true;
            const payload = JSON.parse(snapshotJson);
            payload.revision = revision;
            const json = JSON.stringify(payload);
            try {
                const result = await dotNetRef.invokeMethodAsync(
                    "OnDocumentDebounced",
                    revision,
                    json,
                    selectionJson);
                if (result?.currentManuscriptJson) {
                    replaceDocument(result.currentManuscriptJson);
                    await refreshReviewAnnotations();
                    persistentHistoryState = await dotNetRef.invokeMethodAsync("GetAuthoringHistoryState");
                    updateFormattingControls();
                    return true;
                }
                if (!result?.saved) return false;
                revision = result.revision;
                savedGeneration = Math.max(savedGeneration, targetGeneration);
                await refreshReviewAnnotations();
                try {
                    persistentHistoryState = await dotNetRef.invokeMethodAsync("GetAuthoringHistoryState");
                    updateFormattingControls();
                } catch {}
                updateStatus();
            } catch {
                return false;
            }
            return true;
        });
        return saveChain;
    };

    const scheduleSave = () => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { void saveNow(); }, debounceMs);
    };

    const replaceDocument = (json, compositionSummary = null) => {
        if (compositionSummary?.id)
            compositionById.set(String(compositionSummary.id).toLowerCase(), compositionSummary);
        const incoming = hydrateDesignedPageSummaries(
            hydrateFigureImageUrls(JSON.parse(json), imageById),
            compositionById);
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

    const initialDocument = documentFromDomain(initial);
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
            annotationsPlugin,
            gapCursor(),
            keymap({
                "Mod-z": () => { void performPersistentHistory(false); return true; },
                "Shift-Mod-z": () => { void performPersistentHistory(true); return true; },
                "Mod-y": () => { void performPersistentHistory(true); return true; },
                "Mod-b": toggleMark(schema.marks.strong),
                "Mod-i": toggleMark(schema.marks.em),
                "Tab": (_state, _dispatch, editorView) => { changeParagraphIndent(editorView, 1.5); return true; },
                "Shift-Tab": (_state, _dispatch, editorView) => { changeParagraphIndent(editorView, -1.5); return true; },
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
            if (readOnly && transaction.docChanged)
                return;
            const next = view.state.apply(transaction);
            view.updateState(next);
            if (transaction.docChanged) {
                changeGeneration++;
                scheduleSave();
            }
            findPanel.update();
            outline.update();
            figureInspector.update();
            updateFormattingControls();
            updateStatus();
            schedulePersistentCaret();
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
                const button = event.target.closest("[data-open-page-composition]");
                if (!button?.dataset.openPageComposition) return false;
                event.preventDefault();
                void dotNetRef.invokeMethodAsync("OnOpenDesignedPage", button.dataset.openPageComposition);
                return true;
            },
            dblclick(_view, event) {
                const element = event.target instanceof Element
                    ? event.target.closest("[data-page-composition-id]")
                    : null;
                if (!element?.dataset.pageCompositionId) return false;
                void dotNetRef.invokeMethodAsync("OnOpenDesignedPage", element.dataset.pageCompositionId);
                return true;
            }
        },
        handlePaste(_view, event) {
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
        const text = editorialText(view.state.doc, manuscriptId, revision);
        const words = text.trim() ? text.trim().split(/\s+/u).length : 0;
        status.textContent = `${words.toLocaleString()} words · ${text.length.toLocaleString()} characters · revision ${revision}`;
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
            void insertDesignedPage(view, dotNetRef, () => revision, saveNow, replaceDocument, root))]
        : [];

    const createAnnotation = async kind => {
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
        button("Highlight", "Highlight the selected text for review", () => void createAnnotation("highlight")),
        button("Note", "Add a review note to the selected text", () => void createAnnotation("note")),
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
        iconButton("•≡", "Toggle list formatting", () => toggleListFormatting(view))
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
        toolGroup("Review", [
            controlByTitle("Highlight the selected text for review"),
            controlByTitle("Add a review note to the selected text"),
        ]),
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
    performPersistentHistory = async redoDirection => {
        if (readOnly) return false;
        const flushed = await saveNow();
        if (!flushed) return false;
        try {
            const result = await dotNetRef.invokeMethodAsync(
                redoDirection ? "OnRedoAuthoring" : "OnUndoAuthoring");
            if (!result?.applied) {
                if (result) {
                    persistentHistoryState = result;
                    updateFormattingControls();
                }
                status.textContent = result?.error || (redoDirection ? "Nothing to redo." : "Nothing to undo.");
                return false;
            }
            replaceDocument(result.manuscriptJson);
            await refreshReviewAnnotations();
            restoreStableSelection(view, result.selectionJson);
            persistentHistoryState = result;
            updateFormattingControls();
            view.focus();
            return true;
        } catch (error) {
            status.textContent = error?.message || "History could not be applied.";
            return false;
        }
    };
    toolbar.addEventListener("pointerdown", event => {
        const control = event.target instanceof Element ? event.target.closest("button, select, input") : null;
        if (!control || control.dataset.historyDirection) return;
        void saveNow();
    }, true);
    void dotNetRef.invokeMethodAsync("GetAuthoringHistoryState").then(state => {
        persistentHistoryState = state;
        updateFormattingControls();
    }).catch(() => {});
    updateStatus();
    outline.update();

    return {
        flush: saveNow,
        waitForSaves() { return saveChain.catch(() => false); },
        setReadOnly(value) {
            requestedReadOnly = !!value;
            applyEffectiveReadOnly();
            schedulePersistentCaret();
        },
        setDocument(json) {
            replaceDocument(json);
            return dotNetRef.invokeMethodAsync("GetAuthoringHistoryState").then(state => {
                persistentHistoryState = state;
                updateFormattingControls();
            }).catch(() => {});
        },
        setAnnotations(json) {
            reviewAnnotations = typeof json === "string" ? JSON.parse(json) : json;
            view.dispatch(view.state.tr.setMeta(annotationsKey, true));
        },
        getAnnotationRange() {
            return annotationRangeFromSelection(view);
        },
        selectAnnotation(annotationId) {
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
            if (timer) clearTimeout(timer);
            if (caretFrame !== null) cancelAnimationFrame(caretFrame);
            caretResizeObserver.disconnect();
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
