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
