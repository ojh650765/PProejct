using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PokeLab.Boot.Editor
{
    [InitializeOnLoad]
    public static class InputAndRewardPlayProbe
    {
        private const string KeyName = "PokeLab.InputRewardProbe";
        static InputAndRewardPlayProbe() => EditorApplication.playModeStateChanged += Changed;
        [MenuItem("Tools/Poké Lab/Verification/Play Test Repeat Battle Input And Rewards")]
        public static void Begin()
        {
            if (EditorApplication.isPlaying) return;
            Directory.CreateDirectory("previews/input_rewards");
            File.WriteAllText("Temp/pokelab_jump.txt", "free");
            File.WriteAllText("Temp/input_rewards.json", "{\"state\":\"queued\"}");
            SessionState.SetBool(KeyName, true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity");
            EditorApplication.isPlaying = true;
        }
        private static void Changed(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(KeyName, false)) return;
            SessionState.SetBool(KeyName, false);
            var probe = new GameObject("InputRewardProbe").AddComponent<Probe>();
            UnityEngine.Object.DontDestroyOnLoad(probe.gameObject);
            probe.StartCoroutine(probe.Run());
        }
        [Serializable] public sealed class Report
        {
            public string state = "running";
            public List<string> checks = new List<string>(), failures = new List<string>();
        }
        public sealed class Probe : MonoBehaviour
        {
            private readonly Report report = new Report();
            private Touchscreen touch;
            private Keyboard keyboard;
            private Transform keyboardScope;
            private void Check(bool okay, string note)
            {
                (okay ? report.checks : report.failures).Add(note);
                File.WriteAllText("Temp/input_rewards.json", JsonUtility.ToJson(report, true));
            }
            private void Update() { if (keyboardScope != null) UiKeyboardCursor.Update(keyboardScope); }
            private static Canvas MakeCanvas()
            {
                var host = new GameObject("ProbeCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                var cameraHost = new GameObject("ProbeRenderCamera", typeof(Camera));
                cameraHost.transform.SetParent(host.transform, false);
                var camera = cameraHost.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.04f,.07f,.12f);
                camera.cullingMask = 0; camera.depth = 1000;
                return UiBuilder.ConfigureCanvas(host.GetComponent<Canvas>(), 31000);
            }
            private IEnumerator KeyPress(Key key)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
                yield return null; yield return null;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                yield return null;
            }
            private IEnumerator Tap(RectTransform target)
            {
                var point = RectTransformUtility.WorldToScreenPoint(null, target.position);
                var inputModule = EventSystem.current.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                int pointEvents=0, clickEvents=0;
                Action<InputAction.CallbackContext> onPoint = c => { if(c.control.device==touch) pointEvents++; };
                Action<InputAction.CallbackContext> onClick = c => { if(c.control.device==touch) clickEvents++; };
                inputModule.point.action.performed+=onPoint;inputModule.leftClick.action.performed+=onClick;
                InputSystem.QueueStateEvent(touch, new TouchState { touchId = 1, phase = UnityEngine.InputSystem.TouchPhase.Began, position = point });
                yield return null; yield return null;
                var module = EventSystem.current.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                Check(touch.primaryTouch.press.isPressed, "Synthetic touch reaches device");
                Check(EventSystem.current.currentInputModule == module,"Persistent module is processing input");
                Check(pointEvents>0 && clickEvents>0,"Touch actions callbacks point="+pointEvents+" click="+clickEvents+" pos="+touch.primaryTouch.position.ReadValue()+" target="+point);
                Check(module.GetLastRaycastResult(1).gameObject != null, "UI module tracks touch pointer");
                inputModule.point.action.performed-=onPoint;inputModule.leftClick.action.performed-=onClick;
                InputSystem.QueueStateEvent(touch, new TouchState { touchId = 1, phase = UnityEngine.InputSystem.TouchPhase.Ended, position = point });
                yield return null; yield return null;
            }
            public IEnumerator Run()
            {
                Application.runInBackground = true;
                var previousBackground = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                var gameView = EditorWindow.GetWindow(gameViewType);
                gameView.Show(); gameView.Focus();
                yield return new WaitForSeconds(3);
                touch = InputSystem.AddDevice<Touchscreen>();
                keyboard = InputSystem.AddDevice<Keyboard>();
                foreach (var scene in new[] { "Battle", "MainMenu", "Battle" })
                {
                    yield return SceneManager.LoadSceneAsync(scene);
                    gameView.Focus();
                    yield return new WaitForSeconds(2);
                    foreach (var menu in FindObjectsByType<MainMenuPresenter>(FindObjectsSortMode.None)) menu.enabled = false;
                    UiBuilder.EnsureEventSystem();
                    int active = 0;
                    foreach (var system in FindObjectsByType<EventSystem>(FindObjectsSortMode.None)) if (system.isActiveAndEnabled) active++;
                    Check(active == 1 && EventSystem.current != null, scene + ": exactly one live UI input system");
                    var canvas = MakeCanvas();
                    int clicks = 0;
                    var root = UiBuilder.Rect("TouchTarget", canvas.transform, false);
                    UiBuilder.Anchor(root, new Vector2(.5f,.5f),new Vector2(.5f,.5f),new Vector2(.5f,.5f),Vector2.zero,new Vector2(420,160));
                    var image = UiBuilder.Image("Fill",root,UiSprites.Panel(12),UiPalette.AceCyan);
                    UiBuilder.Stretch(image.rectTransform);
                    UiBuilder.Button("Take",root,image,()=>clicks++);
                    yield return new WaitForSeconds(.3f);
                    var point = RectTransformUtility.WorldToScreenPoint(null, root.position);
                    var hits = new List<RaycastResult>();
                    EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = point }, hits);
                    Check(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(root), "Top raycast hits test button: " + (hits.Count > 0 ? hits[0].gameObject.name : "none"));
                    var module = EventSystem.current.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                    Check(module.leftClick != null && module.leftClick.action.enabled && module.point.action.enabled, "Pointer actions enabled");
                    yield return Tap(root);
                    Check(clicks == 1, scene + ": touch press/release reaches button after scene transition (clicks="+clicks+")");
                    EventSystem.current.SetSelectedGameObject(null);
                    keyboardScope = canvas.transform;
                    yield return KeyPress(Key.Tab);
                    Check(EventSystem.current.currentSelectedGameObject == root.gameObject, scene + ": Tab focuses button");
                    var beforeF = clicks;
                    yield return KeyPress(Key.F);
                    Check(clicks == beforeF + 1, scene + ": F submits once");
                    keyboardScope = null;
                    Destroy(canvas.gameObject);
                }
                var rewardCanvas = MakeCanvas();
                var hudRoot = UiBuilder.Rect("TestHud", rewardCanvas.transform);
                var hud = hudRoot.gameObject.AddComponent<BattleHudView>();
                var creature = PokeLab.Overworld.CreatureFactory.Create(1, 5, 71);
                hud.OnBattleEvent(new CreatureSentOutEvent { Side = BattleSide.Player, Creature = creature });
                var plate = (CreatureStatusPanel)typeof(BattleHudView).GetField("_playerPlate", BindingFlags.NonPublic|BindingFlags.Instance).GetValue(hud);
                int before = creature.CurrentHp;
                creature.CurrentHp = Math.Max(1, before - 5);
                hud.OnBattleEvent(new StatusChangedEvent { Target=BattleSide.Player, Current=StatusCondition.Poison });
                var lastHp = typeof(CreatureStatusPanel).GetField("_lastHp", BindingFlags.NonPublic|BindingFlags.Instance);
                Check((int)lastHp.GetValue(plate)==before,"Status refresh does not reveal HP before damage presentation");
                hud.OnBattleEvent(new DamageDealtEvent { Target=BattleSide.Player,Amount=5,RemainingHp=creature.CurrentHp,MaxHp=creature.MaxHp });
                Check((int)lastHp.GetValue(plate)==creature.CurrentHp,"Presented damage updates HP from event snapshot");
                Destroy(hudRoot.gameObject);
                var summary = BattleExpSummary.Build(rewardCanvas.transform);
                var entries = new List<ExperienceSummaryEntry>();
                for(int i=0;i<6;i++) entries.Add(new ExperienceSummaryEntry { SpeciesId=1,DisplayName="이상해씨",Gained=220,NewTotal=345,NewLevel=7,LevelsGained=2 });
                StartCoroutine(summary.Play(true,entries,reward:"PP +252 · 화염방사 디스크, 10만볼트 디스크, 이상한 사탕 획득!"));
                yield return new WaitForSeconds(1);
                yield return KeyPress(Key.Space);
                yield return new WaitForSeconds(2);
                Canvas.ForceUpdateCanvases();
                var subtitle = (TextMeshProUGUI)typeof(BattleExpSummary).GetField("_subtitle",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(summary);
                subtitle.ForceMeshUpdate();
                Check(!subtitle.isTextTruncated,"PP and long item reward are fully visible");
                Check(subtitle.textBounds.size.x<=subtitle.rectTransform.rect.width+2 && subtitle.textBounds.size.y<=subtitle.rectTransform.rect.height+2,"Reward text bounds stay inside its layout row");
                yield return new WaitForEndOfFrame();
                var screenshot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes("previews/input_rewards/rewards.png", screenshot.EncodeToPNG());
                Destroy(screenshot);
                Destroy(summary.gameObject);
                yield return null;
                var groups = new GachaGroup[5];
                for(int group=0;group<5;group++)
                {
                    groups[group] = new GachaGroup { pulls=new GachaPull[6] };
                    for(int slot=0;slot<6;slot++) groups[group].pulls[slot]=new GachaPull { speciesId=new[]{1,5,10,21,26,31}[slot],level=5,slot=slot,rarity="common" };
                }
                var picker=StarterTeamPicker.Show(rewardCanvas.transform,groups,()=>{});
                yield return new WaitForSeconds(1);
                Check(picker.GetComponentsInChildren<Button>().Length==5,"Five starter groups each offer one choice");
                yield return new WaitForEndOfFrame();
                screenshot=ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes("previews/input_rewards/starter_groups.png",screenshot.EncodeToPNG());Destroy(screenshot);
                Destroy(rewardCanvas.gameObject);
                InputSystem.RemoveDevice(touch);InputSystem.RemoveDevice(keyboard);
                InputSystem.settings.backgroundBehavior = previousBackground;
                report.state=report.failures.Count==0?"passed":"failed";
                File.WriteAllText("Temp/input_rewards.json",JsonUtility.ToJson(report,true));
                EditorApplication.isPlaying=false;
            }
        }
    }
}
