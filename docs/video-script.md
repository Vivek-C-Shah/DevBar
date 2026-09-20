# DevBar demo video — script and shot list

Three cuts from one recording session:

| Cut | Length | Where |
|---|---|---|
| **A — the main demo** | 75–90s | YouTube, README, website |
| **B — the X cut** | 40s, no voice-over, captions only | X/Twitter, LinkedIn |
| **C — the hero loop** | 8–12s, silent, looping | website hero (optional) |

Record once at the highest quality, then cut down. Cut B is Cut A with the voice-over removed and captions added, so don't record it twice.

---

## Before you hit record

**Clean the screen.** This is the part people skip and regret.

- New Windows user profile, or at least: close Slack, WhatsApp, mail, and anything with notifications. Turn on Focus Assist.
- Empty the DevBar clipboard module (it will show your real copies otherwise) — restart DevBar right before recording.
- Open the "What Jarvis knows about you" window once and check nothing private is on screen if you plan to show it. Safer: don't show it in Cut A.
- In `config.json`, set `GitWatchedRepos` to one or two repos with neutral names.
- Start the throwaway dev server you're going to kill: `python -m http.server 3000`.
- Have VS Code open on a small, presentable file, and a browser tab with something boring (docs).
- Wallpaper: plain, dark, no personal photos.

**Capture settings.** OBS, 1920×1080, 60fps, display capture of the primary monitor, desktop audio ON (you need to hear Jarvis speak), mic on a separate track so you can re-record voice-over later. Use headphones so the mic doesn't pick up Jarvis's own voice.

**Rehearse the voice lines once.** Jarvis's replies vary between takes; that's fine, it's a live tool. If a take answers something odd, keep going and do a second pass.

---

## Cut A — the main demo (75–90s)

> Voice-over lines are what **you** say to camera/mic. "SAY TO JARVIS" lines are what you speak at the app. On-screen text = small caption, bottom-left, JetBrains Mono.

### 0:00–0:07 — The problem, shown not told

**Shot:** Your desktop, working in VS Code. Alt-tab to a terminal, alt-tab to a browser, alt-tab back. Deliberately fumble for the right window.

**VO:** "Fifty times a day I check the same five things. What's on port three thousand. Is the container up. What did I just copy."

**On-screen text:** `alt-tab. alt-tab. alt-tab.`

### 0:07–0:15 — Meet the pill

**Shot:** Cursor drifts to the top edge. The pill is already there; the bar drops open. Page through two tabs, then move the cursor away and let it collapse.

**VO:** "DevBar is this. A pill at the top of the screen. Hover it, it drops open. Look away, it's gone."

**On-screen text:** `125 × 22 px · 0.0% CPU idle`

### 0:15–0:27 — The modules, fast

