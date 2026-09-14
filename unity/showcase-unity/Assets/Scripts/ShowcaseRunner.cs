using System;
using UnityEngine;
using Yio;

/// PlayMode showcase 查看器（Unity 壳）：导航与全部纯 Yio API 接线住在引擎无关的
/// ShowcaseWiring（showcase/showcase-cs/，#14 批 A 拆分——202 处接线两引擎共用），
/// 本壳只做生命周期入口 + Unity 专属面（NativeHost 3D 展位 / Kenney 粒子 / 世界锚点 /
/// stress 压测 / texture-lab 的 SpriteResolver 统计 / 特效暂停 / 手型光标皮肤）。
///
/// 挂在与 YioStageDriver 同一 GameObject 上。Play 后：
///   - 首页 home：点 7 张 nav-card（nav-settings / nav-mail / ...）跳对应页
///   - 各页：点顶栏 / 侧栏的「← 首页」(button#back-home) 回 home
/// 订阅随切页 Dispose 自动清理（public-api §5.4），切页即换树即换订阅。
public class ShowcaseRunner : MonoBehaviour
{
    ShowcaseWiring _wiring;   // 引擎无关接线主体（构造注入本壳的引擎能力）
    YioStageDriver _driver;
    Container _current;
    string _shown;

    // ── character 页 3D 展位（NativeHost 同屏渲染验证） ──
    Container _nativeSlot;         // 绑定目标（native-slot div；Unbind 需同节点）
    GameObject _characterModel;    // NativeHost 持位根（挂 wrapper 下）
    Transform _figureSpin;         // 旋转体（模型本体）
    const float FigureSpinDegPerSec = 40f;

    // ── effects 页：Kenney 粒子 × NativeHost 混染验证 ──
    // 每个 UI 槽位绑一个 Kenney Particle Pack 特效。ParticleSystemRenderer 走
    // ConfigureTransparentMaterials 同一透明通道（renderQueue=3000 与 UI 同队列，
    // sortingOrder = 槽位 sort_key + Lift），验证引擎粒子与自绘 UI 同屏插序渲染。
    static readonly (string slotId, string prefab, float scale, float sink)[] FX_SLOTS =
    {
        ("fx-fire", "Fire", 30f, 90f),
        ("fx-sparks", "Sparks", 70f, 0f),
        ("fx-magic", "Magic", 160f, 0f),
        ("fx-electricity", "Electricity", 150f, 0f),
        ("fx-hearts", "Hearts", 110f, 30f),
        ("fx-smoke", "Smoke", 30f, 90f),
    };
    // wrapper 原点 = 槽位左上角，y 翻转同 character（.fx-slot 460×300，中心 = (230,-150)）。
    // sink：上升型羽流（fire/smoke）把发射器沉到槽位下部，羽流主体落在槽内。
    readonly System.Collections.Generic.List<(Container slot, GameObject go)> _fxBindings = new();
    bool _fxPaused;

    // ── world 页：世界锚点（投影路 3D 跟随 · #109 B）──
    // 场景 Main Camera（depth -1 天幕）前轨道运行三块立方体；血条/跳字是普通 UI 节点，
    // Driver 每帧把立方体头顶世界点投影成屏幕点 → 设计坐标写 node.Transform.Position
    //（锚点模板根 position:absolute + left/top 0 直挂页根 → 布局位 (0,0)，transform 即
    // 绝对坐标）。出屏/相机背后由 driver 自动切换渲染隐藏（visibility 继承语义，整子树）。
    // 竖轨立方体周期性扫出屏幕上下缘——血条整体消失/恢复即自动隐藏证据。
    sealed class WorldCube
    {
        internal Transform Tr;
        internal Container Bar, Fill;
        internal Vector3 Center;      // 轨道圆心（立方体局部系）
        internal float Radius, Speed, Phase, Size;
        internal bool Vertical;       // true = 竖直圆轨（会扫出屏），false = 水平地面轨
    }
    sealed class WorldDamage
    {
        internal Container Node;
        internal int Cube;
        internal float Age, DriftX;   // 上浮由锚点 offset 推进；渐隐走 TweenChannel.Opacity
    }
    // (圆心, 半径, 角速度, 初相, 尺寸, 竖直轨)。相机 (0,1,-10) 平视 +z：fov60 在
    // z≈3 深度（d=13）处半高 = tan30°×13 ≈ 7.5——竖轨半径 9.2 + 头顶上抬 0.625 后
    // 锚点 y 摆幅 ≈ [-7.07, 11.3]，上下缘都扫出屏（自动隐藏双向证据）；水平轨全期在屏内。
    static readonly (Vector3 c, float r, float w, float p, float s, bool v)[] WORLD_CUBES =
    {
        (new Vector3(0f, 0f, 2.5f), 2.2f, 0.9f, 0.0f, 0.7f, false),
        (new Vector3(2f, 0f, 3.5f), 3.6f, -0.6f, 2.1f, 0.9f, false),
        (new Vector3(3f, 1.5f, 3f), 9.2f, 0.45f, 4.2f, 0.55f, true),
    };
    const float WorldDmgLife = 1.4f;
    GameObject _worldStageRoot;
    Camera _worldCam;
    readonly System.Collections.Generic.List<WorldCube> _worldCubes = new();
    readonly System.Collections.Generic.List<WorldDamage> _worldDmgs = new();
    bool _worldHpLow;
    int _worldDmgRound;

    // ── world 页补件：地面 + 遮挡墙（ZTest 对照）+ 挂载名牌（C8）+ 双 Stage（A3/A4）──
    GameObject _worldGround, _worldWall, _plateAnchor;
    Container _plate;               // 挂载名牌节点（首次挂载时实例化）
    bool _plateMounted;
    YioStageDriver _miniDriver;    // 运行时第二 Driver（共享相机/宿主 + 输入独占）
    Container _miniPage;
    TextElement _miniClockText;
    float _miniSpawnTime;
    int _probeClicks;

