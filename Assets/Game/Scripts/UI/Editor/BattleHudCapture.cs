using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using PokeLab.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI.Editor
{
    /// <summary>
    /// Photographs the battle HUD, in the states a battle actually puts it in, without
    /// entering play mode and without needing a second player.
    ///
    /// <b>Why it had to exist.</b> The turn clock only ever appears in a PvP match, and a PvP
    /// match needs two live clients — so the one screen that most needed looking at was the
    /// one screen no capture in this project could reach. Reasoning about anchors instead is
    /// how a label ends up culled or a panel ends up overlapping another: the arithmetic
    /// always says it fits.
    ///
    /// <b>How.</b> The same arrangement <see cref="UiOverlayCapture"/> uses for the dialogue
    /// box — a throwaway canvas driven by its own camera, which is the only setup a
    /// RenderTexture can see — but building <see cref="BattleHudView"/> through its own
    /// <c>BuildRuntime</c>, so what is photographed is the shipping layout code and not a
    /// mock-up of it.
    ///
    /// <b>Run it at more than one size.</b> Every element here is pinned to a corner at a
    /// fixed pixel size, which means the failure mode is not stretching — it is two panels
    /// meeting in the middle at an aspect nobody checked. The default sweep is 16:9, 21:9,
    /// 4:3 and a small 16:9, because those are the four shapes that move the corners around.
    ///
    /// Needs a graphics device: run with <c>-batchmode</c> but WITHOUT <c>-nographics</c>,
    /// or the render target is never written and every frame comes back black.
    /// </summary>
    public static class BattleHudCapture
    {
        /// <summary>The shapes worth checking. Wide, ultrawide, boxy, and small.</summary>
        private static readonly (string Name, int W, int H)[] Sizes =
        {
            ("16x9", 1920, 1080),
            ("21x9", 2560, 1080),
            ("4x3", 1440, 1080),
            ("small", 1280, 720),
        };

        [MenuItem("Tools/Poké Lab/Diagnostics/Capture Battle HUD", priority = 901)]
        public static void Run()
        {
            var directory = Path.Combine(Directory.GetCurrentDirectory(), "Captures", "hud");
            Directory.CreateDirectory(directory);

            var motionWas = UiTween.MotionEnabled;
            UiTween.MotionEnabled = false;

            var written = new List<string>();
            try
            {
                foreach (var (name, w, h) in Sizes)
                    foreach (var state in States())
                        written.Add(Capture(state, $"{state.Key}_{name}", directory, w, h));
            }
            finally
            {
                UiTween.MotionEnabled = motionWas;
            }

            Debug.Log($"[HudCapture] wrote {written.Count} frame(s) to {directory}");
        }

        /// <summary>
        /// What to put the HUD into, and what to do to it once it is built.
        ///
        /// Deliberately includes the states that only a PvP match produces AND the ordinary
        /// one, because the clock being invisible outside a match is as much a claim as the
        /// clock being legible inside one.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, Action<BattleHudView>>> States()
        {
            yield return Pair("01_ai_no_clock", hud =>
            {
                Stage(hud);
                hud.BeginPlayerTurn(Mine());
                Flush(hud);
            });

            yield return Pair("02_pvp_full", hud =>
            {
                Stage(hud);
                hud.BeginPlayerTurn(Mine());
                Flush(hud);
                hud.BeginTurnClock(30f);
            });

            yield return Pair("03_pvp_urgent", hud =>
            {
                Stage(hud);
                hud.BeginPlayerTurn(Mine());
                Flush(hud);
                hud.BeginTurnClock(30f);
                // Straight to the last few seconds: the red, flashing, single-digit state is
                // the one whose count could outgrow its row.
                Paint(hud, 0.13f);
            });

            yield return Pair("04_pvp_waiting", hud =>
            {
                Stage(hud);
                hud.BeginPlayerTurn(Mine());
                Flush(hud);
                hud.BeginTurnClock(30f);
                hud.LockCommands();
                hud.BeginOpponentWait();
            });

            yield return Pair("05_pvp_forced_switch", hud =>
            {
                Stage(hud);
                var party = Party();
                hud.BeginTurnClock(30f);
                Paint(hud, 0.45f);
                hud.Log?.Append(Loc.Pick("Out of time — sending out the next Pokémon.",
                                         "시간이 다 됐다! 다음 포켓몬이 나간다."));
                hud.OpenPartyPicker(party, 0, true);
                Flush(hud);
            });

            yield return Pair("06_log_longest_lines", hud =>
            {
                Stage(hud);
                hud.BeginPlayerTurn(Mine());
                hud.BeginTurnClock(30f);
                // Every line this change added, longest first. The log box is three lines
                // tall, so what this is really asking is whether any of them wraps to four.
                hud.Log?.Append(BattleLogStrings.Hesitated("파이어로"));
                hud.Log?.Append(Loc.Pick("Out of time — the turn was lost.",
                                         "시간이 다 됐다! 이번 턴을 놓쳤다."));
                hud.Log?.Append(PvpFailureLine());
                Flush(hud);
            });
        }

        /// <summary>
        /// The longest thing the exchange can say. Reproduced rather than called, because
        /// <c>PvpTurnBroker</c> lives in PokeLab.Boot and this assembly does not see it —
        /// which is exactly why it is worth photographing rather than assuming.
        /// </summary>
        private static string PvpFailureLine() =>
            Loc.Pick("The two battles fell out of step, so the match was stopped.",
                     "양쪽 대전이 어긋나서 대전을 중단했어요.");

/// <summary>
        /// Stands both sides on the field through the real event stream.
        ///
        /// The opponent's plate is bound by <c>CreatureSentOutEvent</c> and by nothing else --
        /// <c>BeginPlayerTurn</c> touches only the near plate -- so a capture that skips this
        /// photographs a HUD with the top-right corner empty, which is precisely the corner
        /// the turn clock had to be checked against.
        /// </summary>
        private static void Stage(BattleHudView hud)
        {
            hud.OnBattleEvent(new BattleStartedEvent { Kind = BattleKind.Trainer });
            hud.OnBattleEvent(new CreatureSentOutEvent
            {
                Side = BattleSide.Player,
                Creature = Mine(),
            });
            hud.OnBattleEvent(new CreatureSentOutEvent
            {
                Side = BattleSide.Opponent,
                // A long name on purpose: the opponent plate is the narrowest thing on screen
                // that carries one, and a name that fits "뚜벅쵸" proves nothing.
                Creature = Make("이상해꽃", 52, 171, 188, "solar-beam"),
            });

            // The start-of-battle banner is held on screen by a Delay, and a delay outlives
            // MotionEnabled = false on purpose -- so in a capture it never times out and lies
            // across the middle of every frame, shading whatever it crosses. By the time a
            // player is choosing a move it is long gone, so it is taken down here rather than
            // photographed over the thing being reviewed.
            if (hud.Overlays != null) hud.Overlays.gameObject.SetActive(false);
        }

        /// <summary>
        /// Reveals every queued line at once.
        ///
        /// The log is a typewriter with a HOLD between lines, and a hold survives
        /// <c>MotionEnabled = false</c> by design -- reduced motion removes motion, not time.
        /// Nothing ticks the tween runner outside play mode, so without this a capture shows
        /// the first line of a three-line narration and looks like a log that lost two.
        /// </summary>
        private static void Flush(BattleHudView hud) => hud.Log?.FlushImmediate();

        private static KeyValuePair<string, Action<BattleHudView>> Pair(
            string key, Action<BattleHudView> apply) =>
            new KeyValuePair<string, Action<BattleHudView>>(key, apply);

        /// <summary>Drives the clock to a given fraction, which only <c>Update</c> does in game.</summary>
        private static void Paint(BattleHudView hud, float remaining)
        {
            var field = typeof(BattleHudView).GetField("_turnClock",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var clock = field?.GetValue(hud) as BattleTurnClockView;
            if (clock == null) return;

            typeof(BattleTurnClockView)
                .GetMethod("Paint", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(clock, new object[] { remaining });
        }

        // ---- stand-ins -------------------------------------------------------------------
        //
        // Nicknamed rather than looked up: UiServices falls back to the species registry and
        // this runs with none registered, but a display name is a display name and a LONG one
        // is the case worth photographing.

        private static CreatureInstance Mine() => Make("파이리", 50, 96, 142,
            "ember", "scratch", "growl", "smokescreen");

        private static IReadOnlyList<CreatureInstance> Party() => new[]
        {
            Make("파이리", 50, 0, 142, "ember"),
            Make("꼬부기", 50, 138, 151, "water-gun"),
            Make("이상해씨", 49, 91, 148, "vine-whip"),
            Make("피카츄", 48, 12, 121, "thunder-shock"),
            Make("뚜벅쵸", 47, 130, 130, "absorb"),
            Make("롱스톤", 51, 0, 160, "rock-throw"),
        };

        private static CreatureInstance Make(string name, int level, int hp, int maxHp,
                                             params string[] moves)
        {
            var creature = new CreatureInstance
            {
                SpeciesId = 4,
                Nickname = name,
                Level = level,
                CurrentHp = hp,
                MaxHp = maxHp,
                InstanceId = "cap-" + name,
                Stats = new int[StatKinds.BaseCount],
                Ivs = new int[StatKinds.BaseCount],
                Moves = new List<MoveSlot>(4),
            };
            for (var i = 0; i < creature.Stats.Length; i++) creature.Stats[i] = 100;
            creature.Stats[(int)StatKind.Hp] = maxHp;

            foreach (var id in moves)
                creature.Moves.Add(new MoveSlot { MoveId = id, CurrentPp = 12, MaxPp = 15 });

            return creature;
        }

        // ---- the camera rig ---------------------------------------------------------------

        private static string Capture(KeyValuePair<string, Action<BattleHudView>> state,
                                      string fileName, string directory, int width, int height)
        {
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
            };
            var readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            var cameraGo = new GameObject("~HudCaptureCamera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            // A mid grey-green rather than black: the panels are dark and translucent, and on
            // black an overflowing edge is invisible for the same reason it is on black.
            camera.backgroundColor = new Color(0.16f, 0.20f, 0.17f, 1f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            camera.targetTexture = rt;

            var canvasGo = new GameObject("~HudCaptureCanvas", typeof(RectTransform))
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, 0);
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 10f;

            try
            {
                var hudGo = new GameObject("BattleHud", typeof(RectTransform));
                hudGo.transform.SetParent(canvasGo.transform, false);
                var hud = hudGo.AddComponent<BattleHudView>();
                hud.BuildRuntime();
                hud.Show();

                state.Value(hud);

                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)canvasGo.transform);
                Canvas.ForceUpdateCanvases();

                // Show() fades in through a tween, and with motion disabled the group lands on
                // its end state — but only once something ticks it. Forced, so a capture never
                // photographs a HUD at alpha zero and reports it as a missing panel.
                foreach (var group in canvasGo.GetComponentsInChildren<CanvasGroup>(true))
                    if (group.gameObject == hudGo) group.alpha = 1f;

                Render(camera, rt);

                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply(false);
                RenderTexture.active = previous;

                var file = Path.Combine(directory, fileName + ".png");
                File.WriteAllBytes(file, readback.EncodeToPNG());
                return file;
            }
            finally
            {
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(canvasGo);
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(readback);
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        private static void Render(Camera camera, RenderTexture target)
        {
            try
            {
                camera.SubmitRenderRequest(
                    new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HudCapture] Render request rejected ({e.GetType().Name}); " +
                                 "falling back to Camera.Render.");
            }

            camera.targetTexture = target;
            camera.Render();
        }
    }
}
