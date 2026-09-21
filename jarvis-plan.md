# Jarvis for DevBar - plan

A voice assistant that lives in DevBar as a new tab (🎙 **Jarvis**). You press a global shortcut you can change, it starts listening right away, answers out loud, and can **act on your machine**. It gets those abilities from the modules DevBar already has (ports, docker, git, clipboard, media, Claude sessions), plus new tools.

Goal: **it costs nothing to run day to day.** Paid keys plug in through the same interfaces, but nothing needs them.

> **Status (2026-09-19):**
> - **Phase 1 is built and installed.**
> - **Phase 2 is built and installed:** long-term memory with learning after each conversation (visible and deletable in "What Jarvis knows"), location (Windows location, then IP) plus weather and system status, screen questions (Gemini vision), dictation, talking over Jarvis to interrupt it, reminders that survive restarts, and shell commands that always ask first.
> - **Phase 3 is built and installed:**
>   - Background web search (Gemini + Google Search grounding, with Groq compound-mini as the last resort; no browser).
>   - Heads-ups that are event-driven, not polled: Claude Code sessions via an optional hook, Build Pulse targets, and reminders. Etiquette: speak / show / off, quiet hours, silent for games and presentations, voice-only over full-screen video.
>   - A daily brief, handing tasks to Claude Code, and tone matching.
>   - An opt-in "Hey Jarvis" wake word (on-device, ~0.16% CPU). On synthesized speech it caught 12–14 of 15 wake phrases with 0–1 false triggers in 20.
> - **Still open:** Calendar/Gmail (needs the user's Google OAuth client), offline speech-to-text and an offline brain (Ollama is already supported in the `Llm` chain once installed), and Gemini Live mode.
> - **Groq retired the Llama models,** so the brain chain now runs on `gpt-oss-120b` → `gpt-oss-20b` → Gemini.

---

## 0. Rules DevBar already follows that Jarvis must keep

| DevBar rule | What it means for Jarvis |
|---|---|
| 0% CPU at idle; modules go quiet on `OnCollapsed` | The shortcut uses `RegisterHotKey`, which is a Windows message and costs no CPU. **The mic is closed until you press the shortcut.** Wake word is opt-in and off by default (it keeps the mic open and costs about 1–2% CPU). |
| Blur only while visible ([[windows-acrylic-blur-cpu-cost]]) | The listening "orb" animation runs only while the Jarvis tab is on screen. |
| Small toolbar, never full-width | Jarvis stays inside the normal 640px card. |
| Nothing steals focus | Listening doesn't need keyboard focus. The shortcut expands and pins the bar, but the app you're typing in keeps focus. |
| A broken plugin is never fatal | Any provider can fail (429, no network, bad key). Jarvis says so and falls back to the next one. It never crashes the bar. |

One small exception to the module contract: a conversation that's still running (you're mid-sentence, or a tool call is in flight) keeps going if the bar collapses. It then goes quiet when the turn ends.

---

## 1. Recommended **free** stack

Your machine: **RTX 3050 6GB, 16GB RAM, i5-13450HX.** That's enough to run the *ears and voice* locally. It's **not** enough for a local *brain* that does reliable multi-step tool calling. The best free setup is a hybrid.

| Layer | Default (free) | Fallback (free) | Fully offline | Paid upgrade |
|---|---|---|---|---|
| **Speech-to-text** | **Deepgram Nova-3 streaming** (your $200 credit, about $0.0077/min ≈ **430 hours**). Or **Deepgram Flux**, which is built for voice agents and detects end-of-turn itself | **Groq Whisper-large-v3-turbo**: free, 2,000 req/day, 2h of audio/hour. It works per utterance, so it needs local VAD to cut the audio | **Whisper (small/base) via sherpa-onnx**, local on CPU/GPU | Deepgram paid, AssemblyAI, OpenAI |
| **LLM brain** | **Groq**: `openai/gpt-oss-120b` or `llama-3.3-70b` for tool calling (free, ~1K req/day each). `llama-3.1-8b-instant` for fast small jobs (14.4K req/day) | **Gemini 2.5 Flash / Flash-Lite**, free tier (250–1,000 req/day). Also the **vision** model for "look at my screen" | **Ollama + Qwen3-4B / Gemma-3-4B (Q4)**. Fits in 6GB and does ~40–60 tok/s. Fine for chat and simple commands, unreliable for chained tools | Claude, OpenAI, Gemini paid, OpenRouter |
| **Text-to-speech** | **Deepgram Aura-2** `aura-2-draco-en` (your choice; credit, ~$0.03 per 1k chars). Measured about 350ms to first audio once the connection is warm | **Piper via sherpa-onnx** (local, free). Measured ~200ms per sentence, about 20× faster than real time on this laptop. **Kokoro** is also offered: it sounds nicer but only runs at about real time on this CPU, and the int8 build was 2.6× *slower* than real time | Windows built-in `SpeechSynthesizer`: zero dependencies, always works | ElevenLabs, OpenAI TTS |
| **Voice activity detection** | **Silero VAD via sherpa-onnx** (local) | - | same | - |
| **Wake word (opt-in)** | **sherpa-onnx keyword spotter**, custom phrase "Jarvis" | openWakeWord | same | Picovoice Porcupine |

