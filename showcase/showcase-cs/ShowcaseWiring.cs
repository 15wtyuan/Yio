using System;
using Yio;

// showcase 接线主体（引擎无关）：HTML 摆台 + TryGet/Clicked 接线的全部「纯 Yio API」逻辑，
// Unity / Godot 各写薄壳（生命周期入口 + 引擎专属页：NativeHost 3D/粒子/世界锚点/纹理逐出），
// 共用本文件——202 处接线一行不改、换引擎壳即跑（#14 档 2 定策）。
//
// 引擎壳注入的五件事（构造参数）：
//   instantiate(pkg, comp)   —— 页面实例化（Unity: YioStageDriver.Instantiate）
//   readPackageBytes(pkg)    —— pkg 字节装载（Unity: driver.LoadPackageBytes；api-infra #7 用）
//   show(page)               —— 切页动作（导航卡 / back-home）
//   log(msg)                 —— 日志（Unity: Debug.Log；Godot: GD.Print）
//   setAdaptMode(mode)       —— 运行时适配模式切换（driver 便利方法，中性层暂未直暴露）
// UIContext 不注入——node.Context 中性层自持（WireXxx 内 page.Context 取）。
//
// 离页清理语义（TeardownPage）：runtime-css 注入句柄 Dispose、component-lab 静态事件解绑
// ——与 Unity 壳的 3D teardown 平级，Show() 先 TeardownPage 再 Dispose 页树。
public sealed class ShowcaseWiring
{
    readonly Func<string, string, Container> _instantiate;
    readonly Func<string, byte[]> _readPackageBytes;
    readonly Action<string> _show;
    readonly Action<string> _log;
    readonly Func<string, bool> _setAdaptMode;

    Container _page;   // 当前接线页（RefreshComponentLabReadouts / ReplayCurrentPage 用）

    public ShowcaseWiring(
        Func<string, string, Container> instantiate,
        Func<string, byte[]> readPackageBytes,
        Action<string> show,
        Action<string> log,
        Func<string, bool> setAdaptMode)
    {
        _instantiate = instantiate;
        _readPackageBytes = readPackageBytes;
        _show = show;
        _log = log;
        _setAdaptMode = setAdaptMode;
    }

    void Log(string s) => _log(s);

    /// setup 期组件类注册（#20）：注册须在 instantiate 前——晚注册只影响未来构造。
    /// 显式工厂委托 = AOT 零反射（IL2CPP 安全）。
    public void Setup(UIContext ctx)
    {
        ctx.RegisterComponent("lifecycle-widget", (c, id) => new LifecycleWidget(c, id));
    }

    /// 每页接线入口（壳在 Instantiate 之后调）。内序同原 ShowcaseRunner.Show：
    /// Nav → Controls → SettingsTabs → ListViews。引擎专属页（character/effects/
    /// world/stress/texture-lab）由壳在 WirePage 前后自行补接。
    public void WirePage(Container page, string pageName)
    {
        _page = page;
        WireNav(page, pageName);
        WireControls(page, pageName);
        WireSettingsTabs(page, pageName);
        WireListViews(page, pageName);
    }

    /// 离页清理（壳 Show() 在 Dispose 页树之前调）：注入句柄 Dispose + 静态事件解绑。
    public void TeardownPage()
    {
        TeardownRuntimeCssPage();
        TeardownComponentLabPage();
        _page = null;
    }

    // home 页 nav-card id → showcase 组件 stem（Instantiate 第二参）。
    // 与 showcase/showcase/home.html 的 nav-card id 一一对应。
    static readonly (string cardId, string page)[] NAV_CARDS =
    {
        ("nav-settings", "settings"),
        ("nav-inventory", "inventory"),
        ("nav-mail", "mail"),
        ("nav-shop", "shop"),
        ("nav-character", "character"),
        ("nav-form", "form"),
        ("nav-lab", "lab"),
        ("nav-anim", "m2-animation"),
        ("nav-layout", "layout-anim"),
        ("nav-infra", "api-infra"),
        ("nav-rtcss", "runtime-css"),
        ("nav-comp", "component-lab"),
        ("nav-shape", "shape-mask"),
        ("nav-swipe", "swipe"),
        ("nav-tree", "tree"),
        ("nav-evict", "texture-lab"),
        ("nav-fx", "effects"),
        ("nav-world", "world"),
        ("nav-stress", "stress"),
        ("nav-adapt", "adapt"),
    };

    // settings 页 tab → panel 配对（HTML 标准 role=tab/tabpanel 模式）。
    // 浏览器里 yio-preview.js 的 JS 切 panel display；Yio 运行时无 JS，这里复刻该逻辑。
    // panel-audio 默认可见，其余 HTML 里 style="display:none" 冻结进 pkg。
    static readonly (string tabId, string panelId)[] SETTINGS_TABS =
    {
        ("tab-audio", "panel-audio"),
        ("tab-graphics", "panel-graphics"),
        ("tab-controls", "panel-controls"),
        ("tab-account", "panel-account"),
        ("tab-search", "panel-search"),
    };

    // runtime-css 页（#11）当前 Add 句柄集——离页全 Dispose（注入规则不跨页泄漏）。
    // shape-mask 页的注入句柄复用同一池（离页统一清理，语义同源）。
    readonly System.Collections.Generic.List<IDisposable> _rtRegs = new();

    /// 用框架事件系统接导航：nav-card 与 back-home 都是 `<button>`（Button.Clicked）。
    /// （nav-card 原为 `<a>`/Link.Activated，围栏紧缩 a→button 后统一走 Button.Clicked。）
    /// TryGet 找不到（本页没该元素）就跳过——home 页无 back-home，其他页无 nav-card，各取所需。
    /// 闭包捕获的 page/target 是 per-iteration 局部，每次 Show 重新订阅当前页实例。
    void WireNav(Container page, string pageName)
    {
        // back-home 两处形态：settings 侧栏（页面域直 Get）；其余 6 页在 <page-top> 组件内
        //（打包期展开 + 硬墙作用域——组件内 id 须经 host 两跳，L3 查找边界）。
        if (!page.TryGet<Button>("back-home", out var back)
            && page.TryGet<CustomElement>("page-top", out var top))
        {
            top.TryGet<Button>("back-home", out back);
        }
        if (back != null)
            back.Clicked += () => _show("home");
        if (pageName == "m2-animation" && page.TryGet<Button>("btn-replay", out var replay))
            replay.Clicked += ReplayCurrentPage;
        if (pageName == "m2-animation")
            WireM2AnimationDrivers(page);
        if (pageName == "layout-anim")
            WireLayoutAnimDrivers(page);
        if (pageName == "api-infra")
            WireInfraDrivers(page);
        if (pageName == "adapt")
            WireAdaptModeSwitch(page);
        if (pageName == "home")
        {
            foreach (var (cardId, target) in NAV_CARDS)
            {
                string p = target;   // 防御性局部拷贝，确保每个闭包绑各自的页名
                if (page.TryGet<Button>(cardId, out var card))
                    card.Clicked += () => _show(p);
            }
        }
        // texture-lab 页接线读 UnityYioBackend.Sprites 统计——引擎专属，由壳补接。
    }

    /// adapt 演示页（#110）：三个模式按钮 → SetAdaptMode 运行时切换 + 读数翻转。
    /// 判据（肉眼强信号）：切 fit-width 后拖视口高度 → 内容重排无黑边、字号随窗口缩放（vmin）；
    /// letterbox 出黑边对照；读数 span 跟随按钮翻转。
    void WireAdaptModeSwitch(Container page)
    {
        if (!page.TryGet<TextElement>("adapt-readout", out var readout)) return;
        void SetMode(string mode)
        {
            if (_setAdaptMode(mode))
                readout.TextContent = mode;
        }
        if (page.TryGet<Button>("btn-mode-letterbox", out var lb)) lb.Clicked += () => SetMode("letterbox");
        if (page.TryGet<Button>("btn-mode-fit-width", out var fw)) fw.Clicked += () => SetMode("fit-width");
        if (page.TryGet<Button>("btn-mode-fit-height", out var fh)) fh.Clicked += () => SetMode("fit-height");
    }

