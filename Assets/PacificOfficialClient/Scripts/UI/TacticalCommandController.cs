using UnityEngine;
using UnityEngine.UI;

/// <summary>MuJoCo-backed reproduction of ega_mock's Ctrl+T tactical page.</summary>
public sealed class TacticalCommandController : MonoBehaviour
{
    // A class, not a struct, so the cached last-values below can be written back through
    // the array element. Everything RefreshUnits writes is derived from (hp, maxHp), so
    // remembering those two is enough to skip the other four writes entirely.
    sealed class UnitRow { public Text hp, percent; public Image fill; public int lastHp = int.MinValue, lastMaxHp = int.MinValue; }
    GameObject root, mapPrimary, videoPrimary, largeMap;
    ControlInput controls; OfficialPanelController panels; SeasonMapController seasonMap; MujocoClient client;
    Camera previewCamera; RenderTexture previewTexture;
    readonly UnitRow[] allies=new UnitRow[7], enemies=new UnitRow[7];
    bool visible, videoFirst;
    public bool IsVisible => visible;
    static readonly Color Navy=new Color(.006f,.025f,.038f,.97f), Cyan=new Color(.02f,.65f,.91f,1), Red=new Color(.95f,.12f,.20f,1), Gold=new Color(.92f,.58f,.08f,1);
    // Row ids are fixed; keeping the array here stops RefreshUnits rebuilding it each frame.
    static readonly int[] UnitIds={1,2,3,4,5,6,7};

