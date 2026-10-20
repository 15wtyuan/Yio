// showcase 的 Godot 薄壳（对仗 Unity ShowcaseRunner 的生命周期壳）：
// 建 driver（boot 在 _Ready 同步完成）→ 注入五委托建 ShowcaseWiring（引擎无关接线主体，
// 202 处一行不改）→ 开场页实例化 + 接线。引擎专属页（character 3D / effects 粒子 /
// world 世界锚点）Godot 首版空缺（#117 NativeHost 后置）。

using System;
using Godot;
using Yio;

public partial class ShowcaseMain : Godot.Node
{
    GodotStageDriver _driver;
    ShowcaseWiring _wiring;
    Yio.Container _page;

    public override async void _Ready()
    {
        _driver = new GodotStageDriver();
        AddChild(_driver); // 入树同步触发 _Ready——boot 完成（失败时 Host 为 null）

        var ctx = _driver.Host?.Context;
        if (ctx == null)
        {
            GD.PrintErr("[ShowcaseMain] driver boot failed — see logs above");
            return;
        }

        _wiring = new ShowcaseWiring(
            (pkg, comp) => _driver.Instantiate(pkg, comp),
            pkg => _driver.ReadBytesFile($"ui/{pkg}.pkg.bin"),
            Show,
            msg => GD.Print($"[showcase] {msg}"),
            _driver.SetAdaptMode);
        _wiring.Setup(ctx);
        Show("home");

        var args = OS.GetCmdlineArgs();

        // 适配取证：--yio-adapt <mode> —— boot 后切换适配模式再截图（letterbox 黑边 /
        // fit 重排的肉眼强信号判据；与 --resolution <WxH> 组合用。判据注意：letterbox
        // 空边是 Godot 默认清屏深灰 RGB(76,76,76)，不是纯黑——阈值放宽到 <100）。
        int adaptIdx = Array.IndexOf(args, "--yio-adapt");
        if (adaptIdx >= 0 && adaptIdx + 1 < args.Length)
            GD.Print($"[adapt] set '{args[adaptIdx + 1]}' -> {_driver.SetAdaptMode(args[adaptIdx + 1])}");

        // 取证模式：--yio-shot <out.png> [页名] —— 入场动画稳定后整窗截图并退出
        //（批 B 验收证据链：程序化截图 vs Unity 基线并排归因；CI smoke 复用同路径）。
        int shotIdx = Array.IndexOf(args, "--yio-shot");
        if (shotIdx >= 0 && shotIdx + 1 < args.Length)
        {
            if (shotIdx + 2 < args.Length) Show(args[shotIdx + 2]);
            await RunShot(args[shotIdx + 1]);
        }
        // 交互取证：--yio-tap <cardId> <out.png> —— 读导航卡世界矩形中心，程序化注入
        // 指针事件（Motion→Down→Up，走引擎输入管线 → collector → FFI → core 命中），
        // 落定后截图。判据 = 页面切到目标页（点击→读数/画面翻转的最响信号）。
        int tapIdx = Array.IndexOf(args, "--yio-tap");
        if (tapIdx >= 0 && tapIdx + 2 < args.Length)
        {
            await RunTap(args[tapIdx + 1], args[tapIdx + 2]);
        }
        // CI smoke（#14 硬验收）：headless 三档断言——帧级（帧循环推进）/树级
        //（DumpSceneJson 节点数 + 关键元素存在）/投影级（镜像池对象数下限——两引擎
        // 同量级 = 渲染树投影不分叉强信号）。退出码 0/1 + "[smoke] ALL PASS" 输出
        //（spike 教训：C# 挂了 Godot 退出码仍 0，CI 须 grep 输出双断言）。
        if (Array.IndexOf(args, "--yio-smoke") >= 0)
        {
            await RunSmoke();
        }
        // 手势取证（#59 批1 swipe 顺验）：--yio-swipe <out.png> —— swipe 页 banner 中心
        // 注入拖拽（Down → 10 帧 Move 流 → Up，跨 40% 宽触发翻页），落定后读 PageIndex
        // 读数（sw-idx 应 "0"→"1"）+ 截图。手势物理全在 core（引擎无关），本模式验证
        // 事件流在 Godot 管线不断链。
        int swipeIdx = Array.IndexOf(args, "--yio-swipe");
        if (swipeIdx >= 0 && swipeIdx + 1 < args.Length)
        {
            await RunSwipe(args[swipeIdx + 1]);
        }
    }