    /// layout-anim 页 driver（#10 布局动画验收）：
    /// #1/#3/#4 折叠/侧栏/vw 按钮 add_class 切换（CSS transition 起效）；
    /// #6 C# TweenBuilder.Height 运行时 API 摆台（60↔220px）。
    void WireLayoutAnimDrivers(Container page)
    {
        bool foldOpen = false;
        if (page.TryGet<Button>("btn-fold", out var bFold) && page.TryGet<Container>("fold-body", out var foldBody))
        {
            bFold.Clicked += () =>
            {
                foldOpen = !foldOpen;
                if (foldOpen) { foldBody.Classes.Add("open"); bFold.TextContent = "收起"; }
                else { foldBody.Classes.Remove("open"); bFold.TextContent = "展开"; }
            };
        }
        bool sideCollapsed = false;
        if (page.TryGet<Button>("btn-sidebar", out var bSide) && page.TryGet<Container>("sidebar-pair", out var pair))
        {
            bSide.Clicked += () =>
            {
                sideCollapsed = !sideCollapsed;
                if (sideCollapsed) { pair.Classes.Add("collapsed"); bSide.TextContent = "展开侧栏"; }
                else { pair.Classes.Remove("collapsed"); bSide.TextContent = "收起侧栏"; }
            };
        }
        bool vwWide = false;
        if (page.TryGet<Button>("btn-vw", out var bVw) && page.TryGet<Container>("vw-panel", out var vwPanel))
        {
            bVw.Clicked += () =>
            {
                vwWide = !vwWide;
                if (vwWide) { vwPanel.Classes.Add("wide"); bVw.TextContent = "缩回"; }
                else { vwPanel.Classes.Remove("wide"); bVw.TextContent = "拉宽"; }
            };
        }
        if (!page.TryGet<Button>("btn-tween", out var bTween) || !page.TryGet<Container>("tween-panel", out var tweenPanel))
            return;
        bool tweenTall = false;
        bTween.Clicked += () =>
        {
            // TweenBuilder.Height（#10 新通道）：值+域码载荷（[v, (float)LenDomain.Px]）。
            tweenTall = !tweenTall;
            float from = tweenTall ? 60f : 220f;
            float to = tweenTall ? 220f : 60f;
            tweenPanel.Tween(TweenChannel.Height)
                .FromPx(from)
                .ToPx(to)
                .Duration(0.6f)
                .Ease(Yio.EaseKind.CubicOut)
                .Start();
        };
    }

    /// m2-animation #11/#12：程序化动画（node.Play + 句柄 L3）的 driver 接线。
    /// #11 点盒子 Play（OnKey/OnHook 回调进日志）；#12 按钮排控制同一句柄的
    /// Pause/Resume/Stop/Time seek。Play 每次新建 programmatic player（句柄换新）。
    void WireM2AnimationDrivers(Container page)
    {
        if (page.TryGet<Container>("b11-target", out var playTarget))
        {
            playTarget.On<ClickEvent>(_ =>
                playTarget.Play("m2-play-fade")
                    .OnEnd(() => Log("[Showcase] m2 #11 Play(m2-play-fade) end")));
        }
        if (page.TryGet<Container>("b11-hook", out var hookTarget))
        {
            hookTarget.On<ClickEvent>(_ =>
                hookTarget.Play("m2-hookanim")
                    .OnKey(0.5f, () => Log("[Showcase] m2 #11 OnKey(0.5) fired"))
                    .OnHook("half", () => Log("[Showcase] m2 #11 OnHook(half) fired")));
        }
        if (!page.TryGet<Container>("b12-target", out var handleTarget))
            return;
        Yio.AnimationHandle handle = null;
        if (page.TryGet<Button>("btn-h-play", out var bPlay))
            bPlay.Clicked += () =>
            {
                handle = handleTarget.Play("m2-play-fade");
                Log("[Showcase] m2 #12 Play -> new handle");
            };
        if (page.TryGet<Button>("btn-h-pause", out var bPause))
            bPause.Clicked += () =>
            {
                handle?.Pause();
                Log($"[Showcase] m2 #12 Pause @ t={(handle?.Time ?? -1f):F2}s");
            };
        if (page.TryGet<Button>("btn-h-resume", out var bResume))
            bResume.Clicked += () =>
            {
                handle?.Resume();
                Log("[Showcase] m2 #12 Resume");
            };
        if (page.TryGet<Button>("btn-h-stop", out var bStop))
            bStop.Clicked += () =>
            {
                handle?.Stop();
                Log("[Showcase] m2 #12 Stop（句柄失效）");
            };
        if (page.TryGet<Button>("btn-h-seek", out var bSeek))
            bSeek.Clicked += () =>
            {
                if (handle == null) return;
                handle.Time = 0.5f;
                Log("[Showcase] m2 #12 seek Time=0.5s");
            };
    }

