using System.Text;
using PokeLab.Battle;
using PokeLab.Cinematics;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot
{
    /// <summary>
    /// Draws what the game is currently doing into the corner of the frame.
    ///
    /// This exists because of a specific, repeated waste: a capture comes back and the first
    /// question is "what am I looking at?". A frame of the route could be the overworld, the
    /// battle arena dressed as a route, or a streamed band drawn over the wrong one — they are
    /// hard to tell apart by eye, and guessing wrong sends the next hour after the wrong bug.
    /// One frame of this session was read as the opening and was actually a battle.
    ///
    /// So the frame says so itself. Scene, mode, episode beat, dialogue speaker, battle phase,
    /// player position — enough that a screenshot is evidence about a known state rather than
    /// a picture that has to be interpreted.
    ///
    /// Drawn with IMGUI rather than a Canvas on purpose: it must not need the UI assemblies,
    /// must not care whether a canvas survived a scene load, and must draw over everything
    /// including the screen wipe. <c>ScreenCapture.CaptureScreenshot</c> takes the composited
    /// game view, so IMGUI lands in the file.
    ///
    /// Off unless asked for. The probe writes <c>Temp/pokelab_stamp.txt</c> before a run, and
    /// <see cref="Enabled"/> can be set directly from a menu item or the console.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CaptureStateStamp : MonoBehaviour
    {
        /// <summary>Drawn only when this is true. The probe turns it on for a capture run.</summary>
        public static bool Enabled { get; set; }

        private const int Margin = 10;

        private GUIStyle _style;
        private Texture2D _backdrop;
        private readonly StringBuilder _text = new StringBuilder(512);

        private GameFlowController _flow;
        private EpisodeRunner _episodes;
        private DialogueRunner _dialogue;
        private PlayerLocomotion _player;

        /// <summary>
        /// Installs itself when a probe run has armed the stamp.
        ///
        /// Runtime rather than authored into the scenes, for two reasons. It has to work
        /// without a level rebuild — a debug tool that needs the thing it is debugging to be
        /// regenerated first is no use during a hunt. And it must survive the battle scene,
        /// the streamed bands and every transition, which a component owned by one scene does
        /// not; <c>DontDestroyOnLoad</c> does.
        ///
        /// The marker file is written by <c>PlayModeProbe</c> before it presses Play. A normal
        /// session has no file, installs nothing, and pays nothing.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (!Enabled)
            {
                // The marker is a disk convention, and a web player has no disk to hold it.
                //
                // Emscripten's in-memory filesystem has no project folder and no Temp, so the
                // probe that arms this stamp cannot have written anything a browser could read.
                // Left in, this would run a File.Exists against a path that cannot exist on the
                // first frame of every page load — silently false at best, and at worst an
                // exception out of a RuntimeInitializeOnLoadMethod, which runs before any scene
                // script and would fail the whole load rather than one debug overlay.
                //
                // Enabled is left alone rather than forced false: a web build has no console to
                // set it from, but if something ever does, the stamp should still draw.
#if UNITY_WEBGL && !UNITY_EDITOR
                return;
#else
                var marker = System.IO.Path.Combine(
                    System.IO.Directory.GetCurrentDirectory(), "Temp", "pokelab_stamp.txt");
                if (!System.IO.File.Exists(marker)) return;
                Enabled = true;
#endif
            }

            if (FindFirstObjectByType<CaptureStateStamp>() != null) return;

            var host = new GameObject("~CaptureStateStamp");
            host.AddComponent<CaptureStateStamp>();
            DontDestroyOnLoad(host);
        }

        private void OnGUI()
        {
            if (!Enabled) return;

            EnsureStyle();
            Compose();

            var content = new GUIContent(_text.ToString());
            var size = _style.CalcSize(content);
            var box = new Rect(Margin, Margin, size.x + 16f, size.y + 12f);

            GUI.DrawTexture(box, _backdrop);
            GUI.Label(new Rect(box.x + 8f, box.y + 6f, size.x, size.y), content, _style);
        }

        private void EnsureStyle()
        {
            if (_style != null) return;

            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                richText = false,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
            };
            _style.normal.textColor = new Color(0.92f, 0.97f, 1f);

            // Near-opaque, because this sits over bright grass as often as over a dark cave and
            // a label that is only legible on one of them is not a label.
            _backdrop = new Texture2D(1, 1);
            _backdrop.SetPixel(0, 0, new Color(0.02f, 0.03f, 0.06f, 0.86f));
            _backdrop.Apply();
        }

        private void Compose()
        {
            _text.Clear();

            _text.Append("SCENES  ");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                if (i > 0) _text.Append(" + ");
                _text.Append(scene.name);
                if (scene == SceneManager.GetActiveScene()) _text.Append('*');
            }
            _text.Append('\n');

            Rebind();

            _text.Append("MODE    ").Append(_flow != null ? _flow.Mode.ToString() : "no flow");
            if (_flow != null && _flow.IsInEncounterSequence) _text.Append("  (encounter running)");
            _text.Append('\n');

            _text.Append("EPISODE ");
            if (_episodes == null) _text.Append("no runner");
            else if (!_episodes.IsPlaying) _text.Append("idle");
            else
            {
                _text.Append(_episodes.PlayingEpisodeId ?? "?")
                     .Append("  beat=").Append(_episodes.CurrentBeat ?? "?");
                if (_episodes.IsAwaitingName) _text.Append("  [awaiting name]");
                if (_episodes.IsAwaitingStarter) _text.Append("  [awaiting starter]");
            }
            // Outside the IsPlaying branch on purpose: a stuck typing latch matters most when
            // no episode is running, because then nothing on screen explains why the space bar
            // has stopped working. It is the one piece of state that silently disables the
            // button the whole game is advanced with.
            if (PokeLab.Core.TextEntry.IsTyping)
            {
                _text.Append("  [TYPING — space will not advance]");
            }
            _text.Append('\n');

            _text.Append("DIALOGUE ");
            if (_dialogue == null) _text.Append("no runner");
            else if (!_dialogue.IsPlaying) _text.Append("closed");
            else
            {
                // DialogueLine is a value type, so there is nothing to null-check — an unset
                // line is one with empty fields, which is what these read as.
                var line = _dialogue.CurrentLine;
                _text.Append("open  speaker=")
                     .Append(string.IsNullOrEmpty(line.SpeakerId) ? "-" : line.SpeakerId)
                     .Append("  portrait=")
                     .Append(string.IsNullOrEmpty(line.PortraitKey) ? "-" : line.PortraitKey);
            }
            _text.Append('\n');

            _text.Append("BATTLE  ");
            var arena = BattleArena.Current;
            if (arena == null) _text.Append("no arena");
            else
            {
                var presenter = arena.Presenter;
                _text.Append(presenter == null ? "arena, no presenter"
                                               : (presenter.IsIdle ? "arena idle" : "performing"));
            }
            _text.Append('\n');

            _text.Append("PLAYER  ");
            if (_player == null) _text.Append("absent");
            else
            {
                var p = _player.transform.position;
                _text.Append('(').Append(p.x.ToString("0.0")).Append(", ")
                     .Append(p.y.ToString("0.0")).Append(", ")
                     .Append(p.z.ToString("0.0")).Append(')');
            }

            _text.Append("\nLANG    ").Append(Loc.Language);
        }

        /// <summary>
        /// Re-finds anything that has gone away.
        ///
        /// Every one of these can be destroyed and rebuilt under the stamp — a battle load, a
        /// streamed band, a scene change — and a stamp that silently reports "no runner"
        /// because it is holding a dead reference would be worse than no stamp at all.
        /// </summary>
        private void Rebind()
        {
            if (_flow == null) _flow = FindFirstObjectByType<GameFlowController>();
            if (_episodes == null) _episodes = FindFirstObjectByType<EpisodeRunner>();
            if (_dialogue == null) _dialogue = FindFirstObjectByType<DialogueRunner>();
            if (_player == null) _player = FindFirstObjectByType<PlayerLocomotion>();
        }
    }
}
