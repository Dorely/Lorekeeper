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
        items = "".join(
            f"<li>{html.escape(chapter['title'])}</li>"
            for chapter in document["chapters"]
        )
        front_matter.append(f'<section class="front contents"><h1>Contents</h1><ol>{items}</ol></section>')

    chapters: list[str] = []
    for chapter in document["chapters"]:
        chapter_id = chapter.get("id", "")
        if chapter.get("blocks") is not None:
            paragraphs = "".join(
                block_html(chapter_id, block)
                for block in chapter["blocks"]
            )
        else:
            paragraphs = "".join(
                f"<p>{html.escape(paragraph)}</p>"
                for paragraph in chapter["body"].split("\n\n")
                if paragraph
            )
        chapters.append(
            f'<section class="chapter"><h1>{html.escape(chapter["title"])}</h1>{paragraphs}</section>'
        )

    back_matter = [
        matter_html(item, "back")
        for item in document["matter"]
        if item["location"] == "Back"
    ]
    return "".join(front_matter) + "".join(chapters) + "".join(back_matter)


def matter_html(item: dict[str, Any], location_class: str) -> str:
    content = "".join(
        block_html(None, block)
        for block in item["blocks"]
    )
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
    text = html.escape(block["text"])
    block_type = block["type"]
    if block_type == "SceneBreak":
        return f'<p{anchor} class="scene-break">* * *</p>'
    if block_type == "Heading":
        return f'<h2{anchor}>{text}</h2>'
    if block_type == "BlockQuote":
        return f'<blockquote{anchor}>{text}</blockquote>'
    if block_type == "ListItem":
        return f'<p{anchor} class="list-item">• {text}</p>'
    if block_type == "Figure":
        return f'<p{anchor} class="figure-caption">{text}</p>'
    return f'<p{anchor}>{text}</p>'