    /// api-infra 页：公共 API 基础设施验收 driver（调度三件套 / option-tab 派生 getter /
    /// 多模板列表 / 包生命周期）。页面只到 HTML 结构，行为全部在此接线——真机上看的就是这些
    /// （确定性断言在 headless SchedulerAndLifecycleTests，本页是行为/视觉面）。
    /// 切页防御：CallLater/CallNextFrame 挂在 UIContext 上跨页存活，回调里对目标节点
    /// IsDisposed 短路；OnUpdate 订阅随页 Dispose 自动清理（契约），无需手动拆。
    void WireInfraDrivers(Container page)
    {
        var ui = page.Context;

        // ── #1 OnUpdate 逻辑时钟：dt 累积逐帧刷新 + 帧计数；按钮 Dispose / 重订阅句柄。 ──
        // span 打包后是 TextElement（SemanticKind::TextElement；运行时 create_node("span")
        // 才产 TextNode——两路径不同型，TryGet 按 C# 类型精确匹配，写错型整块静默跳过）。
        if (page.TryGet<TextElement>("infra-clock", out var clock) &&
            page.TryGet<TextElement>("infra-frames", out var frames))
        {
            float elapsed = 0f;
            long pumps = 0;
            void Tick(float dt)
            {
                elapsed += dt;
                pumps++;
                clock.TextContent = elapsed.ToString("F1") + " s";
                frames.TextContent = pumps + " 帧";
            }
            var sub = page.OnUpdate(Tick);
            if (page.TryGet<Button>("btn-clock-toggle", out var toggle))
                toggle.Clicked += () =>
                {
                    if (sub == null) sub = page.OnUpdate(Tick);
                    else { sub.Dispose(); sub = null; }
                };
        }

        // ── #2 CallLater 倒计时链：每步 1s 延迟，one-shot 链式调度。 ──
        if (page.TryGet<Button>("btn-later", out var laterBtn) &&
            page.TryGet<TextElement>("infra-later", out var later))
        {
            laterBtn.Clicked += () =>
            {
                later.Classes.Remove("done");
                InfraCountdown(later, ui, 3);
            };
        }

        // ── #3 CallNextFrame：点击当帧「已受理」，下一帧帧头改文本。 ──
        if (page.TryGet<Button>("btn-nf", out var nfBtn) &&
            page.TryGet<TextElement>("infra-nf", out var nf) &&
            page.TryGet<TextElement>("infra-nf-count", out var nfCount))
        {
            int fired = 0;
            nfBtn.Clicked += () =>
            {
                nf.TextContent = "已点击（本帧受理）→ 等待下一帧…";
                ui.CallNextFrame(() =>
                {
                    if (nf.IsDisposed) return;
                    fired++;
                    nf.TextContent = "下一帧回调已触发 ✓（帧头 fire）";
                    nfCount.TextContent = fired + " 次";
                });
            };
        }

        // ── #4 Dropdown value 链读数：SelectedValue / option.Value / option.Selected。 ──
        if (page.TryGet<Dropdown>("infra-dd", out var dd) &&
            page.TryGet<TextElement>("dd-sel", out var ddSel) &&
            page.TryGet<TextElement>("dd-va", out var va) && page.TryGet<TextElement>("dd-sa", out var sa) &&
            page.TryGet<TextElement>("dd-vb", out var vb) && page.TryGet<TextElement>("dd-sb", out var sb) &&
            page.TryGet<TextElement>("dd-vc", out var vc) && page.TryGet<TextElement>("dd-sc", out var sc) &&
            page.TryGet<OptionItem>("opt-lang-a", out var oa) &&
            page.TryGet<OptionItem>("opt-lang-b", out var ob) &&
            page.TryGet<OptionItem>("opt-lang-c", out var oc))
        {
            void RefreshDd()
            {
                ddSel.TextContent = dd.SelectedValue ?? "(null)";
                va.TextContent = oa.Value; sa.TextContent = oa.Selected ? "true" : "false";
                vb.TextContent = ob.Value; sb.TextContent = ob.Selected ? "true" : "false";
                vc.TextContent = oc.Value; sc.TextContent = oc.Selected ? "true" : "false";
            }
            RefreshDd();
            dd.SelectionChanged += _ => RefreshDd();
        }

        // ── #5 Tab.Selected 合成读数：切选即跟随（父 TabList 状态派生）。 ──
        if (page.TryGet<TabList>("infra-tabs", out var tabs) &&
            page.TryGet<Tab>("itab-1", out var t1) && page.TryGet<Tab>("itab-2", out var t2) &&
            page.TryGet<Tab>("itab-3", out var t3) &&
            page.TryGet<TextElement>("tab-r1", out var r1) &&
            page.TryGet<TextElement>("tab-r2", out var r2) &&
            page.TryGet<TextElement>("tab-r3", out var r3))
        {
            void RefreshTabs()
            {
                r1.TextContent = t1.Selected ? "true" : "false";
                r2.TextContent = t2.Selected ? "true" : "false";
                r3.TextContent = t3.Selected ? "true" : "false";
            }
            RefreshTabs();
            tabs.SelectionChanged += _ => RefreshTabs();
        }

        // ── #6 多模板 ListView：GetTemplate 取两个蓝图 + TemplateSelector 按 index 分派。
        //    强调行视觉（金左条 + 64px）烙在 row-tpl-accent 蓝图里——BindItem 只填数据。
        //    动态面四按钮（读数翻转即证据，照 #7 同款）：
        //    插/删走 Notify*（C# 侧按新 index 重推受影响区间，模板随数据重排）；
        //    「切换全强调」换 selector（core park 旧蓝图 slot、下帧以新蓝图重新物化——整列翻面）；
        //    「null selector 试验」验证严格派异常（求值抛在前、core 映射未动，捕获后恢复原状）。
        if (page.TryGet<ListView>("infra-mt-list", out var mt) &&
            page.TryGet<TextElement>("infra-mt-status", out var mtStatus))
        {
            UITemplate rowNormal = mt.GetTemplate("row-tpl");
            UITemplate rowAccent = mt.GetTemplate("row-tpl-accent");
            System.Func<int, UITemplate> alternating = i => (i % 3 == 2) ? rowAccent : rowNormal;
            bool allAccent = false;
            mt.TemplateSelector = alternating;
            mt.BindItem = (item, i) =>
            {
                var spans = item.Query<TextElement>();
                if (spans.Count >= 2)
                {
                    spans[0].TextContent = string.Format("#{0:00}", i);
                    spans[1].TextContent = (i % 3 == 2) ? "强调行（蓝图切换）" : "普通行";
                }
            };
            mt.ItemCount = 30;
            if (page.TryGet<Button>("btn-mt-insert", out var mtInsert))
                mtInsert.Clicked += () =>
                {
                    mt.NotifyInserted(0, 3);
                    mtStatus.TextContent = "已插 3 @0 · 共 " + mt.ItemCount + " 项 · 模板按新 index 重排";
                };
            if (page.TryGet<Button>("btn-mt-remove", out var mtRemove))
                mtRemove.Clicked += () =>
                {
                    if (mt.ItemCount < 6) { mtStatus.TextContent = "项数不足，先插几批再删"; return; }
                    mt.NotifyRemoved(0, 3);
                    mtStatus.TextContent = "已删 [0,3) · 共 " + mt.ItemCount + " 项";
                };
            if (page.TryGet<Button>("btn-mt-allaccent", out var mtAll))
                mtAll.Clicked += () =>
                {
                    allAccent = !allAccent;
                    mt.TemplateSelector = allAccent ? (System.Func<int, UITemplate>)(_ => rowAccent) : alternating;
                    mtStatus.TextContent = (allAccent ? "全强调蓝图 ✓（换 selector 重物化）" : "恢复交替分派 ✓") + " · 共 " + mt.ItemCount + " 项";
                };
            if (page.TryGet<Button>("btn-mt-nullsel", out var mtNull))
                mtNull.Clicked += () =>
                {
                    // 严格派探针：setter 赋值即对 [0, ItemCount) 全量求值——null 探针必须落在
                    // 当前范围内，删项后固定 @5 会探空（求值全非 null → 不抛），误报「未抛」。
                    if (mt.ItemCount == 0) { mtStatus.TextContent = "共 0 项——无 index 可探"; return; }
                    int probe = System.Math.Min(5, mt.ItemCount - 1);
                    var prev = mt.TemplateSelector;
                    try
                    {
                        // 求值抛在推送之前——core 的 per-item 映射未被动过，列表原样。
                        // setter 先落 C# 字段后校验：捕获后必须显式回设，getter 才不与 core 脱钩。
                        mt.TemplateSelector = i => (i == probe) ? null : rowNormal;
                        mtStatus.TextContent = "未抛（✗ 严格派失效）";
                    }
                    catch (UIContractException)
                    {
                        mtStatus.TextContent = "null @" + probe + " 抛 UIContractException ✓ · 已恢复原状 · " + mt.ItemCount + " 项不变";
                    }
                    finally
                    {
                        // 恢复点击前状态——全强调开着时恢复交替会让 allAccent 与实际 selector 脱钩。
                        mt.TemplateSelector = prev;
                    }
                };
        }

        // ── #7 UnloadPackage：别名重载 showcase 字节 → 实例化本页微缩窗 → 卸载见存活。
        //    载荷用 api-infra 组件本身（pkg 可寻址组件粒度 = html 文件，无需另造载荷组件）：
        //    实例后 Style 覆写宽高 + overflow hidden 裁成小窗。
        if (page.TryGet<Container>("infra-ul-stage", out var ulStage) &&
            page.TryGet<TextElement>("infra-ul-status", out var ulStatus))
        {
            UIPackage copyPkg = null;
            const string CopyName = "infra-copy";
            Container InstantiateMiniWindow()
            {
                var win = copyPkg.Instantiate("api-infra");
                win.Style.Width = Length.Px(420);
                win.Style.Height = Length.Px(88);
                win.Style.OverflowX = Overflow.Clip;
                win.Style.OverflowY = Overflow.Clip;
                // 微缩窗 88px 高只露出顶栏，正文整棵被 clip 不可见——却照付全额 solve 成本
                //（solve 每帧全量重建 taffy 树，每 mini ≈ +7.7ms/帧，几个窗口就把帧率拖垮）。
                // display:none 让 solve 跳过该子树（taffy 语义），视觉零变化。
                foreach (var c in win.Query<Container>())
                {
                    if (c.Classes.Contains("body")) { c.Style.Display = DisplayMode.None; break; }
                }
                ulStage.AddChild(win);
                return win;
            }
            if (page.TryGet<Button>("btn-ul-load", out var ulLoad))
                ulLoad.Clicked += () =>
                {
                    try
                    {
                        if (copyPkg == null)
                            copyPkg = ui.LoadPackage(CopyName, _readPackageBytes("showcase"));
                        InstantiateMiniWindow();
                        ulStatus.TextContent = "已实例化（api-infra 微缩窗 · 舞台上 " + ulStage.ChildCount + " 个存活）";
                    }
                    catch (System.Exception ex)
                    {
                        ulStatus.TextContent = "Load/Instantiate 异常：" + ex.GetType().Name;
                    }
                };
            if (page.TryGet<Button>("btn-ul-unload", out var ulUnload))
                ulUnload.Clicked += () =>
                {
                    if (copyPkg == null) { ulStatus.TextContent = "副本未加载"; return; }
                    try
                    {
                        ui.UnloadPackage(CopyName);
                        bool staleThrew = false;
                        try { copyPkg.Instantiate("api-infra"); }
                        catch (UIPackageException) { staleThrew = true; }
                        ulStatus.TextContent = "模板已卸载 · 旧句柄 Instantiate 抛 = " + (staleThrew ? "✓" : "✗")
                            + " · 微缩窗独立存活 = " + (ulStage.ChildCount > 0 ? "✓" : "✗");
                        copyPkg = null;   // 下次 Load 重建句柄（重载同名包）
                    }
                    catch (System.Exception ex)
                    {
                        ulStatus.TextContent = "Unload 异常：" + ex.GetType().Name;
                    }
                };
        }
    }

    /// #2 的倒计时链：n→…→1→完成，每步 CallLater(1s)（递归延迟调度）。切页后目标节点
    /// 已 Dispose 即短路（timer 挂在 UIContext 上跨页存活，不随页清理）。
    void InfraCountdown(TextElement label, UIContext ui, int n)
    {
        if (label.IsDisposed) return;
        if (n == 0)
        {
            label.TextContent = "完成 ✓";
            label.Classes.Add("done");
            return;
        }
        label.TextContent = n.ToString();
        ui.CallLater(1f, () => InfraCountdown(label, ui, n - 1));
    }

    /// m2-animation 页「↻ 重播」：原地重启声明式动画（Container.RestartAnimations）——
    /// player 重建、delay 重计，节点/滚动/控件值/订阅全保留。
    void ReplayCurrentPage()
    {
        // 原地重启声明式动画（Container.RestartAnimations）：player 重建、delay 重计，
        // 节点/滚动/控件值/订阅全保留——不再走销毁重实例化。
        _page?.RestartAnimations();
    }

    // ── component-lab 页（#20 RegisterComponent）───────────────────────────

    /// 离开 component-lab：解 OnLifecycleChanged 静态事件（防跨页泄漏——
    /// 静态事件的订阅目标是本页读数 span，页面 Dispose 后必须摘除）。
    void TeardownComponentLabPage()
    {
        LifecycleWidget.OnLifecycleChanged -= RefreshComponentLabReadouts;
        _clPartReg?.Dispose();     // 运行时 ::part 注入句柄（不跨页泄漏）
        _clPartReg = null;
    }
    IDisposable _clPartReg;

    void RefreshComponentLabReadouts()
    {
        if (_page == null) return;
        if (_page.TryGet<TextElement>("cl-conn", out var conn))
            conn.TextContent = LifecycleWidget.Connected.ToString();
        if (_page.TryGet<TextElement>("cl-disc", out var disc))
            disc.TextContent = LifecycleWidget.Disconnected.ToString();
    }

