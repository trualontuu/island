using System.Linq;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Island.Services;

namespace Island.UI;

public partial class IslandWindow
{
    async Task RunSnap(string dir)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        try
        {
            _media.Changed += OnMediaChanged;
            await _media.InitAsync();
            _agent = new AgentServer(47399, Dispatcher);
            _agent.Changed += OnAgentChanged;
            log.AppendLine("agent server: " + _agent.Start());

            try
            {
                var mg = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                foreach (var ss in mg.GetSessions())
                {
                    var pp = await ss.TryGetMediaPropertiesAsync();
                    string dims = "нет";
                    if (pp.Thumbnail != null)
                    {
                        using var rs = await pp.Thumbnail.OpenReadAsync();
                        var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(rs), System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation, System.Windows.Media.Imaging.BitmapCacheOption.None);
                        dims = $"{dec.Frames[0].PixelWidth}x{dec.Frames[0].PixelHeight}";
                    }
                    log.AppendLine($"smtc: {ss.SourceAppUserModelId} thumb={dims} type={pp.PlaybackType} album='{pp.AlbumTitle}' tracks={pp.AlbumTrackCount} genres={pp.Genres.Count} status={ss.GetPlaybackInfo().PlaybackStatus} title='{pp.Title}' artist='{pp.Artist}'");
                }
            }
            catch (Exception ex) { log.AppendLine("smtc err " + ex.Message); }
            await Task.Delay(600);
            Snap(dir, "0_compact");
            Toast("Готово — картинка уже в буфере", "\uE8C8", Mood.Happy, 10);
            await Task.Delay(700);
            Snap(dir, "1_toast");
            _toastText = null;

            // замер плавности: интервалы кадров в первые 300 мс каждого перехода
            {
                var wall = System.Diagnostics.Stopwatch.StartNew();
                var cur = new System.Collections.Generic.List<double>();
                double lw = 0, start = 0; bool on = false;
                EventHandler h = (_, _) => { double n = wall.Elapsed.TotalMilliseconds; if (on && n - start < 300) { cur.Add(n - lw); } lw = n; };
                CompositionTarget.Rendering += h;
                var all = new System.Collections.Generic.List<double>();
                async Task Step(string name, Action act)
                {
                    cur.Clear(); start = lw = wall.Elapsed.TotalMilliseconds; on = true;
                    var t0 = wall.Elapsed.TotalMilliseconds; act(); var tAct = wall.Elapsed.TotalMilliseconds - t0;
                    await Task.Delay(450); on = false;
                    all.AddRange(cur);
                    log.AppendLine($"  {name,-16} call={tAct:F1}ms frames={cur.Count} max={(cur.Count > 0 ? cur.Max() : 0):F1} >8ms={cur.Count(x => x > 8)} first3=[{string.Join(", ", cur.Take(3).Select(x => x.ToString("F1")))}]");
                }
                await Step("expand home", () => Expand("home"));
                foreach (var pg in new[] { "sound", "control", "home", "shelf", "chat", "home" }) await Step("→ " + pg, () => Expand(pg));
                await Step("collapse", Collapse);
                await Step("expand home", () => Expand("home"));
                CompositionTarget.Rendering -= h;
                all.Sort();
                log.AppendLine($"frames ({(_s.SoftwareRendering ? "software" : "GPU")}): n={all.Count} p50={all[all.Count / 2]:F2} p95={all[(int)(all.Count * 0.95)]:F2} max={all[^1]:F1} >8ms={all.Count(x => x > 8)}");
                Collapse(); await Task.Delay(500);
            }

