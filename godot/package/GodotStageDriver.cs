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
        float _designW, _designH;   // 设计分辨率（manifest 正主；_Ready 解析存底）
        uint _modeU32;              // 适配模式（0=letterbox 1=fit-width 2=fit-height）

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

            // 适配（三模式策略数学在 Rust——compute_adaptation 单源）：manifest 的
            // design/match_mode 是正主。1:1 窗口时结果即恒等映射。
            _designW = designW; _designH = designH;
            _modeU32 = Yio.Bindings.YioAdaptMode.FromString(runtime?.match_mode) ?? 0u;
            RecomputeAdaptation();

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

        public override void _Notification(int what)
        {
            // 窗口 resize → 适配重算（fit 模式画布一维随窗口流动；letterbox 黑边重居中）。
            if (what == NotificationWMSizeChanged)
                RecomputeAdaptation();
        }

        /// <summary>
        /// 运行时切换适配模式（letterbox / fit-width / fit-height）——立即重算并喂 Stage
        /// 重排（fit 模式画布随屏幕变，vw/vh/% 当帧跟随）。对仗 Unity Driver.SetAdaptMode。
        /// 未知字符串返 false 不动现状。
        /// </summary>
        public bool SetAdaptMode(string mode)
        {
            var m = Yio.Bindings.YioAdaptMode.FromString(mode);
            if (!m.HasValue) return false;
            _modeU32 = m.Value;
            RecomputeAdaptation();
            return true;
        }

        /// <summary>
        /// 适配重算：窗口/安全区 → Rust compute_adaptation（单源）→ 三出口：canvas 变换
        ///（渲染：screen = design·scale + offset——y-down 同向无翻转，对仗 Unity root
        /// transform 的 scale+y-flip）、SetRootSize（fit 模式一维流动）、输入映射注入。
        /// </summary>
        unsafe void RecomputeAdaptation()
        {
            if (_host == null || _input == null) return;
            float dw = _designW > 0 ? _designW : 1920f;
            float dh = _designH > 0 ? _designH : 1080f;
            var vpSize = GetViewport().GetVisibleRect().Size;
            float sw = vpSize.X, sh = vpSize.Y;
            // safe 区桌面恒传全窗：Godot DisplayServer.GetDisplaySafeArea 是显示器全局
            // 坐标（多显示器下含跨屏原点，直接传会污染 contain 框——实测 off=(1963,0)）。
            // notch/圆角是移动端议题，届时按窗口位置换算局部 safe（Unity 侧 Screen.safeArea
            // 语义 = 窗口相对 top-down）。
            float safeX = 0f, safeY = 0f, safeW = sw, safeH = sh;

            var r = new Yio.Bindings.AdaptResult();
            if (Yio.Bindings.Native.yio_compute_adaptation(
                    dw, dh, sw, sh, safeX, safeY, safeW, safeH, _modeU32, &r) != 0)
            {
                GD.PrintErr("[GodotStageDriver] yio_compute_adaptation failed — keeping current mapping");
                return;
            }
            _input.MapScale = r.scale;
            _input.MapOffX = r.offset_x;
            _input.MapOffY = r.offset_y;

            // canvas 变换：design → screen（含 letterbox 偏移与缩放）。RS per-canvas 版本
            //（viewport 属性对 RS 直建 canvas 无效——GDScript 探针实验定案；item 变换与
            // canvas 变换叠加语义 screen = canvasT × itemT × vertex 已实测验证）。
            var adaptT = new Transform2D(
                new Vector2(r.scale, 0f), new Vector2(0f, r.scale),
                new Vector2(r.offset_x, r.offset_y));
            RenderingServer.ViewportSetCanvasTransform(GetViewport().GetViewportRid(), _canvas, adaptT);

            // clip shader 的 design 测试空间依赖适配逆映射（全局 uniform——所有材质共享，
            // canvas 变换后的 FRAGCOORD 须映回 design 才能对 clip 链 inv_frame 测试）。
            RenderingServer.GlobalShaderParameterSet("yio_adapt_off",
                new Vector2(r.offset_x, r.offset_y));
            RenderingServer.GlobalShaderParameterSet("yio_adapt_scale",
                r.scale > 0f ? r.scale : 1f);

            if (!_host.SetRootSize(r.root_w, r.root_h))
                GD.PrintErr($"[GodotStageDriver] set_root_size({r.root_w},{r.root_h}) rejected");
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
