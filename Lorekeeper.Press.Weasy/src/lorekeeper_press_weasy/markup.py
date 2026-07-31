from __future__ import annotations

import html
from typing import Any


def interior_content(document: dict[str, Any]) -> str:
    front_matter: list[str] = []
    if document.get("includeTitlePage", False):
        subtitle = (
            f'<p class="subtitle">{html.escape(document.get("subtitle", ""))}</p>'
            if document.get("subtitle")
            else ""
        )
        front_matter.append(
            f'<section class="front title-page"><h1>{html.escape(document["title"])}</h1>'
            f'{subtitle}<p>{html.escape(document["author"])}</p></section>'
        )
    if document.get("copyright") or document.get("publisher"):
        front_matter.append(
            f'<section class="front copyright-page"><p>{html.escape(document.get("copyright", ""))}</p>'
            f'<p>{html.escape(document.get("publisher", ""))}</p></section>'
        )
    front_matter.extend(
        matter_html(item, "front")
        for item in document["matter"]
        if item["location"] == "Front"
    )
    if document.get("includeVisibleTableOfContents", False):
        items = "".join(_toc_section_html(section) for section in document["sections"])
        front_matter.append(f'<section class="front contents"><h1>Contents</h1><ol>{items}</ol></section>')

    body: list[str] = []
    for section in document["sections"]:
        if section["includePage"]:
            heading = f'<h1>{html.escape(section["title"])}</h1>' if section["includeHeading"] else ""
            synopsis = _paragraphs(section["synopsis"], "synopsis")
            body.append(f'<section class="part">{heading}{synopsis}</section>')
        for chapter in section["chapters"]:
            heading = f'<h1>{html.escape(chapter["title"])}</h1>' if chapter["includeHeading"] else ""
            synopsis = _paragraphs(chapter["synopsis"], "synopsis")
            blocks = "".join(block_html(chapter["id"], block) for block in chapter["blocks"])
            body.append(f'<section class="chapter">{heading}{synopsis}{blocks}</section>')

    back_matter = [
        matter_html(item, "back")
        for item in document["matter"]
        if item["location"] == "Back"
    ]
    return "".join(front_matter) + "".join(body) + "".join(back_matter)


def style_rules(styles: list[dict[str, Any]]) -> str:
    rules: list[str] = []
    families = {
        "serif": '"Liberation Serif", serif',
        "sans": '"Liberation Sans", sans-serif',
        "mono": '"Liberation Mono", monospace',
    }
    for style in styles:
        definition = style["definition"]
        declarations: list[str] = []
        if definition.get("fontFamilyKey") in families:
            declarations.append(f'font-family: {families[definition["fontFamilyKey"]]}')
        if definition.get("fontSizePoints") is not None:
            declarations.append(f'font-size: {definition["fontSizePoints"]}pt')
        if definition.get("fontWeight") is not None:
            declarations.append(f'font-weight: {definition["fontWeight"]}')
        if definition.get("italic"):
            declarations.append("font-style: italic")
        if definition.get("smallCaps"):
            declarations.append("font-variant-caps: small-caps")
        if definition.get("lineHeight") is not None:
            declarations.append(f'line-height: {definition["lineHeight"]}')
        if definition.get("spaceBeforePoints") is not None:
            declarations.append(f'margin-top: {definition["spaceBeforePoints"]}pt')
        if definition.get("spaceAfterPoints") is not None:
            declarations.append(f'margin-bottom: {definition["spaceAfterPoints"]}pt')
        if definition.get("keepWithNext"):
            declarations.append("break-after: avoid")
        if definition.get("textAlign") is not None:
            declarations.append(f'text-align: {definition["textAlign"]}')
        if not declarations:
            continue
        role = _css_string(style["semanticRole"])
        attribute = "data-character-style" if style["kind"] == "Character" else "data-style-role"
        rules.append(f'[{attribute}="{role}" i] {{ {"; ".join(declarations)}; }}')
    return "\n".join(rules)


def matter_html(item: dict[str, Any], location_class: str) -> str:
    content = "".join(block_html(None, block) for block in item["blocks"])
    return (
        f'<section class="matter {location_class} {item["kind"].lower()}">'
        f'<h1>{html.escape(item["title"])}</h1>{content}</section>'
    )


def block_html(chapter_id: str | None, block: dict[str, Any]) -> str:
    anchor = (
        f' id="lk-block-{chapter_id.replace("-", "")}{block["id"].replace("-", "")}"'
        if chapter_id
        else ""
    )
    role = html.escape(block["styleRole"], quote=True)
    content = "".join(inline_html(inline) for inline in block["content"])
    block_type = block["type"]
    if block_type == "SceneBreak":
        return f'<p{anchor} class="scene-break" data-style-role="{role}">* * *</p>'
    if block_type == "Heading":
        level = block.get("headingLevel") or 2
        return f'<h{level}{anchor} data-style-role="{role}">{content}</h{level}>'
    if block_type == "BlockQuote":
        return f'<blockquote{anchor} data-style-role="{role}">{content}</blockquote>'
    if block_type == "ListItem":
        return f'<p{anchor} class="list-item" data-style-role="{role}">• {content}</p>'
    if block_type == "Figure":
        return f'<p{anchor} class="figure-caption" data-style-role="{role}">{content}</p>'
    return f'<p{anchor} data-style-role="{role}">{content}</p>'


def inline_html(inline: dict[str, Any]) -> str:
    value = html.escape(inline["text"]).replace("\r\n", "\n").replace("\r", "\n").replace("\n", "<br />")
    for mark in inline["marks"]:
        mark_type = mark["type"]
        mark_value = mark.get("value")
        if mark_type == "Strong":
            value = f"<strong>{value}</strong>"
        elif mark_type == "Emphasis":
            value = f"<em>{value}</em>"
        elif mark_type == "Underline":
            value = f'<span class="underline">{value}</span>'
        elif mark_type == "Strikethrough":
            value = f"<s>{value}</s>"
        elif mark_type == "Code":
            value = f"<code>{value}</code>"
        elif mark_type == "SmallCaps":
            value = f'<span class="small-caps">{value}</span>'
        elif mark_type == "Superscript":
            value = f"<sup>{value}</sup>"
        elif mark_type == "Subscript":
            value = f"<sub>{value}</sub>"
        elif mark_type == "CharacterStyle" and mark_value:
            value = f'<span data-character-style="{html.escape(mark_value, quote=True)}">{value}</span>'
        elif mark_type == "Language" and mark_value:
            value = f'<span lang="{html.escape(mark_value, quote=True)}">{value}</span>'
        elif mark_type == "Link" and mark_value:
            value = (
                f'<span class="print-link" data-link-target="'
                f'{html.escape(mark_value, quote=True)}">{value}</span>'
            )
    return value


def _paragraphs(text: str, css_class: str) -> str:
    return "".join(
        f'<p class="{css_class}">{html.escape(paragraph)}</p>'
        for paragraph in text.split("\n\n")
        if paragraph
    )


def _css_string(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def _toc_section_html(section: dict[str, Any]) -> str:
    chapters = "".join(
        f"<li>{html.escape(chapter['title'])}</li>"
        for chapter in section["chapters"]
    )
    if section["includePage"]:
        nested = f"<ol>{chapters}</ol>" if chapters else ""
        return f"<li>{html.escape(section['title'])}{nested}</li>"
    return chapters