    // ── stress 页字段见 WireStressPage 区（_stressBars/_stressCubes/_stressStageRoot 等）──

    void Update()
    {
        if (_figureSpin != null)
            _figureSpin.Rotate(Vector3.up, FigureSpinDegPerSec * Time.deltaTime, Space.Self);
        UpdateWorldStage();
        UpdateStressFollow();
        UpdateMiniClock();
    }

    /// stress 页 FPS 读数（右上角，仅压测页显示——driver _showFps 是全局字段，这里页级自制）。
    void OnGUI()
    {
        if (_shown != "stress") return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 26 };
        style.normal.textColor = new Color32(0x5f, 0xb4, 0xd4, 0xff);
        float fps = Time.smoothDeltaTime > 0f ? 1f / Time.smoothDeltaTime : 0f;
        GUI.Label(new Rect(Screen.width - 420f, 64f, 400f, 40f),
            $"FPS {fps:F0} · bars {_stressBars.Count} · {(_stressFollow ? "跟随" : "静止")}", style);
    }

    void Start()
    {
        // 编辑器验收防冻：编辑器窗口失焦（看 Console/切窗）时播放器循环会被挂起，
        // 表现为「游戏只剩一两帧」。Run In Background 让循环失焦持续跑（真机默认行为）。
        Application.runInBackground = true;
        _driver = GetComponent<YioStageDriver>();
        if (_driver == null)
        {
            Debug.LogError("[Showcase] YioStageDriver not found on same GameObject — runner wired wrong");
            return;
        }
        _wiring = new ShowcaseWiring(
            (pkg, comp) => _driver.Instantiate(pkg, comp),
            name => _driver.LoadPackageBytes(name),
            Show,
            msg => Debug.Log(msg),
            mode => _driver.SetAdaptMode(mode));
        // 手型光标皮肤（消费侧注册示例）：包不内置任何皮肤（intent 1 缺省 = 系统箭头），
        // 这里注册像素画手型让 pressable 悬停有 affordance。热点 = 食指尖 (12,1)，
        // SetCursor 热点从纹理左上角量（Unity docs），与像素画屏幕坐标同系。
        _driver.SetCursorTexture(1u, BuildPixelHandCursorTexture(), new Vector2(12f, 1f));
        // 组件类绑定（#20）：注册须在 instantiate 前（setup 期）——晚注册只影响未来构造。
        // 显式工厂委托 = AOT 零反射（IL2CPP 安全）。
        _wiring.Setup(_driver.Context);
        // 让 driver Awake 完成（同帧 Awake 先于 Start，理论已就绪）+ 给 LateUpdate 几帧余量。
        Invoke(nameof(Boot), 0.1f);
    }

    void Boot()
    {
        if (_current == null) Show("home");
    }

    void Show(string page)
    {
        if (_shown == page) return;
        TeardownCharacterStage();   // 上一页若是 character：解绑 NativeHost + 销毁模型
        TeardownEffectsStage();     // 上一页若是 effects：解绑全部粒子槽 + 销毁实例
        TeardownWorldStage();       // 上一页若是 world：清锚点登记 + 销毁 3D 舞台/小窗
        TeardownStressPage();       // 上一页若是 stress：清 500 锚点登记
        _wiring.TeardownPage();     // 上一页若是 runtime-css/component-lab：Dispose 注入句柄 + 解静态事件
        if (_current != null)
        {
            _current.Dispose();   // 递归销毁旧页 + 清旧页事件订阅（Rust remove_node + 后端镜像下帧清）
            _current = null;
        }
        _current = _driver.Instantiate("showcase", page);
        _shown = _current != null ? page : null;
        if (_current == null)
        {
            Debug.Log($"[Showcase] Instantiate showcase/{page} = FAIL (pkg not loaded? comp not found?)");
            return;
        }
        _wiring.WirePage(_current, page);   // Nav → Controls → SettingsTabs → ListViews（纯 Yio 面）
        if (page == "texture-lab") WireTextureLabDrivers(_current);
        if (page == "effects") WireFxToggle(_current);
        WireCharacterStage(_current, page);
        WireEffectsStage(_current, page);
        WireWorldStage(_current, page);
        WireStressPage(_current, page);
        Debug.Log($"[Showcase] Instantiate showcase/{page} = OK");
    }

    /// texture-lab 页 driver（#62 页纹理逐出验收）：
    /// 三组开关 add/remove hidden class——display:none 剪枝后该图集页失去全部可见引用，
    /// SpriteResolver 宽限期（默认 10s）满即逐出销毁；恢复显示走 GetOrLoadPage 现场重载。
    /// 读数取 driver.Host.Backend 的 SpriteResolver 统计（PagesAlive / PagesEvictedTotal）。
    /// 判据（肉眼强信号）：隐藏一组 → 约 10s 后「存活 -1、累计 +1」且其余组不动；三组错峰
    /// 隐藏则读数逐次 +1（每页独立倒计时，非批量清仓）；恢复 → 图标无缝重现、存活回升、累计不变。
    void WireTextureLabDrivers(Container page)
    {
        void Refresh()
        {
            var sprites = (_driver.Host?.Backend as UnityYioBackend)?.Sprites;
            if (sprites == null) return;
            if (page.TryGet<TextElement>("ro-alive", out var ra))
                ra.TextContent = $"存活页：{sprites.PagesAlive}";
            if (page.TryGet<TextElement>("ro-evicted", out var re))
                re.TextContent = $"累计逐出：{sprites.PagesEvictedTotal}";
        }
        void WireGroup(string btnId, string grpId, string label)
        {
            if (!page.TryGet<Button>(btnId, out var btn)
                || !page.TryGet<Container>(grpId, out var grp))
                return;
            bool hidden = false;
            btn.Clicked += () =>
            {
                hidden = !hidden;
                if (hidden) { grp.Classes.Add("hidden"); btn.TextContent = $"恢复{label}"; }
                else { grp.Classes.Remove("hidden"); btn.TextContent = $"隐藏{label}"; }
                Refresh();
            };
        }
        WireGroup("btn-grp-a", "grp-a", "A 组");
        WireGroup("btn-grp-b", "grp-b", "B 组");
        WireGroup("btn-grp-c", "grp-c", "C 组");
        if (page.TryGet<Button>("btn-refresh", out var bRefresh))
            bRefresh.Clicked += Refresh;
        Refresh();
    }

    /// effects 页特效全局暂停/继续：读数翻转 = 事件路由证据，同时验 ParticleSystem 暂停态。
    /// （原 WireControls 的 effects 分支——Unity ParticleSystem 专属，留壳。）
    void WireFxToggle(Container page)
    {
        if (page.TryGet<Button>("btn-fx-toggle", out var fxBtn)
            && page.TryGet<TextElement>("fx-toggle-val", out var fxRead))
        {
            fxBtn.Clicked += () =>
            {
                _fxPaused = !_fxPaused;
                foreach (var (_, go) in _fxBindings)
                {
                    var ps = go != null ? go.GetComponent<ParticleSystem>() : null;
                    if (ps == null) continue;
                    if (_fxPaused) ps.Pause(); else ps.Play();
                }
                fxRead.TextContent = _fxPaused ? "已暂停" : "播放中";
                Debug.Log($"[Showcase] fx toggle -> {_fxBindings.Count} systems, paused={_fxPaused}");
            };
        }
    }

    // ── character 页 3D 展位：NativeHost 把引擎 GO 嵌进 UI 层级 ──
    //
    // 验证目标：UI（自绘 mesh）与引擎原生渲染（3D 模型 + 光照）同屏 interleaved——
    // 模型 sortingOrder = native-slot 节点 sort_key（NativeHostManager.Sync 每帧写），
    // 与 UI 同 Transparent 队列按 UI 绘制序穿插；模型跟随节点 world transform。
    // 模型 = Stylized Astronaut（prefab 自带 Idle/Run 动画机）+ 双平行光（shading 证明
    // 走的是引擎光照而非 UI 自绘）。尺寸按 design px 归一化到 ~520 高（见 BuildCharacterModel）。

    void WireCharacterStage(Container page, string pageName)
    {
        if (pageName != "character") return;
        if (!page.TryGet<Container>("native-slot", out var slot)) return;
        _nativeSlot = slot;
        _characterModel = BuildCharacterModel(out _figureSpin);
        _driver.BindNativeHost(slot, _characterModel);
        Debug.Log("[Showcase] character native-slot bound to 3D model (NativeHost)");
    }


    void TeardownCharacterStage()
    {
        if (_nativeSlot != null)
        {
            _driver.UnbindNativeHost(_nativeSlot);   // 销毁 wrapper（GO 先 reparent 出来）
            _nativeSlot = null;
        }
        if (_characterModel != null)
        {
            Destroy(_characterModel);
            _characterModel = null;
        }
        _figureSpin = null;
    }

    void WireEffectsStage(Container page, string pageName)
    {
        if (pageName != "effects") return;
#if UNITY_EDITOR
        foreach (var (slotId, prefabName, scale, sink) in FX_SLOTS)
        {
            if (!page.TryGet<Container>(slotId, out var slot))
            {
                Debug.LogWarning($"[Showcase] fx slot missing in HTML: {slotId}");
                continue;
            }
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Kenney/Particle samples/Prefabs/" + prefabName + ".prefab");
            if (prefab == null)
            {
                Debug.LogWarning($"[Showcase] fx prefab missing: {prefabName}");
                continue;
            }
            var go = Instantiate(prefab);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = new Vector3(230f, -150f - sink, 0f);
            _driver.BindNativeHost(slot, go);
            _fxBindings.Add((slot, go));
        }
        Debug.Log($"[Showcase] effects page bound {_fxBindings.Count}/{FX_SLOTS.Length} kenney particle systems");
#endif
    }

    void TeardownEffectsStage()
    {
        foreach (var (slot, go) in _fxBindings)
        {
            _driver.UnbindNativeHost(slot);
            if (go != null) Destroy(go);
        }
        _fxBindings.Clear();
        _fxPaused = false;
    }

    // ── world 页 3D 舞台 + 世界锚点接线（#109 B）──
    // 立方体 = GameObject.CreatePrimitive（无外部资源依赖）；观察相机 = 场景 Main Camera
    //（depth -1 天幕层，UI 相机 clearFlags=Depth 叠上——宿主游戏的标准叠加形态）。
    // 血条/跳字从 HTML 模板实例化、AddChild 到页根（absolute + left/top 0 → 布局位 (0,0)，
    // 锚点写的 Transform.Position 即绝对设计坐标）。
    void WireWorldStage(Container page, string pageName)
    {
        if (pageName != "world") return;
        if (!page.TryGet<Button>("btn-wp-hp", out var hpBtn)
            || !page.TryGet<TextElement>("wp-hp-read", out var hpRead)
            || !page.TryGet<Button>("btn-wp-dmg", out var dmgBtn)
            || !page.TryGet<TextElement>("wp-dmg-read", out var dmgRead)
            || !page.TryGet<TextElement>("wp-count-read", out var countRead))
        {
            Debug.LogWarning("[Showcase] world page controls missing in HTML");
            return;
        }
        _worldCam = Camera.main;
        if (_worldCam == null)
        {
            Debug.LogWarning("[Showcase] world stage needs Camera.main (scene Main Camera)");
            return;
        }
        _worldStageRoot = new GameObject("YioWorldStage");
        // 地面 + 遮挡墙：深度线索（透视缩放可辨）+ ZTest 对照（墙后的挂载名牌被挡、
        // 投影路血条不受影响）。墙立在 1 号轨道与相机之间，立方体周期性从墙后经过。
        _worldGround = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _worldGround.transform.SetParent(_worldStageRoot.transform, false);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.transform.SetParent(_worldStageRoot.transform, false);
        wall.transform.localPosition = new Vector3(-2.6f, 1.6f, 0.4f);
        wall.transform.localScale = new Vector3(2.6f, 3.2f, 0.3f);
        _worldWall = wall;
        var barTpl = page.GetTemplate("wp-bar");
        foreach (var (c, r, w, p, s, v) in WORLD_CUBES)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(_worldStageRoot.transform, false);
            cube.transform.localScale = Vector3.one * s;
            var bar = barTpl.Instantiate();
            page.AddChild(bar);   // 直挂页根：absolute 脱流不占布局，DOM 序最后 = 画在面板上
            _worldCubes.Add(new WorldCube
            {
                Tr = cube.transform, Bar = bar, Fill = bar.Get<Container>("wp-fill"),
                Center = c, Radius = r, Speed = w, Phase = p, Size = s, Vertical = v,
            });
        }

        // 挂载名牌的 3D 锚点：2 号立方体头顶，scale 0.004 把 design px 缩成世界单位。
        _plateAnchor = new GameObject("PlateAnchor");
        _plateAnchor.transform.SetParent(_worldCubes[1].Tr, false);
        _plateAnchor.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        _plateAnchor.transform.localScale = Vector3.one * 0.004f;

        // 扣血/回血：红条 30% ↔ 100% 翻转 + 读数翻转（读数翻转 = 事件路由证据）。
        hpBtn.Clicked += () =>
        {
            _worldHpLow = !_worldHpLow;
            foreach (var cube in _worldCubes)
                cube.Fill.Style.Width = Yio.Length.Px(_worldHpLow ? 36f : 120f);
            hpRead.TextContent = _worldHpLow ? "血量 30%（扣血）" : "血量 100%（回血）";
        };

        // 跳字：每块头顶一枚伤害数字——上浮由每帧锚点 offset 推进（transform 归锚点管），
        // 渐隐走 TweenChannel.Opacity（业务侧 TweenBuilder × 锚点组合范式，票面拍板不做
        // 框架 helper）。到期 CallLater Dispose；锚点随节点销毁自动除名（driver rc≠0 自清）。
        dmgBtn.Clicked += () =>
        {
            _worldDmgRound++;
            var dmgTpl = page.GetTemplate("wp-dmg");
            for (int i = 0; i < _worldCubes.Count; i++)
            {
                var node = dmgTpl.Instantiate();
                node.Get<TextElement>("wp-dmg-text").TextContent = "-" + UnityEngine.Random.Range(80, 999);
                page.AddChild(node);
                // 底层 opacity 先落 0：anim override 播放期优先（1→0），完成后 override
                // 清除回落 style 0——防 CallLater 销毁前的一帧回弹闪现。
                node.Style.Opacity = 0f;
                node.Tween(TweenChannel.Opacity)
                    .From(1f).To(0f)
                    .Duration(WorldDmgLife)
                    .Start();
                var dmg = new WorldDamage { Node = node, Cube = i, DriftX = UnityEngine.Random.Range(-24f, 24f) };
                _worldDmgs.Add(dmg);
                Container captured = node;
                _driver.Context.CallLater(WorldDmgLife, () => captured.Dispose());
            }
            dmgRead.TextContent = "已发射 " + _worldDmgRound + " 轮（上浮渐隐 " + WorldDmgLife + "s）";
        };

        countRead.TextContent = "锚点 " + _worldCubes.Count + " · 轨道跟随中";
        Debug.Log($"[Showcase] world stage: {_worldCubes.Count} cubes orbiting, anchors wired");

        // ── C8 挂载名牌：挂到 2 号立方体头顶的 3D 变换（lazy 实例化；锚点 scale 0.004
        //    把 design px 缩到世界单位——名牌 ~240px ≈ 0.96 世界单位，与立方体同量级）。──
        if (page.TryGet<Button>("btn-wp-mount", out var mountBtn)
            && page.TryGet<TextElement>("wp-mount-read", out var mountRead))
        {
            mountBtn.Clicked += () =>
            {
                if (!_plateMounted)
                {
                    if (_plate == null)
                    {
                        _plate = page.GetTemplate("wp-plate").Instantiate();
                        page.AddChild(_plate);
                    }
                    _driver.BindWorldMount(_plate, _plateAnchor.transform);
                    _plateMounted = true;
                    mountRead.TextContent = "已挂载 2 号立方体（随远近缩放 · 墙后遮挡）";
                }
                else
                {
                    _driver.UnbindWorldMount(_plate);
                    _plateMounted = false;
                    mountRead.TextContent = "已解除（名牌回屏幕中央）";
                }
            };
        }

        // ── A3/A4 双 Stage：运行时拉起第二 Driver（共享相机/字体宿主 + 输入独占）。──
        if (page.TryGet<Button>("btn-wp-stage", out var stageBtn)
            && page.TryGet<TextElement>("wp-stage-read", out var stageRead))
        {
            stageBtn.Clicked += () =>
            {
                if (_miniDriver == null) SpawnMiniStage(stageRead);
                else TeardownMiniStage(stageRead);
            };
            stageRead.TextContent = StageCensusText();
        }

        // ── 底层探针：与小窗同位（左下角）。小窗开启时点击被小窗独占（本读数不动），
        //    关闭后恢复可点——输入独占路由的正反两态证据。──
        if (page.TryGet<Button>("btn-wp-probe", out var probeBtn)
            && page.TryGet<TextElement>("wp-probe-read", out var probeRead))
        {
            probeBtn.Clicked += () =>
            {
                _probeClicks++;
                probeRead.TextContent = "点击 " + _probeClicks + " 次";
            };
        }
    }

    /// 双 Stage 普查读数：Driver 数 + 存活 YioUICamera 数（共享 = 恒 1）。
    /// 自建相机带 DontSaveInEditor hideFlags，FindObjectsByType 不可见（曾数出「相机 0」
    /// 假读数）——用 Resources.FindObjectsOfTypeAll 连隐藏对象一起数。
    string StageCensusText()
    {
        int cams = 0;
        foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            if (c != null && c.name == "YioUICamera" && c.gameObject.scene.IsValid()) cams++;
        return "Driver " + YioStageHub.DriverCount + " · 相机 " + cams;
    }

    /// 拉起第二 Driver：inactive GO 上配好共享宿主/层序再激活（Awake 在 SetActive 时跑），
    /// 加载同一 showcase 包 + 实例化 mini-hud 小窗。字体走共享宿主（A3：注册一次复用）。
    /// 输入采集器必须同 GO 挂上：hub 路由探测（PointerHitProbe）与 backend.CollectInput
    /// 都吃它——缺 collector 时小窗永远输不了路由（点击全穿透到底层 Stage，双 Stage
    /// 验收期实锤）。Awake 里 GetComponent 找的就是它。
    void SpawnMiniStage(TextElement stageRead)
    {
        var go = new GameObject("YioMiniStage");
        go.SetActive(false);
        go.AddComponent<YioInputCollector>();
        var d = go.AddComponent<YioStageDriver>();
        d.ConfigureStage(1, true);   // 高序：画在主 Stage 之上，输入探测优先（Awake 前配好）
        go.SetActive(true);
        _miniDriver = d;
        _miniSpawnTime = Time.time;
        _miniPage = d.Instantiate("showcase", "mini-hud");
        if (_miniPage != null)
        {
            _miniPage.TryGet<TextElement>("mh-clock", out _miniClockText);
            if (_miniPage.TryGet<Button>("btn-mh", out var mb)
                && _miniPage.TryGet<TextElement>("mh-read", out var mr))
            {
                int n = 0;
                mb.Clicked += () => mr.TextContent = "点击 " + (++n) + " 次";
            }
        }
        stageRead.TextContent = StageCensusText() + "（点小窗左下角试输入独占）";
        Debug.Log("[Showcase] mini stage spawned: " + StageCensusText());
    }

    void TeardownMiniStage(TextElement stageRead)
    {
        if (_miniDriver == null) return;
        Destroy(_miniDriver.gameObject);   // OnDestroy：hub 注销 + 相机引用释放 + host 释放
        _miniDriver = null;
        _miniPage = null;
        _miniClockText = null;
        stageRead.TextContent = StageCensusText();
    }

    /// 小窗时钟（每秒走）：本 Stage 独立 tick 的可见证据。按秒去重写，免逐帧 set_text。
    void UpdateMiniClock()
    {
        if (_miniClockText == null || _miniPage == null) return;
        int sec = (int)(Time.time - _miniSpawnTime);
        if (sec == _lastMiniSec) return;
        _lastMiniSec = sec;
        _miniClockText.TextContent = string.Format("{0:00}:{1:00}", sec / 60, sec % 60);
    }
    int _lastMiniSec = -1;

    void TeardownWorldStage()
    {
        // 血条锚点显式解除（登记不随页面树销毁自动清——登记在 driver 上）；跳字锚点靠
        // 节点销毁自动除名（node 已随页 Dispose，rc≠0 自清路径）。3D 舞台整根销毁。
        foreach (var cube in _worldCubes)
            if (cube.Bar != null) _driver.ClearWorldAnchor(cube.Bar);
        _worldCubes.Clear();
        _worldDmgs.Clear();
        // 挂载名牌先解除（容器销毁 + 行回落屏幕路径），再随页树销毁节点。
        if (_plateMounted && _plate != null) _driver.UnbindWorldMount(_plate);
        _plateMounted = false;
        _plate = null;
        _plateAnchor = null;
        // 小窗 Driver 整体销毁（hub 注销 + 相机引用释放——主相机存活）。
        if (_miniDriver != null)
        {
            Destroy(_miniDriver.gameObject);
            _miniDriver = null;
        }
        _miniPage = null;
        _miniClockText = null;
        if (_worldStageRoot != null)
        {
            Destroy(_worldStageRoot);   // 连带 ground/wall/立方体/PlateAnchor
            _worldStageRoot = null;
        }
        _worldGround = null;
        _worldWall = null;
        _worldCam = null;
        _worldHpLow = false;
        _worldDmgRound = 0;
    }

    /// 轨道推进 + 锚点重投影（Update 每帧；SetWorldAnchor 同节点 = 原位更新，跟随移动实体）。
    void UpdateWorldStage()
    {
        if (_worldStageRoot == null || _worldCubes.Count == 0) return;
        float t = Time.time;
        for (int i = 0; i < _worldCubes.Count; i++)
        {
            var c = _worldCubes[i];
            float a = c.Phase + t * c.Speed;
            Vector3 p = c.Vertical
                ? new Vector3(c.Center.x + Mathf.Sin(a) * c.Radius, c.Center.y + Mathf.Cos(a) * c.Radius, c.Center.z)
                : new Vector3(c.Center.x + Mathf.Sin(a) * c.Radius, c.Center.y, c.Center.z + Mathf.Cos(a) * c.Radius);
            c.Tr.localPosition = p;
            // 头顶世界点 = 立方体中心 + 上抬（半高 + 间隙）。血条 120px 宽 → offset x=-60 居中；
            // y=-26 悬在头顶上方（design y-down，负 = 上移）。
            _driver.SetWorldAnchor(c.Bar, _worldCam, p + Vector3.up * (c.Size * 0.5f + 0.35f),
                new Vector2(-60f, -26f));
        }
        // 跳字跟随各自立方体：offset y 随 age 上浮；节点到期被 CallLater 销毁 → 靠
        // IsDisposed 短路出列（锚点已由 driver rc≠0 自动除名）。
        for (int i = _worldDmgs.Count - 1; i >= 0; i--)
        {
            var d = _worldDmgs[i];
            if (d.Node.IsDisposed)
            {
                _worldDmgs.RemoveAt(i);
                continue;
            }
            d.Age += Time.deltaTime;
            var c = _worldCubes[d.Cube];
            Vector3 head = c.Tr.localPosition + Vector3.up * (c.Size * 0.5f + 0.35f);
            _driver.SetWorldAnchor(d.Node, _worldCam, head,
                new Vector2(-30f + d.DriftX, -80f - d.Age * 70f));
        }
    }

    // ── stress 页：500 血条压测（blob v15 + 增量渲染 + 投影跟随 + 渲染隐藏）──
    // 血条 = absolute+left/top:0 直挂页根（模板根 pointer-events:none——纯展示条不抢
    // 命中，否则整片网格盖住左侧面板按钮，隐藏/跟随按钮点不到）。静止网格 = 一次性写
    // Transform.Position 摆位（让开面板区：18 列从 x=388 起）；投影跟随 = 每帧
    // SetWorldAnchor 重投影。场景侧同步生成 500 个小方块（血条的 3D 对应物——跟随
    // 是否正确肉眼可辨：每条血条悬在对应方块头顶）。
    readonly System.Collections.Generic.List<Container> _stressBars = new();
    // 方块与血条同序（index 对应）：静止 = 面前平铺网格；跟随 = 波浪世界点。
    readonly System.Collections.Generic.List<Transform> _stressCubes = new();
    GameObject _stressStageRoot;
    bool _stressFollow, _stressHidden;
    const int StressCols = 18;                    // 18×78 + 96 = 1488 ≤ 1920-388-24
    const float StressGridX0 = 388f, StressGridY0 = 120f, StressCellW = 78f, StressCellH = 30f;

    static YioVector2 StressGridPos(int i)
    {
        return new YioVector2(StressGridX0 + (i % StressCols) * StressCellW,
            StressGridY0 + (i / StressCols) * StressCellH);
    }

    /// 血条 i 的波浪世界点（相机 (0,1,-10) 平视 +z 前方的体积波）。
    static Vector3 StressWavePos(float t, int i)
    {
        return new Vector3(
            Mathf.Sin(t * 0.7f + i * 0.13f) * 5.5f,
            1.2f + Mathf.Sin(t * 1.1f + i * 0.37f) * 3.5f,
            3f + Mathf.Cos(t * 0.5f + i * 0.13f) * 4f);
    }

    /// 方块静止停放：相机前平铺网格（25×20，间距 0.55/0.35）。
    static Vector3 StressParkPos(int i)
    {
        return new Vector3((i % 25) * 0.55f - 7.4f, 0.35f, 3f + (i / 25) * 0.35f);
    }

    void WireStressPage(Container page, string pageName)
    {
        if (pageName != "stress") return;
        if (!page.TryGet<Button>("btn-st-make", out var makeBtn)
            || !page.TryGet<Button>("btn-st-follow", out var followBtn)
            || !page.TryGet<Button>("btn-st-hide", out var hideBtn)
            || !page.TryGet<TextElement>("st-read", out var read))
        {
            Debug.LogWarning("[Showcase] stress page controls missing in HTML");
            return;
        }
        read.TextContent = "未生成（点「生成 500 血条」）";

        makeBtn.Clicked += () =>
        {
            foreach (var b in _stressBars) b.Dispose();
            _stressBars.Clear();
            TeardownStressCubes();
            var tpl = page.GetTemplate("st-bar");
            _stressStageRoot = new GameObject("YioStressStage");
            for (int i = 0; i < 500; i++)
            {
                var bar = tpl.Instantiate();
                page.AddChild(bar);
                bar.Transform.Position = StressGridPos(i);
                _stressBars.Add(bar);
                // 3D 对应物：无碰撞体、不投影（500 方块纯视觉负载，别再叠物理/阴影成本）。
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = cube.GetComponent<Collider>();
                if (col != null) Destroy(col);
                cube.transform.SetParent(_stressStageRoot.transform, false);
                cube.transform.localScale = Vector3.one * 0.3f;
                cube.transform.localPosition = StressParkPos(i);
                _stressCubes.Add(cube.transform);
            }
            _stressFollow = false;
            _stressHidden = false;
            read.TextContent = "500 · 静止网格 + 500 方块（右上角 FPS 读数）";
        };

        followBtn.Clicked += () =>
        {
            if (_stressBars.Count == 0) return;
            _stressFollow = !_stressFollow;
            if (!_stressFollow)
            {
                // 回静止：清锚点登记 + 恢复网格摆位 + 方块回停放网格。
                for (int i = 0; i < _stressBars.Count; i++)
                {
                    _driver.ClearWorldAnchor(_stressBars[i]);
                    _stressBars[i].Transform.Position = StressGridPos(i);
                    _stressCubes[i].localPosition = StressParkPos(i);
                }
            }
            read.TextContent = _stressFollow ? "500 · 投影跟随中（血条悬在方块头顶 · 出屏自动隐藏）" : "500 · 静止网格";
        };

        hideBtn.Clicked += () =>
        {
            if (_stressBars.Count == 0) return;
            _stressHidden = !_stressHidden;
            for (int i = 0; i < 250; i++)
                _driver.SetNodeRenderVisible(_stressBars[i], !_stressHidden);
            read.TextContent = _stressHidden ? "250 隐藏（保留对象，恢复不闪）" : "500 · 全显";
        };
    }

    /// 投影跟随：500 世界点绕相机前方波浪运动，血条锚在方块头顶（上抬 0.35 世界单位
    /// + 设计 offset 居中），每帧重锚（P6 场景的引擎侧实跑）。
    void UpdateStressFollow()
    {
        if (!_stressFollow || _stressBars.Count == 0) return;
        var cam = Camera.main;
        if (cam == null) return;
        float t = Time.time;
        for (int i = 0; i < _stressBars.Count; i++)
        {
            Vector3 p = StressWavePos(t, i);
            _stressCubes[i].localPosition = p;
            _driver.SetWorldAnchor(_stressBars[i], cam, p + Vector3.up * 0.35f,
                new Vector2(-48f, -20f));
        }
    }

    void TeardownStressCubes()
    {
        _stressCubes.Clear();
        if (_stressStageRoot != null)
        {
            Destroy(_stressStageRoot);
            _stressStageRoot = null;
        }
    }

    void TeardownStressPage()
    {
        foreach (var b in _stressBars)
            if (b != null) _driver.ClearWorldAnchor(b);
        _stressBars.Clear();
        TeardownStressCubes();
        _stressFollow = false;
        _stressHidden = false;
    }

    /// 克隆源 controller 的 Idle 态 clip、剥掉根 path（""）的 position/rotation 曲线，
    /// 内存重建单态 controller。剥离的曲线只影响根 transform 摆位（由归一化逻辑接管），
    /// 骨骼动画曲线原样保留。
    static UnityEditor.Animations.AnimatorController BuildRootStrippedController(
        UnityEditor.Animations.AnimatorController src)
    {
        var ctrl = new UnityEditor.Animations.AnimatorController();
        ctrl.AddLayer("Base");
        if (src == null) return ctrl;
        foreach (var layer in src.layers)
            foreach (var st in layer.stateMachine.states)
                if (st.state.motion is AnimationClip clip && st.state.name == "Idle")
                {
                    var cleaned = Instantiate(clip);
                    foreach (var b in UnityEditor.AnimationUtility.GetCurveBindings(cleaned))
                    {
                        if (b.path != "") continue;
                        if (b.propertyName.StartsWith("m_LocalPosition", System.StringComparison.Ordinal)
                            || b.propertyName.StartsWith("m_LocalRotation", System.StringComparison.Ordinal))
                            UnityEditor.AnimationUtility.SetEditorCurve(cleaned, b, null);
                    }
                    var state = ctrl.layers[0].stateMachine.AddState("Idle");
                    state.motion = cleaned;
                    return ctrl;
                }
        return ctrl;
    }

    /// 展位模型：Stylized Astronaut（prefab 自带 Animator + AstronautCharacterController，
    /// Idle 态自动播——验证 NativeHost 带真实骨骼动画与自绘 UI 同屏）。归一化：摆好首帧
    /// pose 后按 renderer 世界包围盒缩放到 ~520 design px、脚底对齐持位点、水平居中，
    /// 与资产原始尺寸解耦。mesh.bounds 对全骨骼驱动的蒙皮网格常读零/极小，不可作基准。
    static GameObject BuildCharacterModel(out Transform spin)
    {
#if UNITY_EDITOR
        const string ModelPath = "Assets/Stylized_Astronaut/Stylized Astronaut.prefab";
        var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (prefab == null)
        {
            Debug.LogError($"[Showcase] character model missing: {ModelPath} — 展位空置");
            spin = null;
            return new GameObject("NativeCharacter");
        }
        // 归一化期间 holder 必须留在原点：bounds 是世界系读数，holder 若已带 slot 偏移
        // （360,-340），偏移会被当几何中心反向"归位"——模型被甩出数万单位（曾现）。
        // 量完再挪到展位中心。
        var holder = new GameObject("NativeCharacter");

        var inst = Instantiate(prefab, holder.transform);
        inst.transform.localPosition = Vector3.zero;
        inst.transform.localRotation = Quaternion.identity;
        inst.transform.localScale = Vector3.one;

        // 剥资产自带的游戏层组件：Rigidbody/Collider（重力会整体掉落）、AstronautPlayer
        // /AstronautThirdPersonCamera（第三人称控制，无输入源）、内嵌 Camera（叠渲染，
        // 且自带 AudioListener → 场景双 listener 报错）。删 Camera 前先剥其依赖组件
        //（FlareLayer 挂着时 DestroyImmediate(Camera) 会被拒绝、相机残留）。
        // 展位是 UI 层静态展示，NativeHost 只消费 Transform + Renderer。
        foreach (var rb in inst.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(rb);
        foreach (var col in inst.GetComponentsInChildren<Collider>(true)) DestroyImmediate(col);
        foreach (var al in inst.GetComponentsInChildren<AudioListener>(true)) DestroyImmediate(al);
        foreach (var cam in inst.GetComponentsInChildren<Camera>(true))
        {
            // FlareLayer 按类型名剥（不引类型——6000.5+ 起 Built-in RP 组件全进废弃名单，
            // 编译期引用即 CS0618；同函数剥 AstronautPlayer 同款名字匹配模式）。
            foreach (var comp in cam.GetComponents<Component>())
                if (comp != null && comp.GetType().Name == "FlareLayer") DestroyImmediate(comp);
            DestroyImmediate(cam);
        }
        foreach (var mb in inst.GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && (mb.GetType().Name == "AstronautPlayer"
                || mb.GetType().Name == "AstronautThirdPersonCamera"))
                DestroyImmediate(mb);

        var animator = inst.GetComponentInChildren<Animator>();
        if (animator != null && animator.runtimeAnimatorController != null)
        {
            animator.applyRootMotion = false;
            // 资产 clip 在 FBX 根上烤了位置关键帧（Generic rig 未声明 root-motion 节点，
            // applyRootMotion=false 拦不住普通曲线），播放数秒后把根写飞十多万单位。
            // 克隆 clip 剥掉根 path 的 TR 曲线 + 内存重建 controller——动画只驱动骨骼，
            // 根 transform 归归一化逻辑所有（缩放/摆位/figureSpin 自转）。
            animator.runtimeAnimatorController = BuildRootStrippedController(
                animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController);
            animator.Rebind();
            animator.Update(0f);   // 评估首帧 pose 再量 bounds
        }
        var rends = inst.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0)
        {
            // prefab != null 只保证序列化壳存在——导入器导不出内容时实例是裸 Transform，
            // 静默黑屏。响报并空置展位（资产侧问题应在导入验收期暴露）。
            Debug.LogError($"[Showcase] {ModelPath} instantiated with 0 Renderers（导入产物为空场景）— 展位空置");
            Destroy(holder);
            spin = null;
            return new GameObject("NativeCharacter");
        }
        foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
            smr.updateWhenOffscreen = true;   // 骨架驱动世界 bounds，杜绝误剔除
        bool have = false;
        var b = new Bounds();
        foreach (var r in rends)
        {
            if (!have) { b = r.bounds; have = true; }
            else b.Encapsulate(r.bounds);
        }
        if (!have || b.size.y < 0.001f || b.size.y > 10000f)
        {
            Debug.LogError($"[Showcase] {ModelPath} posed bounds 退化（{(have ? b.size.ToString() : "无 renderer")}）— 展位空置");
            Destroy(holder);
            spin = null;
            return new GameObject("NativeCharacter");
        }
        float s = 520f / b.size.y;
        inst.transform.localScale = Vector3.one * s;
        // 脚底对齐 + 水平/纵深居中（旋转 pivot = 脚底中心）；z 前移 20：位于 UI 平面
        // （z=0）之前，与 slot 底色同 sort_key 时以距离赢 tiebreak（近者后画）。
        inst.transform.localPosition = new Vector3(
            -b.center.x * s, -b.min.y * s, -b.center.z * s + 20f);
        // wrapper 原点 = native-slot 左上角（design 坐标 y 下 → container y-up 空间取负）。
        // 模型归一化后高 ~520px，脚底落在展位底部、留 24px 边距（design y = 680-24 →
        // local -656）：模型整体落在槽内偏下，居中摆法（y=-340）会让头冒出槽顶 180px。
        holder.transform.localPosition = new Vector3(360f, -656f, 0f);


        var lightGo = new GameObject("rimLight");
        lightGo.transform.SetParent(holder.transform, false);
        lightGo.transform.localPosition = Vector3.zero;
        // 平行光（无距离衰减；design px 尺度的模型下点光衰减到近黑）+ 暖色斜照。
        var pl = lightGo.AddComponent<Light>();
        pl.type = LightType.Directional;
        pl.transform.localRotation = Quaternion.Euler(50f, -30f, 0f);
        pl.color = new UnityEngine.Color(1f, 0.94f, 0.85f);
        pl.intensity = 2.2f;
        // 正面补光（贴图深色系，纯侧逆光太暗）：从相机方向低强度补。
        var fillGo = new GameObject("fillLight");
        fillGo.transform.SetParent(holder.transform, false);
        var fl = fillGo.AddComponent<Light>();
        fl.type = LightType.Directional;
        fl.transform.localRotation = Quaternion.Euler(10f, 190f, 0f);
        fl.color = new UnityEngine.Color(0.85f, 0.92f, 1f);
        fl.intensity = 0.9f;
        Debug.Log($"[Showcase] native-slot model = Stylized Astronaut（animator={animator != null}, boundsH={b.size.y:F2}）");
        spin = inst.transform;
        return holder;
#else
        Debug.LogError("[Showcase] BuildCharacterModel 依赖 AssetDatabase（editor-only）——built player 无展位模型");
        spin = null;
        return new GameObject("NativeCharacter");
#endif
    }

    // ── 手型光标皮肤（消费侧注册示例）──

    /// <summary>
    /// 32×32 经典 link-select 手型像素画（热点 (12,1) 在食指尖）。逐格手绘字符画 +
    /// 4 色调色板——32px 这种 1:1 小尺寸下几何体并集堆不出指缝（读作一团色块），
    /// 像素画是系统光标同款工艺。字符画 y=0 是顶行，SetPixels32 下标 0 是左下角像素
    /// ——写入按 (S-1-y) 翻行，否则整张纹理上下颠倒。
    /// </summary>
    static Texture2D BuildPixelHandCursorTexture()
    {
        const int S = 32;
        var transparent = new Color32(0, 0, 0, 0);
        var line = new Color32(28, 28, 32, 255);
        var fill = new Color32(255, 255, 255, 255);
        var shade = new Color32(216, 216, 222, 255);
        var px = new Color32[S * S];
        for (int y = 0; y < S; y++)
        {
            string row = HandArt[y];
            for (int x = 0; x < S; x++)
                px[(S - 1 - y) * S + x] = row[x] switch
                {
                    'o' => line,
                    'w' => fill,
                    's' => shade,
                    _ => transparent,
                };
        }
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        tex.filterMode = FilterMode.Point;
        tex.SetPixels32(px);
        // Cursor.SetCursor 的纹理要求：RGBA32 / 可读 / 无 mip 链——Apply 第二参
        // false 保持可读（makeNoLongerReadable=true 会被 SetCursor 拒收并告警）。
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>手型像素画（32 行 × 32 列，y=0 顶行）：'o' 描边 / 'w' 白填充 /
    /// 's' 阴影 / '.' 透明。食指尖 = 第 1 行 x10-13 的顶边中心（热点所在）。
    /// 填充像素的 4 邻域必须闭合（透明侧邻居必须为 'o'）。</summary>
    static readonly string[] HandArt =
    {
        "................................",
        ".........oooooo.................",
        ".........owwwwo.................",
        ".........owwwwo.................",
        ".........owwwwo.................",
        ".........owwwwo.................",
        ".........owwwwooooo.............",
        ".........owwwwowwwooooo.........",
        ".........owwwwowwwowwwooooo.....",
        ".........owwwwowwwowwwowwwo.....",
        ".........owwwwowwwowwwowwwo.....",
        ".........owwwwowwwowwwowwwo.....",
        ".........owwwwowwwowwwowwwo.....",
        "......oooowwwwowwwowwwowwwo.....",
        ".....owwwwwwwwwssssssssssso.....",
        ".....owwwwwwwwwwwwwwwwwwwwo.....",
        ".....owwwwwwwwwwwwwwwwwwwwo.....",
        ".....owwwwwwwwwwwwwwwwwwwwo.....",
        "......oooowwwwwwwwwwwwwwwwo.....",
        ".........owwwwwwwwwwwwwwwwo.....",
        ".........owwwwwwwwwwwwwwooo.....",
        ".........ooooooooooooooo........",
        "...........ooooooooooooo........",
        "...........ossssssssssso........",
        "...........owwwwwwwwwwwo........",
        "...........owwwwwwwwwwwo........",
        "...........owwwwwwwwwwwo........",
        "...........owwwwwwwwwwwo........",
        "...........ooooooooooooo........",
        "................................",
        "................................",
        "................................",
    };
}