    /// component-lab 页（#20）：RegisterComponent 类绑定 + 生命周期回调摆台。
    /// 判据（肉眼强信号）：进页静态 ×2 connect（读数非零）；「再挂一个」= 组件出现 +
    /// connect 读数 +1；「销毁最后」= 组件消失 + disconnect 读数 +1；离页整页 Dispose
    /// 连带静态/动态实例全 disconnect（回页再 connect，计数累加）。
    void WireComponentLabPage(Container page)
    {
        var ui = page.Context;
        LifecycleWidget.OnLifecycleChanged += RefreshComponentLabReadouts;
        RefreshComponentLabReadouts();   // 进页 connect 已发生（instantiate 早于 wire）
        TextElement status = page.TryGet<TextElement>("cl-status", out var st) ? st : null;
        void Say(string s)
        {
            if (status != null) status.TextContent = s;
            Log($"[Showcase] comp: {s}");
        }

        if (page.TryGet<Container>("cl-stage", out var stage))
        {
            // 重复注册 fail-loud（#20）：同 tag 二次注册必须抛 UIContractException。
            if (page.TryGet<Button>("cl-dup", out var dupBtn))
                dupBtn.Clicked += () =>
                {
                    try
                    {
                        ui.RegisterComponent("lifecycle-widget",
                            (c, id) => new LifecycleWidget(c, id));
                        Say("重复注册未拒?");
                    }
                    catch (UIContractException)
                    {
                        Say("重复注册→拒绝 ✓");
                    }
                };
            // 运行时 ::part 注入（#57 × #11）：StyleSheet.Add 的 ::part 通道——注入后
            // 舞台区两组件的 title 整组变红（同 specificity 后 Add 赢，覆盖 .lw-hot 金）。
            if (page.TryGet<Button>("cl-rt", out var rtBtn))
                rtBtn.Clicked += () =>
                {
                    _clPartReg?.Dispose();
                    // specificity 对齐烘焙规则（.lw-hot::part(title) = (0,2,1)）：注入同选择器，
                    // 同优先后 Add 赢（#11 cascade 语义）→ 金组件翻红；裸 ::part (0,1,1) 会输。
                    _clPartReg = ui.StyleSheet.Add(
                        ".lw-hot::part(title) { color: #e74c3c; }");
                    Say("已注入 ::part 红");
                };
            // 列表换绑淘汰（#20 pump 路径）：ItemCount 减员 → core 杀克隆 → 下帧
            // PumpRemovedNodes 对已物化 wrapper fire OnDisconnected（disc 跳增）。
            if (page.TryGet<ListView>("cl-list", out var lv))
            {
                // 模板句柄必须在 ItemCount（enter_data_driven 收编并清掉 <template> 子）之前
                // 全部取好——enter 之后再 GetTemplate 会「not found in scope」抛异常
                //（api-infra 同款顺序：全部 GetTemplate → 模板设定 → BindItem → ItemCount）。
                UITemplate rowWidget = lv.GetTemplate("cl-row");
                UITemplate rowPlain = lv.GetTemplate("cl-row-plain");
                lv.ItemTemplate = rowWidget;
                // BindItem 触达子树（Query 物化 widget wrapper → OnConnected）；
                // 不触达则克隆永不物化、死亡无 wrapper 可通知。
                lv.BindItem = (item, i) =>
                {
                    // 触达即物化：Query 构造 widget wrapper（→ OnConnected）；
                    // 不触达则克隆永不物化、死亡无 wrapper 可通知。
                    var _ = item.Query<CustomElement>();
                };
                lv.ItemCount = 8;
                if (page.TryGet<Button>("cl-swap", out var swapBtn))
                {
                    ListView captured = lv;
                    swapBtn.Clicked += () =>
                    {
                        // Rust 侧死亡触发器 = 整列表销毁（蓝图/全部克隆一次释放——park 语义下
                        // 换蓝图/减员都不 free 节点，那是池化不是死亡）。disc 大跳增即泵实证。
                        captured.Dispose();
                        Say("列表已销毁（disc 跳增；重新进页复原）");
                    };
                }
            }
            if (page.TryGet<Button>("cl-pop", out var popBtn))
            {
                // 销毁动态挂的最后一个（按挂载记录，不按 Query 序——DFS 序与视觉序不必然一致，
                // 按文档尾取会销毁错对象：金色静态组件可能恰在末位）。
                System.Collections.Generic.List<Container> added = new System.Collections.Generic.List<Container>();
                if (page.TryGet<Button>("cl-add", out var addBtn))
                    addBtn.Clicked += () =>
                    {
                        Container holder = _instantiate("showcase", "widget-holder");
                        if (holder == null) { Log("[Showcase] comp: widget-holder instantiate FAIL"); return; }
                        holder.RemoveFromParent();
                        stage.AddChild(holder);
                        added.Add(holder);
                        Log($"[Showcase] comp: +1 (conn={LifecycleWidget.Connected} disc={LifecycleWidget.Disconnected})");
                    };
                popBtn.Clicked += () =>
                {
                    if (added.Count == 0) { Say("没有动态组件可销毁（先点「再挂一个」）"); return; }
                    Container last = added[added.Count - 1];
                    added.RemoveAt(added.Count - 1);
                    last.Dispose();
                    Log($"[Showcase] comp: -1 (conn={LifecycleWidget.Connected} disc={LifecycleWidget.Disconnected})");
                };
            }
        }
    }

    /// 离开 runtime-css 页：Dispose 全部 Add 句柄（注入规则不跨页泄漏；主题 SetVar
    /// 随页面节点销毁自然失效——node_vars 挂节点）。
    void TeardownRuntimeCssPage()
    {
        foreach (var r in _rtRegs) r?.Dispose();
        _rtRegs.Clear();
    }

    /// shape-mask 页（#52）：命中穿透读数（圆/角计数）+ 运行时注入圆遮罩。
    /// 判据（肉眼强信号）：点橙角=角读数翻（clip-path 裁命中——穿透到下层按钮）、
    /// 点圆心=圆读数翻、注入后方图变圆/撤销复原。静态区（头像/槽位/嵌套滚动/旋转卡）
    /// 无接线——浏览器预览与运行时同为 CSS 声明面。
    /// 注入句柄复用 _rtRegs（离页 TeardownPage 统一 Dispose——句柄生命周期语义同源）。
    void WireShapeMaskPage(Container page)
    {
        var ui = page.Context;
        var ss = ui.StyleSheet;
        if (page.TryGet<TextElement>("sm-status", out var status))
            status.TextContent = "待命";
        // 命中穿透读数：圆按钮与角按钮各计数（TryGet 类型对齐：button 命中 Button）。
        if (page.TryGet<TextElement>("sm-hit-c", out var hitC)
            && page.TryGet<TextElement>("sm-hit-x", out var hitX))
        {
            int c = 0, x = 0;
            if (page.TryGet<Button>("sm-center", out var centerBtn))
                centerBtn.Clicked += () => { c++; hitC.TextContent = c.ToString(); };
            if (page.TryGet<Button>("sm-corner", out var cornerBtn))
                cornerBtn.Clicked += () => { x++; hitX.TextContent = x.ToString(); };
        }
        // 运行时注入：.sm-rt-img 圆遮罩（类规则 clip-path），Dispose 复原。
        if (page.TryGet<Button>("sm-rt", out var rtBtn))
            rtBtn.Clicked += () =>
            {
                foreach (var r in _rtRegs) r?.Dispose();
                _rtRegs.Clear();
                _rtRegs.Add(ss.Add(".sm-rt-img { clip-path: circle(50%); }"));
                if (status != null) status.TextContent = "已注入圆";
            };
        if (page.TryGet<Button>("sm-rt-off", out var offBtn))
            offBtn.Clicked += () =>
            {
                foreach (var r in _rtRegs) r?.Dispose();
                _rtRegs.Clear();
                if (status != null) status.TextContent = "已撤销";
            };
        // G 圆角命中读数：圆角裁剪器（overflow+radius）角外点击穿透（Q6 存量偏差修复）。
        if (page.TryGet<TextElement>("sm-rhit-c", out var rHitC)
            && page.TryGet<TextElement>("sm-rhit-x", out var rHitX))
        {
            int rc = 0, rx = 0;
            if (page.TryGet<Button>("sm-r-center", out var rCenterBtn))
                rCenterBtn.Clicked += () => { rc++; rHitC.TextContent = rc.ToString(); };
            if (page.TryGet<Button>("sm-r-corner", out var rCornerBtn))
                rCornerBtn.Clicked += () => { rx++; rHitX.TextContent = rx.ToString(); };
        }
        // F2 var() 换形：遮罩值走 var(--sm-mask)，SetVar(string) 在圆/六边形间切
        // （var 代换 clip-path 的端到端肉眼判据——core 已有契约测试）。
        if (page.TryGet<Image>("sm-var-img", out var varImg))
        {
            bool round = false;
            if (page.TryGet<Button>("sm-var", out var varBtn))
                varBtn.Clicked += () =>
                {
                    round = !round;
                    varImg.Style.SetVar(
                        "--sm-mask",
                        round ? "circle(50%)" : "polygon(25% 0%, 75% 0%, 100% 50%, 75% 100%, 25% 100%, 0% 50%)");
                    if (status != null) status.TextContent = round ? "var·圆" : "var·六边";
                };
        }
    }

