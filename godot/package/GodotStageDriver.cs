// GodotStageDriver：Godot 场景驱动（对仗 unity YioStageDriver）。
//
// 启动序与 Unity 版同构：读 yio.runtime.json → MaterialCache/backend → YioHost →
// canvas RID 注入 → YioBootstrapper.Boot（packages→atlas→image sizes）→ InitSprites →
// RegisterFonts → EnsureSceneRoot → 消费者 Instantiate。每帧 _Process 调 host.Step
// （引擎无关驱动序：CollectInput→逻辑泵→flush→tick→borrow_frame→SyncFrame→events）。
//
// 与 Unity 版的差异：
//   - 渲染根 = Canvas RID（CanvasCreate + ViewportAttachCanvas），不建 GameObject/相机
//     （2D canvas 直投窗口；letterbox/fit 适配是后续批——首版窗口=设计分辨率 1:1）。
//   - 产物目录直读（FileAccess 绝对路径）——Unity/Godot 吃同一份 yio build 输出。
//   - ExecuteAlways/domain reload 类 Unity 生命周期坑不存在（进程退出即清）。

using System;
using System.IO;
using Godot;

namespace Yio
{
    public partial class GodotStageDriver : Godot.Node
    {
        /// <summary>
        /// 产物根目录（含 yio.runtime.json + ui/ + atlas/ + fonts/）。空 = 默认
        /// 引擎仓 showcase 产物（res:// 相对解析 —— dev 工程默认对齐 Unity 工程吃同一份）。
        /// </summary>
        [Export] public string BundlesRoot = "";

        GodotMaterialCache _materials;
        GodotYioBackend _backend;
        GodotInputCollector _input;
        YioHost _host;
        string _root;
        Rid _canvas;

        public YioHost Host => _host;
        public GodotYioBackend Backend => _host != null ? _backend : null;

        public override void _Ready()
        {
            _root = ResolveBundlesRoot();

            // manifest 先读（设计分辨率/字体清单正主）。
            RuntimeManifest runtime = null;
            string runtimeJson = ReadTextFile("yio.runtime.json");
            if (!string.IsNullOrEmpty(runtimeJson))
            {
                try { runtime = RuntimeManifest.ParseRuntime(runtimeJson); }
                catch (Exception e)
                {
                    // 解析失败 = 产物契约破裂，阻断启动（对仗 Unity 版语义）。
                    GD.PrintErr($"[GodotStageDriver] Failed to parse yio.runtime.json — UI install aborted: {e.Message}");
                    return;
                }
            }
            else
            {
                GD.PrintErr($"[GodotStageDriver] yio.runtime.json not found under {_root} — UI install aborted");
                return;
            }

            float designW = runtime?.design?.w ?? 1920f;
            float designH = runtime?.design?.h ?? 1080f;
            _materials = new GodotMaterialCache();
            _backend = new GodotYioBackend(_materials);
            try
            {
                _host = new YioHost(designW, designH, _backend);
            }
            catch (Exception e)
            {
                GD.PrintErr($"[GodotStageDriver] YioHost construct failed: {e.Message}");
                return;
            }

            // 缺字诊断 + 运行时警告面（dev 工程恒接——对仗 Unity 的 EDITOR/DEVELOPMENT_BUILD 门）。
            _host.MissingGlyphReport += msg => GD.PrintErr($"[Yio] missing glyphs (tofu):\n{msg}");
            _host.RuntimeWarning += msg => GD.PrintErr($"[Yio] {msg}");

            // 渲染根：canvas 挂当前 viewport（design 坐标 1:1 直投——FRAGCOORD/canvas 均 y-down）。
            _canvas = RenderingServer.CanvasCreate();
            RenderingServer.ViewportAttachCanvas(GetViewport().GetViewportRid(), _canvas);
            _input = new GodotInputCollector();
            _backend.SetRuntimeRoot(_canvas, _input);
            _host.SetRootSize(designW, designH);

            // 引导序列（引擎无关）：packages → atlas manifests → image sizes。
            var boot = new YioBootstrapper(_host, new BundlesLoadSource(this),
                msg => GD.PrintErr($"[GodotStageDriver] {msg}"));
            var bootResult = runtime != null ? boot.Boot(runtime) : null;

            // 图集页懒加载（atlas/{page}.png → ImageTexture）。
            _backend.InitSprites(bootResult?.Atlases, pageName =>
            {
                var img = Godot.Image.LoadFromFile(Path.Combine(_root, "atlas", pageName));
                return img != null ? ImageTexture.CreateFromImage(img) : null;
            });

            // manifest 字体注册（family/file/default/fallback + 回退链）。
            boot.RegisterFonts(runtime);

            try { _host.EnsureSceneRoot(); }
            catch (Exception e) { GD.PrintErr($"[GodotStageDriver] {e.Message}"); }
        }

        public override void _Process(double delta)
        {
            _host?.Step((float)delta);
        }

        /// <summary>引擎事件 → collector 缓冲（CollectInput 帧消费；窗口 top-left y-down
        /// 与 design 同构，坐标零映射直入）。</summary>
        public override void _Input(InputEvent @event)
        {
            _input?.OnInputEvent(@event);
        }

        public override void _ExitTree()
        {
            _host?.Dispose();
            _backend?.Dispose();
            _materials?.Clear();
            if (_canvas.IsValid) RenderingServer.FreeRid(_canvas);
            _host = null;
            _backend = null;
        }

        /// <summary>页面实例化（pkg 必须已在 manifest packages 段——_Ready 已 load）。</summary>
        public Container Instantiate(string pkgName, string compPath)
        {
            try { return _host.Instantiate(pkgName, compPath); }
            catch (Exception e)
            {
                GD.PrintErr($"[GodotStageDriver] Instantiate({pkgName},{compPath}) failed: {e.Message}");
                return null;
            }
        }

        /// <summary>产物目录绝对路径解析。默认 = res:// 相对定位引擎仓 showcase 产物。</summary>
        string ResolveBundlesRoot()
        {
            if (!string.IsNullOrEmpty(BundlesRoot)) return BundlesRoot;
            string proj = ProjectSettings.GlobalizePath("res://");
            return Path.GetFullPath(Path.Combine(proj, "..", "..",
                "unity", "showcase-unity", "Assets", "Bundles"));
        }

        // —— 产物目录加载钩子（IYioLoadSource；消费者覆写链锚点，对仗 Unity Driver virtual）——

        public virtual string ReadTextFile(string relPath)
        {
            string p = Path.Combine(_root, relPath);
            return Godot.FileAccess.FileExists(p) ? Godot.FileAccess.GetFileAsString(p) : null;
        }

        public virtual byte[] ReadBytesFile(string relPath)
        {
            string p = Path.Combine(_root, relPath);
            if (!Godot.FileAccess.FileExists(p)) return null;
            using var f = Godot.FileAccess.Open(p, Godot.FileAccess.ModeFlags.Read);
            return f?.GetBuffer((long)f.GetLength());
        }

        sealed class BundlesLoadSource : IYioLoadSource
        {
            readonly GodotStageDriver _d;
            public BundlesLoadSource(GodotStageDriver d) { _d = d; }
            public string ReadText(string relPath) => _d.ReadTextFile(relPath);
            public byte[] ReadBytes(string relPath) => _d.ReadBytesFile(relPath);
            public byte[] ReadPackageBytes(string pkgName) => _d.ReadBytesFile($"ui/{pkgName}.pkg.bin");
            public byte[] ReadFontBytes(string fontFile) => _d.ReadBytesFile($"fonts/{fontFile}");
        }
    }
}
