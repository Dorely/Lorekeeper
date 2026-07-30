# Vision: an AI-native bookmaking studio

Lorekeeper is an end-to-end environment for creating books with AI while keeping
the author in control. It joins long-form narrative intelligence, professional
editing, visual composition, and publication production in one durable project.

The product's foundational insight remains:

> An AI can assist across a long book while respecting established canon,
> evolving story state, authorial intent, and reviewable change.

The destination extends that insight through the rest of bookmaking. An author
should be able to plan, draft, revise, design, proof, and produce validated
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
- Support long-form organization, rich editing, named styles, references,
  assets, and stable anchors for review.
- Preserve one canonical source while allowing paperback, ebook, illustrated,
  and later reference-book editions to differ intentionally.
- Make migrations, imports, exports, and revisions safe, inspectable, and
  recoverable.

### 3. Human-led AI collaboration

- Let authors write and design manually, delegate bounded work, compare
  alternatives, and review proposed changes.
- Give assistants complete access to the same safe capabilities as the UI
  through shared application services.
- Require stable references, validation, revision awareness, and reviewable
  mutations rather than opaque document replacement.
- Reserve proof approval, external publication, rights declarations, purchases,
  and other consequential actions for explicit human decisions.

### 4. Book design and edition production

- Provide approachable defaults for prose books and progressively deeper tools
  for editorial review, page design, illustrated books, and nonfiction.
- Generate deterministic pagination, real previews, full-wrap covers, EPUB, and
  vendor-specific print artifacts from the semantic manuscript.
- Preflight fonts, images, color, geometry, metadata, accessibility, and format
  constraints inside Lorekeeper.
- Make every artifact traceable to its source revision, edition settings,
  assets, renderer, validation profile, and proof state.

### 5. Trustworthy publication

- Never describe an artifact as print-ready, accessible, PDF/X-conformant, or
  vendor-compatible without evidence for that declared scope.
- Keep standards, vendor requirements, dependency licenses, and validation
  profiles versioned and reviewable.
- Preserve local-first ownership of projects and credentials.
- Prefer permissively licensed, inspectable production components and make
  dependency obligations explicit.

## Delivery shape

Lorekeeper will grow in five deliberate phases:

1. publisher-ready novel and paperback/EPUB foundation;
2. professional editing and proofing;
3. illustrated and picture-book design;
4. nonfiction and reference books;
5. publisher operations and controlled distribution.

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
- a supported edition can be created, edited, designed, rendered, preflighted,
  proofed, and packaged inside the application;
- publication claims are backed by repeatable automated checks, independent
  inspection, vendor acceptance, and physical proofs where relevant;
- the system remains maintainable enough to extend from novels to illustrated
  and reference books without parallel document or rendering models.

## Guiding principle

Lorekeeper should understand the book as both a living body of ideas and a
physical or digital publication. Intelligence, authorship, design, and production
must remain connected—but never at the cost of user control or trustworthy
output.
