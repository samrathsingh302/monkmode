<!--
    Copyright (C) 2026 Samrath Singh

    This file is part of MonkMode, a fork of Cold Turkey.
    Source: https://github.com/samrathsingh302/monkmode

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
-->

# MonkMode — The Complete User Guide

MonkMode locks you out of distracting websites and apps on your Windows
computer, **for a length of time you choose, and it does not let you change
your mind halfway through.** That is the whole point: you set the block while
you are feeling strong, and it holds while you are feeling weak.

It has no windows or buttons — you control it by **typing short commands**
into a window on your computer. Every command you need is written out in this
guide, ready to copy. You do not need to know anything about computers to use
it; start at Section 1 and go in order.

**What it does, in one paragraph.** You tell MonkMode which websites and apps
to block and for how long — say, "block reddit.com for 2 hours". From that
moment, those websites will not load in any browser, those apps close
themselves the moment they open, and a countdown runs. When the time is up,
everything unblocks by itself. Before the time is up, there is no easy way
out — that is a feature, and Section 8 explains the three deliberate exits
that do exist.

**What it honestly cannot do.** MonkMode is *impulse-proof, not
unbreakable*. It defeats the casual "I'll just turn it off for a minute"
urge and quite determined fiddling too, but you own this computer: an
administrator with time, tools and determination can always win eventually —
booting from a USB stick and editing the disk is outside anything a program
can stop. What it *does* no longer have is a way out from inside: as of
30/08/2026 there is **no escape hatch and no self-serve wait**. A block ends
at its end time or with its one-time code, and nothing else. Hand that code
to someone else and the lock is one even you can't open (Section 8).

**Privacy:** MonkMode needs no internet, no account and no licence. It sends
nothing anywhere, ever. Everything it stores stays on your computer.

---

## Contents