    public void Initialize(Canvas canvas,Font font,MujocoClient stateClient,ControlInput input,OfficialPanelController panelController,SeasonMapController mapController,Camera sourceCamera,Sprite marker)
    {
        client=stateClient;controls=input;panels=panelController;seasonMap=mapController;
        root=new GameObject("Tactical Command Page");RectTransform page=root.AddComponent<RectTransform>();page.SetParent(canvas.transform,false);page.anchorMin=Vector2.zero;page.anchorMax=Vector2.one;page.offsetMin=page.offsetMax=Vector2.zero;
        // The official match header remains visible above this 1920x980 tactical surface.
        RectTransform surface=Panel("Tactical Surface",page,new Vector2(.5f,0),Vector2.zero,new Vector2(1920,980),Navy);surface.pivot=new Vector2(.5f,0);
        BuildLogistics(surface,font);BuildUnits(surface,font,true);BuildUnits(surface,font,false);BuildBottom(surface,font,sourceCamera);
        // These extracted panels contain fixed example economy, event and
        // analysis numbers. Hide them until the source simulator supplies
        // those specific live fields; never present fabricated match data.
        foreach (string unsupported in new[] { "Logistics Status", "Battle Events", "Battle Analysis", "Industrial Camera/Wait", "Industrial Camera/Diagnostic" })
        {
            Transform item = surface.Find(unsupported);
            if (item != null) item.gameObject.SetActive(false);
        }
        mapPrimary=BuildMap(surface,font,stateClient,marker,false);videoPrimary=BuildPrimaryVideo(surface,font);largeMap=BuildMap(surface,font,stateClient,marker,true);
        videoPrimary.SetActive(false);largeMap.SetActive(false);root.SetActive(false);
    }
    void Update(){bool ctrl=Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl);if(ctrl&&Input.GetKeyDown(KeyCode.T)){SetVisible(!visible);return;}if(!visible)return;RefreshUnits();if(ctrl&&Input.GetKeyDown(KeyCode.Y)){videoFirst=!videoFirst;RefreshLayout();return;}if(!ctrl&&Input.GetKeyDown(KeyCode.M)){largeMap.SetActive(!largeMap.activeSelf);return;}if(Input.GetKeyDown(KeyCode.Escape))SetVisible(false);}
    public void SetVisible(bool value){if(value&&seasonMap)seasonMap.HideForExternalOverlay();visible=value;if(root){root.SetActive(value);root.transform.SetAsLastSibling();}if(previewCamera)previewCamera.enabled=value;if(!value&&largeMap)largeMap.SetActive(false);if(panels)panels.SetExternalOverlay(value);if(controls)controls.SetUiBlocked(value,false);}
    void RefreshLayout(){mapPrimary.SetActive(!videoFirst);videoPrimary.SetActive(videoFirst);largeMap.SetActive(false);}

    static void BuildLogistics(RectTransform p,Font f)
    {
        RectTransform s=Panel("Logistics Status",p,new Vector2(.5f,1),new Vector2(0,-4),new Vector2(1920,72),new Color(.004f,.018f,.028f,.98f));s.pivot=new Vector2(.5f,1);
        Stat(s,f,"已方战况",-620,150,Color.white);Stat(s,f,"当前经济\n100",-440,170,Red);Stat(s,f,"累计总经济\n400",-260,170,Red);Stat(s,f,"科技等级\n0",-80,160,Red);Stat(s,f,"加密等级\n2",90,160,Red);Stat(s,f,"堡垒状态",255,130,Color.white);Stat(s,f,"己方堡垒对方占领计时\n0s",470,280,Cyan);Stat(s,f,"敌方堡垒己方占领计时\n0s",770,280,Cyan);
        OfficialUiKit.Line(s,new Vector2(-755,-32),new Vector2(910,-32),2,new Color(.05f,.45f,.65f,.8f));
    }
    static void Stat(Transform p,Font f,string value,float x,float width,Color color){Label("Status",p,f,value,15,TextAnchor.MiddleCenter,new Vector2(x,0),new Vector2(width,58),color);}

    void BuildUnits(RectTransform p,Font f,bool ally)
    {
        Color team=ally?Red:Cyan;RectTransform panel=Panel(ally?"Ally Units":"Enemy Units",p,new Vector2(.5f,1),new Vector2(ally?-727:727,-78),new Vector2(454,540),new Color(.008f,.035f,.05f,.9f));panel.pivot=new Vector2(.5f,1);Border(panel,panel.sizeDelta,team);
        Text title=Label("Title",panel,f,ally?"我方机器人（5/6）":"敌方机器人（5/6）",19,TextAnchor.MiddleLeft,new Vector2(-214,246),new Vector2(300,36),Color.white);title.rectTransform.pivot=new Vector2(0,.5f);Label("Tag",panel,f,ally?"ALLY UNIT":"ENEMY UNIT",11,TextAnchor.MiddleRight,new Vector2(205,246),new Vector2(130,30),team);
        string[] roles={"英雄","工程","步兵3","步兵4","空中","哨兵"};int[] ids={1,2,3,4,6,7};
        title.text=ally?"ALLY UNITS":"ENEMY UNITS";
        for(int i=0;i<7;i++){
            RectTransform row=Panel("Robot "+UnitIds[i],panel,Vector2.one*.5f,new Vector2(0,196-i*66),new Vector2(434,62),new Color(.012f,.045f,.063f,.84f));OfficialUiKit.Line(row,new Vector2(-213,-30),new Vector2(213,-30),1,new Color(team.r,team.g,team.b,.75f));
            Label("Id",row,f,UnitIds[i]+"  "+(UnitIds[i]==1?"Hero":UnitIds[i]==2?"Engineer":UnitIds[i]==7?"Sentry":"Infantry"),17,TextAnchor.MiddleLeft,new Vector2(-74,16),new Vector2(260,28),Color.white);
            Text hp=Label("HP",row,f,"--/--   OFFLINE",11,TextAnchor.MiddleLeft,new Vector2(-68,-14),new Vector2(245,23),new Color(.65f,.75f,.78f,1));Text percent=Label("Percent",row,f,"--",12,TextAnchor.MiddleRight,new Vector2(177,16),new Vector2(70,24),Color.white);
            Image track=OfficialUiKit.Image("HP Track",row,Vector2.one*.5f,Vector2.one*.5f,new Vector2(68,-14),new Vector2(245,5),null,new Color(.12f,.15f,.17f,1));Image fill=OfficialUiKit.Image("HP Fill",track.transform,new Vector2(0,.5f),new Vector2(0,.5f),Vector2.zero,new Vector2(245,5),null,team);fill.rectTransform.localScale=new Vector3(0,1,1);
            (ally?allies:enemies)[i]=new UnitRow{hp=hp,percent=percent,fill=fill};
        }
    }
    void RefreshUnits(){
        // Runs every frame the page is up. Everything below derives from (hp, maxHp), so
        // rebuilding the strings and the bar on every frame was pure garbage -- and this
        // page is the heaviest allocator in the HUD. Only touch a row when it changed.
        RobotMasterRobot[] rs=client&&client.State?.robotMaster!=null?client.State.robotMaster.robots:null;
        for(int side=0;side<2;side++)for(int i=0;i<7;i++){
            bool blueOperator=client&&client.State?.rules!=null&&client.State.rules.localFaction==1;
            int wanted=(side==0 ? (blueOperator?20:0) : (blueOperator?0:20))+UnitIds[i];
            RobotMasterRobot hit=null;
            if(rs!=null)foreach(RobotMasterRobot r in rs)if(r!=null&&r.id==wanted){hit=r;break;}
            UnitRow row=side==0?allies[i]:enemies[i];if(row==null)continue;
            int hp=hit==null?0:hit.hp, maxHp=hit==null?0:hit.maxHp;
            if(hp==row.lastHp&&maxHp==row.lastMaxHp)continue;
            row.lastHp=hp;row.lastMaxHp=maxHp;
            bool online=hit!=null&&hit.hp>0;
            float q=maxHp>0?Mathf.Clamp01((float)hp/maxHp):0f;
            row.fill.rectTransform.localScale=new Vector3(q,1,1);
            row.percent.text=Mathf.RoundToInt(q*100)+"%";
            row.hp.text=hit==null?"--/--   OFFLINE":$"{hp}/{maxHp}   {(online?"ONLINE":"OFFLINE")}";
            row.hp.color=online?new Color(.28f,1f,.58f,1):new Color(.6f,.66f,.68f,1);
        }
    }

    void BuildBottom(RectTransform p,Font f,Camera source)
    {
        RectTransform events=Panel("Battle Events",p,Vector2.zero,Vector2.zero,new Vector2(464,355),new Color(.006f,.028f,.04f,.96f));events.pivot=Vector2.zero;Border(events,events.sizeDelta,Gold);Label("Title",events,f,"战场事件",20,TextAnchor.MiddleLeft,new Vector2(-205,154),new Vector2(260,35),Color.white);Label("Items",events,f,"6:59    获得400金币\n\n6:59    空中机器人获得30秒空中支援时间\n\n1:28    当前机器人已升到1级",14,TextAnchor.UpperLeft,new Vector2(8,68),new Vector2(420,220),new Color(.78f,.84f,.86f,1));
        RectTransform analysis=Panel("Battle Analysis",p,Vector2.zero,new Vector2(478,0),new Vector2(878,355),new Color(.006f,.028f,.04f,.96f));analysis.pivot=Vector2.zero;Border(analysis,analysis.sizeDelta,Cyan);Label("Title",analysis,f,"战场数据分析",20,TextAnchor.MiddleLeft,new Vector2(-395,154),new Vector2(320,35),Color.white);
        string[] names={"我方总经济\n100\n我方优势","我方总伤害\n0\n需要关注","敌方总伤害\n20\n需要关注","总血量差\n-58\n敌方优势"};Color[] cs={Cyan,Gold,Gold,Red};for(int i=0;i<4;i++){RectTransform card=Panel("Data "+i,analysis,Vector2.one*.5f,new Vector2(-318+i*210,10),new Vector2(195,195),new Color(cs[i].r*.14f,cs[i].g*.14f,cs[i].b*.14f,.8f));Label("Value",card,f,names[i],18,TextAnchor.MiddleCenter,Vector2.zero,new Vector2(180,170),cs[i]);}Label("Conclusion",analysis,f,"分析结论：1台机器人弹量不足 30%，建议回补给舱",14,TextAnchor.MiddleLeft,new Vector2(-395,-148),new Vector2(790,30),Color.white);
        RectTransform camera=Panel("Industrial Camera",p,new Vector2(1,0),Vector2.zero,new Vector2(550,355),new Color(.02f,.04f,.055f,.96f));camera.pivot=new Vector2(1,0);Border(camera,camera.sizeDelta,new Color(.65f,.78f,.8f,1));Label("Title",camera,f,"工业相机预览",20,TextAnchor.MiddleLeft,new Vector2(-245,154),new Vector2(300,35),Color.white);Label("Wait",camera,f,"WAIT",11,TextAnchor.MiddleRight,new Vector2(244,154),new Vector2(80,28),Color.white);
        previewTexture=new RenderTexture(960,540,24,RenderTextureFormat.ARGB32);previewTexture.name="Tactical Video Preview";RawImage small=new GameObject("Live Preview").AddComponent<RawImage>();small.transform.SetParent(camera,false);small.rectTransform.anchorMin=Vector2.zero;small.rectTransform.anchorMax=Vector2.one;small.rectTransform.offsetMin=new Vector2(12,12);small.rectTransform.offsetMax=new Vector2(-12,-45);small.texture=previewTexture;small.color=new Color(.72f,.78f,.82f,.72f);small.raycastTarget=false;Label("Diagnostic",camera,f,"WAIT CUSTOM CAM   FPS --   LAT --ms",11,TextAnchor.MiddleLeft,new Vector2(-238,-155),new Vector2(470,25),Gold);
        if(source){previewCamera=new GameObject("Tactical Preview Camera").AddComponent<Camera>();previewCamera.transform.SetParent(source.transform,false);previewCamera.CopyFrom(source);previewCamera.targetTexture=previewTexture;previewCamera.enabled=false;}
    }
    GameObject BuildPrimaryVideo(RectTransform p,Font f){RectTransform box=Panel("Video Primary",p,new Vector2(.5f,1),new Vector2(0,-78),new Vector2(958,540),Color.black);box.pivot=new Vector2(.5f,1);Border(box,box.sizeDelta,Cyan);RawImage image=new GameObject("Live Camera").AddComponent<RawImage>();image.transform.SetParent(box,false);image.rectTransform.anchorMin=Vector2.zero;image.rectTransform.anchorMax=Vector2.one;image.rectTransform.offsetMin=new Vector2(4,4);image.rectTransform.offsetMax=new Vector2(-4,-38);image.texture=previewTexture;image.raycastTarget=false;Label("Title",box,f,"主图传 / 操作视角",18,TextAnchor.MiddleLeft,new Vector2(-455,251),new Vector2(300,30),Color.white);return box.gameObject;}
    static GameObject BuildMap(RectTransform p,Font f,MujocoClient c,Sprite marker,bool expanded){Vector2 size=expanded?new Vector2(1728,972):new Vector2(958,540);RectTransform box=Panel(expanded?"Tactical Large Map":"Map Primary",p,expanded?new Vector2(.5f,.5f):new Vector2(.5f,1),expanded?new Vector2(0,-2):new Vector2(0,-78),size,new Color(.88f,.96f,.97f,.92f));if(!expanded)box.pivot=new Vector2(.5f,1);Border(box,size,Cyan);Image image=box.gameObject.GetComponent<Image>();Texture2D map=SimulatorMapCapture.AuthoredMap();if(map)image.sprite=Sprite.Create(map,new Rect(0,0,map.width,map.height),Vector2.one*.5f);image.preserveAspect=false;RuntimeMiniMap live=box.gameObject.AddComponent<RuntimeMiniMap>();live.mapRect=box;live.sharedMarkerSprite=marker;live.markerSize=expanded?34:22;live.Bind(c);Label("Title",box,f,"模拟器场地地图",expanded?25:18,TextAnchor.MiddleLeft,new Vector2(-size.x*.5f+20,size.y*.5f-22),new Vector2(300,34),Color.white);Label("Latency",box,f,expanded?"MuJoCo 实时坐标":"MuJoCo --",13,TextAnchor.MiddleRight,new Vector2(size.x*.5f-80,size.y*.5f-22),new Vector2(150,30),new Color(.2f,1f,.55f,1));if(expanded)Label("Hint",box,f,"M  返回战术指挥界面",14,TextAnchor.MiddleCenter,new Vector2(0,-size.y*.5f+18),new Vector2(360,28),Color.white);return box.gameObject;}
    static void Border(Transform parent,Vector2 size,Color color){float x=size.x*.5f,y=size.y*.5f;OfficialUiKit.Line(parent,new Vector2(-x,-y),new Vector2(x,-y),2,color);OfficialUiKit.Line(parent,new Vector2(-x,y),new Vector2(x,y),2,color);OfficialUiKit.Line(parent,new Vector2(-x,-y),new Vector2(-x,y),2,color);OfficialUiKit.Line(parent,new Vector2(x,-y),new Vector2(x,y),2,color);}
    static RectTransform Panel(string n,Transform parent,Vector2 anchor,Vector2 pos,Vector2 size,Color color){RectTransform r=new GameObject(n).AddComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=anchor;r.anchoredPosition=pos;r.sizeDelta=size;Image i=r.gameObject.AddComponent<Image>();i.color=color;i.raycastTarget=false;return r;}
    static Text Label(string n,Transform parent,Font f,string value,int size,TextAnchor align,Vector2 pos,Vector2 dims,Color color){Text t=new GameObject(n).AddComponent<Text>();t.transform.SetParent(parent,false);t.font=f;t.text=value;t.fontSize=size;t.alignment=align;t.color=color;t.raycastTarget=false;t.rectTransform.anchorMin=t.rectTransform.anchorMax=Vector2.one*.5f;t.rectTransform.anchoredPosition=pos;t.rectTransform.sizeDelta=dims;return t;}
}