            foreach (var p in new[] { "home", "control", "sound", "shelf" })
            {
                Expand(p);
                await Task.Delay(p == "control" ? 6000 : 1200);
                Snap(dir, "2_" + p);
                if (p == "sound")
                {
                    var mx = new System.Collections.Generic.Dictionary<string, float>();
                    for (int i = 0; i < 30; i++)
                    {
                        foreach (var a in _mixer.Apps)
                            foreach (var ss in a.Sessions)
                            {
                                string k = $"{a.Name} pid={a.Key} sess='{ss.DisplayName}' state={ss.State}";
                                float v = 0; try { v = ss.AudioMeterInformation.MasterPeakValue; } catch { }
                                mx[k] = Math.Max(mx.TryGetValue(k, out var o) ? o : 0, v);
                            }
                        await Task.Delay(100);
                    }
                    foreach (var kv in mx) log.AppendLine($"peak {kv.Value:F3}  {kv.Key}");
                    var seq = new System.Text.StringBuilder();
                    for (int i = 0; i < 15; i++)
                    {
                        seq.Append(i == 0 ? "" : " | ").Append($"m{_mixer.SamplePeaks():F3}");
                        foreach (var a in _mixer.Apps) if (!a.IsSystem) seq.Append($" {a.Name[..3]}{a.Peak:F3}/{a.ShownPeak:F3}");
                        await Task.Delay(60);
                    }
                    log.AppendLine("seq: " + seq);
                    var pr = System.Diagnostics.Process.GetCurrentProcess();
                    var t0 = pr.TotalProcessorTime; var sw = System.Diagnostics.Stopwatch.StartNew();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"sound tab cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}% of all cores");
                    Collapse(); await Task.Delay(1500);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"collapsed cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%");
                    OpenFlyout(); await Task.Delay(800);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(4000);
                    pr.Refresh();
                    log.AppendLine($"flyout cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F2}%");
                    OpenFlyout();
                    await Task.Delay(500);
                    HideIsland(); await Task.Delay(1500);
                    t0 = pr.TotalProcessorTime; sw.Restart();
                    await Task.Delay(6000);
                    pr.Refresh();
                    log.AppendLine($"hidden cpu: {(pr.TotalProcessorTime - t0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100:F3}% (в тесте окно за экраном, WPF всё время переотправляет кадр — в реальном запуске 0%)  visible={IsVisible}");
                    ShowIsland(); await Task.Delay(600);
                    log.AppendLine($"after show: visible={IsVisible}");
                    // спящий режим: «мышь не двигалась 20 с» → уезжает и прячется; уведомление и движение будят
                    Island.Core.Native.GetCursorPos(out _lastCursor); _lastMove = Environment.TickCount64 - SleepAfterMs - 1;
                    IdleTick(); await Task.Delay(700);
                    log.AppendLine($"sleep: asleep={_asleep} visible={IsVisible} y={ShellSlide.Y:0} op={Shell.Opacity:0.00}");
                    Toast("Будильник", null, Mood.Happy, 0.6); await Task.Delay(300);
                    log.AppendLine($"toast wake: asleep={_asleep} visible={IsVisible}");
                    await Task.Delay(1300); IdleTick(); await Task.Delay(700);
                    log.AppendLine($"re-sleep: asleep={_asleep} visible={IsVisible}");
                    _lastCursor.X += 5; IdleTick(); await Task.Delay(700);
                    log.AppendLine($"move wake: asleep={_asleep} visible={IsVisible} y={ShellSlide.Y:0} op={Shell.Opacity:0.00}");
                }
            }

            if (Environment.GetEnvironmentVariable("ISLAND_CHAT_TEST") == "1")
            {
                var keep = (_s.Provider, _s.Ai.TryGetValue("custom", out var kc) ? kc : null, _s.Ai.TryGetValue("openrouter", out var ko) ? ko : null);
                try
                {
                    _s.Provider = "custom";
                    _s.Ai["custom"] = new Island.Core.AiProvider { Key = "test-key", BaseUrl = "http://127.0.0.1:8799/v1", Model = "mock" };
                    var streamed = new StringBuilder();
                    var r1 = await _chat.SendAsync("первый", t => streamed.Append(t));
                    var r2 = await _chat.SendAsync("второй", _ => { });
                    log.AppendLine($"chat mock: r1='{r1}' streamed='{streamed}' r2='{r2}'");
                    _chat.Reset();
                    _s.Provider = "openrouter";
                    _s.Ai["openrouter"] = new Island.Core.AiProvider { Key = "test-key", BaseUrl = "http://127.0.0.1:8799/v1", Model = "dead:free" };
                    var r3 = await _chat.SendAsync("фолбэк", _ => { });
                    log.AppendLine($"chat fallback: r3='{r3}'");
                    _chat.Reset();
                    var mig = new Island.Core.Settings(); mig.Ai["openrouter"] = new Island.Core.AiProvider { Model = "deepseek/deepseek-v4-flash:free" };
                    log.AppendLine("migrated model: " + mig.AiFor("openrouter").Model);
                    _s.Provider = "openrouter";
                    _s.Ai["openrouter"] = new Island.Core.AiProvider { Key = "sk-or-invalid-test", BaseUrl = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4-flash:free" };
                    try { await _chat.SendAsync("тест", _ => { }); log.AppendLine("openrouter: неожиданно ответил"); }
                    catch (Exception ex) { log.AppendLine("openrouter bad key: " + ex.Message); }
                }
                catch (Exception ex) { log.AppendLine("chat test error: " + ex); }
                finally
                {
                    _s.Provider = keep.Item1;
                    if (keep.Item2 != null) _s.Ai["custom"] = keep.Item2; else _s.Ai.Remove("custom");
                    if (keep.Item3 != null) _s.Ai["openrouter"] = keep.Item3; else _s.Ai.Remove("openrouter");
                    _chat.Reset();
                }
            }

            Expand("chat");
            ChatList.Children.Remove(ChatHint);
            AddBubble("Сколько будет 27 × 453?", true);
            AddBubble("27 × 453 = 12 231", false);
            await Task.Delay(900);
            Snap(dir, "3_chat");
            Collapse();
            await Task.Delay(1200);
            Snap(dir, "3b_compact_" + (_media.Info.IsPlaying ? "media" : "idle"));
            log.AppendLine($"media: '{_media.Info.Title}' / {_media.Info.App}, cover: {(_media.Info.Cover != null ? "есть" : "нет")}");

            // сквозная проверка: «Claude Code» шлёт запрос разрешения → остров раскрывается → отвечаем «Разрешить»
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var ev = "{\"hook_event_name\":\"PreToolUse\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:\\\\proj\\\\tg-bot\\\\bot.py\"}}";
            await http.PostAsync("http://127.0.0.1:47399/claude/event", new StringContent(ev));
            await Task.Delay(800);
            Snap(dir, "4_agent_working");

            var perm = "{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git push origin main\"}}";
            var permTask = http.PostAsync("http://127.0.0.1:47399/claude/permission", new StringContent(perm));
            await Task.Delay(1500);
            Snap(dir, "5_agent_permission");
            _agent.Answer("allow");
            var resp = await (await permTask).Content.ReadAsStringAsync();
            log.AppendLine("permission response: " + resp);

            var q = "{\"hook_event_name\":\"PermissionRequest\",\"cwd\":\"C:\\\\proj\\\\tg-bot\",\"tool_name\":\"AskUserQuestion\",\"tool_input\":{\"questions\":[{\"question\":\"Какую тему сделать по умолчанию?\",\"header\":\"Тема\",\"multiSelect\":false,\"options\":[{\"label\":\"Тёмная\",\"description\":\"\"},{\"label\":\"Светлая\",\"description\":\"\"}]}]}}";
            var qTask = http.PostAsync("http://127.0.0.1:47399/claude/permission", new StringContent(q));
            await Task.Delay(1500);
            Snap(dir, "6_agent_question");
            _agent.Answer("Тёмная");
            log.AppendLine("question response: " + await (await qTask).Content.ReadAsStringAsync());

            await http.PostAsync("http://127.0.0.1:47399/claude/event", new StringContent("{\"hook_event_name\":\"Stop\",\"cwd\":\"C:\\\\proj\\\\tg-bot\"}"));
            await Task.Delay(1200);
            Snap(dir, "7_agent_done");

            var proc = System.Diagnostics.Process.GetCurrentProcess();
            log.AppendLine($"memory: WS={proc.WorkingSet64 / 1048576}MB private={proc.PrivateMemorySize64 / 1048576}MB");
        }
        catch (Exception ex) { log.AppendLine("ERROR: " + ex); }
        File.WriteAllText(Path.Combine(dir, "log.txt"), log.ToString());
        Application.Current.Shutdown();
    }

    void Snap(string dir, string name)
    {
        Root.UpdateLayout();
        int w = (int)Math.Ceiling(Root.ActualWidth), h = (int)Math.Ceiling(Root.ActualHeight);
        if (w < 2 || h < 2) return;
        var rtb = new RenderTargetBitmap(w * 2, h * 2, 192, 192, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (Environment.GetEnvironmentVariable("ISLAND_SNAP_CLEAR") != "1") dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(90, 120, 150)), null, new Rect(0, 0, w, h)); // «обои», чтобы видеть края
            dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Top, Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute }, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(Path.Combine(dir, name + ".png"));
        enc.Save(fs);
    }
}
