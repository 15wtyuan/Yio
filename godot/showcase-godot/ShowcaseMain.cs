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
            // 运行时适配模式切换首版不支持（适配是后续批）。
            _ => false);
        _wiring.Setup(ctx);
        Show("home");

        // 取证模式：--yio-shot <out.png> [页名] —— 入场动画稳定后整窗截图并退出
        //（批 B 验收证据链：程序化截图 vs Unity 基线并排归因；CI smoke 复用同路径）。
        var args = OS.GetCmdlineArgs();
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
