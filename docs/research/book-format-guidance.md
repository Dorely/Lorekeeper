# Genre-aware book-format guidance

Last reviewed: 2026-08-01
Source review date: 2026-07-14

## Finding

Book structure and visual treatment should be derived from reader behavior,
purpose, and the author-owned Book Brief, not from a chapter type or a universal
genre template. Conventions are useful prompts for tradeoffs; they are not
permission to overwrite explicit creative direction.

The research basis is the professional-editing and picture-book evidence in
[Story writing and editorial practice](story-writing-and-editorial-practice.md),
the semantic/source-versus-edition distinction in
[Book authoring and visual-design software](book-authoring-and-design-software.md),
and the page-turn, gutter, reading-order, and accessibility evidence in
[Page composition, typesetting, and accessibility](page-composition-and-typesetting.md).

## Current guidance contract

`BookFormatGuidanceService` selects compact modules from `BookKind`, free-form
genre/subgenre, audience and reader ages, reading-level guidance, target word
count, read-aloud priority, house style, visual direction, accessibility goals,
creative constraints, and the project's current paperback, Digital PDF, and
EPUB editions. The Outline system prompt receives at most four relevant modules;
`read_book_format_guidance` pages the expanded set when needed.

The modules cover:

- fiction: acts, chapters, scenes, causality, pacing, section breaks,
  illustrated moments, maps, and ornamental matter;
- narrative nonfiction: chronology, claim/evidence flow, sidebars, figures,
  captions, source notes, and image credits;
- general/reference nonfiction: reader questions, heading hierarchy,
  navigation, lists, diagrams, callouts, indexes, and accessible reading order;
- picture books: page turns, facing spreads, read-aloud cadence, copy density,
  text-image counterpoint, quiet copy regions, gutter risk, and total extent;
- illustrated books: prose/image rhythm, plates, captions, color/bleed,
  Designed Pages, spreads, and EPUB adaptation;
- poetry: poem and stanza integrity, intentional line breaks, whitespace,
  recto starts, ornamental pages, and explicit reading order;
- hybrid or uncertain books: combine relevant modules and ask one focused
  question only when the answer materially changes structure or format.

Every module distinguishes a flowing Figure from a Designed Page or facing
spread. Physical layout requires a concrete edition geometry; the guidance
never supplies trim sizes, image sizes, or provider canvases. Long-lived choices
belong in the Book Brief.

## Assistant and token behavior

Outline treats chapters as format-neutral containers of semantic text, Figures,
and Designed Pages. Concrete placeholders use the same revision-aware visual
services as Editor, Images, and Publish. Large scenes use one-use persisted
staging so a scene payload is sent once and the apply call contains only a stage
ID and expected revision. Reads are compact and paginated; results report
changed IDs/fields and prioritized diagnostics rather than full manuscripts or
unchanged scenes.

## Limitations and future research

The modules are intentionally qualitative. They do not encode a canonical
chapter count, picture-book extent, word count, type size, or illustration ratio.
User research should evaluate whether the selected advice is useful across
hybrid genres and culturally specific forms. Multilingual layout, advanced
reference structures, formal accessibility certification, and additional
vendor products remain separate roadmap work.
