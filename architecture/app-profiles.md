# App profiles: gestures made for one phone app

Status (2026-10-05): **proposed, not built.** First apps: **Claude** (`com.anthropic.claude`) and
**Chrome** (`com.android.chrome`). Both maps come from the user's description. Claude's buttons
were checked against its UI tree on the S25 the same day (below), and so was Chrome's web tree;
whether Chrome's element walk answers our service is still to check (step 2).

A phone session uses the same gestures in every app ([glasses-client.md](glasses-client.md),
"Swipes" and "Pinches"). That's fine for scrolling and tapping, but slow for an app you use all
the time: in Claude, reaching the message box, + or the microphone means pinch-dragging the
cursor to each one; in Chrome, reaching the next link does too. An **app profile** gives one app
its own gestures and behaviours on top of the generic ones, as many as the app needs:

- **Claude:** down twice puts the cursor on the message box; from there down goes to +, and
  right on to the model picker and the microphone. A pinch presses whatever is highlighted.
- **Chrome:** up and down still scroll. Down twice hops to the next link or button, up twice to
  the previous one, right and left twice to the next and previous heading. A pinch presses it.

Phone sessions only. A PC session has its own app shortcuts (1, 2, …) and nothing here
changes them.

## The rules

These decide what an app may change and what stays the same everywhere. The ? panel and the
tests follow them.

1. **Back belongs to the glasses app, never to a phone app.** Back (middle-finger pinch) from the
   view or a highlight brings up the bar **with Apps focused**, so Back then a pinch opens the
   phone's app overview from anywhere. In the app overview Back leaves it, as today. Back on the
   bar returns to the view. No
   app can take Back over, which is how you always get out of an app's profile.
2. **The bar, the Type panel and the app overview are the same in every app.** Once the bar is
   up, every gesture does what it does in any other app.
3. **On the view, the app's gestures come first and the rest stay generic.** A profile lists the
   swipes it takes over. Anything it doesn't list does what it does in any other app: Claude
   takes only down twice, so right twice is still Type there. Chrome takes all four doubles, so
   in Chrome Type is a text field's pinch (rule 4) or the bar's Type button, and the app overview
   is Back then a pinch (rule 1).
4. **Pinches don't change.** A pinch taps at the cursor, two make a double tap, and
   tap-and-a-half holds a finger down, in every app. A profile only decides where the cursor is.
   A highlighted **text field** opens Type after its tap, in every profile.
5. **Only the app in front counts.** The phone tells the glasses which app the view follows. When
   that app changes, its profile stops, and anything highlighted is dropped.
