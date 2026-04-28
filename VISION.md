# Vision Statement: Narrative Coherence Engine

This project is a proof-of-concept system for AI-assisted long-form storytelling that demonstrates one core capability:

> An LLM can generate and assist with multi-chapter narrative writing while maintaining consistency with both established canon and evolving story state.

The system achieves this by combining:

- **Structured story memory** — characters, events, relationships
- **Retrievable context** — vectorized lore and prior chapters
- **User-guided control** — over what the AI knows and uses

## What This System Must Do

At its core, the app is not a writing tool — it is a controlled environment for testing narrative intelligence.

It must:

### 1. Maintain Story State Over Time

- Track characters, events, and locations as structured data
- Update this state after every chapter
- Use it to influence future writing decisions

### 2. Inject Relevant Context Into Generation

- Select only the most relevant story elements for each scene
- Ground AI output in:
  - Prior chapters
  - Extracted lore
  - User-defined tone/style
- Make this context visible and adjustable

### 3. Enable Assisted Writing Without Losing Control

- Allow the user to:
  - Write manually
  - Expand or modify text with AI
- Ensure all AI output is shaped by:
  - Current context
  - Stored narrative memory
  - Writing style reference

### 4. Detect and Surface Inconsistencies

- Identify contradictions in:
  - Character state
  - Timeline of events
  - Established lore
- Present them clearly and actionably
- Allow the user to resolve or override them

### 5. Evolve Memory as the Story Grows

- Extract new information from each chapter
- Update both:
  - Structured graph (entities + relationships)
  - Vector memory (semantic retrieval)
- Log these changes transparently

## What This System Is Not

To stay focused, this project explicitly avoids:

- Polished UX or onboarding
- Broad LLM provider support
- Monetization or productization
- General-purpose chat interfaces

This is not a competitor to Scrivener or Notion.

It is a testbed for solving narrative coherence with AI.

## Success Criteria

The POC is successful if:

- A multi-chapter story (e.g. in the Warcraft Universe) can be written
- The system consistently:
  - References past events correctly
  - Maintains character traits and states
  - Avoids major timeline contradictions
- The user can:
  - See what context the AI is using
  - Understand why outputs are generated
  - Correct mistakes through interaction

## Guiding Principle

The system must not just generate text — it must demonstrate that it understands and respects the evolving story.
