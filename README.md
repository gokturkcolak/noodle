# Noodle

Stretching while Claude is noodling. **[tonitherigatoni.com](https://tonitherigatoni.com)**

![Toni the Rigatoni sliding in with a stretch](docs/demo.gif)
<!-- GIF placeholder: record a card popping up and drop it at docs/demo.gif -->

While Claude Code is busy on a long prompt, a card slides in from the edge of your screen with a quick desk stretch and a little noodle sticker to show you how. Hit Start and your noodle times the set. Pick your pasta: Toni the Rigatoni, Sammy the Spaghetti, Patty the Penne, or Lenny the Lasagna. Quick prompts never trigger a card, and it never steals focus from your terminal or editor.

Works everywhere Claude Code runs hooks: the CLI, the VS Code and Cursor extensions, and the Code tab in Claude Desktop. macOS, plus [Windows](#windows-beta) in beta.

## Install

The easy way, no Terminal: download [Noodle.zip](https://github.com/pcybriwsky/noodle/releases/latest/download/Noodle.zip), unzip it, drag Noodle into Applications, and open it. It's signed with a Developer ID and notarized by Apple, so it opens like any other Mac app. Noodle then opens on a **Connect to Claude Code** step that shows the exact change to `~/.claude/settings.json`. Click Connect and you're set.

Or from Terminal:

```bash
curl -fsSL https://raw.githubusercontent.com/pcybriwsky/noodle/main/scripts/get.sh | bash
```

Or with Homebrew:

```bash
brew install --cask pcybriwsky/tap/noodle
noodle setup
```

Either way you get the latest release in `~/Applications` (Homebrew uses `/Applications`), and `noodle setup` adds the same two hooks from Terminal.

However you connect, you see the exact diff and get asked before anything is written, and your old file gets backed up to `settings.json.bak-<timestamp>`. Only Noodle's two hooks are added, everything else in the file (other hooks, key order) stays as it is. Connecting again is safe, it won't add the hooks twice. Claude Code sessions that were already open may need a restart to pick them up.

Both ways also offer to open the app at login (you can flip that later in Settings). First launch, your noodle says hi and walks you through a quick desk setup check.

`noodle setup` needs `jq` (built into macOS 15 and later, `brew install jq` before that); the in-app Connect step doesn't.

### From source

```bash
scripts/install.sh
```

Builds `Noodle.app` and does the same setup. Needs Xcode command line tools (`swift`) and `jq`.

### Windows (beta)

Download **NoodleSetup.exe** from the [latest release](https://github.com/pcybriwsky/noodle/releases/latest) and run it. It installs just for you (no admin prompt) into `%LOCALAPPDATA%\Programs\Noodle`. The build isn't code-signed yet, so SmartScreen asks once: **More info › Run anyway**. Needs Windows 10 1809 or later with the Edge WebView2 Runtime (built into Windows 11). Prefer no installer? `Noodle-Windows.zip` runs from any folder you keep it in.

Noodle opens on the same **Connect to Claude Code** step, with the same diff, backup, and confirm before anything is written to `%USERPROFILE%\.claude\settings.json`. The hooks run `"…/Noodle.exe" --event prompt` and `--event stop`, which hand off to the running copy and exit right away. From a terminal, `noodle setup` and `noodle remove` work like on the Mac (tick "Add the noodle command to PATH" in the installer).

It's the same cards, cast, and settings, from the tray icon instead of the menu bar (left-click opens Noodle, right-click for the menu). "Hold during calls" reads which apps are using your mic or camera, the same record behind Windows' privacy icons. Holding for calendar events is Mac-only for now. Uninstalling from Settings › Apps takes the hooks back out.

From source: `dotnet build windows/Noodle` (needs the .NET 8 SDK), then run `windows/Noodle/bin/Debug/net8.0-windows/Noodle.exe`. `windows/test/hooks-test.ps1` tests the settings.json changes against temp files. Set `CLAUDE_SETTINGS` and `CLAUDE_POSTURE_HOME` to try it on copies, and `NOODLE_DEBUG=1` to log to `%LOCALAPPDATA%\Noodle\debug.log`.

## How it works

- You send a prompt. If Claude is still working 15 seconds later, a card slides in from the corner, like a notification. Want more of a show? Settings > Entrance > Drop in on a noodle, and your noodle rides a strand of spaghetti down from the menu bar first.
- At most one card every 20 minutes.
- No cards while you're on a call. If any app is using your mic or camera, Toni waits. You can also have him skip busy calendar events.
- Working late? No quiet hours by default, so you still get nudged.
- **Start** runs the timer while your noodle demos the move. When time's up (or you hit Done early) it logs the set, celebrates a little, and gets out of your way. **Snooze** pushes the next one back 10 minutes.
- Ignoring it is fine too. After 20 seconds your noodle peeks over the top of the card once, and after a minute the card drifts off on its own. It stays put while your pointer is on it.
- **How to** opens beginner steps for the move, plus the most common mistake to avoid.
- If Claude finishes while a card is up, it says so, and you can finish your set.
- The menu bar icon shows today's count and has Pause, Show one now, Say hi to Toni, and Settings.
- **Open Noodle** (top of the menu, or open Noodle.app again) brings up the Noodle window: the whole cast idling on a little stage. Hover one to get a wave, click one to make it yours, or try a stretch from there.
- Not connected to Claude Code yet? Noodle opens its window on the Connect step at launch, and the menu and the Noodle window both have **Connect to Claude Code**. Click **Not now** and it stops asking at launch.

### No Claude? Use the timer

Noodle doesn't need Claude Code. Pick **Settings › Remind me › On a timer** (or **Remind me on a timer instead** on the Connect step) and your noodle drops by every 20 minutes while you're at your Mac, Claude or not. The interval is the same **Every** setting, so 10, 30, 45, or 60 minutes work too.

- Five minutes away from the keyboard counts as a break, so the clock starts over when you're back instead of greeting you with a card.
- Pause, Snooze, quiet hours, and holding during calls or meetings all work the same as with Claude.
- The menu and the Noodle window show when the next one is due.
- Switch back with **Settings › Remind me › While Claude works**. If the hooks aren't in yet, that opens the Connect step.
- With Reduce motion on, cards fade in and out instead of sliding, and your noodle skips the peek.

## Settings

Everything lives under **Settings** in the menu bar: when to remind you (while Claude works, or on a timer), focus, character, entrance, how long before a card shows, how often, snooze length, corner, which exercises, holding during calls or calendar events, quiet hours, always show steps, open at login, and Connect to / Disconnect from Claude Code.

**Focus** picks the rotation: *Tech neck* leans on chin tucks, neck, and upper back. *Tight hips* (anterior pelvic tilt) leans on hip flexors and legs. *General stiffness* is all eight. The intro asks, and you can switch anytime.

Under the hood it's `~/.claude-posture/config.json` (Settings > Edit config file…). Changes apply on the next card, no restart needed.

| key | default | what it does |
|---|---|---|
| `mode` | `claude` | `claude` shows cards while Claude works (needs the hooks), `timer` shows one every `cooldownMinutes` |
| `delaySeconds` | `15` | how long a prompt has to run before a card shows (Claude mode) |
| `cooldownMinutes` | `20` | minimum gap between cards, or the interval in timer mode |
| `snoozeMinutes` | `10` | extra wait after Snooze |
| `position` | `top-right` | `top-right`, `top-left`, `bottom-right`, `bottom-left` |
| `inset` | `16` | distance from the screen edge |
| `quietHours` | off, `22:00` to `07:00` | no cards in this window when `enabled` is true |
| `enabledExercises` | all | ids from `exercises.json`, in the order you want them |
| `character` | `rigatoni` | `rigatoni` (Toni), `spaghetti` (Sammy), `penne` (Patty), `lasagna` (Lenny), or `sprout` |
| `entrance` | `card` | `card` slides in from the screen edge, `noodle` drops in from the menu bar first (top corners, skipped with Reduce motion) |
| `expandSteps` | `false` | open How to on every card, handy while the moves are new |
| `announce` | `false` | each card opens with "It's time to stretch!" and a wave, then shows the exercise (skipped with Reduce motion) |
| `focus` | `all` | `neck`, `hips`, `all`, or `custom` after picking exercises by hand |
| `holdDuringCalls` | `true` | skip cards while any app is using the mic or camera |
| `holdDuringCalendarEvents` | `false` | skip cards during busy calendar events (asks for calendar access) |

State lives next to it in `state.json`, and every card gets a line in `log.jsonl`.

## Add an exercise

1. Add an entry to `app/Resources/exercises.json` with an `id`, `name`, short `instruction`, `spec`, `durationSeconds`, beginner `steps`, and a `tip`.
2. Draw `app/Resources/figures/<id>.svg`. Copy an existing figure and keep the tagged parts (`body`, `head`, `face`, `topper`, `legs`) so every character can be generated from it. Animate with SMIL (`<animate>`, `<animateTransform>`), and use `currentColor` for limbs so dark mode works.
3. Run `scripts/dev.sh show`.

`scripts/build-figures.py` makes the Toni versions from the base drawings and rebuilds `app/figures.html`, a preview page with every move, dark mode, and a peak pose toggle.

## Working on it

```bash
scripts/dev.sh          # rebuild and restart the installed app
scripts/dev.sh show     # ...and show a card
scripts/dev.sh intro    # ...and replay the intro
```

`scripts/test-hooks.sh` checks the settings.json merge on temp files, through both the jq scripts and the app's Swift version (`ClaudeHooks.swift`), and fails if the two ever write different bytes. Pass your own `settings.json` to round-trip a copy of it too. `CLAUDE_SETTINGS=/path/to/copy.json` points the app (and the scripts) at a test file instead of `~/.claude/settings.json`.

`app/Resources/card.html` opens in a browser as a demo with every move, the peek, the noodle drop, dark mode, Reduce motion, and the intro. Serve `app/` locally so it can load the figures, e.g. `python3 -m http.server -d app`.

## Filming

The menu bar has a **Film** submenu for recording demos. Pick a countdown (none, 3, 5, or 10 sec), then cue a specific stretch, the intro, Claude finishing, or the Noodle window. Toggles make Toni peek after 3 seconds instead of 20, make Start run a 5 second timer so you get to the celebration fast, and float cards over full-screen apps. Film cards never touch your count, cooldown, or rotation. Pause Noodle while you record so a real prompt doesn't cut in; film cues still work.

From a script: `open -g 'claudeposture://film-doorway'` cues any stretch by id, and `open -g 'claudeposture://home'` opens the Noodle window.

## Uninstall

```bash
brew uninstall --cask noodle
```

For a curl install, disconnect it and then delete the app:

```bash
~/Applications/Noodle.app/Contents/Resources/bin/noodle remove && rm -rf ~/Applications/Noodle.app
```

From source, `scripts/uninstall.sh` does both.

Each one takes out only the two hooks Noodle added, leaving the rest of `settings.json` alone. `~/.claude-posture` stays unless you delete it (or `brew uninstall --zap`). To keep the app but disconnect it from Claude Code, use Settings › Disconnect from Claude Code… in the menu bar (same preview and backup as connecting), or run `noodle remove`.

If you installed by download, disconnect first (Settings › Disconnect from Claude Code…), then quit Noodle and drag it to the Trash.

## Releasing

The tag sets the version, so there's nothing to bump by hand. Tag and push:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

[`.github/workflows/release.yml`](.github/workflows/release.yml) builds a universal `Noodle.app`, signs it with the NGEN ART CO Developer ID (hardened runtime, [`app/Noodle.entitlements`](app/Noodle.entitlements)), has Apple notarize it and staples the ticket, attaches `Noodle.zip` and its sha256 to a GitHub Release, and, with the `TAP_DEPLOY_KEY` secret set (an SSH deploy key that can only write to the tap), updates `Casks/noodle.rb` in [pcybriwsky/homebrew-tap](https://github.com/pcybriwsky/homebrew-tap) from [`packaging/homebrew/noodle.rb`](packaging/homebrew/noodle.rb).

Signing needs five repo secrets, listed at the top of the workflow: the Developer ID certificate as a base64 `.p12` plus its password, and an App Store Connect API key (`.p8`, key ID, issuer ID). Without them a release still goes out, ad-hoc signed. To sign locally, `SIGN_IDENTITY="Developer ID Application: NGEN ART CO (HY8S2WXJ2X)" scripts/build-app.sh`.

## License

MIT, see [LICENSE](LICENSE).