    /// swipe 页（#59 批1）：scroll-snap 声明面验收。判据（肉眼强信号）：
    /// 拖过 1/3 吸附翻页 / 慢拖回位 / 快甩只翻一页 / 首末页拉不动（回弹）/ 滚轮
    /// 一滚一页 / dots 高亮跳变 / PageIndex 与事件计数读数翻转。
    /// 接线：banner 容器（div→Container 对齐）PageChanged 驱动 dots 类切换 + 读数；
    /// 按钮 = ScrollToPage 重载粒度（Smooth ±1 / Instant 边界页）。
    /// 首帧初始页由 core 首次结算的 PageChanged 自动同步（settled_page=-1 起步）。
    void WireSwipePage(Container page)
    {
        var dots = new TextElement[4];
        for (int i = 0; i < 4; i++)
            page.TryGet<TextElement>($"sw-dot-{i}", out dots[i]);
        if (page.TryGet<TextElement>("sw-idx", out var idx)) idx.TextContent = "0";
        if (page.TryGet<TextElement>("sw-evcount", out var ev)) ev.TextContent = "0 / –";
        if (page.TryGet<Container>("sw-banner", out var banner))
        {
            int count = 0;
            banner.PageChanged += e =>
            {
                count++;
                if (ev != null) ev.TextContent = $"{count} / {e.PageIndex}";
                if (idx != null) idx.TextContent = e.PageIndex.ToString();
                for (int i = 0; i < 4; i++)
                    dots[i]?.Classes.Set("sw-dot-on", i == e.PageIndex);
            };
            if (page.TryGet<Button>("sw-fwd", out var fwd))
                fwd.Clicked += () => banner.ScrollToPage(banner.PageIndex + 1);
            if (page.TryGet<Button>("sw-back", out var back))
                back.Clicked += () => banner.ScrollToPage(banner.PageIndex - 1);
            if (page.TryGet<Button>("sw-end", out var end))
                end.Clicked += () => banner.ScrollToPage(3, ScrollBehavior.Instant);
            if (page.TryGet<Button>("sw-home", out var home))
                home.Clicked += () => banner.ScrollToPage(0, ScrollBehavior.Instant);
        }
    }

    /// runtime-css 页（#11）：StyleSheet.Add/Dispose/Clear + SetVar/RemoveVar + var() 消费面。
    /// 判据（肉眼强信号）：目标块变色/复原、同优先后 Add 赢、非法 CSS 异常读数带行列、
    /// chips 组整组翻色/回落、嵌套链 swatch 变色、行内源 chip 恒橙（打包期通路回归）。
    /// 环 warning 判据不在运行时验收（走 yio check 输出，agent 自测）。
    void WireRuntimeCssPage(Container page)
    {
        var ui = page.Context;
        var ss = ui.StyleSheet;
        if (!page.TryGet<TextElement>("rt-status", out var status)) return;
        void Say(string s) { status.TextContent = s; Log($"[Showcase] rt-css: {s}"); }
        void DropRegs()
        {
            foreach (var r in _rtRegs) r?.Dispose();
            _rtRegs.Clear();
        }

        // ① Add 生效 + Dispose 复原：注入 .rt-target 红规则。
        if (page.TryGet<Button>("rt-add", out var addBtn))
            addBtn.Clicked += () =>
            {
                DropRegs();
                _rtRegs.Add(ss.Add(".rt-target { background-color: #c0392b; border-color: #c0392b; }"));
                Say("已注入红");
            };
        if (page.TryGet<Button>("rt-dispose", out var disBtn))
            disBtn.Clicked += () =>
            {
                DropRegs();
                Say("已撤销");
            };
        // ② 同 specificity 后 Add 赢：连注绿→橙两条，橙（后者）胜出；撤销后回灰。
        if (page.TryGet<Button>("rt-later", out var laterBtn))
            laterBtn.Clicked += () =>
            {
                DropRegs();
                _rtRegs.Add(ss.Add(".rt-target { background-color: #2ecc71; border-color: #2ecc71; }"));
                _rtRegs.Add(ss.Add(".rt-target { background-color: #f39c12; border-color: #f39c12; }"));
                Say("后Add赢·橙");
            };
        // ⑥ Clear 全清（pkg 规则不动——chips 边框色是 pkg 规则的 var 消费，Clear 后仍在）。
        if (page.TryGet<Button>("rt-clear", out var clearBtn))
            clearBtn.Clicked += () =>
            {
                DropRegs();
                ss.Clear();
                Say("已Clear");
            };
        // ③ 非法 CSS：at-rule 在注入通道全拒 → UIStyleException 带行列读数。
        if (page.TryGet<Button>("rt-bad", out var badBtn))
            badBtn.Clicked += () =>
            {
                try
                {
                    ss.Add("@keyframes fade { from { opacity: 0 } }");
                    Say("未抛异常?");
                }
                catch (UIStyleException ex)
                {
                    Say($"UIStyleException L{ex.Line}C{ex.Column}");
                }
            };
        // ④ SetVar 主题 + RemoveVar 回落：--rt-accent 在 .rt-page 规则声明（深蓝），
        //    SetVar 同节点覆盖 → 整组 chips 翻亮青；RemoveVar 回落。
        if (page.TryGet<Container>("rt-page", out var rtPage))
        {
            if (page.TryGet<Button>("rt-theme", out var themeBtn))
                themeBtn.Clicked += () =>
                {
                    rtPage.Style.SetVar("--rt-accent", new YioColor(0.37f, 0.71f, 0.83f, 1f));
                    Say("主题·亮青");
                };
            if (page.TryGet<Button>("rt-untheme", out var unthemeBtn))
                unthemeBtn.Clicked += () =>
                {
                    rtPage.Style.RemoveVar("--rt-accent");
                    Say("已回落");
                };
            // ⑤ 嵌套链：--rt-chain-a 吃 --rt-chain-b（页面 CSS 声明）；SetVar --chain-b
            // 在声明节点覆盖 → 链解析传播，swatch 变红。
            if (page.TryGet<Button>("rt-chain", out var chainBtn))
                chainBtn.Clicked += () =>
                {
                    rtPage.Style.SetVar("--rt-chain-b", new YioColor(0.75f, 0.22f, 0.17f, 1f));
                    Say("链·红");
                };
        }
        // ⑧⑨⑩ typed 重载三连（#11）：长度（border-width 变粗）/ 透明度（淡化）/
        // 字符串（fallback 消费可见化——默认紫 = var 缺席 fallback，SetVar 后红）。
        // 均 toggle：未设 → SetVar；已设 → RemoveVar 回落（CSS 声明/继承值）。
        if (page.TryGet<Container>("rt-w-block", out var wBlock)
            && page.TryGet<Button>("rt-w", out var wBtn))
        {
            bool thick = false;
            wBtn.Clicked += () =>
            {
                if (thick) { wBlock.Style.RemoveVar("--rt-border-w"); Say("边框·回落3px"); }
                else { wBlock.Style.SetVar("--rt-border-w", Length.Px(10)); Say("边框·10px"); }
                thick = !thick;
            };
        }
        if (page.TryGet<Container>("rt-fade-block", out var fadeBlock)
            && page.TryGet<Button>("rt-fade", out var fadeBtn))
        {
            bool faded = false;
            fadeBtn.Clicked += () =>
            {
                if (faded) { fadeBlock.Style.RemoveVar("--rt-alpha"); Say("透明·回落1"); }
                else { fadeBlock.Style.SetVar("--rt-alpha", 0.25f); Say("透明·0.25"); }
                faded = !faded;
            };
        }
        if (page.TryGet<Container>("rt-fb-swatch", out var fbSwatch)
            && page.TryGet<Button>("rt-fb", out var fbBtn))
        {
            bool injected = false;
            fbBtn.Clicked += () =>
            {
                if (injected) { fbSwatch.Style.RemoveVar("--rt-fb"); Say("字符串·回落fallback紫"); }
                else { fbSwatch.Style.SetVar("--rt-fb", "#c0392b"); Say("字符串·红"); }
                injected = !injected;
            };
        }
    }