6. **You can turn an app's profile off, and the glasses remember it for that app.** Profiles are
   on by default. An app update can move or rename what a profile looks for, so **✦** on the bar
   turns the profile off for the app in front. With it off, the app gets the generic gestures.
   (User's request, 2026-10-05.) An app without a profile of its own can have **Walk** instead
   (below): off by default, ✦ turns it on for that app.

## Seeing and switching a profile: the status bar and ✦

- **The status bar shows when a profile is at work.** The app's name there (today
  `Claude 1080×1080`) gets a mark in front of it: `✦Claude 1080×1080` while a profile is on (the
  app's own, or Walk), `✧Claude` (hollow) while the app's own profile is off, and nothing for an
  app without a profile of its own and Walk off. One character, so the status bar stays one
  line; the yellow input readout already takes most of the room.
- **✦ on the bar** switches the profile of the app in front: its own one, or Walk for any other
  app. It's highlighted while a profile is on and plain while it's off, as ♪ is in a PC session.
  The status bar says which way it went ("Chrome profile off: generic gestures", "Walk on in
  Settings: down twice for the next item").
- The setting is kept per app in localStorage (`glasses.appProfiles`: package → on/off), like
  the ↕ scroll strength. Only a choice that differs from the default is stored (own profiles
  start on, Walk starts off). Pair again doesn't clear it.
- With an app's profile off, the ? panel shows the generic rows under a line saying "Claude
  profile off (✦ turns it on)". That way it's clear why down twice is Back again.
- When a profile can't find what it was asked for, the status bar says so and suggests the
  switch ("Claude: no message box found (app updated?) · ✦ on the bar turns Claude's profile
  off"). The highlight doesn't start, and nothing is tapped.
- The bar label is one character with no space (`✦`), as CLAUDE.md asks for bar labels. A wider
  label could push the bar onto two lines.

## What a profile is made of

Claude and Chrome look different but are built from the same parts. The shared ones live once,
in `client-web/src/apps/`; a profile only fills them in.

| Part | What it is | Claude | Chrome |
| --- | --- | --- | --- |
| **Shortcuts** | the view gestures it takes over (rule 3) | down twice → the message box | down/up twice → next/previous item; right/left twice → next/previous heading |
| **The highlight** | a box round one thing on the phone's screen, with the cursor on it and its name in the status bar; a pinch presses it (rule 4) | shared | shared |
| **How the highlight moves** | **by a map** between named buttons the glasses find in the phone's `controls` list, or **by walking** the page in reading order, which the phone does | by a map | by walking |
| **Swipes while highlighted** | `map`: single swipes step through the map at once (no doubles) · `view`: swipes act as on the view (singles scroll and drop the highlight, doubles hop again) | `map` | `view` |
| **After the tap** | a text field opens Type; a profile may add more later | shared | shared |
| **Notes** | extra rows for the ? panel | yes | yes |

A profile may use both ways of moving: Chrome could later take a map for its own toolbar
(address bar, tabs) beside walking the page.

### Walking the page (Chrome)

A web page already says what its parts are: links, buttons, form fields, headings, landmarks and
articles, from its HTML and ARIA roles. Chrome turns that into Android's accessibility tree, and
lets an accessibility service step through it **by kind, in reading order**: the same
`ACTION_NEXT_HTML_ELEMENT` / `ACTION_PREVIOUS_HTML_ELEMENT` that TalkBack's "Links", "Headings"
and "Controls" reading modes use. Chrome finds the next one even when it's off screen and
scrolls it into view. So the companion doesn't need to understand Reddit or any other site: it
asks Chrome for "the next link after this one", waits for the scroll to settle, and reports
where it ended up. A well-built site (real links and headings) works best; a site that makes
buttons out of plain `div`s may hide some of them from the walk.

- **Units** (a fixed list, mapped to Chrome's element types): `item` (anything you can press:
  links, buttons, fields; Chrome's `FOCUSABLE`), `link`, `heading`, `field`, `landmark`.
- **Where a walk starts:** from the highlighted thing if it's still on screen; otherwise (nothing
  highlighted yet, or you scrolled it away) from the top of the screen going down, the bottom
  going up. So scroll, then down twice, and you get the first item where you're reading, not
  one back where you were.
- **The end of the page:** the phone answers "none" and the status bar says so ("no more links
  below"); the highlight stays where it was.
- **Chrome gives us the whole page (checked 2026-10-05).** With our service on, a UI dump of a
  Reddit post in Chrome on the S25 held the full web tree: 634 nodes, 145 of them clickable,
  with their labels ("Upvote", "3 Go to comments", "Share", "Open menu"), **including the parts
  below the screen** (their boxes squashed flat at the bottom edge). So the walk can reach
  things off screen; a box that's flat isn't on screen yet, and the phone waits for Chrome to
  scroll it in before answering.
- **Still to check:** whether Chrome's element walk (`ACTION_NEXT_HTML_ELEMENT`) answers our
  service, and scrolls. If it doesn't, the companion walks the same tree itself in reading
  order and scrolls the node in (`ACTION_SHOW_ON_SCREEN`). That is a fallback inside the
  companion; the protocol and the profile stay the same.

## The app overview stays separate

The app overview (left twice, or Apps on the bar: `pickingApps`, `appsRow` and the
`overviewApps` message) works much like a highlight, but it isn't an app's profile: it's opened
rather than followed, it's the same in every app, it swipes the phone's cards rather than
stepping between buttons, and ✦ must never switch it off. Folding it into the profile structure
would bend the structure to fit, so it stays as it is, beside profiles (user's decision,
2026-10-05). Rule 2 keeps it the same in every app.

## Walk: the built-in profile for any app

An app without a profile of its own can have **Walk**: down twice and up twice step to the next
and previous thing you can press (`walk item`), in reading order; up and down still scroll, and
a pinch presses the highlighted thing. It changes nothing else: right twice stays Type and left
twice the app overview.

- **Off by default, on per app with ✦** (rule 6): it takes down twice, which is the phone's Back
  in every app without a profile.
- The phone walks a native app's tree itself, in reading order (top to bottom, left to right,
  as `controls` lists it), and uses Chrome's element walk for web content.
- **Chrome is Walk plus headings:** its profile extends Walk with right/left twice for headings.
  Profiles compose this way: a profile can start from another and change a few gestures.

## States of the view

Today the view has two states: the free cursor and the app overview. App profiles add a third:

```
            Back (anywhere)                      pinch on Apps (focused)
  view ───────────────────────► bar ─────────────────────────────► app overview
  (free cursor)  ◄── Back / up/down ──┘                                  │
     │   ▲                                                              │ pinch picks
     │   │ pinch-drag, a swipe with nowhere to go (map),                ▼
     │   │ a scroll (view), the app changes                      view (free cursor)
     ▼   │
  highlight  (a profile's shortcut: down twice in Claude or Chrome)
     swipes: the map (Claude) or as on the view (Chrome: scroll, or hop again)
     pinch:  taps it (a text field then opens Type)
```

- **Free cursor:** as today. Pinch-drag moves the cursor, swipes scroll and page, and the app's
  shortcuts (rule 3) apply.
- **Highlight:** the cursor jumps to one thing and a box is drawn round it; the status bar names
  it and what comes next ("Claude · message box: pinch to type · down: +", "Chrome · link
  'Comments': pinch opens · down twice: next"). With a `map`, swipes step through it at once and
  one the map doesn't list leaves the highlight. With `view`, a scroll leaves it and a double
  hops again. Pinch-drag always leaves it. The screen keeps moving underneath (a reply streams
  in, a page loads), so after each step and each tap the glasses ask the phone again.
- **App overview:** unchanged and separate from profiles (see "The app overview stays
  separate").

## How it's built

The two ends split the job:

- **The phone reports and walks; it knows no app.** On request, the companion lists what you can
  tap in the window the view follows (`controls`), or steps through the page by kind (`walk`).
  Either way it answers with boxes and labels, as it does for the overview's row of apps today.
- **The glasses know the apps.** Each supported app has one file in `client-web/src/apps/`: its
  shortcuts, how it finds its buttons, its map, notes for the ? panel. Plain TypeScript, tested
  against snapshots taken from the real app.

Why the app knowledge lives on the glasses and not in the companion:

- Apps change their screens with updates. A fix on the glasses goes out as a web deploy (the
  relay, or `npm run build` on the PC), and the user just presses Restart. A fix in the companion
  means building and installing a new APK on every phone.
- The gestures, the highlight and the ? panel are all on the glasses already, so the app's
  map sits beside them in one file and one test.
- The companion stays general: `controls` and `walk` work for any app (see "Later").

### Phone ⇄ glasses: additions to the DataChannel protocol

Kept in sync in `phoneProtocol.ts` ⇄ `InputProtocol.kt` (glasses → phone) and the glasses'
parser (phone → glasses), as strict as the rest. One item shape is shared: `{x, y, w, h, kind,
label, id}`, the box in 0..1 of the frame the glasses see (clipped to it), `kind` one of
`button`, `link`, `field`, `heading`, `toggle` or `text`; `id` the entry name of the view's
resource id (`input_field` from `com.anthropic.claude:id/input_field`; ≤ 40 characters, or
empty: Jetpack Compose apps such as Claude have none). `label` (≤ 40 characters):

- for a button or link, its content description, else its text, else those of the nodes
  inside it (in order, joined with a space). Compose puts the label on the icon inside the
  clickable node: Claude's + is a clickable node with no label holding an icon described "Add
  context", and its model picker holds the texts "Opus 5.5" and "High";
- for a text field, only its hint; never its text or the nodes inside it, which are what you
  typed (or Compose's placeholder, which becomes your text once you type).

| Message | Direction | Content |
| --- | --- | --- |
| `screen{…, pkg?}` | phone → glasses | New field: the package of the app the view follows (`com.anthropic.claude`; ≤ 100 chars, letters, digits, `_` and `.`). `app` stays the display name for the status bar. Profiles are keyed by package because display names change with the language and with Samsung's renaming. |
| `controls{}` | glasses → phone | Where are the tappable things in the window the view follows? |
| `controls{pkg, items}` | phone → glasses | Up to 64 items: visible nodes that are clickable or editable, at most 20 levels deep. `pkg` lets the glasses drop an answer that arrives after the app changed. |
| `walk{dir, unit}` | glasses → phone | Step to the `next` or `previous` thing of that `unit` (`item`, `link`, `heading`, `field`, `landmark`) in the window the view follows, from where the last walk ended (see "Where a walk starts"). |
| `walked{pkg, item?}` | phone → glasses | Where it landed, once the page has stopped scrolling; no `item` at the end of the page. |

What the phone **never** sends:

- **What you typed.** A text field's own text isn't sent, only its hint ("Reply to Claude").
  Password fields are left out altogether, and a walk skips them.
- **Anything outside the followed app's window**: no status bar, notifications, keyboard or
  other apps.

The labels are words visible on the phone's screen, which the glasses already show in the video.
Even so they're treated like typed text: never logged on either side (log the count of items),
never stored.

### On the glasses

```
client-web/src/apps/
  types.ts         AppProfile, ControlMatcher, ProfileAction; nothing app-specific
  match.ts         finds a profile's named buttons in a `controls` list (pure, tested)
  highlight.ts     the highlight state: what a swipe or pinch does in it, given the profile (pure)
  walk.ts          Walk (built in, for any app, off by default)
  claude.ts        the Claude profile
  chrome.ts        the Chrome profile (extends Walk)
  *.test.ts        against fixtures/<app>-<screen>.json (real lists from the S25, personal text removed)
  index.ts         the registry: package → profile
  prefs.ts         on/off per app (localStorage `glasses.appProfiles`), like scrollPrefs.ts
```

A profile, in outline:

```ts
type ProfileAction =
  | { highlight: string }                          // a named button (found with `controls`)
  | { walk: 'next' | 'previous'; unit: WalkUnit }; // the phone walks the page

interface AppProfile {
  name: string;                         // 'Chrome': the ? panel's heading, the status bar
  packages: readonly string[];          // ['com.android.chrome', 'com.chrome.beta']
  /** View gestures this app takes over (rule 3); the rest stay generic. */
  gestures: Partial<Record<SwipeGesture, ProfileAction>>;
  /** How swipes behave while something is highlighted (see "What a profile is made of"). */
  whileHighlighted: 'map' | 'view';
  /** Named buttons and how to find them in `controls`; tried in order, first match wins. */
  controls?: Record<string, readonly ControlMatcher[]>;
  /** From each named button, where a swipe goes: a list means "the first of these on screen". */
  moves?: Record<string, Partial<Record<Swipe, string | readonly string[]>>>;
  /** The ? panel's rows for this app, besides the gestures, which are derived. */
  notes: readonly ShortcutRow[];
}

// Profiles compose: const chrome = extend(walk, { name: 'Chrome', packages: […], gestures: {…} }).

type ControlMatcher =
  | { id: string }                                    // resource id entry name
  | { label: RegExp }                                 // content description / hint / text
  | { kind: 'field'; pick: 'lowest' | 'highest' }     // by structure
  | { between: [string, string] };                   // the button between two named ones
```

- `swipes.ts` stays the one place swipes are decided: `phoneSwipeAction(gesture, profile?)`
  checks the profile's `gestures` first (rule 3). The reader's `waitFor` comes from the merged
  map: Chrome takes up twice, so in Chrome a single up waits 0.3 s like the other three, and
  apps without such a double don't pay for it.
- `shortcuts.ts`: `shortcutRows('phone', profile?)` returns a heading ("In Chrome") with the
  app's gestures and notes, then the generic rows the app hasn't taken over. The doubles still
  come from `swipes.ts`, so the panel can't drift from what the gestures do. The panel's title
  says which app it's for, so you know these shortcuts belong to that app.
- `PhoneScreen.tsx`: the highlight is a third view state beside the free cursor and
  `pickingApps`, driven by `highlight.ts`, with a box drawn in `overlay.ts` (bright on dark: the
  display is additive). The bar's buttons become **Apps** · Back · Home · Notif · Type · ↕ ·
  ✦ · ? · End (one more than today: check it still fits on one line on the glasses). Apps is the first and focused button
  (rule 1). The glasses reset focus to the first button after a Back anyway, so putting Apps
  first also avoids a fight with `focusPinned`.
- The active profile is the registry's match for `screen.pkg` unless `prefs.ts` says it's off,
  or Walk where `prefs.ts` says it's on. None while the app overview is open. Everything else (`swipes.ts`, `shortcuts.ts`, the highlight) sees only the
  active profile, so "off" and "no profile" behave the same.
- When a profile becomes active, the status bar says so once ("Chrome profile: down twice for
  the next link · ✦ turns it off").

### On the phone

- `ScreenSession.sendScreen` adds `pkg` (it has `followPackage`).
- `InputService.controls()` walks the followed app's top window, like `overviewApps()`, with the
  same limits on depth and count, and the filtering above.
- `InputService.walk(dir, unit)`: Chrome's element walk on the web content of the followed
  window (or the fallback above), remembering the node it ended on for the next walk; it waits
  for the page to stop scrolling (its box steady for ~150 ms, at most 1 s) before answering.
- New `InputCommand.Controls` and `InputCommand.Walk` in `InputProtocol.kt`, with tests.
- Possibly a change to the accessibility service's config if Chrome needs it to build the full
  web tree (step 1 finds out). Any added event types must stay cheap: the service sees every
  app on the phone.

## Claude: the first map

Proposed from the user's description (2026-10-05) and checked the same day against a UI dump of
the app on the S25 (`adb shell uiautomator dump`, read-only; a Claude Code session screen, with
the box saying "Type / for commands"). What the dump showed:

- **No resource ids** (Jetpack Compose), so matchers go by label and position.
- The clickable nodes have no label of their own; the icon inside does (see `label` above).
- From the bottom row up, left to right: the **message box** (the only `EditText`); **+**
  ("Add context"); the **model picker** (no description: the texts "Opus 5.5 · High", which
  change with the model, so it's found by position: the button between + and the microphone);
  the **microphone** ("Start speech input"); **Send** ("Send", there even with the box empty).
  Top: **menu** ("Open menu…", top left) and, in a Code session, "Session menu" (top right).
- The "close" button the user remembered after the microphone is Send on this screen. While
  Claude is answering it may become Stop; to check on the device.
- Labels are the English ones; another phone language needs its own matchers.

| Button | Matcher |
| --- | --- |
| message box | `{ kind: 'field', pick: 'lowest' }` |
| + (attach) | `{ label: /^Add context$/ }` |
| model picker | `{ between: ['attach', 'mic'] }` |
| microphone | `{ label: /^Start speech input$/ }` |
| send / stop | `{ label: /^(Send\|Stop)/ }` |
| menu | `{ label: /^Open menu/ }` |

| On the view | Does |
| --- | --- |
| up / down | scroll the chat (generic) |
| down twice | highlight the **message box** (instead of the phone's Back, which stays on the bar) |
| up twice | *open question:* highlight the **menu** (☰, top left), pinch opens the chat list. It would make every scroll up in Claude wait 0.3 s |
| right twice, left twice | *open question:* generic (Type, app overview) unless the user wants them for something in Claude |

| Highlighted | up | down | left | right | pinch |
| --- | --- | --- | --- | --- | --- |
| message box | leave | **+** | | | tap it, then the **Type panel** (composer → Send text → Send) |
| + (attach) | message box | | | model picker | tap (opens Claude's menu) |
| model picker | message box | | + | microphone | tap |
| microphone | message box | | model picker | send / stop | tap |
| send / stop | message box | | microphone | | tap |

- If a button is missing (a different screen, or Claude swaps one), the map's lists ("the first
  of these on screen") skip to the next one without a second map.
- Once Claude's own menus are open (after + or the model picker), the highlight ends and they
  work like any other screen: scroll, tap. Profiles for menus can come later if they're worth it.

## Chrome: walking the page

Proposed from the user's description (2026-10-05): quick hops to the next thing, while plain
up and down keep scrolling. The page's tree checked the same day (see "Walking the page").

What the Reddit dump says about the hops:

- **`item` is fine-grained.** A Reddit post page has about 145 things to press: the header
  (Open menu, Home, search, Inbox), each post's Upvote / Downvote / comments / Repost / Share,
  ads, and in each comment the author's avatar, name and time as separate links. Down twice
  through all of them is slow, so the bigger hop (right/left twice) matters, and the walk skips
  a clickable that only wraps another one at the same spot (the avatar inside the profile link).
- **Chrome's own toolbar** (at the bottom of the screen on this phone) is native, with resource
  ids: `home_button`, `url_bar`, `tab_switcher_button`, `menu_button`. A map can reach it later
  (the open question below).

| On the view, and while highlighted | Does |
| --- | --- |
| up / down | scroll (generic, after 0.3 s now that up and down both have doubles); drops the highlight |
| down twice / up twice | the **next / previous item**: a link, button or field (`walk item`) |
| right twice / left twice | the **next / previous heading** (`walk heading`): bigger hops, from section to section or post to post |
| left / right | page sideways (generic) |
| pinch | press the highlighted thing; a text field also opens Type |

- Taken over from the generic set: down twice (Back: on the bar), right twice (Type: pinch a
  field, or the bar), left twice (the app overview: Back then a pinch).
- **Open question:** whether right and left twice should hop by heading, by article (a Reddit
  post is an `<article>`), or by landmark. Headings are the most common on the web; the unit is
  one word in `chrome.ts`, so it's cheap to try each on the device.
- **Open question:** whether one highlighted thing should also take a map, for Chrome's toolbar
  (the address bar and tabs, by resource id). Not in the first version.
- The same walk should work in other browsers that build their tree from the page the same way
  (Samsung Internet, Edge): their packages can join this profile once checked.

## Adding an app

1. Open the app on the phone and dump its UI tree (`adb shell uiautomator dump /sdcard/ui.xml`,
   `adb pull`). Note the resource ids and labels of the buttons you want.
2. Decide how its highlight moves: a map between named buttons (an app with a fixed layout, like
   Claude), walking (content in reading order, like a web page), or both.
3. Take a `controls` list in a session (or build one from the dump), remove any personal text,
   and save it as `client-web/src/apps/fixtures/<app>-<screen>.json`.
4. Write `client-web/src/apps/<app>.ts`: gestures, `whileHighlighted`, matchers (resource ids
   first, then labels, then structure) and moves if it has a map, notes. Register it in
   `index.ts`.
5. Test it against the fixtures. Every named button must be found, every move must land on a
   mapped button, and the ? panel must show the app's gestures.
6. Add the app's tables to this doc. Check it on the device, and when the app updates, take new
   fixtures.

## Security

Nothing here gives the glasses a new power over the phone. They could already tap anywhere on
the followed app; now they also learn where its buttons are, and can ask the phone to move its
accessibility focus within that app.

- The glasses still never name an app, a package or a URL to the phone. They only read the
  `pkg` the phone reports, tap points from its answers, and ask for walks by a fixed list of
  units.
- Neither `controls` nor `walked` ever carries what was typed in a text field, password fields,
  or anything outside the followed app's window. Labels are never logged or stored on either
  side.
- Everything at run time goes over the session's DataChannel, straight between the phone and the
  glasses: `pkg`, `controls`, `walk`, the taps. The relay (or the PC) only serves the page, and
  the profiles are part of the page's code, so it never sees which app is open or what's on
  screen.
- Both directions are parsed as strictly as the rest of the DataChannel protocol.
- The glasses store one more thing: per app package, a profile switched from its default
  (`glasses.appProfiles`). It holds no secret, but CLAUDE.md's list of what the client stores
  gets this added.

## Tests (planned)

- `apps/match.test.ts`: matchers, the order they're tried in, a button that's missing, an answer
  for another package.
- `apps/highlight.test.ts`: `map` and `view` behaviour, leaving the highlight, a field's pinch
  opening Type, an answer for another package dropped.
- `apps/claude.test.ts`: every named button found in each fixture, every move lands on a mapped
  button, the gestures merge with the generic ones (what Claude doesn't take stays generic).
- `apps/chrome.test.ts`: all four doubles walk, single up/down still scroll, the generic doubles
  it takes are gone from the ? panel.
- Every profile: Back can't be mapped (rule 1).
- `swipes.test.ts`: the merged map and `waitFor` with and without a profile.
- `shortcuts.test.ts`: the heading and the app's rows, with the overridden generic doubles left
  out.
- `apps/prefs.test.ts`: own profiles on by default and Walk off, a switch remembered per app,
  bad stored data read as the defaults. With a profile off, the gestures and ? rows are generic.
- `apps/walk.test.ts`: Walk's gestures; Chrome extends it (headings) and keeps the rest.
- `phoneProtocol.test.ts`: the `controls` and `walked` parsers (limits, clamping, bad items),
  `walk`, and `pkg`.
- `InputProtocolTest.kt`: `controls{}`, `walk{}` (units allowlisted). A small Robolectric-free
  test of the filtering, with fake nodes, if it can be pulled out from `AccessibilityNodeInfo`.
- On the device: the Claude map and the composer after the message box; in Chrome the hops on a
  few sites (Reddit, a news article, a search results page), scroll then hop, the end of a page;
  the app overview as before (cards, the row of apps, picking); Walk in a native app (Settings);
  Back from a highlight; ✦ off and on again (remembered after a Restart); the bar on one line.

## Plan

1. **Check both apps on the S25** (read-only; the phone must be on adb). Claude: done
   2026-10-05 (above); still to see what Send becomes while Claude answers, and a chat screen
   besides a Code session. Chrome: our service gets the whole web tree (done 2026-10-05); its
   element walk gets tried with the companion's `walk` (step 2), else the fallback. Fixtures come from the companion's own
   `controls` answers once step 2 is built, with personal text removed.
2. **Companion:** `pkg` in `screen`, `controls{}`, `walk{}` and their answers, parser tests,
   install.
3. **Glasses, shared parts:** `apps/` types, matcher, highlight state, registry and prefs;
   `swipes.ts` and `shortcuts.ts` take a profile; the highlight and its box in
   `PhoneScreen.tsx`; the bar with Apps first and focused, ✦, and the ✦/✧ mark in the status bar.
4. **Glasses, the profiles:** Walk and Chrome (extending Walk) first, then Claude, each with
   its tests and committed on its own.
5. **Docs:** this file to "built"; [phone-mode.md](phone-mode.md) (protocol, bar);
   [glasses-client.md](glasses-client.md) (Swipes: "an app's profile may take a double");
   CLAUDE.md (Glasses controls, repo map, what the client stores); the ? panel.
6. **On the device**, then ask the user before deploying the relay (the glasses page changed).

## Later

- Profiles for screens inside an app (Claude's chat list, its menus), chosen by which
  buttons are on screen; per-site tweaks in Chrome (a site that hops better by article).
