# UI philosophy

These rules decide what goes on screen and how it's said. They don't set a look: layout, palette, typeface and
component style are yours to choose. Whatever you pick, the result should feel **calm, restrained and finished, like
a premium native app**.

The core rule is **verbose, not bloated**. Give the user every fact they act on, and only those. "Verbose" means
rich, honest data where it helps a decision. It doesn't mean everything you could measure.

## 1. Every element earns its place

- **Ask what each element helps the user decide or do on this surface.** If nothing, cut it. Being measurable doesn't
  earn a spot: file counts, server names and internal IDs usually go.
- **Say each fact once per surface.** If a number, state or word is already visible (in a heading, badge, stepper,
  button or chart), every other copy goes. Use one formatter and one precision per quantity everywhere, so "58.8 MB/s"
  never sits next to "59".
- **Give each surface one job.** A setting lives in Settings. Other screens show only its *effect*, and only when that
  effect is unusual ("Held to your 25 MB/s limit." plus a `Change` link that opens the setting). When things are
  normal, show nothing.
- **Let labels carry the meaning.** Skip helper text that repeats a label. Headings and buttons say different things
  (heading `Connect your account`, button `Sign in`). A tooltip adds something the label doesn't, like a shortcut.
- **Use a short status and disclose the reason.** Write `Slow download ⓘ`, not a sentence explaining why. The full
  reason opens on click or focus. Anything essential works without hover.
- **Show only the sections that exist for this item.** Hide empty sections. Put rare or advanced controls under one
  collapsed group (for example "Item settings").
- **Cut filler**: greetings, welcome banners, and copy that restates what the user just did.

## 2. Hierarchy and density

- **Build density with type and whitespace, not boxes.** No card inside a card, no bordered panel inside a dialog, and
  few dividers. Size, weight, colour strength and spacing make the hierarchy.
- **Show rich data as readouts, not a spreadsheet.** Headline numbers are big with a quiet unit and tabular figures.
  A chart's legend doubles as its live values. Leftover facts go on one caption line of short phrases. Avoid columns
  of label/value rows.
- **Give each screen one primary action.** On repeated items (cards, rows), actions appear on hover or focus, so the
  page doesn't open with several competing primary buttons.
- **Lead with the answer.** A dialog opens with what the decision turns on ("Fits, with 624 GB to spare"). Lists
  appear only when they have entries.
- **Put problems first.** Failures and items that need attention go above everything else, including success states.
  A reassuring headline never sits above an unresolved problem. A failure goes away only when it's fixed or the user
  dismisses it explicitly, never through a bulk "Clear all".

## 3. Status and colour

- **Use colour to mean something, as one system** that every surface reads from a single status table:
  - danger: something failed, or data is at risk
  - warning: needs the user's action, but nothing is broken
  - accent: informational (an update is available)
  - normal states: no colour at all
- **Say a state once.** A failed row isn't an icon plus a red label plus a red box plus a red title. The section
  heading ("Needs attention") already says it; the row adds the one-line reason and the fix button.
- **Use one accent colour and a neutral primary.** Keep the rest of the palette quiet so status colour stands out.

## 4. Copy

- **Be plain, specific and actionable**: what happened, then one fix, e.g. "Close Foo and any program using its
  files, then try again." Put the fix button next to the message.
- **Design every edge state**: offline, signed out, expired session, empty, first run, partial success ("6 ready,
  2 need a repair", not "8 imported"). Each one is honest and comes with its own action.
- **Keep a glossary**: one term per concept, one form per phrase (`Try again` everywhere), sentence case, `…` for
  actions that open something, and one date rule (relative by calendar day under 7 days, absolute after that).
- **Name actions exactly**, without repeating the title: `Cancel install`, not `Cancel install of Some Long Name`.

## 5. Geometry and consistency

- Every page starts at the same left edge. Narrower pages end sooner instead of being centred.
- Use one spacing grid (4 px), one icon scale, and the same height and radius for controls in a row.
- Offer each action once per surface. Search results, menus and suggestions show it a single time.
- Make the light and dark themes equally finished.

## 6. Motion

- **Keep motion brief and purposeful**: about 100–200 ms, growing from the element that triggered it (menus from
  their button), and honouring reduced-motion settings.
- **Keep layout still.** Reserve space for content that's loading, open dialogs only once their content is ready (or
  anchor them to the top), and make a paused state the same size as the running one.
- **Animate in order.** Content arrives with or after its container. A shared-element transition looks like the thing
  that was clicked, and cached images show at frame 1.
- **Allow at most one perpetual animation on screen** (a progress sheen, say), and only while the window is focused.

## 7. How to work

1. **Build** the screen with real-shaped data.
2. **Shoot** every state: normal, loading, empty, error, partial failure, offline, signed out, worst-case data (very
   long, very short, non-Latin text, emoji, huge numbers), narrow window, both themes.
3. **Dump the visible text** of each state and look for repeated facts, a heading that matches its button, helper
   text that restates a label, and inconsistent terms.
4. **Cut.** Aim to remove about 30% of visible text and elements per pass, and list what you removed and why.
5. **Check** against the list below. A screen is done when every item holds in every state you shot.

### Done checklist

- [ ] Every visible element helps a decision or action on this surface.
- [ ] No fact, number or action appears twice on one surface.
- [ ] Settings are changed only in Settings; other screens show the effect only when it's unusual.
- [ ] One primary action per screen.
- [ ] Problems sit above success states, with a fix next to each.
- [ ] Colour follows the status table, and normal states have none.
- [ ] No boxes inside boxes; rich data reads as readouts plus a caption.
- [ ] Empty sections are hidden; rare controls sit in one collapsed group.
- [ ] Every edge state has designed copy and its own action.
- [ ] Nothing jumps or resizes after it appears.
- [ ] Copy matches the glossary.