**Shot:** Hover, then click straight through four tabs, pausing about 2s on each: Clipboard (click a chip to re-copy), Ports (hover the kill button, don't click), Docker (one container), Git (dirty repo).

**VO:** "Your clipboard history. Every listening port with the process holding it. Containers. Repos. Claude Code sessions, with the one that's waiting on you."

**On-screen text:** `9 modules · each one quiet until it's on screen`

### 0:27–0:42 — Jarvis, the useful kind

**Shot:** Press Ctrl+Alt+Space. The bar opens on the Jarvis tab, the orb turns green.

**SAY TO JARVIS:** "What's running on my ports?"

Let it answer out loud. Don't talk over the reply — the audience needs to hear it.

**VO (after the reply):** "That's not a chatbot in a window. It read the actual TCP table."

**On-screen text:** `press · talk · it answers`

### 0:42–0:56 — It acts, and it asks first

**Shot:** Same session, keep talking.

**SAY TO JARVIS:** "Kill whatever is on port three thousand."

Jarvis says *"Shall I kill python on port three thousand?"* — **pause on this frame**, it's the most important second in the video.

**SAY TO JARVIS:** "Yes."

Cut to the terminal where the server died.

**VO:** "Anything destructive gets read back to me first. It waits for a yes."

**On-screen text:** `every destructive action is confirmed`

### 0:56–1:10 — The part people don't expect

Pick **two** of these, whichever demo cleanly:

- **Screen reading.** Show a red compiler error in VS Code. Say: *"What does this error say and how do I fix it?"*
- **Dictation.** Put the cursor in a commit message field. Say: *"Type this: fix the port collision in the dev server."* Watch it type.
- **Mail.** Say: *"Any important unread email?"* — only if your inbox is presentable. Blur it in post if not.
- **Reminder.** Say: *"Remind me in fifteen seconds to stretch."* Then wait for it to speak up by itself. Good ending beat.

**VO:** "It can look at my screen, type for me, check my calendar, and tell me when a Claude session needs me."

### 1:10–1:22 — The honest bit

**Shot:** Jarvis settings window, scrolling slowly past the brain order and the keys section (keys show as `••••` — check before recording).

**VO:** "It runs on free API tiers with your own keys, encrypted locally. No account, no server, no telemetry. The voice can run fully on-device. And it's MIT licensed."

**On-screen text:** `bring your own keys · nothing phones home`

### 1:22–1:30 — Call to action

**Shot:** The site's hero (or the GitHub repo page).

**VO:** "It's open source. If you check something fifty times a day that isn't on the bar yet, a module takes an afternoon. Link's below."

**On-screen text:** `devbar.vercel.app · github.com/Vivek-C-Shah/DevBar`

---

## Cut B — the X cut (40s, captions only)

X autoplays muted, so the first 2 seconds have to work without sound.

**Structure:**
1. `0:00–0:02` — Cold open on the bar dropping open. Big caption: **"A toolbar you can talk to."**
2. `0:02–0:12` — The module sweep from Cut A, sped up 1.25×. Caption: **"Clipboard. Ports. Containers. Repos. Claude sessions."**
3. `0:12–0:26` — The port kill, at normal speed, with **subtitles on the spoken lines** ("What's running on my ports?" → "Shall I kill python on port 3000?" → "Yes"). Caption: **"It asks before anything destructive."**
4. `0:26–0:34` — One surprise (screen reading or dictation). Caption: **"It can see your screen and type for you."**
5. `0:34–0:40` — End card: logo, `0.0% CPU idle · MIT · devbar.vercel.app`.

**Post copy (X):**

> I got tired of alt-tabbing to check the same five things, so I built a toolbar that lives at the top of my screen — and then gave it a voice.
>
> "What's on port 3000?" → it reads the real TCP table → "Shall I kill python on port 3000?"
>
> 0.0% CPU when idle. Runs on free API tiers. MIT licensed.

Reply with the GitHub link rather than putting it in the post (reach).

---

## Cut C — the hero loop (8–12s, silent)

Screen-capture region: just the bar, 640×300, centred at the top of the screen.

Sequence: idle pill → drop open on the Jarvis tab → the orb pulses → a tool chip appears → collapse. No audio, no captions. Export as MP4 (H.264) **and** WebM, under 2MB, and add it to the site hero in place of the DOM mock if you want the real thing there.

---

## YouTube metadata

**Title:** DevBar — I gave my Windows taskbar a voice (open source)

**Description:**

```
DevBar is a small, hover-expand toolbar for Windows developers — clipboard history,
listening ports, Docker containers, git status, Claude Code sessions — plus Jarvis, a
voice assistant that can actually act on your machine.

Press a shortcut, ask "what's running on my ports?", and it reads the real TCP table.
Ask it to kill one and it reads the action back and waits for your yes.

0.0% CPU while it's collapsed. Runs on free API tiers with your own keys, encrypted
locally. No account, no server, no telemetry. MIT licensed.

Download / source: https://github.com/Vivek-C-Shah/DevBar
Site: https://devbar.vercel.app

00:00 The problem
00:07 The pill
00:15 The modules
00:27 Talking to it
00:42 It asks before it acts
00:56 Screen reading and dictation
01:10 Keys, privacy, cost
01:22 Open source

Built with .NET 8 / WPF. Speech by Deepgram, brains on Groq and Gemini free tiers,
on-device voices via Piper and Kokoro (sherpa-onnx), wake word on-device too.
```

**Tags:** windows, developer tools, open source, dotnet, wpf, voice assistant, jarvis, productivity, groq, gemini, whisper, devtools

**Thumbnail:** the bar expanded on the Jarvis tab over a dark desktop, with three words in large type: **"Talk to your taskbar."** Keep the text out of the bottom-right corner (timestamp overlay).

---

## Post-production checklist

- [ ] No API keys, tokens, real email addresses or client names visible in any frame — scrub through at 25% speed and check the clipboard module and settings window carefully.
- [ ] Jarvis's replies are audible and not clipped; normalise desktop audio to about −14 LUFS.
- [ ] Captions burned in for the X cut (most people watch muted).
- [ ] Cuts land on action, not on you reaching for the mouse.
- [ ] Show the confirmation prompt for at least 1.5 seconds. It's the trust moment.
- [ ] End card holds for 3 seconds so people can read the URL.
