# Story writing and professional editorial practice

Last reviewed: 2026-07-14  
Time-sensitive: moderately — review when Editors Canada publishes a successor to the 2024 standards.

## Executive finding

Lorekeeper's Editor should not be a generic chat assistant with optional writing flavor. Its actual system-role message should establish a professionally competent author/editor who can draft, critique, perform developmental or line work, copyedit, proofread, art-direct, and compose pages — while deliberately selecting the discipline appropriate to the request. Project Guidance and the Book Brief communicate the author's direction inside that code-owned role; neither substitutes for it.

## Professional roles and editorial stages

### Evidence

Editors Canada's 2024 standards define four editorial stages. Structural editing shapes organization and content for audience, medium, and purpose. Stylistic editing clarifies meaning and improves sentence/paragraph coherence and language. Copyediting corrects usage, grammar, spelling, punctuation, accuracy, and consistency. Proofreading occurs after textual and visual layout and checks correctness, completeness, formatting, design support, and adherence to the style guide. The standards note that stages can overlap but also caution proofreaders against silently undertaking broader editorial passes without authorization ([Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

Drafting, rewriting, coaching, manuscript evaluation, and production editing are related practices rather than interchangeable names for those four stages. Editors Canada describes production editing as coordinating design, formatting, proofreading, and integration of design and content; manuscript evaluation is critique rather than direct revision ([Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

### Lorekeeper interpretation

An agent that applies every pass simultaneously tends to over-edit: a proofreading request becomes a rewrite, a drafting request becomes a lecture, or developmental concerns disappear into sentence polish. The agent should identify the requested operation and use later-stage corrections only when needed to avoid leaving a new error.

### Implementation decisions

The code-owned system role names the available disciplines and instructs the agent to select the relevant one:

- **Drafting/revising author:** creates or substantially reshapes prose to the user's target.
- **Critic/manuscript evaluator:** diagnoses strengths, risks, and options without silently changing the work.
- **Developmental/structural editor:** addresses premise, architecture, content selection, causality, pacing, and large-scale reader experience.
- **Line/stylistic editor:** works on clarity, coherence, rhythm, emphasis, dialogue, and sentence/paragraph craft.
- **Copyeditor:** corrects mechanics, consistency, factual contradictions, and house-style issues while preserving prose.
- **Proofreader:** checks the near-final text and composed page, avoiding unauthorized structural changes.
- **Picture-book editor/art director/designer/typographer:** coordinates words, images, page turns, hierarchy, and accessibility.

Contest candidates and revision workers receive the same professional quality charter, narrowed by their mutation permissions.

## Audience, purpose, medium, and authorial intent

### Evidence

Editors Canada frames editing as optimizing content, language, style, and design for audience, medium, and purpose while balancing the author's/client's interests with the intended audience. Its standards call for attention to audience accessibility, final production, intended effect, and consistency of voice, style, point of view, tone, and register with the author's goals ([Editors Canada overview](https://editors.ca/publications/professional-editorial-standards/), [Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

The standards also distinguish correcting from querying: accuracy, missing information, quotations, visual labels, permissions, and conflicting instructions sometimes require a question or escalation rather than invention ([Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

### Lorekeeper interpretation

Audience and purpose cannot be a generic “write well” clause. They need durable structured representation so all agents and page tools can use them. At the same time, asking a full questionnaire before every useful action frustrates the writing process.

### Implementation decisions

- The Book Brief is the canonical structured home for premise, book kind, genre, themes, purpose, audience/age/reading level, length, POV, tense, voice/tone, locale, house style, read-aloud priority, accessibility goals, visual direction, and non-negotiable constraints.
- Project Guidance remains a free-form, optional, user-authored section for directions that do not fit or need not be normalized into the brief.
- Writing samples are style evidence, not canon. Facts, entities, manual links, beats, and directly read sources are canon evidence. The Book Brief and Project Guidance are authorial direction.
- Ask one or two focused questions only when a material creative choice cannot be safely inferred. Do not block a concrete request because optional brief fields are empty.
- Preserve meaning, voice, POV, tense, established continuity, and unrelated prose unless the task requires changing them. Prefer the smallest coherent edit that fully achieves the goal.

### Model limitation

A language model can infer plausible intent but cannot know private authorial preference. “Ask only when material” reduces interruption; it does not eliminate the need for user decisions when two reasonable directions would produce materially different books.

## Narrative coherence and craft

### Evidence

Editors Canada's structural standards call for coherent sequence, logical progression, narrative flow, appropriate content, removal of repetition/irrelevance, filling gaps, and effective placement of visual elements. Its narrative stylistic standards address intended effect, mood, scene movement, characterization, dialogue, viewpoint, and language-level engagement ([Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

BookTrust author Joyce Dunbar describes a picture-book story as more than a chain of events: it needs a predicament, meaningful unfolding, a turning point, and resolution. She recommends dummying the book to distribute events across spreads, making every word earn its place, attending to sound/pattern/repetition, and letting illustrations complement rather than merely duplicate the words ([BookTrust guide](https://www.booktrust.org.uk/resources/find-resources/joyce-dunbars-guide-to-writing-picture-books/)).

### Lorekeeper interpretation

The system role needs concrete editorial lenses, but they are evaluation prompts rather than a demand to force every story into one formula. Nonfiction and poetry need distinct modules because conventional scene causality or prose normalization can damage them.

### Implementation decision: adaptive craft checks

For fiction and narrative nonfiction, apply as relevant:

- structural promise, stakes, causality, escalation, and resolution;
- protagonist/subject agency and the consequences of choices;
- scene purpose and the balance of scene, summary, reflection, and exposition;
- POV access, tense, chronology, spatial logic, and continuity;
- pacing at sentence, scene, chapter, and book level;
- transitions that preserve cause, time, place, and emphasis;
- dialogue with distinct voice, subtext, credible action beats, and readable attribution;
- paragraph movement, syntax, diction, specificity, rhythm, and emphasis.

For general nonfiction, prioritize reader questions, claim/evidence order, definitions, examples, signposting, factual boundaries, source attribution, and usable navigation.

For poetry, protect lineation, stanza architecture, sound, image, rhythm, productive ambiguity, white space, and intentional departures from standard grammar.

These belong in compact dynamic system modules keyed to `BookKind`, not in user guidance and not as automatic diagnostics.

## Picture-book and illustrated-book practice

### Evidence

BookTrust's guide emphasizes visual thinking, spread-level pacing, brevity, read-aloud sound, patterns/refrains, a meaningful turning point, complementary illustration, and white space that invites the child reader's interpretation. The cited word counts and typical spread counts describe one experienced author's guidance, not a universal specification ([BookTrust guide](https://www.booktrust.org.uk/resources/find-resources/joyce-dunbars-guide-to-writing-picture-books/)).

### Lorekeeper interpretation

The useful principle is not “every book must have exactly N spreads or words.” It is that page turns are narrative units, text competes for limited visual and read-aloud attention, and the illustrator should carry some information unavailable in the prose.

### Implementation decisions

- Evaluate the story as pages and spreads, not only as a continuous manuscript.
- Place reveal, reversal, anticipation, or emotional change around page turns deliberately.
- Read short text aloud for cadence, breath, phonetic pleasure, repetition, and accidental tongue-twisters.
- Remove prose that merely inventories visible illustration unless repetition is purposeful.
- Give images narrative work: contradiction, extension, foreshadowing, humor, setting, secondary action, or emotional subtext.
- Protect negative space and avoid filling a page simply because space exists.
- Treat target word count and read-aloud priority as Book Brief direction, not fixed rules.

### Platform limitation

Lorekeeper can render and inspect pages, but it cannot reproduce every physical-book factor: paper, binding, ink, actual reading distance, adult/child shared-reading behavior, or a publisher's house format. Final production still merits human proofing in the intended medium.

## Editorial queries and decision discipline

### Evidence

Professional standards expect editors to correct when authorized, query uncertainty, explain editorial judgment when asked, manage conflicts, and avoid undoing earlier approved work. Proofreaders should verify requested changes without introducing new problems ([Professional Editorial Standards 2024](https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf)).

### Lorekeeper interpretation and implementation

The agent should:

1. Act when the user's target and safe preservation boundary are clear.
2. State consequential assumptions when evidence is incomplete but a reversible choice is possible.
3. Ask when the missing choice changes premise, audience, format, POV, ethics, factual truth, or other non-reversible direction.
4. Identify conflicts between author direction and canon rather than silently selecting one.
5. After mutation, inspect the returned/current state and repair obvious errors in the same turn.

This decision policy belongs in system instructions. Automatic retrieval and mutation verification belong in application workflow. Project-specific exceptions belong in Project Guidance or the Book Brief.

## Practical Lorekeeper editorial checklist

### Before work

- What operation did the user request: draft, critique, development, line edit, copyedit, proofread, image, or composition?
- What are audience, purpose, format, intended effect, and non-negotiables?
- What material is canon, style evidence, authorial direction, or merely a retrieval hint?
- Is the selected passage/chapter/page complete enough for the requested change?

### During story work

- Does each structural unit have a purpose and causal relationship to what surrounds it?
- Are stakes, agency, viewpoint, time, place, knowledge, and continuity intelligible?
- Does pacing spend words where reader attention and emotion require them?
- Are transitions, dialogue, paragraphs, sentences, rhythm, and diction doing the intended work?
- Have voice, tense, POV, meaning, and unrelated material been preserved?

### For picture books

- What does the page turn promise or reveal?
- Does the text work aloud?
- Are words and images complementary?
- Is there enough visual and interpretive space?
- Is copy short because it is precise, not merely sparse?

### At completion

- Did the agent use the correct mutation path for Prose, IllustratedProse, or PicturePage?
- Were changes verified against current state?
- Did the edit introduce a new inconsistency, overflow, accessibility problem, or canon conflict?
- Is any remaining uncertainty material enough to tell the user?

## System-prompt requirement mapping

| Finding | Home | Requirement |
|---|---|---|
| Professional stage distinction | Code-owned system role | Name disciplines; select one appropriate pass rather than applying all passes. |
| Audience/purpose/medium | System role + Book Brief | Optimize toward structured authorial direction. |
| Voice and intent preservation | Code-owned system role | Preserve meaning, voice, POV, tense, continuity, and unrelated prose. |
| Canon versus style evidence | System role + context metadata | Facts/manual links/beats/sources as canon; samples as style evidence. |
| Missing creative direction | Outline operating rules | Ask 1–2 focused questions and patch the Book Brief in the same turn. |
| Narrative checks | Dynamic book-kind prompt modules | Fiction, nonfiction, picture book, illustrated book, and poetry guidance. |
| Page-turn/read-aloud practice | Picture-book module + Book Brief | Apply when book kind/read-aloud priority makes it relevant. |
| Context selection | Application automation | Direct links and adjacent structure before semantic retrieval; retain provenance. |
| Measurable page failures | Diagnostics | Overflow, collision, safety, reading order, and contrast; do not turn craft judgment into false hard failures. |

## Sources

- Editors' Association of Canada / Editors Canada, “Professional Editorial Standards 2024,” April 2024, https://editors.ca/wp-content/uploads/2024/05/EditorsCanada_ProfessionalEditorialStandards_2024.pdf (accessed 2026-07-14).
- Editors Canada, “Professional Editorial Standards,” page describing the 2024 edition, publication page update date not displayed, https://editors.ca/publications/professional-editorial-standards/ (accessed 2026-07-14).
- Joyce Dunbar, “Joyce Dunbar's guide to writing picture books,” BookTrust, publication/update date not displayed, https://www.booktrust.org.uk/resources/find-resources/joyce-dunbars-guide-to-writing-picture-books/ (accessed 2026-07-14).
