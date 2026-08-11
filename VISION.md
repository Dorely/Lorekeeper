# Vision: an AI-native bookmaking studio

Lorekeeper is an end-to-end environment for creating books with AI while keeping
the author in control. It joins long-form narrative intelligence, professional
editing, visual composition, and publication production in one durable project.

The product's foundational insight remains:

> An AI can assist across a long book while respecting established canon,
> evolving story state, authorial intent, and reviewable change.

The destination extends that insight through the rest of bookmaking. An author
should be able to plan, draft, revise, design, inspect, and produce validated
publication files without moving the manuscript through a chain of unrelated
external tools.

## Product pillars

### 1. Narrative and project intelligence

- Maintain structured people, places, events, relationships, sources, and
  project facts.
- Retrieve the most relevant canon and prior text for each task.
- Keep authorial direction, Book Brief, source provenance, and generated output
  visible and correctable.
- Detect contradictions and help the author resolve them without silently
  rewriting canon.

### 2. A professional semantic manuscript

- Store the meaning and structure of the book, not only presentation text.
- Support long-form organization, rich editing, Book Text Styles, references,
  assets, and stable anchors for review.
- Preserve one canonical source while allowing paperback, ebook, illustrated,
  and later reference-book releases to differ intentionally through explicit
  Editor-owned content and style overrides.
- Make migrations, imports, exports, and revisions safe, inspectable, and
  recoverable.

### 3. Human-led AI collaboration

- Let authors write and design manually, delegate bounded work, compare
  alternatives, and review proposed changes.
- Give assistants complete access to the same safe capabilities as the UI
  through shared application services.
- Require stable references, validation, revision awareness, and reviewable
  mutations rather than opaque document replacement.
- Reserve external publication, rights declarations, purchases, and other
  consequential actions for explicit human decisions.

### 4. Book design and edition production

- Provide approachable defaults for prose books and progressively deeper tools
  for editorial review, page design, illustrated books, and nonfiction.
- Keep shared bibliographic, structural, matter, design, and front-cover choices
  in one Core Book. Let optional publication releases inherit that intent live
  and override only product-specific decisions.
- Make a private Core reading copy available without implying an ISBN, vendor,
  package, or publication claim; reserve those concepts for releases.
- Generate deterministic pagination, real previews, full-wrap covers, EPUB, and
  vendor-specific print artifacts from the semantic manuscript.
- Provide Generic profiles for unknown or custom destinations and built-in,
  versioned Specific profiles for named vendors and products.
- Preflight fonts, images, color, geometry, metadata, accessibility, and format
  constraints inside Lorekeeper.
- Make every artifact traceable to its source revision, edition settings,
  assets, renderer, and validation profile.

### 5. Trustworthy publication

- Never describe an artifact as print-ready, accessible, PDF/X-conformant, or
  vendor-compatible without evidence for that declared scope.
- Keep standards, vendor requirements, dependency licenses, and validation
  profiles versioned and reviewable.
- Preserve local-first ownership of projects and credentials.
- Prefer permissively licensed, inspectable production components and make
  dependency obligations explicit.

## Delivery shape

Lorekeeper will grow through deliberate publication milestones:

1. the current paperback, EPUB, PDF ebook, Core Book, illustration, cover, and
   edition-specific Editor foundation;
2. artifact-complete release inspection, diagnostics, downloads, and broader
   built-in profile coverage;
3. DOCX manuscript import/export;
4. hardcover and additional Generic and Specific publication profiles;
5. advanced design, language, accessibility, and reference-book support.

The detailed scope and verification gates live in
[`docs/publishing-roadmap.md`](docs/publishing-roadmap.md). That roadmap
distinguishes research and plans from implemented or verified behavior.

## Success criteria

Lorekeeper succeeds when:

- a long book remains coherent as its manuscript, canon, assets, and editions
  evolve;
- authors understand what context and changes AI assistants use;
- manual and assistant actions follow the same validation and review paths;
- existing projects survive structural evolution without content loss;
- a supported release can be created, edited, designed, rendered, validated,
  previewed, and downloaded inside the application;
- publication claims are backed by repeatable automated checks, independent
  inspection, and a versioned Generic or Specific profile;
- the system remains maintainable enough to extend from novels to illustrated
  and reference books without parallel document or rendering models.

## Guiding principle

Lorekeeper should understand the book as both a living body of ideas and a
physical or digital publication. Intelligence, authorship, design, and production
must remain connected—but never at the cost of user control or trustworthy
output.