**Why sherpa-onnx:** it's one NuGet package (`org.k2fsa.sherpa.onnx`) with native C# bindings. It covers VAD, local Whisper, Kokoro/Piper TTS, and wake word. That gives you all the local pieces without a Python sidecar.

**Other free providers worth knowing about** (all OpenAI-compatible, so they're a config line away):
- **Cerebras**: free tier, extremely fast, good for the fallback chain.
- **OpenRouter** `:free` models: small daily quota, useful as a last resort.
- **Mistral** "Experiment" plan: free, rate-limited.

**Budget check on your Deepgram credit:** an hour of talking per day (STT) plus ~15 min of Aura speech is about $0.85/day, so the $200 lasts ~7 months. If you use local Kokoro for TTS and ~20 min/day of STT, it lasts **years**. *Check whether the credit expires in the Deepgram console.* Flux had a free launch promo that ended 2026-09-12, so confirm its current rate before making it the default.

> Free-tier numbers change often. These are from Sept 2026 sources (linked at the bottom). The provider router (§3) handles 429s automatically, so a tightened quota only costs you a fallback.

### Option B: one pipe for speech in and speech out
**Gemini Live API** (native audio) does STT, LLM, and TTS over a single WebSocket, with built-in barge-in and tool calling. It's the lowest-effort way to get a conversation that feels "real". The downsides are a less predictable free quota and lock-in to one vendor. Plan: build the modular pipeline first (you control every piece), then add Live as a switchable "mode" in phase 3.

---

## 2. Latency budget (target: under 1s from when you stop talking to when Jarvis starts)

```
you stop talking ─► end-of-turn detected   ~250ms  (Deepgram endpointing / Flux / Silero VAD)
                 ─► LLM first token        ~200ms  (Groq)
                 ─► first sentence ready   ~150ms  (stream tokens, cut at sentence end)
                 ─► TTS first audio        ~150ms  (Kokoro local, or Aura-2 streaming)
                                           ≈ 750ms
```
Two rules make this work. First, **stream everything** and never wait for the full LLM reply before speaking. Second, **speak the first sentence while the rest is still generating.**

---

## 3. Architecture (fits the existing codebase)

```
src/DevBar/Modules/Jarvis/
  JarvisModule.cs          IDevBarModule: tab "Jarvis", glyph 
  JarvisCard.xaml(.cs)     orb + live transcript + reply + tool-action chips + settings gear
  Audio/
    MicCapture.cs          NAudio WASAPI capture, 16kHz mono; opens only while listening
    AudioPlayer.cs         playback queue, can be stopped instantly (for barge-in)
    Vad.cs                 Silero via sherpa-onnx
  Speech/
    ISpeechToText.cs       StreamAsync(audio) → partial/final transcripts
    DeepgramStt.cs  GroqWhisperStt.cs  LocalWhisperStt.cs
    ITextToSpeech.cs       SpeakAsync(text chunks) → audio stream
    KokoroTts.cs  DeepgramAuraTts.cs  WindowsTts.cs
  Brain/
    ILanguageModel.cs      ChatStreamAsync(messages, tools) → tokens + tool calls
    OpenAiCompatibleLlm.cs one class covers Groq, Cerebras, OpenRouter, Ollama, LM Studio, OpenAI
    GeminiLlm.cs  AnthropicLlm.cs
    ProviderRouter.cs      ordered fallback chain; on 429/timeout/network error, try the next one
    Conversation.cs        turn loop: listen → think → act → speak
  Tools/
    ITool.cs               name, JSON schema, RiskLevel, ExecuteAsync
    ToolRegistry.cs
    (one file per tool, see §5)
  Memory/
    MemoryStore.cs         SQLite (Microsoft.Data.Sqlite) in %LOCALAPPDATA%\DevBar\jarvis.db
  Security/
    SecretStore.cs         API keys encrypted with DPAPI (ProtectedData), never plain text in config.json
    ApprovalGate.cs        voice/click confirmation for risky tools
Core/
  GlobalHotkey.cs          RegisterHotKey wrapper and parser ("Ctrl+Alt+J")
```

**Shortcut behavior** (customizable in `config.json` and from a "press new shortcut" box in the Jarvis card's settings):
- `JarvisHotkey`: default `Ctrl+Alt+Space` (check for conflicts; `Win+J` is reserved on some builds).
- `JarvisHotkeyMode`: `toggle` (press to start, press again or stay silent to stop) or `pushToTalk` (hold to talk). Push-to-talk needs to know when you release the key. Poll `GetAsyncKeyState` **only while the key is held**, not with an always-on keyboard hook.
- `Esc`, or saying "stop" / "cancel", interrupts at any point.
- Pressing the shortcut expands the bar, switches to the Jarvis tab, and pins it until the turn ends. Then it collapses as usual. If `JarvisShowBar: false` it runs headless and only shows a small listening dot on the pill.

**Config additions:**
```jsonc
"Jarvis": {
  "Hotkey": "Ctrl+Alt+Space",
  "HotkeyMode": "toggle",
  "WakeWord": { "Enabled": false, "Phrase": "jarvis" },
  "Stt":  ["deepgram-nova3", "groq-whisper", "local-whisper"],
  "Llm":  ["groq:openai/gpt-oss-120b", "gemini:gemini-2.5-flash", "ollama:qwen3:4b"],
  "FastLlm": "groq:llama-3.1-8b-instant",
  "Vision": "gemini:gemini-2.5-flash",
  "Tts":  ["kokoro:bm_george", "deepgram:aura-2", "windows"],
  "Persona": "jarvis",           // jarvis | terse | friendly | custom prompt file
  "AutonomyLevel": "confirm-risky", // read-only | confirm-risky | trusted
  "SpeakReplies": true
}
```
The keys themselves are **not** in this file. They go in DPAPI-encrypted storage, entered through the card's settings UI.

---

## 4. Full feature catalog (mapped to your Claude chat's tiers)

Legend: **🆓** free · **💳** needs a paid key or has a real cost · **P1/P2/P3/P4** = phase (§6)

### Must-have ("without these it's a chatbot")
| Feature | How in DevBar | Cost | Phase |
|---|---|---|---|
| Push-a-key-and-talk | Global shortcut, mic opens at once, orb reacts to your voice level | 🆓 | P1 |
| Streaming voice conversation | Streaming STT → streaming LLM → speak sentence by sentence | 🆓 | P1 |
| **Barge-in** (interrupt it) | VAD keeps running during playback; if you start talking, playback stops and it listens. Needs echo cancellation: headphones work automatically, and for speakers use WASAPI *communications* capture (Windows AEC) | 🆓 | P2 |
| Hands-free follow-up | After a reply, keep listening ~6s for a follow-up without pressing the shortcut again | 🆓 | P1 |
| Real actions (agentic) | Tool calling to DevBar modules and the system (§5) | 🆓 | P1–P2 |
| Judgment on stakes | Each tool has a `RiskLevel` (§7). Risky actions need you to say "yes" or click | 🆓 | P1 |
| Security | DPAPI key storage, allowlists, prompt-injection defense for content it reads (§7) | 🆓 | P1 |
| Persistent memory | "Remember that I…" stores facts. Facts go into the system prompt; old conversations are summarized into SQLite | 🆓 | P2 |
| Multimodal: sees your screen | "What's this error?" captures the active window or screen and sends it to Gemini Flash vision | 🆓 | P2 |
| Multimodal: reads files/code | "Summarize this file" uses the clipboard path, the selected file in Explorer, or a Shelf item | 🆓 | P2 |
| Situational awareness (light) | Each turn gets context: active window title, time, git branch of the focused repo, running containers, Claude session states | 🆓 | P2 |

### Should-have (useful, not just impressive)
| Feature | How | Cost | Phase |
|---|---|---|---|
| Graceful degradation | Router fallback: cloud → other cloud → local Ollama/Whisper/Kokoro. Works fully offline with lower quality | 🆓 | P2 |
| Proactive notices | Event-driven only, no polling: "Your Claude session is waiting on you", "Build finished", "Port 5173 died", "Docker container exited". These come from events the modules already raise. Spoken only if you allow it; otherwise a chip on the pill | 🆓 | P3 |
| Calendar / email | Google Calendar + Gmail API (free, OAuth). "What's next today?", "Read my unread from X" | 🆓 | P3 |
| Reminders & timers | "Remind me in 20 min to push" uses Windows toast + spoken alert. Stored in SQLite so they survive restarts | 🆓 | P2 |
| Long-horizon tasks | A "missions" table: goal, steps, next check-in. Jarvis brings them up in the morning brief | 🆓 | P4 |
| Tone matching | Short, precise replies when you're in a hurry (short utterances, words like "quick" or "now", errors on screen); chattier when relaxed. Done with prompt rules plus the Deepgram sentiment/pace signals | 🆓 | P3 |
| Explainability | "Why did you do that?" gets an answer from the per-turn action log (tool, arguments, reason) shown as chips in the card | 🆓 | P2 |
| Hand off coding work | "Ask Claude Code to fix the failing test in DevBar" launches `claude -p` in that repo and tracks it in the existing Claude module | 💳 (your Claude plan) | P3 |
| Cross-device (phone) | Telegram bot bridge: send text or voice to your bot and the same Jarvis on your PC answers or acts (free, no server) | 🆓 | P4 |

### Nice-to-have (polish)
| Feature | How | Cost | Phase |
|---|---|---|---|
| Jarvis personality | British-butler system prompt ("Sir" is optional), dry wit, short replies, consistent across providers | 🆓 | P1 |
| Wake word "Jarvis" | sherpa-onnx KWS, opt-in, ~1–2% CPU while on. Pauses automatically on battery | 🆓 | P3 |
| Morning / session brief | On first hotkey of the day: calendar, git state of watched repos, CI status, reminders | 🆓 | P3 |
| Dictation mode | "Type this…" transcribes and pastes into the focused window, with light cleanup by the fast LLM | 🆓 | P2 |
| Premium voice | Deepgram Aura-2 / ElevenLabs voice for the "real Jarvis" sound | 💳/credit | P2 |
| Web search / answers | Tavily or Brave Search API (both have free tiers) + Gemini grounding | 🆓 (quota) | P3 |
| Smart home | Home Assistant REST API if you run it (free) | 🆓 | P4 |
| Predictive assistance | Notices patterns in the memory log ("you always start docker + vite at 10am") and offers a one-word routine | 🆓 | P4 |
| What-if modeling | "What happens if I bump this package?" runs a dry-run in a scratch git worktree and reports back | 🆓 | P4 |

### Maybe (open questions: build these only with safeguards)
- **Fully autonomous initiative**: never on by default. At most "suggest, don't do" plus a daily cap on spoken interruptions.
- **Continuity across model changes**: the fix is that **memory and persona live in your SQLite and prompt files, not in the model**. Swapping Groq for Claude keeps the same "Jarvis".
- **Learning from failure**: log every tool call's outcome and your corrections ("no, I meant the *other* repo"). Feed the lessons back in as memory facts. That's cheap and safe; no fine-tuning.
- **Emotional bonding**: a deliberate design choice. The persona is warm but honest, and never guilt-trips or flatters to keep you engaged.

---

## 5. Tool list (what it can *do*)

**Built on existing DevBar modules (zero new dependencies, and nothing like Siri does this):**
- `ports.list`, `ports.kill(port)` ⚠ confirm: "what's on 3000? kill it"
- `docker.list`, `docker.start/stop(name)`: "restart the postgres container"
- `git.status(repo)`, `git.summary()`: "anything uncommitted?"
- `clipboard.recent(n)`, `clipboard.copy(text)`: "copy the second-to-last thing again", "put my email in the clipboard"
- `media.play/pause/next/nowPlaying`: "what's playing?", "skip"
- `claude.sessions()`, `claude.focus(project)`: "which Claude session needs me?"
- `ci.status()`: "did the build finish?"
- `shelf.list/add`

**System tools:**
- `app.open(name)`, `url.open(url)`, `folder.open(path)`
- `window.focus(title)`, `window.list()`
- `system.volume/mute/brightness`, `system.lock` ⚠, `system.sleep` ⚠
- `screen.capture(activeWindow|fullScreen)` goes to vision
- `keyboard.type(text)` for dictation
- `file.search(query)` via Windows Search index; `file.read(path)` (read-only, size-capped)
- `shell.run(command, cwd)` 🛑 **always confirm, show the command first**; allowlisted commands can skip confirmation
- `timer.set`, `reminder.add/list/cancel`
- `memory.remember/forget/recall`
- `web.search(query)` (P3)

---

## 6. Build phases

**P1: "It talks" (the smallest version that feels like Jarvis)**
1. `GlobalHotkey` + config + settings box to record a new shortcut.
2. Jarvis tab: orb, live partial transcript, reply text.
3. Mic → Deepgram Nova-3 streaming → Groq (streaming + tools) → Kokoro TTS, speaking sentence by sentence.
4. `ProviderRouter` with Groq → Gemini fallback.
5. ~8 read-only and low-risk tools (ports list, docker list, git status, media, app.open, clipboard, timer, time/date).
6. Persona prompt, DPAPI key entry UI, `Esc` / "stop" cancel.
✅ *Done when:* shortcut → "what's running on my ports?" → spoken answer in under 1.5s, and CPU is 0% when idle again.

**P2: "It acts, remembers, and sees"**
Barge-in, follow-up listening, SQLite memory, confirmation gate, risky tools (kill, docker stop, shell), screen vision, dictation, reminders, full offline fallback (local Whisper + Ollama), and the action log in the card.

**P3: "It anticipates"**
Event-driven proactive notices, wake word, Calendar/Gmail, morning brief, web search, Claude Code hand-off, tone matching, Gemini Live mode.

**P4: "It follows you"**
Telegram phone bridge, missions, routines and predictions, Home Assistant, what-if worktrees.

---

## 7. Trust & safety model

| Risk level | Examples | Behavior at `confirm-risky` (default) |
|---|---|---|
| **Read** | list ports, git status, what's playing, read calendar | Runs automatically |
| **Reversible** | open app, set volume, copy to clipboard, pause media, set timer | Runs automatically and says what it did |
| **Destructive** | kill process, stop container, run shell, type into window, send email | Says the exact action and waits for **"yes" / click**. A plain "yes" in the next utterance counts; anything else cancels |
| **Forbidden** | payments, entering passwords, changing security settings, deleting files outside scratch | Refuses. Nothing overrides this |

Also:
- **Prompt-injection defense.** Text from the screen, files, web pages, and emails is wrapped as *untrusted data*. Tool calls made because of that content need confirmation even at `trusted` level.
- **Mic indicator.** Whenever the mic is open, the pill shows a red dot. No exceptions.
- **Nothing recorded by default.** Audio is streamed and thrown away. Transcripts are kept only if memory is on, and "forget the last conversation" works.
- **Limits.** Max 5 tool calls per turn and max 3 destructive actions per turn, so a model stuck in a loop can't do much.

---

## 8. Dependencies to add
- `NAudio` (WASAPI mic + playback)
- `org.k2fsa.sherpa.onnx` (VAD, local Whisper, Kokoro TTS, wake word). Models are downloaded on first use into `%LOCALAPPDATA%\DevBar\models\` and are *not* bundled in the installer (Kokoro ≈ 330MB, Silero VAD ≈ 2MB, whisper-base ≈ 150MB)
- `Microsoft.Data.Sqlite`
- `System.Security.Cryptography.ProtectedData`
- HTTP/WebSocket: built into .NET 8 (`HttpClient`, `ClientWebSocket`). No vendor SDKs needed; every provider above has a plain REST/WS API.
- Optional: Ollama (separate install) for the offline brain.

## 9. Open decisions (answer these before P1)
1. Shortcut default and mode: `Ctrl+Alt+Space` toggle, or hold-to-talk?
2. Voice: local Kokoro (free forever, very good) or Deepgram Aura-2 (credit, a bit more natural) as the default?
3. Should Jarvis address you as "Sir", by name, or neither?
4. Speakers or headphones most of the time? (This decides how much work barge-in needs.)

---

Sources (free-tier and pricing figures, Sept 2026):
- Groq free tier: https://www.eesel.ai/blog/groq-pricing · https://klymentiev.com/blog/groq-pricing
- Gemini API rate limits: https://ai.google.dev/gemini-api/docs/rate-limits · https://ai.google.dev/gemini-api/docs/pricing
- Deepgram pricing: https://deepgram.com/pricing · https://www.layer3labs.io/guides/deepgram-flux-tts-pricing
