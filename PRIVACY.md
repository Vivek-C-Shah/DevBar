# DevBar — Privacy Policy

**Last updated: 20 September 2026**

DevBar is a free, open-source desktop app for Windows, published by Vivek Shah. Its Jarvis module adds a voice assistant. This policy explains exactly what happens to your data.

**The short version:** DevBar has no servers and no accounts. Nothing is collected, stored or seen by the developer. Everything stays on your PC, except the data you deliberately send to the AI services you configure with your own API keys.

---

## 1. There is no DevBar service

DevBar runs entirely on your computer. There is no DevBar backend, no telemetry, no analytics, no crash reporting, no advertising and no user accounts. The developer cannot see your data, your usage or even that you installed the app.

## 2. What DevBar stores on your PC

All of it lives in `%LOCALAPPDATA%\DevBar\` on your own machine, and nowhere else:

| What | Where | Notes |
|---|---|---|
| Settings | `config.json` | Shortcut, voice, model order, toggles. Contains no secrets. |
| API keys and tokens | `secrets.dat` | Your AI provider keys, your Google OAuth client and refresh token — encrypted with Windows DPAPI, readable only by your Windows user account on that machine. |
| Long-term memory | `jarvis.db` | Facts about you, reference notes you import, reminders, and recent conversation lines (auto-deleted after 30 days). |
| Voice models | `models\` | Downloaded speech models, if you choose local voices or the wake word. |
| Diagnostics | `error.log`, `jarvis-trace.log` | Only written when something fails, or when you switch tracing on yourself. |

You can read, edit or delete any of it. "What Jarvis knows about you" in the app lists every remembered fact with a delete button and a "Forget everything" option. Deleting the folder resets DevBar completely.

## 3. Your microphone

The microphone is **closed** unless you press the Jarvis shortcut, or you have deliberately enabled the "Hey Jarvis" wake word. When the wake word is on, listening happens **entirely on your PC** with a local model: nothing is transmitted until the wake word is detected. The app shows a green indicator whenever the mic is open, and Windows shows its own microphone indicator.

## 4. What is sent to third parties, and when

DevBar sends data only to services **you** configure with **your own** API keys, only while you are using the assistant. Each provider handles that data under its own privacy policy and terms.

| Trigger | What is sent | To whom (by default) |
|---|---|---|
| You speak to Jarvis | Your microphone audio, while the conversation is active | Deepgram (speech-to-text) |
| Every request | Your transcribed words, your pinned profile facts, recent conversation, the names of your reference notes, the current time, your approximate city, and the title of your foreground window | Your configured AI model provider (e.g. Groq, Google Gemini) |
| Jarvis speaks | The reply text to be voiced — **unless** you choose a local voice (Piper, Kokoro or the Windows voice), in which case nothing is sent | Deepgram (text-to-speech) |
| You ask about something on screen | A screenshot of your active window or screen | Your configured vision provider (e.g. Google Gemini) |
| You ask something that needs the web | Your search question | Google (via the Gemini API) or Groq |
| You use a tool | Its result, which may contain your emails, calendar entries, file contents, command output or clipboard text | Your configured AI model provider |
| After a conversation | Recent conversation lines, so lasting facts about you can be extracted | Your configured AI model provider |

Points worth being explicit about:

- **Your Google data is not stored by DevBar, but it is processed by AI models.** When you ask Jarvis about your mail or calendar, the app fetches it from Google and includes the relevant part in the request it sends to your AI provider so it can answer. It is not written to disk.
- **Turning things off works.** No location (setting), no learning (setting), no cloud voice (choose a local one), no web search, no screen reading, no Google — each is optional, and Google is off until you connect it.
- **Secrets are filtered from memory.** Anything that looks like a password, API key or token is refused before it can be remembered, whether you or the model proposed it.

## 5. Google user data

If you connect Google Calendar and Gmail, DevBar uses **your own** Google Cloud OAuth client and requests only:

- `calendar.events` — read your events and add events you ask for.
- `gmail.readonly` — read mail you ask about.
- `gmail.compose` — create drafts, and send an email when you explicitly confirm it.

The refresh token is stored encrypted on your PC (see §2) and is never transmitted anywhere except to Google. DevBar does not copy, index, retain or share your mail or calendar; it reads what is needed to answer the question you asked. Google data is never used for advertising, is never sold, and is never used to train any model by DevBar. Note that your chosen AI provider does receive the excerpt needed to answer you, as described in §4.

DevBar's use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

**To revoke access:** click "Disconnect Google" in Jarvis settings, or remove it at [myaccount.google.com/permissions](https://myaccount.google.com/permissions).

## 6. Children

DevBar is a developer tool and is not directed at children under 13.

## 7. Changes

This policy may be updated; the date at the top changes with it, and the history is public in the repository.

## 8. Contact

Vivek Shah — vivekchiragshah2004@gmail.com · [github.com/Vivek-C-Shah/DevBar](https://github.com/Vivek-C-Shah/DevBar)