    /// settings 页 tab 切换：HTML 的 role=tab/tabpanel 模式依赖运行时 JS 改 panel display，
    /// Yio 运行时无 JS，这里订阅 tab 按钮 Clicked → 隐藏当前 panel + 显示目标 panel。
    /// panel 是裸 <div>（.panel CSS 无 display 声明）→ 默认 display:block（子元素 page-title/
    /// page-desc/field 垂直堆叠）。显示用 DisplayMode.Block，**不能用 Flex**——Flex 默认
    /// flex-direction:row 会让 panel 的子元素水平排列，布局错乱。隐藏用 DisplayMode.None。
    /// 改 Style.Display 攒批下帧 flush 到 core 触发 solve 重排（display 变是低频 UI 操作）。
    void WireSettingsTabs(Container page, string pageName)
    {
        if (pageName != "settings") return;
        // 预取 tab 按钮与 panel，过滤掉本页不存在的（宽松查询，同 WireNav）。
        var tabs = new System.Collections.Generic.List<(Button tab, Container panel)>();
        foreach (var (tabId, panelId) in SETTINGS_TABS)
        {
            if (page.TryGet<Button>(tabId, out var tab) && page.TryGet<Container>(panelId, out var panel))
                tabs.Add((tab, panel));
        }
        if (tabs.Count == 0) return;
        // 找当前可见的 panel 作初始 active（HTML 里 panel-audio 默认可见）。
        Container initial = null;
        foreach (var (_, panel) in tabs)
        {
            if (panel.Style.Display != DisplayMode.None) { initial = panel; break; }
        }
        // active 用单元素数组承载：C# 闭包捕获数组引用，所有 tab 闭包共享 arr[0]，
        // 任一 tab 点击后更新它，其余 tab 下次点击读到最新 active（避免 per-iteration 快照失同步）。
        var active = new Container[] { initial };
        foreach (var (tab, panel) in tabs)
        {
            Container target = panel;        // 防御性局部拷贝
            tab.Clicked += () =>
            {
                if (active[0] == target) return;   // 已是当前页，no-op
                if (active[0] != null) active[0].Style.Display = DisplayMode.None;
                target.Style.Display = DisplayMode.Block;
                active[0] = target;                // 后续点击以新 active 为基准
            };
        }
    }

    // tree 页 helper（#8）：条目自身 label——首子常是 HTML 换行缩进折叠出的空白 TextNode，
    // 须跳过再判形态：branch = .row 容器（label 在第二子 span，首子是折叠箭头）；
    // leaf = .row 容器（唯一子即 label 文本）。非预期结构回落层级描述（读数仍可辨）。
    static string OwnTreeLabel(TreeItem item)
    {
        for (int i = 0; i < item.ChildCount; i++)
        {
            if (item.GetChildAt(i) is not Container child) continue;   // 空白 TextNode 跳过
            if (child.ChildCount >= 2 && child.GetChildAt(1) is TextElement lbl)
                return lbl.TextContent;
            return child.TextContent;
        }
        return item.Level + " 级条目";
    }

    static int CountExpanded(Yio.Tree tree)
    {
        int CountBranch(Container n)
        {
            int total = 0;
            for (int i = 0; i < n.ChildCount; i++)
            {
                if (n.GetChildAt(i) is TreeItem ti && ti.IsBranch)
                {
                    if (ti.Expanded) total++;
                    total += CountBranch(ti);
                }
                else if (n.GetChildAt(i) is Container c) total += CountBranch(c);
            }
            return total;
        }
        return CountBranch(tree);
    }

