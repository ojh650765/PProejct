using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.UI
{
    /// <summary>One shell and one content region for every adventure menu page.</summary>
    public sealed class AdventureMenuView : MonoBehaviour
    {
        public static readonly Color Ink = new Color(.065f, .09f, .17f);
        public static readonly Color Surface = new Color(.15f, .21f, .32f, .94f);
        public static readonly Color Muted = new Color(.65f, .75f, .83f);
        public static readonly Color Accent = new Color(.67f, .89f, .31f);
        public static readonly Color Aqua = new Color(.20f, .88f, .82f);
        public RectTransform Content { get; private set; }
        public Action<int> Selected;
        public Action Back, Closed;
        private RectTransform root;
        private TextMeshProUGUI title, money;
        private readonly Image[] tabs = new Image[6];
        private readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[6];

        public void Build(string[] pages)
        {
            root = (RectTransform)transform;
            UiBuilder.Stretch(root);
            Fill(root, "Backdrop", new Color(.04f,.09f,.18f,.96f));
            Box(root,"LeftTint",0,0,.33f,1,new Color(.10f,.24f,.35f,.48f));
            Text(root,"Brand","MENU",.05f,.83f,.32f,.96f,68,Color.white);
            title=Text(root,"PageTitle","모험",.37f,.86f,.76f,.95f,32,Muted);
            money=Text(root,"Money","",.77f,.86f,.95f,.95f,28,Accent,TextAlignmentOptions.Right);
            for(int i=0;i<pages.Length;i++)
            {
                int index=i; float top=.78f-i*.105f;
                var row=Button(root,"Nav_"+i,"",.05f,top-.09f,.32f,top,()=>Selected?.Invoke(index));
                tabs[i]=row.GetComponent<Image>();
                Text(row.transform,"Number",(i+1).ToString("00"),.04f,.1f,.16f,.9f,22,Aqua);
                labels[i]=Text(row.transform,"Label",pages[i],.19f,.08f,.95f,.92f,30,Color.white);
            }
            Content=Rect(root,"Content",.365f,.16f,.95f,.80f);
            Button(root,"Back","뒤로",.05f,.05f,.16f,.115f,()=>Back?.Invoke());
            Button(root,"Close","닫기",.18f,.05f,.32f,.115f,()=>Closed?.Invoke());
            Text(root,"Keys","Tab 이동    F 결정    Esc 뒤로",.40f,.05f,.95f,.115f,22,Muted,TextAlignmentOptions.Right);
        }

        public void Page(string heading,int index,int coins)
        {
            title.text=heading; money.text=coins.ToString("N0")+" 원";
            // Deactivate immediately so deferred Destroy never leaves a previous page clickable.
            foreach(Transform child in Content) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(null);
            for(int i=0;i<tabs.Length;i++)
            {
                tabs[i].color=i==index?new Color(.92f,.96f,.97f):Surface;
                labels[i].color=i==index?Ink:Color.white;
            }
        }

        public static RectTransform Rect(Transform parent,string name,float x0,float y0,float x1,float y1)
        {
            var rect=UiBuilder.Rect(name,parent,false);
            rect.anchorMin=new Vector2(x0,y0);rect.anchorMax=new Vector2(x1,y1);
            rect.offsetMin=rect.offsetMax=Vector2.zero;
            return rect;
        }
        public static Image Fill(RectTransform rect,string name,Color color)
        {
            var image=UiBuilder.Image(name,rect,null,color,Image.Type.Simple);
            UiBuilder.Stretch(image.rectTransform); image.raycastTarget=false;return image;
        }
        public static RectTransform Box(Transform parent,string name,float x0,float y0,float x1,float y1,Color? color=null)
        {
            var rect=Rect(parent,name,x0,y0,x1,y1);Fill(rect,"Fill",color??Surface);return rect;
        }
        public static TextMeshProUGUI Text(Transform parent,string name,string value,float x0,float y0,float x1,float y1,float size,Color? color=null,TextAlignmentOptions align=TextAlignmentOptions.Left)
        {
            var rect=Rect(parent,name,x0,y0,x1,y1);
            var text=UiBuilder.Text(name+"Text",rect,value,UiTextRole.Body,color??Color.white,align);
            UiBuilder.Stretch(text.rectTransform);text.fontSize=size;text.enableAutoSizing=true;
            text.fontSizeMin=size*.75f;text.fontSizeMax=size;text.overflowMode=TextOverflowModes.Ellipsis;
            text.raycastTarget=false;return text;
        }
        public static Button Button(Transform parent,string name,string caption,float x0,float y0,float x1,float y1,Action clicked)
        {
            var rect=Rect(parent,name,x0,y0,x1,y1);
            var image=rect.gameObject.AddComponent<Image>();image.color=Surface;
            var button=UiBuilder.Button(name,rect,image,clicked);
            UiButtonMotion.Attach(rect);
            if(!string.IsNullOrEmpty(caption)) Text(rect,"Label",caption,.04f,.05f,.96f,.95f,25,null,TextAlignmentOptions.Center);
            return button;
        }
        public static void Art(Transform parent,string name,Sprite sprite,float x0,float y0,float x1,float y1)
        {
            if(sprite==null)return;
            var rect=Rect(parent,name,x0,y0,x1,y1);
            var image=UiBuilder.Image("Sprite",rect,sprite,Color.white,Image.Type.Simple);
            UiBuilder.Stretch(image.rectTransform);image.preserveAspect=true;image.raycastTarget=false;
        }
        public static void Bar(Transform parent,string name,float fraction,float x0,float y0,float x1,float y1,Color color)
        {
            var rect=Box(parent,name,x0,y0,x1,y1,Ink);
            Box(rect,"Value",0,0,Mathf.Clamp01(fraction),1,color);
        }
    }
}
