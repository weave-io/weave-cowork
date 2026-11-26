---
name: cowork-design
description: Design any HTML page you show people with cowork_show, such as reports, comparisons, results, dashboards, documents and small tools, so it looks chosen for its subject instead of generic. Covers how much design a page needs, palette, type from CoWork's font kit, layout, light and dark themes, phone width and copy. Use before writing a page's HTML or CSS.
---

# Designing a page for CoWork

Pages built by an agent tend to look alike: a white page, a saturated blue header row, cards with a coloured bar down
the side. This guide makes each page look chosen for its subject and built with care. People read your page in
CoWork's canvas, beside the thread, in CoWork's light or dark theme.

## 1. Where the page goes and what it may load

- Write it inside your workspace, in a folder of its own: `reports/<name>/index.html`, with any other files it needs
  in the same folder. Then call `cowork_show({ path: "reports/<name>/index.html" })`. After a change, call it again
  with the same path: people get the new version.
- **Nothing from the internet loads.** The page may load only the files in its own folder and CoWork's font kit. No
  Google Fonts, no CDN scripts or stylesheets, no remote images. Keep your CSS and JavaScript inline; copy a library
  into the folder only when it does real work.
- **Fonts come from CoWork's kit.** Link it once, in the `<head>`:
  `<link rel="stylesheet" href="/a/fonts/fonts.css">`. Then name a family, always with a fallback stack:

  | Family | Weights | Mood and use |
  |---|---|---|
  | `"Fraunces"` | 400, 600 | Warm, soft display serif. Headlines and big figures. |
  | `"Instrument Serif"` | 400, italic 400 | Elegant, narrow display serif. Large headlines only. |
  | `"Source Serif 4"` | 400, 600, italic 400 | Book serif made for reading. Long text, documents. |
  | `"IBM Plex Sans"` | 400, 600 | Plain, engineered sans. Body text, interfaces, tables. |
  | `"Bricolage Grotesque"` | 400, 700 | Sans with character. Headlines with a voice. |
  | `"IBM Plex Mono"` | 400, 500 | Figures, codes, labels, captions. |

  Fonts you put in the page's folder (`.woff2`, under an open licence) work too.
- **Size:** the canvas is 400 to 900 pixels wide, and full screen on a phone (375 pixels).

## 2. Decide how much design it needs

- **A report, a comparison, results, a plan, a document:** clear structure, plus one move taken from the subject.
  Everything else stays quiet. Follow the recipe below.
- **Distinctive** (a landing page, a game, a tool people will keep or share): take a point of view (section 6).

A well-set page with one good idea beats an overdesigned one.

**The recipe for a report.** These five are where agents' pages most often go wrong; keep to all of them.

1. **Open with the answer.** Right under the title: the verdict or recommendation in one or two sentences, with the key
   figure set large (in the display face). Tables and detail come after it. Figures use the subject's own units and
   currency (an EU question is in euros).
2. **Show amounts as a chart** when the page compares numbers: simple horizontal bars in HTML and CSS (a track and a
   filled bar sized with `width: NN%`), each with its label and value. Each bar's width is its value divided by the
   largest value (the largest is 100%), never a rank or a preference. Keep the full table too if it has more columns.
   A pattern that works (the fill sits inside a track, so its width counts; never give a bar `flex: 1`):

   ```html
   <div class="bar is-answer"><span>Levy.io</span><span class="track"><i style="width: 52%"></i></span><b>€0.22</b></div>
   ```
   ```css
   .bar { display: grid; grid-template-columns: 7rem 1fr 5rem; gap: 12px; align-items: center; }
   .bar .track { height: 12px; background: var(--track); border-radius: 2px; }
   .bar .track i { display: block; height: 100%; background: var(--bar); border-radius: 2px; }
   .bar.is-answer .track i { background: var(--accent); }
   .bar b { text-align: right; font-variant-numeric: tabular-nums; }
   ```
3. **Tables are quiet:** the header row is small, muted, uppercase text with no fill colour; rows are split by thin
   lines in the divider colour; numbers are right-aligned in the mono face with `tabular-nums`.
4. **No boxes and bars around sections:** no `border-left` stripe, no card per section. Separate sections with space
   and a heading. If one thing needs setting apart (the answer), give only that one a soft tint.
5. **One accent, used sparingly:** for the answer and at most one highlight in the chart. Body text stays in the text
   colour.

## 3. Start from what exists

The person's words win, then the project's own design system, then your choices. If the page is part of the project's
app, or a mockup of one, look for its tokens or theme file, its main stylesheet, or design notes in AGENTS.md or
CLAUDE.md, and use its colours, fonts, spacing and components. This guide only fills the gaps.

## 4. Plan before writing CSS

Write a short plan first, as a comment at the top of your `<style>`:

- **Colour:** 4 to 6 named hex values, for example ground, surface, text, muted text and accent, for light and for dark.
- **Type:** a face for headings, one for body text, and the mono for figures and captions if needed, from the kit.
- **Layout:** one or two sentences.

Take every colour and type choice from the plan.

Ground it in the subject: who reads the page, and the one job it does. Include at least one detail only this subject
has, such as its real units, currency, terms or conventions, as content rather than decoration. Use real content
throughout, never lorem ipsum.