    void WireControls(Container page, string pageName)
    {
        // effects 页的特效全局暂停/继续接线读 Unity ParticleSystem——引擎专属，由壳补接。
        if (pageName == "tree")
        {
            // tree 页（#8）：HTML 摆台（树 + 全展开/全折叠按钮 + 读数），事件路由证据 = 读数翻转。
            // 选中读数 = 选中条目自身 label（branch 取 .row 第二子 span——首子是折叠箭头；
            // leaf 直接取 TextNode 文本）；展开读数 = 展开态 branch 计数（遍历树条目）。
            if (page.TryGet<Yio.Tree>("inv-tree", out var tree)
                && page.TryGet<TextElement>("sel-readout", out var selRead)
                && page.TryGet<TextElement>("expand-readout", out var expRead))
            {
                // 展开读数：DFS 数展开态 branch（程序化批量后刷新读数用）。
                System.Func<int> countExpanded = () => CountExpanded(tree);
                tree.SelectionChanged += e =>
                {
                    var item = e.SelectedItem;
                    selRead.TextContent = item != null ? OwnTreeLabel(item) : "（空）";
                    Log($"[Showcase] tree selection -> {selRead.TextContent}");
                };
                if (page.TryGet<Button>("btn-tree-expand-all", out var expBtn)) expBtn.Clicked += () => { tree.ExpandAll(); expRead.TextContent = countExpanded().ToString(); };
                if (page.TryGet<Button>("btn-tree-collapse-all", out var colBtn)) colBtn.Clicked += () => { tree.CollapseAll(); expRead.TextContent = countExpanded().ToString(); };
                // 展开读数随交互切换刷新：点击条目折叠/展开、键盘 →← 都发 ExpandedChanged；
                // ExpandAll/CollapseAll 是程序化批量（不发该事件），上面按钮各自手动刷新。
                foreach (var ti in tree.Query<TreeItem>())
                    ti.ExpandedChanged += _ => expRead.TextContent = countExpanded().ToString();
                // 初始读数（HTML 初值对齐：默认选中首项「武器」、展开分组 2 个）。
                selRead.TextContent = tree.SelectedItem != null ? OwnTreeLabel(tree.SelectedItem) : "（空）";
                expRead.TextContent = countExpanded().ToString();
            }
        }
        if (pageName == "runtime-css")
        {
            WireRuntimeCssPage(page);
        }
        if (pageName == "component-lab")
        {
            WireComponentLabPage(page);
        }
        if (pageName == "shape-mask")
        {
            WireShapeMaskPage(page);
        }
        if (pageName == "swipe")
        {
            WireSwipePage(page);
        }
        if (pageName == "lab")
        {
            // lab #14 运行时 ZIndex：按钮把 B 片在 4（置顶）/ 0（回落 DOM 序）间切换——
            // 便签层 inline override，下帧绘制序生效（不触发 flex solve）。
            if (page.TryGet<Button>("zi-btn", out var ziBtn)
                && page.TryGet<Container>("zi-b", out var ziB))
            {
                ziBtn.Clicked += () =>
                {
                    bool raised = ziB.Style.ZIndex > 0;
                    ziB.Style.ZIndex = raised ? 0 : 4;
                    Log($"[Showcase] lab #14 B z-index -> {ziB.Style.ZIndex}");
                };
            }
            // lab #15 长按 + CancelClick：长按触发后砍掉本次 click（松开读数不得变「click」）；
            // 短按走 ClickEvent。验证 LongPressEvent 路由 + CancelClick FFI 链。
            if (page.TryGet<Container>("lp-target", out var lpTarget)
                && page.TryGet<TextElement>("lp-read", out var lpRead))
            {
                lpTarget.On<LongPressEvent>(e =>
                {
                    lpTarget.CancelClick(e.TouchId);
                    lpRead.TextContent = "长按触发 @" + (int)e.Position.X + "," + (int)e.Position.Y + "（click 已取消）";
                });
                lpTarget.On<ClickEvent>(e => { lpRead.TextContent = "click 触发（短按）"; });
            }
            // lab #15 pointer capture：Down 时 SetPointerCapture(0)（鼠标槽），拖出元素外
            // Move 仍路由到本节点（读数持续跟随 = capture 生效）；Up 自动释放（契约）。
            if (page.TryGet<Container>("cap-target", out var capTarget)
                && page.TryGet<TextElement>("cap-read", out var capRead))
            {
                capTarget.On<PointerDownEvent>(e =>
                {
                    // touchId 从事件取（鼠标 = -1，触摸 = fingerId——手写 0 会注册到空槽）。
                    capTarget.SetPointerCapture(e.TouchId);
                    capRead.TextContent = "down（已 capture，touchId=" + e.TouchId + "）";
                });
                capTarget.On<PointerMoveEvent>(e =>
                {
                    capRead.TextContent = "move " + (int)e.Position.X + "," + (int)e.Position.Y;
                });
                capTarget.On<PointerUpEvent>(e => { capRead.TextContent = "up（capture 已自动释放）"; });
            }
            // lab #16 runtime TweenBuilder：按钮触发 C# 链式 tween（Transform 五元组，
            // EaseBezier 精确 CSS ease + Repeat+yoyo 两轮 + OnComplete tag 路由收尾）。
            // HTML 只摆台（块/按钮/读数），动画全走运行时 API——CSS 面 + 运行时面的分工演示。
            if (page.TryGet<Button>("tw-btn", out var twBtn)
                && page.TryGet<Container>("tw-target", out var twTarget)
                && page.TryGet<TextElement>("tw-read", out var twRead))
            {
                twBtn.Clicked += () =>
                {
                    twRead.TextContent = "tween 播放中（2 轮 yoyo）…";
                    twTarget.Tween(TweenChannel.Transform)
                        .From(0f, 0f, 1f, 1f, 0f)
                        .To(176f, 0f, 1f, 1f, 0f)
                        .Duration(0.5f)
                        .EaseBezier(0.25f, 0.1f, 0.25f, 1f)
                        .Repeat(1, yoyo: true)
                        .OnComplete(_ => twRead.TextContent = "tween 完成（OnComplete 触发）")
                        .Start();
                };
            }
            // lab #17 动态内容范式（#88）：模板实例化 + Query 注入 + 运行时切类。
            // dyn-* 类声明在 lab.dynamic.css（<link> 引入 = 动态样式声明位——围栏可校验、
            // 随 pkg 打包、预览可见），C# 侧只做类切换、不拼 CSS 串。伪类（:hover /
            // :nth-child）对实例化节点照常生效，是本节同时验证的点。
            if (page.TryGet<Button>("dyn-btn", out var dynBtn)
                && page.TryGet<Button>("dyn-sel-btn", out var dynSelBtn)
                && page.TryGet<Container>("dyn-list", out var dynList)
                && page.TryGet<TextElement>("dyn-read", out var dynRead))
            {
                var cards = new System.Collections.Generic.List<Container>();
                string[] names = { "哨塔", "兵营", "金矿" };
                int[] levels = { 3, 7, 12 };
                dynBtn.Clicked += () =>
                {
                    foreach (var c in cards) c.Dispose(); // 重复点击 = 重建
                    cards.Clear();
                    var tpl = page.GetTemplate("dyn-card");
                    for (int i = 0; i < names.Length; i++)
                    {
                        var card = tpl.Instantiate();
                        card.Get<TextElement>("dyn-name").TextContent = names[i];
                        card.Get<TextElement>("dyn-count").TextContent = "LV." + levels[i];
                        dynList.AddChild(card);
                        cards.Add(card);
                    }
                    dynRead.TextContent = "已实例化 " + cards.Count + " 节点（Query 注入完成）";
                };
                dynSelBtn.Clicked += () =>
                {
                    if (cards.Count < 2) { dynRead.TextContent = "先点「实例化 3 节点」"; return; }
                    var card = cards[1];
                    card.Classes.Toggle("dyn-selected");
                    var bg = card.Computed.Background;
                    // computed 背景色进读数：选中翻转即级联证据（#17331f ↔ 奇偶行底色）。
                    string bgHex = bg.HasValue
                        ? string.Format("#{0:X2}{1:X2}{2:X2}",
                            (int)(bg.Value.R * 255f), (int)(bg.Value.G * 255f), (int)(bg.Value.B * 255f))
                        : "null";
                    dynRead.TextContent = "dyn-selected=" + card.Classes.Contains("dyn-selected")
                        + " computed bg=" + bgHex;
                };
            }
            // lab #19 链接（#74）：Get<Link> + Clicked 把 href 写进读数 span——href 原样
            // 回传（opaque 标识符，游戏自解释路由），点击命中细化到 a 节点（含嵌 span 文字）；
            // 点击链接外普通文字不触发（读数不变即判据）。四个链接共用一个读数；第四个
            //（link-custom）作者 color/text-decoration 声明覆盖 UA 默认——视觉判据在页内 desc。
            if (page.TryGet<Link>("link-shop", out var linkShop)
                && page.TryGet<Link>("link-bag", out var linkBag)
                && page.TryGet<Link>("link-quest", out var linkQuest)
                && page.TryGet<Link>("link-custom", out var linkCustom)
                && page.TryGet<TextElement>("link-readout", out var linkRead))
            {
                linkShop.Clicked += () => { linkRead.TextContent = linkShop.Href; Log("[Showcase] link -> " + linkShop.Href); };
                linkBag.Clicked += () => { linkRead.TextContent = linkBag.Href; };
                linkQuest.Clicked += () => { linkRead.TextContent = linkQuest.Href; };
                linkCustom.Clicked += () => { linkRead.TextContent = linkCustom.Href; };
            }
            // lab #21 拖拽使能（#75）：声明式块（draggable="true"）DragMove 增量施加 user
            // transform + 读数计 start 次/累计位移；运行时开关块按钮翻 Node.Draggable；
            // 对照块订阅 DragStart 但永不使能——读数翻动即失败信号。
            if (page.TryGet<Container>("drag-a", out var dragA)
                && page.TryGet<TextElement>("drag-a-read", out var dragARead))
            {
                int starts = 0; float ax = 0f, ay = 0f;
                dragA.On<DragStartEvent>(_ => { starts++; });
                dragA.On<DragMoveEvent>(e =>
                {
                    ax += e.DeltaX; ay += e.DeltaY;
                    dragA.Transform.Position = new YioVector2(ax, ay);
                    dragARead.TextContent = "拖拽 " + starts + " 次 · 累计 " + (int)ax + "," + (int)ay;
                });
            }
            if (page.TryGet<Container>("drag-b", out var dragB)
                && page.TryGet<Button>("drag-b-btn", out var dragBBtn)
                && page.TryGet<TextElement>("drag-b-read", out var dragBRead))
            {
                float bx = 0f, by = 0f;
                dragBBtn.Clicked += () =>
                {
                    dragB.Draggable = !dragB.Draggable;
                    dragBBtn.TextContent = dragB.Draggable ? "关闭拖拽" : "开启拖拽";
                    dragBRead.TextContent = dragB.Draggable ? "已开启（现在能拖）" : "已关闭";
                };
                dragB.On<DragMoveEvent>(e =>
                {
                    bx += e.DeltaX; by += e.DeltaY;
                    dragB.Transform.Position = new YioVector2(bx, by);
                    dragBRead.TextContent = "拖动中 · 累计 " + (int)bx + "," + (int)by;
                });
            }
            if (page.TryGet<Container>("drag-c", out var dragC)
                && page.TryGet<TextElement>("drag-c-read", out var dragCRead))
            {
                // 未使能节点不参与 drag 仲裁——DragStart 不应到达；读数翻动即失败。
                dragC.On<DragStartEvent>(_ => { dragCRead.TextContent = "!! 不应触发"; });
            }
            // lab #22 TabList 激活模型（#13）：manual 组方向键只移焦点（FocusEvent 读数跟随）、
            // Enter/Space 才提交选中（SelectionChanged 只在提交时发）；auto 组焦点与选中同步。
            if (page.TryGet<TabList>("mtab-manual", out var mTabs)
                && page.TryGet<Tab>("mt-m1", out var m1)
                && page.TryGet<Tab>("mt-m2", out var m2)
                && page.TryGet<Tab>("mt-m3", out var m3)
                && page.TryGet<TextElement>("mtab-manual-read", out var mRead))
            {
                string mFocus = "—";
                string ManualSel() => m1.Selected ? "甲" : m2.Selected ? "乙" : m3.Selected ? "丙" : "—";
                void RefreshManual() { mRead.TextContent = "焦点 " + mFocus + " / 选中 " + ManualSel(); }
                m1.On<FocusEvent>(_ => { mFocus = "甲"; RefreshManual(); });
                m2.On<FocusEvent>(_ => { mFocus = "乙"; RefreshManual(); });
                m3.On<FocusEvent>(_ => { mFocus = "丙"; RefreshManual(); });
                mTabs.SelectionChanged += _ => RefreshManual();
                RefreshManual();
            }
            if (page.TryGet<TabList>("mtab-auto", out var aTabs)
                && page.TryGet<Tab>("mt-a1", out var a1)
                && page.TryGet<Tab>("mt-a2", out var a2)
                && page.TryGet<Tab>("mt-a3", out var a3)
                && page.TryGet<TextElement>("mtab-auto-read", out var aRead))
            {
                string aFocus = "—";
                string AutoSel() => a1.Selected ? "A" : a2.Selected ? "B" : a3.Selected ? "C" : "—";
                void RefreshAuto() { aRead.TextContent = "焦点 " + aFocus + " / 选中 " + AutoSel(); }
                a1.On<FocusEvent>(_ => { aFocus = "A"; RefreshAuto(); });
                a2.On<FocusEvent>(_ => { aFocus = "B"; RefreshAuto(); });
                a3.On<FocusEvent>(_ => { aFocus = "C"; RefreshAuto(); });
                aTabs.SelectionChanged += _ => RefreshAuto();
                RefreshAuto();
            }
            // lab #23 按住重复（#76）：长按 Backspace 连删到空（文字变短计数），方向键
            // 重复不产 ValueChanged——判据主体是框内文字连续消失，计数是辅助读数。
            if (page.TryGet<TextField>("kr-input", out var krInput)
                && page.TryGet<Button>("kr-fill", out var krFill)
                && page.TryGet<TextElement>("kr-count", out var krCount))
            {
                int deletes = 0; string last = krInput.Value;
                krInput.ValueChanged += _ =>
                {
                    if (krInput.Value.Length < last.Length)
                    {
                        deletes++;
                        krCount.TextContent = "删除计数 " + deletes;
                    }
                    last = krInput.Value;
                };
                krFill.Clicked += () =>
                {
                    krInput.Value = "abcdefghijk l";
                    last = krInput.Value;
                    deletes = 0;
                    krCount.TextContent = "删除计数 0";
                };
            }
        }
        if (pageName == "settings")
        {
            // Slider.ValueChanged 逐帧拖拽值 → 同步刷新旁边的数值标签。
            if (page.TryGet<Slider>("vol-master", out var vol)
                && page.TryGet<TextElement>("vol-master-val", out var volVal))
            {
                vol.ValueChanged += e => volVal.TextContent = ((int)Math.Round(e.NewValue)).ToString();
            }
            // Toggle.CheckedChanged → 日志输出（演示 checkbox 事件链）。
            if (page.TryGet<Toggle>("gfx-fullscreen", out var fs))
                fs.CheckedChanged += e => Log($"[Showcase] fullscreen = {e.NewValue}");
            // Dropdown.SelectionChanged（控件束 P3 typed 事件链：select 弹出列表选中）。
            if (page.TryGet<Dropdown>("gfx-res", out var res))
                res.SelectionChanged += e => Log($"[Showcase] gfx-res selected index = {e.NewIndex}");
            // NumberField.ValueChanged（控件束 P3：数值框，float 值经 min/max clamp + step 量化）。
            if (page.TryGet<NumberField>("snd-voices", out var voices))
                voices.ValueChanged += e => Log($"[Showcase] snd-voices = {e.NewValue}");
            // TextField.Submitted（控件束 P2：单行框回车提交）。
            if (page.TryGet<TextField>("key-custom", out var keyCustom))
                keyCustom.Submitted += v => Log($"[Showcase] key-custom submitted: \"{v}\"");
        }

        if (pageName == "character")
        {
            // Button.Clicked → ProgressBar.Value += 10（clamp 由 core 做），并刷新百分比标签。
            // EXP 条在 <stat-bar id="exp-bar"> 组件展开域内（投影内容归组件域）——两跳获取。
            ProgressBar exp = null;
            TextElement expVal = null;
            if (page.TryGet<ProgressBar>("stat-exp", out var expDirect))
            {
                exp = expDirect;   // 兼容未组件化的直排形态
                page.TryGet<TextElement>("stat-exp-val", out expVal);
            }
            else if (page.TryGet<CustomElement>("exp-bar", out var expBar))
            {
                expBar.TryGet<ProgressBar>("stat-exp", out exp);
                expBar.TryGet<TextElement>("stat-exp-val", out expVal);
            }
            if (page.TryGet<Button>("btn-train", out var train) && exp != null && expVal != null)
            {
                train.Clicked += () =>
                {
                    exp.Value = Math.Min(exp.Value + 10f, exp.Max);
                    expVal.TextContent = $"{(int)Math.Round(exp.Value)}%";
                };
            }

            // #97 min≠0 验收用例（对照式，肉眼可辨）：两条条数值相同（600/1000）——
            // 「总经验」无下限 → 60% 填充；「本级」从 500 起算（aria-valuemin）→ 只走
            // 500~1000 这一段，即 20%。按钮把「本级」的起点归零（运行时写 ProgressBar.Min，
            // 走新开放的 FFI set_control_min）：两条填充立刻对齐成同宽——同一数值、
            // 不同起点、不同填充，min 参与数学这件事一眼可读。
            ProgressBar lvl = null;
            TextElement lvlVal = null;
            if (page.TryGet<CustomElement>("min-bar2", out var lvlBar))
            {
                lvlBar.TryGet<ProgressBar>("stat-min", out lvl);
                lvlBar.TryGet<TextElement>("stat-min-val", out lvlVal);
            }
            if (page.TryGet<Button>("btn-rage", out var minBtn) && lvl != null && lvlVal != null)
            {
                minBtn.Clicked += () =>
                {
                    lvl.Min = 0f;
                    lvlVal.TextContent = $"{(int)Math.Round(lvl.Value)}/{(int)Math.Round(lvl.Max)} 起点0";
                };
            }
        }

        // form 页（角色创建表单）= 控件束 P2/P3 typed 事件主力验收页：文本框全家 + Dropdown。
        // 每个变体类型各接一条事件 → 日志，证明 C# 投影类的 typed 事件链全通。
        // 文本框 ValueChanged 逐字符触发（验收时输几个字符看几条 log）；Submitted 回车触发。
        if (pageName == "form")
        {
            // TextField：ValueChanged（逐字符）+ Submitted（回车提交）。
            if (page.TryGet<TextField>("char-name", out var name))
            {
                name.ValueChanged += e => Log($"[Showcase] char-name: \"{e.NewValue}\"");
                name.Submitted += v => Log($"[Showcase] char-name submitted: \"{v}\"");
            }
            // char-pass：password 掩码由 CSS -webkit-text-security:disc 声明（core 显示层
            // 变换，value 原文不变）；这里只 log 长度证明 value 未被掩码污染。
            if (page.TryGet<TextField>("char-pass", out var pass))
                pass.ValueChanged += e => Log($"[Showcase] char-pass changed (len={(e.NewValue?.Length ?? 0)})");
            // char-search：<input type="search"> 同样折叠为 TextField。
            if (page.TryGet<TextField>("char-search", out var search))
                search.ValueChanged += e => Log($"[Showcase] char-search: \"{e.NewValue}\"");
            // Dropdown.SelectionChanged（P3：select 弹出列表，typed 事件链）。
            if (page.TryGet<Dropdown>("char-class", out var cls))
                cls.SelectionChanged += e => Log($"[Showcase] char-class selected index = {e.NewIndex}");
            // 初始属性分配 slider：ValueChanged → 旁边数字标签（同 settings vol-master 模式）。
            // label 的 id 在 form.html 里（attr-str-val / attr-agi-val / attr-int-val）。
            string[] attrSliders = { "attr-str", "attr-agi", "attr-int" };
            foreach (string sid in attrSliders)
            {
                if (page.TryGet<Slider>(sid, out var attr)
                    && page.TryGet<TextElement>(sid + "-val", out var attrVal))
                {
                    Slider s = attr;
                    TextElement v = attrVal;
                    s.ValueChanged += e => v.TextContent = ((int)Math.Round(e.NewValue)).ToString();
                }
            }
            // TextArea.ValueChanged（P2 多行变体类型对）。
            if (page.TryGet<TextArea>("char-bio", out var bio))
                bio.ValueChanged += e => Log($"[Showcase] char-bio changed (len={(e.NewValue?.Length ?? 0)})");
        }
    }

