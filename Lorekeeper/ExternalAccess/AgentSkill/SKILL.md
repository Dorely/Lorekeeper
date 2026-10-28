---
name: lorekeeper-external-access
description: Build, edit, research, illustrate, inspect, and exchange Lorekeeper projects through its local MCP workspaces. Use for Lorekeeper creative work and transfers to another project; repository development uses that repository's own instructions.
metadata:
  version: "1.0.1"
---

Lorekeeper is authoritative project state. Discover its currently advertised capabilities; this skill never grants a permission or implies that a tool is installed.

## Enter a workspace

Discover project IDs with `list_projects`, or create a project when requested. Call `open_workspace` with the explicit project, surface, task, and relevant chapter/Core/release target. Read every returned context page before surface tools. The app assembles role context from current authored state; don't reconstruct it from remembered chat.

Continue one task across surfaces by passing its workId to subsequent workspace openings. Keep each workspaceId with its project and target. After reconnect or compaction, use `list_unfinished_work` and `get_workspace_context`. Refresh when direction or the target changes. Workspace changes do not navigate the author's UI.

## Choose the owning surface

- **Outline:** project direction, Book Brief, acts/chapters, beats, canon, facts, relationships, and material chapter associations. Discover existing records before creating them. Use arbitrary project entity types for concepts that do not fit Character or Location.
- **Editor:** semantic Core/release manuscript edits, annotations, styles, Figures, Designed Pages, and layout. Core is shared source; a release is an explicit override target. Read the manuscript and styles before editing.
- **World:** World Brief, Working Notes, source research, and canon development. Brief/canon are established state; notes hold proposals and uncertainty. Search evidence is not automatically canon.
- **Images:** existing artwork, canonical entity associations, generation/editing, and inspection. Creating an asset and attaching or placing it are distinct operations. Inspect generated/edited pixels before use.
- **Voice:** project-wide writing samples and per-character voice profiles. A writing sample is a substantial passage of the book's own prose that shows its style, never single lines or one character's dialogue. Put a character's voice in their profile: study their existing dialogue, describe how they speak and think, and quote a few representative exact lines. Keep appearance and backstory on the entity record.
- **Publish:** Core Book, releases, sections, covers, previews, readiness, and artifact preparation. Preparation is not external publication or proof of vendor acceptance. Follow returned diagnostics and verification requirements.

## Ground and preserve edits

Search one concise content facet at a time, then read exact records. Snippets, scores, ordinal positions, and remembered names are not mutation identities. Follow pagination and retain source-project provenance. Direct-reference material is one-hop, read-only evidence; active-project canon and explicit author direction take precedence.

Read revisions/version tokens before writes. Supply a new mutationId for each intended mutation, and reuse that same ID with the original payload after an uncertain response. On a conflict, reread and reassess; never blindly replay against a newer revision.

Use semantic manuscript operations, not wholesale projected-document replacement. Preserve stable block IDs, inline formatting, Figures, Designed Pages, tables, notes, citations, and annotations. InsertBlock is additive and takes plain `text` or marked `inlineContent` runs; revisions must replace or remove superseded content in the same batch. Read every returned readback range at the resulting revision/hash. Inspect annotated and clean visual previews when the owning workflow requires them.

Call `finish_work` only after the entire requested task and its background jobs are finished. Review Edits controls whether work stays pending or the complete live project is checkpointed. Checkpoints inherit the project's previously configured automatic remote synchronization. Abandoning work preserves its saved changes.

## Transfer to an external project

Keep game/application file-format decisions in that project. Use stable external source keys when linking Lorekeeper records; do not match later changes by names alone. Capture a transfer against the link's last acknowledged baseline, inspect all changed records and associations, and read exact before/after state and assets. Saved pending edits are included.

Use the installed transfer helper for binary uploads/downloads instead of generating base64 text. The connection file is private: never print it or copy its contents into chat, a repository, or this skill.

Prefer the `binaryTransfer` instructions returned by the current workspace over the installation example below. They select this connection's credential when several Lorekeeper connections are configured. The private connection path identifies local setup; it never grants access by itself.

{{TRANSFER_COMMAND}}

Advance a baseline only after successfully handling that captured transfer in the external project, including any deliberately reviewed exclusions. Acknowledgement does not approve Lorekeeper edits; changes made after capture remain outstanding. Do not bypass an available Lorekeeper capability through raw SQLite or internal files.
