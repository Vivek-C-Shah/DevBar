using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBar.Core;
using DevBar.Modules.Jarvis.Audio;
using DevBar.Modules.Jarvis.Brain;
using DevBar.Modules.Jarvis.Context;
using DevBar.Modules.Jarvis.Memory;
using DevBar.Modules.Jarvis.Speech;
using DevBar.Modules.Jarvis.Tools;
using DevBar.Sdk;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DevBar.Modules.Jarvis;

/// <summary>
/// `DevBar.exe --jarvis-selftest [outDir]` — exercises the voice pipeline
/// end-to-end without a mic or speakers: each TTS engine renders a WAV,
/// Deepgram transcribes the Aura render back (loopback), every read-only tool
/// runs, and the brain is asked a tool-using question if a key exists.
/// Writes selftest.log + WAVs to outDir.
/// </summary>
internal static class JarvisSelfTest
{
    private const string Phrase = "Good evening, Vivek. Port three thousand is free, and all systems are running.";

    public static async Task RunAsync(string outDir, bool phase2Only = false)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        void Log(string s) { log.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "selftest.log"), log.ToString()); }

        var cfg = Config.Load().Jarvis;
        Log($"keys: deepgram={Has("deepgram")} groq={Has("groq")} gemini={Has("gemini")}");
        if (phase2Only)
        {
            await Phase2Async(cfg, Log);
            Log("done");
            return;
        }

        // ---- sentence splitter ----
        var parts = new List<string>();
        var sp = new SentenceSplitter(parts.Add);
        foreach (var tok in new[] { "Port **3000** is ", "held by node. ", "Shall I kill it? It's", " version 3.5 of the thing." }) sp.Push(tok);
        sp.Flush();
        Log("splitter: " + string.Join(" | ", parts));

        // ---- TTS engines ----
        byte[]? auraPcm = null;
        foreach (var (name, make) in new (string, Func<ITextToSpeech?>)[]
                 {
                     ("aura", () => SecretStore.Get("deepgram") is { } k ? new DeepgramAuraTts(k, cfg.AuraVoice) : null),
                     ("piper", () => LocalTts.Find("piper", cfg.PiperVoice) is { IsInstalled: true } v ? new LocalTts(v) : null),
                     ("kokoro", () => LocalTts.Find("kokoro", cfg.KokoroVoice) is { IsInstalled: true } v ? new LocalTts(v) : null),
                     ("windows", () => new WindowsTts()),
                 })
        {
            var tts = make();
            if (tts is null) { Log($"tts {name}: SKIPPED (not configured)"); continue; }
            try
            {
                var sw = Stopwatch.StartNew();
                long firstMs = -1;
                var ms = new MemoryStream();
                await tts.SpeakAsync(Phrase, pcm => { if (firstMs < 0) firstMs = sw.ElapsedMilliseconds; ms.Write(pcm); }, CancellationToken.None);
                var pcmAll = ms.ToArray();
                double seconds = pcmAll.Length / 2.0 / AudioPlayer.SampleRate;
                Log($"tts {name}: OK first-audio {firstMs}ms, total {sw.ElapsedMilliseconds}ms, {seconds:0.0}s of audio, peak level {Pcm.Rms(pcmAll, pcmAll.Length):0.00}");
                using (var w = new WaveFileWriter(Path.Combine(outDir, $"tts-{name}.wav"), new WaveFormat(AudioPlayer.SampleRate, 16, 1)))
                    w.Write(pcmAll, 0, pcmAll.Length);
                if (name == "aura") auraPcm = pcmAll;
                if (name is "piper" or "kokoro" or "aura")
                {
                    sw.Restart();
                    firstMs = -1;
                    await tts.SpeakAsync("Right away, Vivek.", _ => { if (firstMs < 0) firstMs = sw.ElapsedMilliseconds; }, CancellationToken.None);
                    Log($"tts {name} warm sentence: first-audio {firstMs}ms, total {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception ex) { Log($"tts {name}: FAIL {ex.Message}"); }
        }

        // ---- STT loopback: stream Aura's render into Deepgram as if it were the mic ----
        if (auraPcm != null && SecretStore.Get("deepgram") is { } dgKey)
        {
            var pcm16k = Resample(auraPcm, AudioPlayer.SampleRate, MicCapture.SampleRate);
            foreach (int endpointing in new[] { 400, 600, 800 })
            {
                try
                {
                    var stt = new DeepgramStt();
                    var done = new TaskCompletionSource<string>();
                    var utterances = new List<string>();
                    var sw = Stopwatch.StartNew();
                    long lastAudio = 0;
                    stt.Utterance += u => { lock (utterances) utterances.Add($"{u} (+{sw.ElapsedMilliseconds - Volatile.Read(ref lastAudio)}ms)"); };
                    stt.Failed += f => done.TrySetResult("FAILED: " + f);
                    await stt.ConnectAsync(dgKey, cfg.SttModel, cfg.SttLanguage, MicCapture.SampleRate, endpointing, CancellationToken.None);
                    long connectMs = sw.ElapsedMilliseconds;
                    int chunk = MicCapture.SampleRate / 20 * 2; // 50ms
                    // Trim Aura's trailing silence so "end of speech" is the end of the buffer.
                    int end = pcm16k.Length;
                    while (end > chunk && Pcm.Rms(pcm16k[(end - chunk)..end], chunk) < 0.02) end -= chunk;
                    for (int i = 0; i < end; i += chunk)
                    {
                        stt.Send(pcm16k[i..Math.Min(end, i + chunk)]);
                        await Task.Delay(50); // real time, like a mic
                    }
                    Volatile.Write(ref lastAudio, sw.ElapsedMilliseconds);
                    var silence = new byte[chunk];
                    for (int i = 0; i < 50; i++) { stt.Send(silence); await Task.Delay(50); }
                    lock (utterances)
                        Log($"stt endpointing={endpointing}: connect {connectMs}ms, utterances: {(utterances.Count == 0 ? "(none)" : string.Join(" || ", utterances))}");
                    await stt.DisposeAsync();
                }
                catch (Exception ex) { Log($"stt endpointing={endpointing}: FAIL " + ex.Message); }
            }
        }

        // ---- tools ----
        var tools = BuiltInTools.Create(Config.Load(), () => Array.Empty<IDevBarModule>(), new ProviderRouter(() => cfg.Vision));
        foreach (var (tool, args) in new[] { ("list_ports", "{}"), ("docker_list", "{}"), ("git_status", "{}"), ("claude_sessions", "{}"), ("media", "{\"action\":\"now_playing\"}"), ("system_volume", "{\"action\":\"get\"}") })
        {
            try
            {
                var t = tools.First(x => x.Name == tool);
                var r = await t.RunAsync(JsonDocument.Parse(args).RootElement);
                Log($"tool {tool}: {(r.Length > 200 ? r[..200] + "…" : r)}");
            }
            catch (Exception ex) { Log($"tool {tool}: FAIL {ex.Message}"); }
        }
        var kill = tools.First(t => t.Name == "kill_port");
        Log($"kill_port risk={kill.RiskOf(JsonDocument.Parse("{\"port\":3000}").RootElement)} describe=\"{kill.Describe(JsonDocument.Parse("{\"port\":3000}").RootElement)}\"");
        Log("schema sample: " + tools.First(t => t.Name == "docker_container").Schema().ToJsonString());

        // ---- brain ----
        foreach (var spec in cfg.Llm)
        {
            var llm = OpenAiCompatibleLlm.Parse(spec);
            if (llm is null || !llm.HasKey) { Log($"llm {spec}: SKIPPED (no key)"); continue; }
            try
            {
                var messages = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = "You are Jarvis, a concise voice assistant. Use tools when needed." },
                    new JsonObject { ["role"] = "user", ["content"] = "What's running on my ports right now?" },
                };
                var schemas = new JsonArray(tools.Select(t => (JsonNode)t.Schema()).ToArray());
                var sw = Stopwatch.StartNew();
                long first = -1;
                var reply = await llm.ChatAsync(messages, schemas, _ => { if (first < 0) first = sw.ElapsedMilliseconds; }, CancellationToken.None);
                Log($"llm {spec}: first-token {first}ms, total {sw.ElapsedMilliseconds}ms, prompt {reply.PromptTokens} tok, tool calls: {string.Join(", ", reply.ToolCalls.Select(c => c.Name + c.ArgumentsJson))}, text: \"{reply.Text}\"");

                if (reply.ToolCalls.Count > 0)
                {
                    var mem = new ConversationMemory();
                    mem.AddUser("What's running on my ports right now?");
                    mem.AddAssistantToolCalls(reply.Text, reply.ToolCalls);
                    foreach (var c in reply.ToolCalls)
                        mem.AddToolResult(c.Id, await tools.First(t => t.Name == c.Name).RunAsync(JsonDocument.Parse(c.ArgumentsJson).RootElement));
                    sw.Restart();
                    first = -1;
                    var final = await llm.ChatAsync(mem.BuildMessages("You are Jarvis, a concise voice assistant. Reply in one or two spoken sentences."),
                        schemas, _ => { if (first < 0) first = sw.ElapsedMilliseconds; }, CancellationToken.None);
                    Log($"llm {spec} follow-up: first token {first}ms, total {sw.ElapsedMilliseconds}ms: \"{final.Text}\"");
                }
            }
            catch (Exception ex) { Log($"llm {spec}: FAIL {ex.Message}"); }
        }

        Log("done");
    }

    private static bool Has(string k) => SecretStore.Has(k);

    /// <summary>
    /// Phase 2 checks. Deliberately never sends the real screen anywhere (a
    /// synthetic image is used) and never writes to real memory except one
    /// clearly-marked fact that is deleted again.
    /// </summary>
    private static async Task Phase2Async(JarvisConfig cfg, Action<string> Log)
    {
        var sw = Stopwatch.StartNew();
        var place = await LocationService.GetAsync(cfg);
        Log($"location: {(place is null ? "(none)" : $"{place.Describe()} via {place.Source}")} in {sw.ElapsedMilliseconds}ms");

        var tools = BuiltInTools.Create(Config.Load(), () => Array.Empty<IDevBarModule>(), new ProviderRouter(() => cfg.Vision));
        async Task Run(string name, string args)
        {
            try
            {
                var t = tools.First(x => x.Name == name);
                var r = await t.RunAsync(JsonDocument.Parse(args).RootElement);
                Log($"tool {name} {args}: {(r.Length > 260 ? r[..260] + "..." : r)}");
            }
            catch (Exception ex) { Log($"tool {name}: FAIL {ex.Message}"); }
        }
        await Run("weather", "{}");
        await Run("weather", "{\"place\":\"London, UK\"}");
        await Run("system_status", "{}");
        await Run("list_reminders", "{}");

        // memory round trip on one marked fact
        long id = MemoryStore.AddFact("SELFTEST: Vivek enjoys writing self-tests.", "told");
        bool found = MemoryStore.Search("self-tests").Any(f => f.Id == id);
        MemoryStore.DeleteFact(id);
        Log($"memory: add/search/delete ok={found && !MemoryStore.Facts().Any(f => f.Id == id)}");
        Log($"keepable: plain={ProfileLearner.IsKeepable("Vivek lives in Pune.")} key={ProfileLearner.IsKeepable("My groq key is gsk_abcdefghijklmnopqrstuvwxyz123456")} pw={ProfileLearner.IsKeepable("Vivek's password is hunter2")}");

        // run_command guard rails (the forbidden check happens before anything runs)
        var runCmd = tools.First(t => t.Name == "run_command");
        Log($"run_command risk={runCmd.RiskOf(JsonDocument.Parse("{}").RootElement)}");
        await Run("run_command", "{\"command\":\"Format-Volume C:\"}");
        await Run("run_command", "{\"command\":\"git --version\"}");

        // barge-in echo filter
        var saying = "Port fifty five thousand two hundred eighty eight is being used by a Node dev server.";
        Log($"echo filter: own-voice={JarvisSession.IsRealSpeech("is being used by a node dev server", saying)} " +
            $"real={JarvisSession.IsRealSpeech("wait stop that open chrome instead", saying)} " +
            $"one-word={JarvisSession.IsRealSpeech("stop", saying)}");

        // learner proposal on a synthetic conversation (not applied)
        try
        {
            var learner = new ProviderRouter(() => new List<string> { "groq:openai/gpt-oss-20b", "gemini:gemini-3.5-flash-lite" });
            var lines = new List<(string, string)>
            {
                ("user", "Morning. I'm heading into a long day on the ClientPulse dashboard, it's a Next.js app with Postgres."),
                ("assistant", "Good morning. Shall I start the Postgres container?"),
                ("user", "Yeah. Also I prefer short answers, and my API key is gsk_notarealkeyatall1234567890abc."),
                ("user", "Open Chrome."),
            };
            var existing = new List<Fact> { new(1, "Vivek lives in Pune.", "told", DateTime.Now) };
            sw.Restart();
            var (json, provider) = await ProfileLearner.ProposeAsync(learner, "Vivek", lines, existing);
            Log($"learner ({provider}, {sw.ElapsedMilliseconds}ms): {json.Replace('\n', ' ')}");
        }
        catch (Exception ex) { Log("learner: FAIL " + ex.Message); }

        // vision on a synthetic screenshot
        try
        {
            using var bmp = new System.Drawing.Bitmap(900, 220);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.FromArgb(30, 30, 30));
                using var font = new System.Drawing.Font("Consolas", 16);
                g.DrawString("Program.cs(12,17): error CS0246: The type or namespace name", font, System.Drawing.Brushes.OrangeRed, 20, 40);
                g.DrawString("'HttpClinet' could not be found (are you missing a using directive?)", font, System.Drawing.Brushes.OrangeRed, 20, 75);
                g.DrawString("Build FAILED.  1 Error(s)", font, System.Drawing.Brushes.White, 20, 140);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
            sw.Restart();
            var answer = await ScreenVision.AskImageAsync(new ProviderRouter(() => cfg.Vision), ms.ToArray(), "a terminal",
                "What's the error, where is it, and what's the likely fix?", CancellationToken.None);
            Log($"vision ({sw.ElapsedMilliseconds}ms): {answer.Replace('\n', ' ')}");
        }
        catch (Exception ex) { Log("vision: FAIL " + ex.Message); }

        // prompt size with the full Phase 2 tool set — the free tier's per-minute token budget
        try
        {
            var llm = OpenAiCompatibleLlm.Parse(cfg.Llm[0])!;
            var schemas = new JsonArray(tools.Select(t => (JsonNode)t.Schema()).ToArray());
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are Jarvis. Reply briefly." },
                new JsonObject { ["role"] = "user", ["content"] = "What's the weather like?" },
            };
            sw.Restart();
            var reply = await llm.ChatAsync(messages, schemas, _ => { }, CancellationToken.None);
            Log($"brain with {tools.Count} tools: prompt {reply.PromptTokens} tok, {sw.ElapsedMilliseconds}ms, calls: {string.Join(", ", reply.ToolCalls.Select(c => c.Name + c.ArgumentsJson))}");
        }
        catch (Exception ex) { Log("brain: FAIL " + ex.Message); }
    }

    private static byte[] Resample(byte[] pcm16, int from, int to)
    {
        var src = new RawSourceWaveStream(new MemoryStream(pcm16), new WaveFormat(from, 16, 1));
        var resampled = new WdlResamplingSampleProvider(src.ToSampleProvider(), to).ToWaveProvider16();
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = resampled.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }
}