    /// ListView 虚拟化驱动：背包 / 邮件左侧列表。
    /// runtime ListView 是数据驱动的——data-fill 只供浏览器 preview 克隆（yio-preview.js），
    /// runtime 必须业务侧设 ItemCount + BindItem 才克隆 slot 渲染 item（见 Yio.Nodes ListView）。
    /// 按 index 区分图标（Image.Src 轮换）+ badge 数量 + 耐久（背包）/ 发件人 + 主题（邮件）。子节点用
    /// Query<T> 按类型取：template 蓝图克隆后 N 个 slot 子节点 id 重复，Get<T> 全局首匹配只命中
    /// 首个 slot（Nodes.cs Get gap），故不用 id。
    /// BindItem 须先于 ItemCount 设：ItemCount setter 首次会 drain_now + DrainPendingBinds 触发 BindItem。
    void WireListViews(Container page, string pageName)
    {
        if (pageName == "inventory" && page.TryGet<ListView>("inv-list", out var invList))
        {
            string[] icons = { "item-potion", "item-chest", "item-gem", "item-scroll", "item-staff", "item-wand" };
            invList.BindItem = (item, i) =>
            {
                var dur = item.Query<ProgressBar>();
                if (dur.Count > 0) dur[0].Value = (i * 7) % 100;
                var spans = item.Query<TextElement>();
                if (spans.Count > 0) spans[0].TextContent = "x" + ((i * 13) % 99 + 1);
                var img = item.Query<Image>();
                if (img.Count > 0) img[0].Src = "res/icons/" + icons[i % icons.Length] + ".png";
            };
            invList.ItemCount = 120;
        }

        if (pageName == "mail" && page.TryGet<ListView>("mail-list", out var mailList))
        {
            string[] senders = { "系统奖励", "竞技场", "公会战报", "好友留言", "商会通知", "赛季手册" };
            string[] subjects =
            {
                "每日登录奖励已发放", "本赛季排名结算完毕", "公会贡献度更新",
                "你的基地被探访了", "本周交易汇总已生成", "新赛季手册已解锁",
                "限时活动即将开启", "背包已满请及时清理"
            };
            mailList.BindItem = (item, i) =>
            {
                var spans = item.Query<TextElement>();
                if (spans.Count >= 2)
                {
                    spans[0].TextContent = senders[i % senders.Length];
                    spans[1].TextContent = subjects[i % subjects.Length];
                }
            };
            mailList.ItemCount = 100;
        }
    }
}

/// <summary>
/// #20 RegisterComponent 摆台用的 typed 组件子类：OnConnected/OnDisconnected 静态计数 +
/// 变更事件（页面读数刷新）。构造链 protected internal 基类构造；工厂委托在
/// ShowcaseWiring.Setup 注册（setup 期）。
/// </summary>
public class LifecycleWidget : CustomElement
{
    public static int Connected;
    public static int Disconnected;
    public static event Action OnLifecycleChanged;

    public LifecycleWidget(UIContext ctx, ulong id) : base(ctx, id) { }

    protected override void OnConnected()
    {
        Connected++;
        OnLifecycleChanged?.Invoke();
    }

    protected override void OnDisconnected()
    {
        Disconnected++;
        OnLifecycleChanged?.Invoke();
    }
}