    async System.Threading.Tasks.Task RunSwipe(string outPath)
    {
        Show("swipe");
        for (int i = 0; i < 30; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_page == null || !_page.TryGet<Yio.Container>("sw-banner", out var banner))
        {
            GD.PrintErr("[yio-swipe] sw-banner not found");
            GetTree().Quit(1);
            return;
        }
        var r = banner.Geometry.WorldRect;
        float y = r.Y + r.Height * 0.5f;
        float x0 = r.X + r.Width * 0.5f;
        float xEnd = x0 - r.Width * 0.4f;   // 左滑 40%（> 1/3 翻页阈值）

        void Inject(Vector2 pos, bool? pressed)
        {
            if (pressed == null) Input.ParseInputEvent(new InputEventMouseMotion { Position = pos });
            else Input.ParseInputEvent(new InputEventMouseButton
            {
                Position = pos, ButtonIndex = MouseButton.Left, Pressed = pressed.Value,
            });
        }

        Inject(new Vector2(x0, y), null);
        Inject(new Vector2(x0, y), true);
        for (int i = 0; i < 5; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        for (int s = 1; s <= 10; s++)
        {
            Inject(new Vector2(x0 + (xEnd - x0) * s / 10f, y), null);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        Inject(new Vector2(xEnd, y), false);
        for (int i = 0; i < 45; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        GD.Print($"[yio-swipe] scroll state after release:\n{_driver.Host.DumpSceneTree("sw-banner")}");

        // 读数双类型尝试：HTML div 的投影类型按内容判定（AGENTS：div 配 TryGet<TextElement>
        // 是静默死线——Container 也有 TextContent，两种都试并报告命中类型）。
        string idx = "(missing)";
        if (_page.TryGet<Yio.TextElement>("sw-idx", out var te)) idx = te.TextContent + " [TextElement]";
        else if (_page.TryGet<Yio.Container>("sw-idx", out var cc)) idx = cc.TextContent + " [Container]";
        GD.Print($"[yio-swipe] PageIndex readout after swipe = '{idx}' (expect '1...')");
        // scroll 探针：pos 反映 snap 物理（0→720 = 翻一页）。读数/dots 不随动 =
        // PageChanged 事件→wiring 读数链问题（#59 既有债，见 issue；与 Godot 无关）。
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(outPath);
        GetTree().Quit(idx.StartsWith("1") ? 0 : 1);
    }

    async System.Threading.Tasks.Task RunSmoke()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            GD.Print($"[smoke] {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? $" — {detail}" : "")}");
            if (!ok) failures++;
        }

        ulong frame0 = Engine.GetProcessFrames();
        for (int i = 0; i < 30; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        // 30 次 await 对应 29-30 帧差（首次 await 时当前帧已计数）——断言下限 25 即可，
        // 判据是「帧循环在推进」不是精确计数。
        Check("frame-loop", Engine.GetProcessFrames() - frame0 >= 25,
            $"frames={Engine.GetProcessFrames() - frame0}");

        string json = _driver?.Host?.DumpSceneJson();
        Check("scene-tree", !string.IsNullOrEmpty(json) && json.Length > 2000,
            $"dumpLen={json?.Length ?? 0}");
        Check("nav-card-present", json?.Contains("nav-shop") == true);

        int mirror = _driver?.Backend?.MirrorCount ?? 0;
        Check("mirror-pool", mirror > 20, $"objs={mirror}");

        GD.Print(failures == 0 ? "[smoke] ALL PASS" : $"[smoke] {failures} FAILURES");
        GetTree().Quit(failures == 0 ? 0 : 1);
    }

    async System.Threading.Tasks.Task RunTap(string cardId, string outPath)
    {
        for (int i = 0; i < 30; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_page == null || !_page.TryGet<Yio.Button>(cardId, out var card))
        {
            GD.PrintErr($"[yio-tap] card '{cardId}' not found");
            GetTree().Quit(1);
            return;
        }
        var r = card.Geometry.WorldRect;
        float cx = r.X + r.Width * 0.5f, cy = r.Y + r.Height * 0.5f;
        GD.Print($"[yio-tap] tapping '{cardId}' at ({cx:F0},{cy:F0}) rect=({r.X:F0},{r.Y:F0},{r.Width:F0}x{r.Height:F0})");

        var move = new InputEventMouseMotion { Position = new Vector2(cx, cy) };
        var down = new InputEventMouseButton
            { Position = new Vector2(cx, cy), ButtonIndex = MouseButton.Left, Pressed = true };
        var up = new InputEventMouseButton
            { Position = new Vector2(cx, cy), ButtonIndex = MouseButton.Left, Pressed = false };
        Input.ParseInputEvent(move);
        for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(down);
        for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Input.ParseInputEvent(up);
        for (int i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(outPath);
        GD.Print($"[yio-tap] saved {outPath} (page after tap — should be the card's target)");
        GetTree().Quit(0);
    }

    async System.Threading.Tasks.Task RunShot(string outPath)
    {
        const int settleFrames = 60; // 入场动画落定（交错入场 ~0.5s@60fps）
        for (int i = 0; i < settleFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(outPath);
        GD.Print($"[yio-shot] saved {outPath} ({img.GetWidth()}x{img.GetHeight()})");
        GetTree().Quit(0);
    }

    void Show(string page)
    {
        _wiring?.TeardownPage();
        _page?.Dispose();
        _page = _driver.Instantiate("showcase", page);
        if (_page != null)
            _wiring.WirePage(_page, page);
    }
}