1. [The one skill you need: the Administrator window](#1-the-one-skill-you-need-the-administrator-window)
2. [Install MonkMode](#2-install-monkmode)
3. [First-time setup (required, once)](#3-first-time-setup-required-once)
4. [Blocking things: the `block` command](#4-blocking-things-the-block-command)
5. [Running several blocks at once](#5-running-several-blocks-at-once)
6. [Blocking pages rather than whole sites: `--urls`](#6-blocking-pages-rather-than-whole-sites---urls)
7. [Blocks on a timetable: `schedule`](#7-blocks-on-a-timetable-schedule)
8. [How a block ends: the exits](#8-how-a-block-ends-the-exits)
9. [Checking in: `status`, `stats`, `version`, and what you'll see on screen](#9-checking-in-status-stats-version-and-what-youll-see-on-screen)
10. [Restarts, clock changes, and what survives](#10-restarts-clock-changes-and-what-survives)
11. [Troubleshooting — things that look broken but are not](#11-troubleshooting--things-that-look-broken-but-are-not)
12. [Uninstalling](#12-uninstalling)
13. [Reference: every command, every flag](#13-reference-every-command-every-flag)

---

## 1. The one skill you need: the Administrator window

Every MonkMode command is typed into an **Administrator PowerShell window**
(a window where you type commands, opened with extra permission). Here is how
to open one — this exact sequence, every time:

1. Click the **Start** button (the Windows logo, bottom-left of the screen).
2. Type: `powershell` (you'll see **Windows PowerShell** appear in the menu).
3. **Right-click** on **Windows PowerShell**.
4. Click **"Run as administrator"**.
5. Windows asks *"Do you want to allow this app to make changes to your
   device?"* — click **Yes**.

A blue (or black) window opens with a line ending in `>`. That is where you
type the commands in this guide. Type a command, press **Enter**, and read
what comes back.

> ### ⚠️ THE #1 MISTAKE — read this box before anything else
>
> If you open PowerShell **without** "Run as administrator" (a *normal*
> window), every MonkMode command will appear to **do nothing at all**:
> nothing prints, no error, it just returns instantly. It looks exactly like
> the program is broken. **It is not.** MonkMode needs administrator
> permission, so Windows relaunches it in a *new* window that closes the
> instant it finishes — your answer appeared and vanished in a window you
> never saw.
>
> **The fix is always the same:** close the window, and reopen PowerShell
> using the five steps above — the right-click and **"Run as administrator"**
> are the part people skip. If commands print things back at you, you are in
> the right kind of window.

---

## 2. Install MonkMode

You need the MonkMode folder (the source code you downloaded — for example
from GitHub, via **Code → Download ZIP**, then right-click the ZIP →
**Extract All**). Installation is one command. It copies the program to
`C:\Program Files\MonkMode` and makes the word `monkmode` work from any
Administrator window. **Installing does not block anything yet.**

In an Administrator PowerShell window, go into the MonkMode folder and run
the installer. If you extracted it to your Downloads folder, that looks like:

```
cd C:\Users\YOURNAME\Downloads\monkmode-main
powershell -ExecutionPolicy Bypass -File tools\install.ps1
```

(Replace `YOURNAME` with your Windows user name, and the folder name with
wherever you put it. `cd` means "go into this folder".)

The installer builds a **self-contained** copy — the .NET runtime is bundled,
so the computer it lands on needs nothing else installed. To *build*, the
machine needs the free
[.NET 10 SDK](https://dotnet.microsoft.com/download) once. When it finishes,
**close the window and open a fresh Administrator PowerShell window** (the
`monkmode` command is only known to windows opened after the install).

Check it worked:

```
monkmode version
```

You should see something like:

```
MonkMode 1.1.0 (850f1ef, built 30/08/2026 16:31) at C:\Program Files\MonkMode
```

That one line answers three questions at once: which release, which exact build
(`850f1ef` is the code revision it was built from), and **which copy of MonkMode
you are talking to**. The last one matters more than it sounds: a developer
machine can have a second copy in a `dist\` folder, each with its own settings,
and without the folder name it is easy to configure one and wonder why the other
never noticed. `monkmode status` prints the same line first, for the same
reason.

Two rules about installing:

- **The installer refuses to run while the MonkMode service exists** (that
  is, after you have armed your first block, until you remove the service —
  Section 12). Never upgrade the program across a running block.
- **Re-installing keeps your data.** The installer copies program files only;
  your setup, history and any snapshots already on the machine are left
  alone.

---

## 3. First-time setup (required, once)

Before your first block, MonkMode makes you run `setup` once. This is
deliberate: setup explains how blocks end **before** you are inside one. If
you skip it, `block` refuses and tells you to run it.

The simplest form:

```
monkmode setup
```

The most useful form names your **accountability partner** — a person you
trust (friend, spouse, anyone). Each block you start prints a **one-time
unlock code**; you hand that code to your partner, and only someone holding
it can end the block early without a long wait:

```
monkmode setup --partner "Alex (my sister)"
```

The partner text is just a reminder label shown to you — MonkMode never
contacts anyone. Handing the code over is something *you* do (text it, say
it out loud — and then don't keep a copy).

Setup can also store **defaults** so future commands are shorter. All of
these are optional:

| Option | What it does | Example |
|---|---|---|
| `--partner "..."` | Names who to hand each block's code to. | `--partner "Alex (my sister)"` |
| `--cooloff 2h` | **Does nothing since 30/08/2026.** Still accepted so old commands don't break; there is no cooling-off wait to set. | — |
| `--default-sites a.com,b.com` | Sites blocked when you start a block without naming any. | `--default-sites reddit.com,x.com` |
| `--default-preset social` | Folds a ready-made category (Section 4.3) into those default sites. | `--default-preset social,video` |
| `--default-apps a.exe,b.exe` | Apps closed when you start a block without naming any. | `--default-apps steam.exe` |
| `--default-app-preset games` | Folds a ready-made app category into those default apps. | `--default-app-preset games,chat` |

A complete example — defaults for a social-media-and-games household:

```
monkmode setup --partner "Alex (my sister)" --default-preset social --default-app-preset games
```

Things worth knowing about setup:

- **Safe to re-run any time.** It never touches a running block.
- **Each run replaces the defaults.** If you re-run setup and want to keep
  your defaults, type them again — a run that omits them clears them.
- A mistyped category name makes setup fail cleanly before saving anything.

---

## 4. Blocking things: the `block` command

### 4.1 Your first block

```
monkmode block --sites reddit.com --for 2h
```

Press Enter, and MonkMode prints something like this:

```
Block #1 is now active until 26/08/2026 16:04 (2h).
  Sites: reddit.com
Close and reopen your browser to see the block. It cannot be removed until the timer ends.

Emergency unlock code for block 1 (give it to your accountability partner NOW - it will NOT be shown again):
    XXXX-XXXX-XXXX
Send it to your accountability partner NOW: Alex (my sister)
To end block 1 early, they run:  monkmode unblock --code <CODE>
```

Three things just happened:

1. **reddit.com stopped working**, in every browser, for the whole computer
   (close and reopen the browser to see it take effect).
2. A **countdown started**. When it reaches zero the site comes back on its
   own, within about ten seconds — you do nothing.
3. A **one-time code was shown, once.** Send it to your partner now, then
   let it leave your screen. It will never be shown again, and it is not
   stored anywhere readable — if you keep it for yourself, you have simply
   made your own lock pickable.

That is the whole product. Everything below is variations.

### 4.2 What to block: sites, apps, files

You can mix and match any of these in one command:

| Flag | What it blocks | Example |
|---|---|---|
| `--sites a.com,b.com` | Websites (separate several with commas). Blocking `snapchat.com` also blocks its `www.`, `m.`, `web.` and `mobile.` versions automatically. | `--sites reddit.com,x.com` |
| `--apps name.exe` | Programs — they are closed within seconds whenever they start. Use the program's file name. | `--apps steam.exe,discord.exe` |
| `--file list.txt` | Reads website names from a text file, one per line (lines starting `#` are ignored). | `--file mylist.txt` |
| `--preset <category>` | A ready-made bundle of well-known sites — see 4.3. | `--preset social` |
| `--app-preset <category>` | A ready-made bundle of well-known apps — see 4.3. | `--app-preset games` |
| `--urls "..."` | Parts of a site rather than all of it — see Section 6. | `--urls "youtube.com/shorts"` |

A block needs **at least one thing to block** and **one length of time**. If
you name no sites and no apps at all, your setup defaults (Section 3) fill
in; with no defaults either, MonkMode refuses rather than arm an empty
block.

Worked examples:

```
monkmode block --sites facebook.com,instagram.com --for 3h
monkmode block --apps steam.exe --for 1d
monkmode block --preset social,video --apps discord.exe --for 90m
monkmode block --file exam-season.txt --until "2026-09-01 09:00"
```

### 4.3 Presets — ready-made categories

Instead of typing site lists, name a category. These are built in:

**Site categories** (`--preset`):

| Name | What's in it |
|---|---|
| `social` | facebook.com, instagram.com, twitter.com, x.com, tiktok.com, reddit.com, snapchat.com, tumblr.com, pinterest.com, linkedin.com, threads.net |
| `video` | youtube.com, netflix.com, twitch.tv, hulu.com, disneyplus.com, primevideo.com |
| `news` | cnn.com, nytimes.com, foxnews.com, bbc.com, buzzfeed.com, theverge.com |
| `shopping` | amazon.com, ebay.com, etsy.com, aliexpress.com, walmart.com, target.com |
| `adult` | six well-known adult sites |

**App categories** (`--app-preset`):

| Name | What's in it |
|---|---|
| `games` | steam.exe, epicgameslauncher.exe, battle.net.exe, riotclientservices.exe, leagueclient.exe, valorant.exe, robloxplayerbeta.exe |
| `chat` | discord.exe, telegram.exe, whatsapp.exe, signal.exe, slack.exe |

Combine categories with commas (`--preset social,video`) and freely mix them
with your own `--sites`/`--apps`. A category name you mistype makes the
whole command refuse, listing the valid names — it never quietly blocks
less than you asked for.

### 4.4 How long: `--for` and `--until`

Every block needs exactly one of these:

| Flag | Meaning | Examples |
|---|---|---|
| `--for <length>` | Block for this long, starting now. | `--for 45` (45 minutes) · `--for 90m` · `--for 2h` · `--for 1d12h` |
| `--until "<date and time>"` | Block until this moment. | `--until "2026-06-11 18:00"` |

The length grammar: `d` = days, `h` = hours, `m` = minutes, in any
combination; a bare number means minutes. A block must end **more than one
minute in the future** — `--for 1` is refused (it lands exactly on the
one-minute line), so the shortest block is `--for 2`.

**Once a block starts it can never be shortened.** Not by you, not by
another command, not by changing the clock. Adding *more* sites is allowed
(Section 4.6); taking anything away is not.

### 4.5 Starting later: `--start`

```
monkmode block --preset social --start "2026-08-27 07:00" --for 8h
monkmode block --sites reddit.com --start +90m --for 2h
```

`--start` arms a block that begins in the future — at a set time, or after a
delay (`+90m` = in ninety minutes). `--for` measures from the **start**, so
the second example blocks for two full hours beginning ninety minutes from
now. A start can be at most 30 days ahead; a start time already in the past
just means "now".

Two honest details about the waiting period:

- **The sites are blocked from the moment you arm**, not from the start
  time — a waiting block blocks too much rather than too little, on purpose.
- **A waiting block cannot be cancelled.** The on-screen help used to claim
  `unblock --cancel` worked "freely until it starts"; it never did, and both
  the flag and the claim were removed on 30/08/2026. A delayed block starts
  on schedule and then ends like any other — its end time, or its code.
  Treat `--start` as seriously as `block` itself.

### 4.6 Growing a running block: `add`

```
monkmode add --sites x.com,y.com
```

Adds sites to a block that is already running (it takes effect within about
ten seconds). A block can only ever **grow** — there is no command to remove
a site from a running block, on purpose. With more than one block running,
say which one: `monkmode add --sites x.com --id 2` (Section 5).

### 4.7 The strictness dial: `--all-session-kill`

| Flag | What it does |
|---|---|
| `--all-session-kill` | If several people (or accounts) are logged into this computer, blocked apps are closed in **every** login session, not just yours — so switching to a second Windows account doesn't dodge the app block. Does nothing unless the block includes apps. |
| `--commit` | **Does nothing since 30/08/2026** — every block is committed now, so there is nothing left to opt into. Still accepted so old commands don't break. |
| `--cooloff 4h` | **Does nothing since 30/08/2026** — there is no cooling-off wait to lengthen. Still accepted so old commands don't break. |

```
monkmode block --preset social --for 8h
monkmode block --app-preset games --for 2h --all-session-kill
```

Note: `--commit` and `--all-session-kill` are on/off switches — write them
bare, exactly as shown. If you write `--commit=yes`, the flag is **ignored**
(MonkMode warns you and continues without it). Any flag it doesn't
recognise — a typo like `--site` for `--sites` — also gets a warning and is
ignored rather than stopping the block.

---

## 5. Running several blocks at once

Starting a block **never** replaces the ones already running — it starts a
**new** one beside them, up to **eight at a time**. Each block is fully
independent: its own timer, its own sites and apps, its own one-time code.

```
monkmode block --preset social --for 8h          → Block #1
monkmode block --apps steam.exe --for 2h         → Block #2
monkmode block --sites news.ycombinator.com --for 30m   → Block #3
```

Each expires on its own timer and the others carry on untouched. See them
all:

```
monkmode status
```

```
MonkMode 1.1.0 (850f1ef, built 30/08/2026 16:31) at C:\Program Files\MonkMode
MonkMode: 3 blocks active
 Id  State     Ends / Starts             Sites Apps URLs  Exit
  1  ACTIVE    2026-08-26 22:04             11    0    0  code  (~5h 12m of active time left)
     Exit:  ends at its end time, or earlier with the partner code (shown once at block start): 'monkmode unblock --id 1 --code <CODE>'. There is no other way out.
  2  ACTIVE    2026-08-26 16:04              0    1    0  code  (~1h 42m of active time left)
     ...
  Note: the end time counts machine-ON time; time spent off or asleep is credited back once the service can confirm the real time online (otherwise it pushes the end later).
```

The **Ends** column is a wall-clock stamp, but a block's timer only runs while
the computer is on. So the trailing
`(~5h 12m of active time left)` is the number that decides when it lifts.

Time spent shut down or asleep is **credited back** on the first check after the
machine returns — but only once MonkMode can confirm what the real time is,
which it does by asking several websites what time they think it is and
requiring at least two of them to agree. So: shut down at midnight with two
hours left on a block, boot at ten in the morning, and within a couple of
minutes of the network coming up the block is over. If there is no internet (or
the answers disagree) nothing is credited and the downtime pushes the end later
instead, until a connection settles it.

That caution is the whole point. Simply believing your computer's own clock
about how long it was off would hand you a one-line bypass — set the clock
forward, turn the machine off and on, and the block is gone. MonkMode never
takes an unverified clock's word for it, so the worst case is that a block lasts
*longer* than you expected, never shorter.

When more than one block is running, commands that act on *one* block need
`--id <number>` (the Id column):

- `monkmode add --sites x.com --id 2` — grow block 2.
- `monkmode unblock --code <CODE>` — no `--id` needed: the code is offered to
  every running block and can only ever match the one that minted it.
- With exactly **one** block running you can leave `--id` off an `add`.
- MonkMode **refuses to guess**: an `add` without `--id` while several blocks
  run is refused with the list of ids — never aimed at a block you didn't name.

The unlock **code** needs no id — each code belongs to exactly one block and
can only ever open that one. Ids are never reused; when block 3 ends, the
next block is #4. When all eight slots are busy, a ninth `block` refuses and
lists what is running.

Only when the **last** block ends does MonkMode stand down (sites restored,
service idle). One more rule: a manual block and a **schedule** (Section 7)
cannot run at the same time, in either direction.

---

## 6. Blocking pages rather than whole sites: `--urls`

Sometimes you don't want to block all of YouTube — just Shorts. `--urls`
attaches **page patterns** to a block:

```
monkmode block --urls "youtube.com/shorts" --for 4h
monkmode block --urls "youtube.com/shorts,instagram.com/reels" --for 2h
monkmode block --sites reddit.com --urls "youtube.com/shorts" --for 3h
```

While the block runs, MonkMode watches the address bar of the browser window
you are looking at (Chrome, Edge and Brave), and if the page you are on
matches a pattern, it steers the browser back to that site's front page
(YouTube goes to your Subscriptions feed). At most one nudge every five
seconds.

> ### ⚠️ HOW PATTERNS MATCH — this catches everyone out
>
> A pattern is a **piece of text that must appear in the web address.
> Nothing more.** There are **no wildcards**: a `*` is treated as a literal
> star character, and since real web addresses never contain a star, **a
> pattern with `*` in it matches nothing, silently — the block arms and the
> nudge simply never fires.**
>
> - ✅ `youtube.com/shorts` — right. Catches every Shorts page.
> - ❌ `*/shorts*` — WRONG. Never matches anything, no error is shown.
> - ✅ `reddit.com/r/all` — right. Catches that page and everything under it.
> - ❌ `/shorts` (no site name) — accepted but useless: write the site name in.
>
> One deliberate special form: a pattern ending in `/` with nothing after it
> means **only the front page**. `youtube.com/` blocks just the YouTube home
> feed (where the recommendations are) while leaving the rest of YouTube
> alone; `youtube.com` (no slash) covers the entire site.

Capital letters don't matter, `www.` is ignored, and the mobile site
(`m.youtube.com`) is caught by the same patterns. Up to 32 patterns per
block, each up to 200 characters, separated by commas.

**Be clear about what this is: a nudge, not a wall.** Site blocking
(`--sites`) rewires the whole computer and cannot be dodged by another
browser; `--urls` only watches the front window of the three browsers named
above, reading what the address bar says. It is enforcement's little
sibling — very effective against habit-scrolling, not against determination.
Also, deliberately: a block that names **only** `--urls` (no sites, no apps)
does **not** pull in your setup defaults — "block Shorts only" means only
Shorts.

---

## 7. Blocks on a timetable: `schedule`

A **schedule** opens and closes blocks automatically on a weekly rhythm —
"every weekday, nine to five", with no command to type each morning. While a
window is open it blocks at full strength, and **an open window cannot be
ended early at all** — not even with a code. It closes at its end time.

```
monkmode schedule --sites reddit.com,x.com --windows "Mon-Fri 09:00-17:00"
monkmode schedule --sites youtube.com --apps steam.exe --windows "Mon-Fri 09:00-17:00; Sat,Sun 10:00-14:00"
```

The `--windows` grammar, in plain words:

- Days: `Mon` `Tue` `Wed` `Thu` `Fri` `Sat` `Sun` — a single day (`Tue`), a
  range (`Mon-Fri`), or a list (`Sat,Sun`).
- Times: 24-hour `start-end`, like `09:00-17:00`.
- Several windows: separate with `;` inside the quotes.
- **Overnight windows:** an end time *before* the start means "through the
  night" — `Mon-Fri 22:30-04:00` runs from 22:30 each weekday evening to
  04:00 the next morning. A computer restarted at 02:00 inside such a window
  comes back still blocked.

The other schedule commands:

```
monkmode schedule --show       # print the armed schedule (changes nothing)
monkmode schedule --clear      # stop future windows (an open window still runs to its end)
monkmode schedule --validate --sites a.com --windows "Mon-Fri 09:00-17:00"
                               # check your grammar without arming anything
```

Rules:

- **One schedule at a time**, and a schedule and a manual block **cannot
  coexist** — `schedule` refuses while any block is armed, and `block`
  refuses while a schedule is armed. Clear the schedule first
  (`schedule --clear`), then start the block.
- Re-running `monkmode schedule ...` with new sites/windows **replaces** the
  schedule (when no window is currently open).
- To change an armed schedule's sites, re-run the full command with the
  complete list — `add` does not work on schedules.

---

## 8. How a block ends: the exits

There are exactly **two**, and there is deliberately no third. Know them
before you start your first block.

> **Changed on 30/08/2026.** MonkMode used to have four exits. Two of them — a
> self-serve **cooling-off** wait (`monkmode unblock` counted down about an hour
> of machine-on time and then lifted the block for you) and an **emergency
> escape hatch** (`monkmode unblock --force`, which tore everything down at
> once) — were removed on purpose. They are not hidden, not behind a flag and
> not behind an environment variable: the code is gone. `--force` and `--cancel`
> are now reported as commands that do not exist.

### 8.1 The timer (do nothing)

Every block ends by itself at its end time. The sites come back within about
ten seconds; you don't run anything. This is the intended exit.

### 8.2 The partner code (immediate, needs another person)

```
monkmode unblock --code XXXX-XXXX-XXXX
```

The one-time code shown when the block started (Section 4.1) ends **that
block** within about ten seconds. This is why you hand the code to a
partner: the exit exists, but not in your own pocket. A wrong code changes
nothing (and MonkMode deliberately doesn't say whether a code was right —
watch whether the block lifts). Each code opens only the block that minted
it; codes are never shown twice and never stored readably.

### 8.3 What happens if you just type `unblock`

```
monkmode unblock
```

It refuses, and says so:

```
A running block ends only at its end time or with the partner code. Run:  monkmode unblock --code <CODE>
If the code is lost, you wait. There is no cooling-off wait, no escape hatch and no recovery - that is the point.
```

Nothing is started, nothing is queued, and the block is untouched.

### 8.4 If you lose the code

**You wait.** That is the whole answer, and it is the design rather than a gap
in it: an exit that exists is an exit that gets used at 2am. There is no
recovery command, no override, no reset, no support channel and no admin
bypass. The block runs to its end time and lets itself go.

Two practical consequences worth taking seriously *before* you arm anything:

- **Choose durations you actually mean.** A 30-day block with a lost code is a
  30-day block.
- **Hand the code to your partner the moment it appears**, and don't keep the
  only copy somewhere you might lose it in a moment of frustration.

There is one sharper edge, stated plainly rather than buried. If MonkMode's
stored settings are ever damaged — by editing them by hand, by disk corruption,
or by arming a block and then upgrading the program underneath it — MonkMode
freezes: it keeps blocking and refuses to lift, **including for the code**,
because it will not trust a file that failed its own integrity check. A frozen
block holds past its end time, indefinitely. Avoid it by never editing
MonkMode's files and never upgrading while a block is running (Section 11).

---

## 9. Checking in: `status`, `stats`, `version`, and what you'll see on screen

### `monkmode status`

The live picture: one row per running block (id, when it ends, how many
sites/apps/URL-patterns it covers) and, under each row, exactly how that
block can end. Also shown when relevant: what MonkMode has stopped today, a
an armed schedule and whether a window
is open right now. Between blocks it says
`no active block (service installed but idle)` — that is the normal resting
state, not a problem.

### `monkmode stats`

Your history, read-only: blocks started and completed, total planned focus
time, longest block, plus the measured actuals — real hours blocked, apps
closed, browser nudges, and your **focus-day streak**. Nothing in `stats`
records *which* sites or apps — counts only. The history survives blocks
ending and uninstalls.

### `monkmode version`

One line: which release, which exact build (the code revision it was compiled
from and when), and which folder this copy lives in. (Don't trust right-click →
Properties on the exe — it shows an inherited version number from the original
upstream project.) `monkmode status` prints the same line first.

### The things MonkMode itself puts on screen

- **The blocked-page screen.** While a block runs, blocked `http://` sites
  show a local "**Locked in.**" page listing what is blocked and until when.
  Most modern sites are `https://`, and those simply **fail to load** with
  the browser's own error page instead — the block is working either way;
  the browser just refuses to show a substituted page over a secure address.
- **The tray icon.** A small icon by the clock while blocks run. Hover it
  for today's numbers; right-click for the same summary.
- **Toasts.** Brief corner notifications — when a blocked app is closed,
  and when a block expires.

None of these are the enforcement — closing the tray icon does not weaken
the block (it re-launches itself anyway; the real enforcement is a Windows
service plus a watchdog that guard each other).

---

## 10. Restarts, clock changes, and what survives

**Restarting the computer does not end a block.** The blocking service
starts itself at boot, before you log in; the sites are blocked even in the
seconds before it starts. This is tested, not hoped — restart mid-block and
you come back still blocked, with the timer having credited only real
elapsed time.

**Changing the clock does not end a block.** The timer runs on the
machine's internal elapsed-time counter, not the wall clock. Setting the
clock forward doesn't bring the end closer; setting it back doesn't
shorten anything either (the display may look odd; the enforcement doesn't
care). Time the machine spends switched off is handled conservatively — a
block never expires *while* the machine is off because of clock tricks.

**Killing MonkMode's processes does not end a block.** The service and its
watchdog restart each other; the service cannot be stopped through the
normal Windows controls while a block runs (`sc delete` is refused — by
design); the blocked-sites list self-heals within about ten seconds if
edited. If tampering ever manages to corrupt MonkMode's files, it **freezes
fail-closed**: the block keeps enforcing and won't lift by itself — and since
30/08/2026 there is no way out of that state at all (Section 8.4). Don't
edit MonkMode's files, and don't upgrade across a running block.

What survives what:

| Event | Block running? | Your setup + history |
|---|---|---|
| Restart / shutdown | ✅ still blocked | ✅ kept |
| Block expires normally | block ends; others continue | ✅ kept |
| `unblock --force` | *(removed 30/08/2026 — the command no longer exists)* | — |
| Uninstall (Section 12) | refuses while a block runs | ✅ kept (unless you ask) |

---

## 11. Troubleshooting — things that look broken but are not

**These four states are normal.** They are the ones everyone mistakes for
breakage (the program's own `monkmode help` prints the same list):

1. **A command printed nothing and returned instantly.** You are in a
   normal (non-administrator) window — the answer flashed by in a window
   that closed itself. Section 1's box. Nothing is broken.
2. **`status` says "no active block (service installed but idle)".** Normal
   between blocks. The service stays registered after a block ends; it is
   blocking nothing.
3. **`status` notes "the MonkMode service isn't running at the moment".**
   The blocked sites stay blocked regardless (the block lives in a system
   file, not in the running service), and the service starts itself again.
   App-closing and the countdown pause briefly; nothing unblocks.
4. **"the stored configuration failed its integrity check"** means someone
   or something edited MonkMode's protected files. MonkMode is now
   **FROZEN**: it keeps blocking and will not lift by itself — deliberately,
   because the alternative is that any tampering unlocks it. Since 30/08/2026
   there is no command that ends this state: not the timer, and not the code
   either (Section 8.4). It is the one genuinely unrecoverable corner.

And a few more honest answers:

- **"Another program is warning it can't update the hosts file."** Some
  networking tools (Tailscale is a known example) routinely rewrite the
  system's hosts file and will complain during a block that they can't.
  That warning **is the lock working** — MonkMode holds that file locked so
  nothing can quietly unblock your sites. The warning is harmless and stops
  when the block ends.
- **A site still loads right after arming.** Close and reopen the browser —
  browsers cache addresses. Still loading? It may be reached via a different
  domain; `monkmode add --sites <that-domain>` grows the block.
- **A site still looks blocked right after the block ended.** The reverse of
  the same cache. Reopen the browser; the system file is already clean.
- **`--urls` never nudges.** Almost always a `*` in the pattern — see
  Section 6's box. Patterns are plain text matched inside the address, and
  stars match nothing.
- **`--for 1` is refused.** By design; the shortest block is `--for 2`.

If something is genuinely stuck, there is no rescue command any more
(Section 8.4). A healthy block still ends at its end time on its own, and its
code still lifts it early — those are the only two things that end a block.

---

## 12. Uninstalling

While a block is running, uninstalling is refused — the exits in Section 8
are the way out of a block. Once nothing is blocking:

```
cd <your monkmode source folder>
powershell -ExecutionPolicy Bypass -File tools\uninstall.ps1
```

The uninstaller double-checks that nothing is enforcing (it refuses rather
than fight a live block), then removes the idle service, the program folder
and the PATH entry. **Your data is kept** — setup, history, and the saved
copy of your browser's DNS setting — so a reinstall picks up where you left
off. For a truly clean slate add `-PurgeData`.

If you never ran the installer (you ran straight from a build folder), there
is nothing in Program Files: once idle, `sc delete MONKMODE` in an
Administrator window removes the service, and you can delete the folder.

---

## 13. Reference: every command, every flag

Run `monkmode help` any time for the always-current version of this.

### Commands

| Command | What it does |
|---|---|
| `monkmode setup [options]` | Required once before the first block. Records the partner label and your defaults. Re-run any time (re-state defaults you want to keep). |
| `monkmode block [what] [when] [dials]` | Starts a **new** block beside any already running (max 8). |
| `monkmode status` | One row per running block + its exit; schedule state; today's counts. Read-only. |
| `monkmode stats` | History and streaks. Read-only, counts only. |
| `monkmode add --sites a.com[,b.com] [--id N]` | Adds sites to a running block (within ~10 s). Growth only. |
| `monkmode schedule --sites ... [--apps ...] --windows "..."` | Arms the weekly timetable (replaces any previous one; refuses beside a manual block). |
| `monkmode schedule --show` / `--validate ...` / `--clear` | Inspect / dry-run / stop future windows. |
| `monkmode unblock --code <CODE>` | Submits the partner code; a correct one lifts its block in ~10 s. **The only early exit.** |
| `monkmode unblock` (bare) | Refused. It starts nothing. |
| `monkmode version` | One line: release, build revision + date, install folder. |
| `monkmode help` | Usage, live preset names, and the troubleshooting list. |

### `block` flags

| Flag | Takes | Notes |
|---|---|---|
| `--sites` | `a.com,b.com` | Mirrors (`www.`/`m.`/`web.`/`mobile.`) covered automatically. |
| `--preset` | `social,video,news,shopping,adult` | Fail-closed on typos. |
| `--apps` | `name.exe,other.exe` | Closed on sight while the block runs. |
| `--app-preset` | `games,chat` | Fail-closed on typos. |
| `--file` | `list.txt` | One site per line; `#` comments fine. |
| `--urls` | `"youtube.com/shorts,..."` | **Substring match, no wildcards** (Section 6). Max 32 patterns × 200 chars; no `\|` or `;` inside a pattern. |
| `--for` | `45` / `90m` / `2h` / `1d12h` | Bare number = minutes. Must exceed 1 minute. |
| `--until` | `"2026-06-11 18:00"` | Alternative to `--for`. |
| `--start` | `+90m` / `2h` / `"2026-08-10 07:00"` | Max 30 days ahead. Sites block immediately; **cannot be cancelled**. |
| `--commit` | *(bare)* | Accepted, does nothing (every block is committed since 30/08/2026). |
| `--cooloff` | `2h` | Accepted, does nothing (there is no cooling-off wait since 30/08/2026). |
| `--all-session-kill` | *(bare)* | App-closing in every logged-in Windows session. |

### Exit codes (for scripting)

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Usage error: bad/missing argument, nothing to block, unknown preset, an ambiguous `add` with several blocks running, or a bare `unblock` (which is always refused). |
| 2 | Not elevated, DPAPI unavailable, or an internal error. |
| 3 | Schedule/block conflict, or all 8 slots in use. |
| 4 | Setup has not been run yet. |

### Where things live

Everything MonkMode stores sits beside the program (`monkmode version`
prints the folder), plus daily counters in `C:\ProgramData\MonkMode`. The
blocked sites live between the two marker lines `#### MonkMode Entries ####`
and `#### MonkMode End ####` in the Windows hosts file — MonkMode only ever
touches its own marked region, never your own entries. No internet, no
account, nothing sent anywhere, nothing expires.

### See also

- `README.md` — what this project is, the exit model, and the honest ceiling.
- `docs/RUNBOOK.md` — the operator's manual: diagnosis, forced removal, and
  the known limitations of this release.
- `monkmode help` — always current for the build you are on.