## 5. Rules for every page

**Type**
- Pair faces on purpose, from the kit. Always give a fallback stack.
- Keep running text about 65 characters wide. Pick a type scale and keep to it.
- `text-wrap: balance` on headings, a little letter-spacing on uppercase labels, and
  `font-variant-numeric: tabular-nums` wherever digits line up in columns.
- Don't break headings by hand with `<br>`. Let `text-wrap: balance` place the breaks.

**Colour and themes**
- Tint the greys slightly toward the accent. A pure mid-grey looks like a default nobody chose.
- Every colour is a variable on `:root`. Under `@media (prefers-color-scheme: dark)`, redefine only the variables and
  set `color-scheme: dark`. Never define a colour only inside the dark block. CoWork switches these rules to match the
  theme people chose in CoWork, so always write both and don't pick the theme with JavaScript.
- Give `body` its background from a variable.
- Design the dark theme instead of inverting the light one. Check contrast, and check the accent works on both grounds.
- Status colours (good, warning, critical) are separate from the accent.

**Layout**
- Space siblings with flex or grid and `gap`, not margins on each element.
- Keep 16px side gutters at every width, set once on `body` or one wrapper. Stack to one column around 400px wide.
  The page never scrolls sideways; only a wide table, code block or diagram may, inside its own `overflow-x: auto` box.
- Long unbroken text is what usually breaks phone width: a shell command, a URL, a hash. Give the grid or flex item
  that holds it `min-width: 0`, and give the `pre` `overflow-x: auto` (or let a URL wrap with
  `overflow-wrap: anywhere`).
- `max-width: 100%` on images.
- Repeated items, like cards in a row or rows in a list, share edges, padding and baselines. Choose a column count the
  items fill, so nothing sits alone in a row or stretches over empty space. Text that can outgrow its box wraps;
  clipped text is a bug.
- Borders, fills, shadows and rounded corners mark something as separate. Use them on the one thing that needs setting
  apart, not on every block. Open with big-number tiles only when the numbers are the point of the page.

**Complete on load**
- Everything meant to be read shows on first load, with no `opacity: 0` waiting for a scroll. Size a hero to its
  content, not to `100vh`.
- A tool opens with example data filled in and labelled as an example, never as an empty shell.

**Structure means something**
- Number sections 01, 02, 03 only when the order matters. Eyebrows, dividers and labels say something true about the
  content.

**Pages people operate** (dashboards, tools)
- Summary first, detail after. Show state in shape as well as colour: a pill, a chip, a stripe. Controls look
  clickable.
- Charts use one scale for marks, ticks and labels. Labels only name values the chart reaches, take their colour from
  the theme variables, and stay inside the drawing. Simple bars in HTML and CSS are often enough.

**Avoid the generic AI look.** Unless someone asks for one of these, don't use:
- a white page with a saturated blue table header, or a coloured bar down the side of rounded cards
- a cream or warm off-white ground with an orange or terracotta accent, whatever the typeface
- near-black with a single acid-green or vermilion accent
- a purple-to-blue gradient hero on white
- newspaper hairlines and dense columns
- emoji as section markers
- everything centred, the same rounded corners on everything

**Build it cleanly**
- Visible focus, real buttons and links, and `prefers-reduced-motion` respected.
- Watch selector specificity: a type selector and a class both setting margins will quietly undo your spacing.
- For generated art, draw on a canvas instead of writing long SVG paths by hand.

**Copy**
- Name things the way the reader knows them: "notifications", not "webhook config".
- Buttons say what they do ("Publish"), and confirmations say what happened ("Published").
- Errors say what went wrong and how to fix it, without apologising.
- Short, plain sentences. No asides between dashes, no "not X, but Y", no scare quotes, no "it's worth noting".
- The page title is a name, two to four words, not a summary.

## 6. Distinctive pages

- Before the plan, write two or three directions in a sentence each, taken from different parts of the subject's world
  (its tools, its places, its paperwork, its people), and pick the one that fits best. Your first idea is the one
  every other agent has too.
- Check the plan against the subject. Change any part you'd have picked for any similar page.
- Open with the most characteristic thing in the subject's world: a headline, a figure, a live demo.
- Type carries the personality. Pick from the kit on purpose.
- Make one bold move and keep everything around it quiet. If the accent fights the ground, shift or desaturate it
  instead of replacing it.
- Motion: one planned moment beats effects scattered everywhere, and none at all is often better.

## 7. Check it, then show it

CoWork can't look at the page for you, so check the file before you call `cowork_show`:

- No `http://` or `https://` in a `src`, an `href` to a stylesheet, or a `url()`: only the folder's files and
  `/a/fonts/fonts.css`.
- Every colour is a variable, and the dark block redefines them all.
- No fixed width over 360px outside an `overflow-x: auto` box; tables and code sit in one.
- The answer is the first thing after the title.
- Every bar's width is its value divided by the largest value: check each one.
- Search your CSS: no `border-left` (or `border-inline-start`) in a colour, and no `background` on `th` or `thead`. If
  you find one, take it out.

Then show it, say in one or two sentences what it shows, and let people ask for changes.
