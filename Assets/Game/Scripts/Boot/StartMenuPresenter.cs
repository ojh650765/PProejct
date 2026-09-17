using System;
using System.Collections.Generic;
using System.Linq;
using PokeLab.Audio;
using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using V = PokeLab.UI.AdventureMenuView;

namespace PokeLab.Boot
{
    /// <summary>Profile-backed adventure pages sharing one view and one navigation state.</summary>
    [DisallowMultipleComponent]
    public sealed class StartMenuPresenter : MonoBehaviour
    {
        private V view;
        private GameFlowController flow;
        private OverworldInputReader input;
        private PlayerProfileHost host;
        private SaveDialogPresenter save;
        private bool open, pushed, inputBefore;
        private int page=-1, dexOffset, dexSelection, bagOffset;
        private string pendingItem;
        private readonly string[] pages={"포켓몬", "가방", "도감", "트레이너", "리포트", "설정"};
        public bool IsOpen => open;
        private IPlayerProfile Profile => host != null ? host.Profile : null;

        private void Update()
        {
            if(flow==null)flow=FindFirstObjectByType<GameFlowController>();
            if(input==null)input=FindFirstObjectByType<OverworldInputReader>();
            if(host==null)host=FindFirstObjectByType<PlayerProfileHost>();
            var keys=Keyboard.current;if(keys==null)return;
            if(!open)
            {
                if(keys.escapeKey.wasPressedThisFrame && (flow==null || flow.Mode==GameMode.Exploring))Open();
                return;
            }
            if(save!=null && save.IsRunning)return;
            if(keys.escapeKey.wasPressedThisFrame){Back();return;}
            UiKeyboardCursor.Update(view.transform);
            if(page==2 && keys.pageDownKey.wasPressedThisFrame)MoveDex(8);
            if(page==2 && keys.pageUpKey.wasPressedThisFrame)MoveDex(-8);
        }
        public void Open()
        {
            if(open)return;
            if(flow==null)flow=FindFirstObjectByType<GameFlowController>();
            if(input==null)input=FindFirstObjectByType<OverworldInputReader>();
            if(host==null)host=FindFirstObjectByType<PlayerProfileHost>();
            if(flow!=null && flow.Mode!=GameMode.Exploring)return;
            EnsureView();open=true;view.gameObject.SetActive(true);
            if(flow!=null){flow.PushMode(GameMode.Menu);pushed=true;}
            if(input!=null){inputBefore=input.InputEnabled;input.InputEnabled=false;}
            Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
            Show(-1);
        }
        public void Close()
        {
            if(!open || (save!=null && save.IsRunning))return;
            open=false;pendingItem=null;view.gameObject.SetActive(false);
            if(pushed && flow!=null){flow.PopMode();pushed=false;}
            if(input!=null)input.InputEnabled=inputBefore;
        }
        private void Back()
        {
            if(pendingItem!=null){pendingItem=null;Show(1);}
            else if(page>=0)Show(-1);
            else Close();
        }
        private void OnDestroy(){if(open)Close();}
        private void EnsureView()
        {
            if(view!=null)return;
            var go=new GameObject("AdventureMenuCanvas",typeof(RectTransform),typeof(Canvas));
            go.transform.SetParent(transform,false);
            UiBuilder.ConfigureCanvas(go.GetComponent<Canvas>(),460);UiBuilder.EnsureEventSystem();
            var root=new GameObject("AdventureMenu",typeof(RectTransform));root.transform.SetParent(go.transform,false);
            view=root.AddComponent<V>();view.Build(pages);
            view.Selected=i=>{pendingItem=null;Show(i);};view.Back=Back;view.Closed=Close;
        }
        public void Show(int selected)
        {
            page=selected;view.Page(selected<0?"모험을 계속할 준비":pages[selected],selected,Profile?.Money??0);
            switch(selected)
            {
                case 0: Party();break;
                case 1: Bag();break;
                case 2: Dex();break;
                case 3: Trainer();break;
                case 4: Report();break;
                case 5: Settings();break;
                default: Overview();break;
            }
        }
        private SpeciesData Species(int id)
        {
            return ServiceHub.TryGet<ISpeciesRegistry>(out var registry) && registry.TryGet(id,out var data)?data:null;
        }
        private string CreatureName(CreatureInstance creature) => !string.IsNullOrWhiteSpace(creature.Nickname)?creature.Nickname:Species(creature.SpeciesId)?.DisplayName??"알 수 없는 포켓몬";
        private string TrainerName => string.IsNullOrWhiteSpace(Profile?.TrainerName)?"트레이너":Profile.TrainerName;
        private void Empty(string title,string description)
        {
            V.Text(view.Content,"EmptyTitle",title,.05f,.52f,.95f,.69f,36,V.Aqua);
            V.Text(view.Content,"EmptyHelp",description,.05f,.24f,.95f,.51f,25,V.Muted);
        }
        private void Overview()
        {
            var party=Profile?.Party;
            for(int i=0;i<6;i++)
            {
                int slot=i;float x=i/6f;
                var card=V.Button(view.Content,"PartySummary_"+i,"",x,.55f,x+.153f,1,()=>{Show(0);if(Profile?.Party!=null && slot<Profile.Party.Count)PartyDetails(slot);});
                var creature=party!=null && i<party.Count?party[i]:null;
                if(creature==null){V.Text(card.transform,"Empty","—",.1f,.35f,.9f,.65f,30,V.Muted,TextAlignmentOptions.Center);continue;}
                V.Art(card.transform,"Pokemon",CreatureThumbnail.Front(creature.SpeciesId),.05f,.35f,.95f,.97f);
                V.Text(card.transform,"Level","Lv. "+creature.Level,.08f,.03f,.92f,.18f,22);
                V.Bar(card.transform,"HP",creature.HpFraction,.08f,.23f,.92f,.28f,HpColor(creature));
            }
            var note=V.Box(view.Content,"Journey",0,0,1,.48f);
            V.Text(note,"Heading",TrainerName+"의 모험",.035f,.76f,.97f,.95f,31,V.Aqua);
            var journal=StoryProgress.Journal();
            V.Text(note,"Progress",string.IsNullOrWhiteSpace(journal)?"첫 파트너와 함께할 모험이 기다리고 있어요.":journal,.035f,.1f,.96f,.74f,25,V.Muted,TextAlignmentOptions.TopLeft);
        }
        private static Color HpColor(CreatureInstance c)=>c.HpFraction>.5f?V.Aqua:c.HpFraction>.2f?V.Accent:new Color(1,.35f,.35f);
        private void Party()
        {
            var party=Profile?.Party;
            if(party==null || party.Count==0){Empty("아직 포켓몬이 없어요","첫 파트너를 만나면 모습과 이름, 체력, 기술을 여기에서 확인할 수 있어요.");return;}
            for(int i=0;i<party.Count;i++)
            {
                int slot=i;var c=party[i];float x=(i%2)*.515f,top=1-(i/2)*.31f;
                var row=V.Button(view.Content,"Party_"+i,"",x,top-.275f,x+.485f,top,()=>PartyDetails(slot));
                V.Art(row.transform,"Pokemon",CreatureThumbnail.Front(c.SpeciesId),.02f,.06f,.32f,.95f);
                V.Text(row.transform,"Name",CreatureName(c),.35f,.58f,.96f,.9f,29);
                V.Text(row.transform,"Level","Lv. "+c.Level,.35f,.34f,.6f,.58f,22,V.Muted);
                V.Text(row.transform,"Hp",c.CurrentHp+" / "+c.MaxHp,.60f,.34f,.96f,.58f,22,V.Muted,TextAlignmentOptions.Right);
                V.Bar(row.transform,"HP",c.HpFraction,.35f,.19f,.96f,.25f,HpColor(c));
            }
        }
        private void PartyDetails(int slot)
        {
            var party=Profile?.Party;if(party==null || slot<0 || slot>=party.Count)return;
            var c=party[slot];var data=Species(c.SpeciesId);
            view.Page(CreatureName(c),0,Profile.Money);page=0;
            V.Art(view.Content,"Pokemon",CreatureThumbnail.Front(c.SpeciesId),.02f,.30f,.40f,.97f);
            V.Text(view.Content,"Name",CreatureName(c),.02f,.15f,.41f,.30f,38);
            V.Text(view.Content,"Number",data==null?"":"전국도감 No. "+data.NationalDex.ToString("000"),.02f,.04f,.43f,.14f,22,V.Muted);
            V.Text(view.Content,"Stats","Lv. "+c.Level+"     HP "+c.CurrentHp+" / "+c.MaxHp,.47f,.86f,.98f,.98f,29,V.Aqua);
            V.Bar(view.Content,"HP",c.HpFraction,.47f,.80f,.98f,.83f,HpColor(c));
            ServiceHub.TryGet<IMoveRegistry>(out var moves);
            for(int i=0;i<c.Moves.Count;i++)
            {
                var m=c.Moves[i];float top=.73f-i*.14f;
                var row=V.Box(view.Content,"Move_"+i,.47f,top-.115f,.98f,top);
                var name=moves!=null && moves.TryGet(m.MoveId,out var move)?Loc.Pick(move.NameEn,move.NameKo):m.MoveId;
                V.Text(row,"Move",name,.035f,.1f,.66f,.9f,25);
                V.Text(row,"PP",m.CurrentPp+" / "+m.MaxPp,.67f,.1f,.96f,.9f,22,V.Muted,TextAlignmentOptions.Right);
            }
            if(slot>0)V.Button(view.Content,"Lead","선두로 보내기",.47f,.01f,.73f,.11f,()=>{host.Profile.ReorderParty(slot,0);Show(0);});
            V.Button(view.Content,"PartyBack","목록으로",.75f,.01f,.98f,.11f,()=>Show(0));
        }
        private List<SpeciesData> DexEntries()
        {
            return ServiceHub.TryGet<ISpeciesRegistry>(out var registry)?registry.All.Where(x=>x!=null && x.NationalDex>0).GroupBy(x=>x.NationalDex).Select(x=>x.First()).OrderBy(x=>x.NationalDex).ToList():new List<SpeciesData>();
        }
        private void MoveDex(int count){dexOffset=Mathf.Clamp(dexOffset+count,0,Mathf.Max(0,DexEntries().Count-8));Show(2);}
        private void Dex()
        {
            if(!StoryProgress.Has("story.pokedex")){Empty("도감을 아직 받지 않았어요","마박사에게 도감을 받은 뒤 발견한 포켓몬의 기록을 확인할 수 있어요.");return;}
            var entries=DexEntries();if(entries.Count==0){Empty("도감을 불러오는 중","잠시 뒤 다시 열어 주세요.");return;}
            var seen=new HashSet<int>(Profile.SeenSpecies);var caught=new HashSet<int>(Profile.CaughtSpecies);
            dexSelection=Mathf.Clamp(dexSelection,0,entries.Count-1);var chosen=entries[dexSelection];
            bool known=seen.Contains(chosen.Id);
            V.Box(view.Content,"Display",0,.20f,.43f,1);
            if(known)V.Art(view.Content,"Pokemon",CreatureThumbnail.Front(chosen.Id),.025f,.36f,.405f,.91f);
            else V.Text(view.Content,"Unknown","?",.02f,.45f,.40f,.90f,130,V.Muted,TextAlignmentOptions.Center);
            V.Text(view.Content,"Name",known?chosen.DisplayName:"아직 발견하지 못했어요",.025f,.23f,.405f,.38f,29,null,TextAlignmentOptions.Center);
            V.Text(view.Content,"Counts","발견 "+seen.Count+"    포획 "+caught.Count,0,.05f,.43f,.16f,25,V.Aqua);
            for(int n=0;n<8 && dexOffset+n<entries.Count;n++)
            {
                int index=dexOffset+n;var d=entries[index];float top=1-n*.10f;bool found=seen.Contains(d.Id);
                var row=V.Button(view.Content,"Dex_"+d.NationalDex,"",.47f,top-.086f,1,top,()=>{dexSelection=index;Show(2);});
                row.GetComponent<Image>().color=index==dexSelection?new Color(.24f,.36f,.43f):V.Surface;
                V.Text(row.transform,"No",d.NationalDex.ToString("000"),.03f,.08f,.18f,.92f,22,V.Muted);
                V.Text(row.transform,"Name",found?d.DisplayName:"――――",.21f,.08f,.80f,.92f,25);
                V.Text(row.transform,"Caught",caught.Contains(d.Id)?"●":"",.83f,.08f,.97f,.92f,23,V.Aqua);
            }
            V.Button(view.Content,"Previous","이전",.47f,.035f,.71f,.14f,()=>MoveDex(-8));
            V.Button(view.Content,"Next","다음",.76f,.035f,1,.14f,()=>MoveDex(8));
        }
        private void Bag()
        {
            var items=Profile?.Inventory?.Where(x=>x.Value>0).OrderBy(x=>x.Key).ToList();
            if(items==null || items.Count==0){Empty("가방이 비어 있어요","여행 중 얻은 도구와 중요한 물건이 이곳에 정리됩니다.");return;}
            bagOffset=Mathf.Clamp(bagOffset,0,Mathf.Max(0,items.Count-7));
            for(int i=0;i<7 && bagOffset+i<items.Count;i++)
            {
                var item=items[bagOffset+i];float top=1-i*.12f;
                var row=V.Button(view.Content,"Item_"+item.Key,"",0,top-.102f,1,top,()=>Item(item.Key));
                V.Text(row.transform,"Name",StoryProgress.ItemName(item.Key),.04f,.12f,.79f,.88f,29);
                V.Text(row.transform,"Count","× "+item.Value,.80f,.12f,.96f,.88f,27,V.Aqua,TextAlignmentOptions.Right);
            }
            V.Button(view.Content,"Previous","이전",0,.015f,.22f,.10f,()=>{bagOffset=Mathf.Max(0,bagOffset-7);Show(1);});
            V.Button(view.Content,"Next","다음",.26f,.015f,.48f,.10f,()=>{bagOffset=Mathf.Min(Mathf.Max(0,items.Count-7),bagOffset+7);Show(1);});
        }
        private void Item(string id)
        {
            pendingItem=id;view.Page(StoryProgress.ItemName(id),1,Profile.Money);
            if(id=="journal"){Empty("모험 노트",StoryProgress.Journal());return;}
            if(FieldItems.IsBall(id)){Empty(StoryProgress.ItemName(id),"야생 포켓몬과의 배틀에서 사용할 수 있어요.");return;}
            if(id=="parcel"){Empty("용식에게 전할 물건","친구에게 소포를 전해 주세요.");return;}
            var party=Profile.Party;
            if(party.Count==0){Empty("사용할 포켓몬이 없어요","포켓몬이 동료가 된 뒤 사용할 수 있어요.");return;}
            V.Text(view.Content,"Choose","누구에게 사용할까요?",0,.85f,1,1,34,V.Aqua);
            for(int i=0;i<party.Count;i++)
            {
                int slot=i;var c=party[i];float top=.80f-i*.115f;
                V.Button(view.Content,"Use_"+i,CreatureName(c)+"    HP "+c.CurrentHp+" / "+c.MaxHp,0,top-.095f,1,top,()=>
                {
                    var result=host.Profile.UseItemOnPartyMember(id,slot);
                    if(result==PartyOperationResult.Success){pendingItem=null;Show(1);}
                    else {view.Page(StoryProgress.ItemName(id),1,Profile.Money);Empty("지금은 사용할 수 없어요","효과가 없어 도구는 소모되지 않았어요. 뒤로를 눌러 다른 포켓몬을 선택해 주세요.");}
                });
            }
        }
        private void Trainer()
        {
            var card=V.Box(view.Content,"TrainerCard",0,.05f,1,1);
            V.Art(card,"Portrait",DialoguePortraits.For(PlayerBody.IsFemale?"player_f":"player"),.02f,.04f,.41f,.95f);
            V.Text(card,"Name",TrainerName,.45f,.73f,.95f,.94f,48);
            V.Text(card,"Region","신오지방의 트레이너",.45f,.61f,.95f,.73f,25,V.Aqua);
            V.Text(card,"Money","소지금    "+(Profile?.Money??0).ToString("N0")+" 원",.45f,.42f,.95f,.58f,28);
            V.Text(card,"Party","동료    "+(Profile?.Party?.Count??0)+"마리",.45f,.23f,.95f,.39f,28);
        }
        private void Report()
        {
            Empty("여기까지의 모험을 기록할까요?","현재 위치와 포켓몬, 가방, 스토리 진행 상황을 저장합니다.");
            V.Button(view.Content,"Save","리포트 작성",.05f,.09f,.48f,.22f,()=>
            {
                if(host==null)return;
                if(save==null)save=gameObject.AddComponent<SaveDialogPresenter>();
                save.Run(host.CanSaveNow,host.TrySaveGame,saved=>{if(saved){Show(-1);}});
            });
        }
        private void Settings()
        {
            var audio=ServiceHub.TryGet<AudioDirector>(out var registered)?registered:FindFirstObjectByType<AudioDirector>();
            var buses=new[]{AudioBus.Master,AudioBus.Music,AudioBus.Sfx,AudioBus.Ambience};
            var names=new[]{"전체 음량","배경 음악","효과음","환경음"};
            for(int i=0;i<buses.Length;i++)
            {
                var bus=buses[i];float top=1-i*.18f;float value=audio!=null?audio.GetBusVolume(bus):0;
                var row=V.Box(view.Content,"Volume_"+bus,0,top-.15f,1,top);
                V.Text(row,"Label",names[i],.035f,.12f,.50f,.88f,29);
                V.Text(row,"Value",Mathf.RoundToInt(value*100)+"%",.60f,.12f,.80f,.88f,28,V.Aqua,TextAlignmentOptions.Center);
                V.Button(row,"Minus","−",.50f,.18f,.59f,.82f,()=>{audio?.SetBusVolume(bus,Mathf.Clamp01(value-.1f));Show(5);});
                V.Button(row,"Plus","+",.82f,.18f,.93f,.82f,()=>{audio?.SetBusVolume(bus,Mathf.Clamp01(value+.1f));Show(5);});
            }
            V.Button(view.Content,"Motion",UiTween.MotionEnabled?"화면 효과: 켜짐":"화면 효과: 줄이기",0,.08f,.58f,.23f,()=>{UiTween.MotionEnabled=!UiTween.MotionEnabled;Show(5);});
        }
    }
}
