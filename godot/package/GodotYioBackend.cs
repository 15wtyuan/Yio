// GodotYioBackend：Godot 引擎后端（对仗 UnityYioBackend）。
//
// YioHost 契约驱动：每帧 CollectInput（首版空实现——输入采集是独立批）→ SyncFrame
// （borrow_frame 已由 YioHost 完成）：blob 复制 → 字体 atlas 脏页上传 → MirrorPool.Sync
// （渲染树镜像，策略在中性层）。NativeHost（#117 3D/粒子绑定）Godot 首版空缺。

using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Godot;
using Yio.Bindings;

namespace Yio
{
    public sealed unsafe class GodotYioBackend : YioBackend, IDisposable
    {
        readonly MirrorPool _pool = new();
        readonly GodotMirrorHost _mirror;
        internal readonly GodotSpriteResolver Sprites = new();
        Rid _canvas;                       // 屏幕 root canvas（driver 经 SetRuntimeRoot 注入）
        GodotInputCollector _input;        // Driver 注入（事件缓冲 + 帧消费）
        byte[] _frameBuf;                  // ArrayPool 复用（同 Unity 版）
        uint _lastFfiPanicCount;           // FFI panic 兜底计数上次采样
        // 字体页纹理拥有者（resolver 只存 RID）——Dispose 时统一释放。
        readonly System.Collections.Generic.Dictionary<uint, ImageTexture> _fontPages = new();

        public GodotYioBackend(GodotMaterialCache materials)
        {
            _mirror = new GodotMirrorHost(materials, Sprites);
        }

        /// <summary>driver 注入渲染根 canvas + 输入采集器（必须在第一次 SyncFrame 前）。</summary>
        public void SetRuntimeRoot(Rid canvas, GodotInputCollector input)
        {
            _canvas = canvas;
            _input = input;
        }

        /// <summary>图集页懒加载入口（driver 经 InitSprites 注入产物目录）。</summary>
        public void InitSprites(System.Collections.Generic.List<AtlasManifest> atlases,
            Func<string, ImageTexture> loadPage) =>
            Sprites.Init(atlases, loadPage);

        /// <summary>
        /// 输入采集帧消费：collector 缓冲的指针/键盘/滚轮/文本 → set_input 系 FFI
        ///（引擎中立，collector 内直调——对仗 Unity 版四段采集）。
        /// </summary>
        public override void CollectInput(IntPtr stage)
        {
            _input?.Collect(stage);
        }

        public override void SyncFrame(IntPtr stage, IntPtr framePtr, int frameLen)
        {
            WarnOnNativePanic();
            if (framePtr == IntPtr.Zero || frameLen <= 0 || !_canvas.IsValid) return;
            StageHandle* h = (StageHandle*)stage.ToPointer();

            if (_frameBuf == null || _frameBuf.Length < frameLen)
            {
                if (_frameBuf != null) ArrayPool<byte>.Shared.Return(_frameBuf);
                _frameBuf = ArrayPool<byte>.Shared.Rent(frameLen);
            }
            Marshal.Copy(framePtr, _frameBuf, 0, frameLen);
            var blob = new FrameBlob(_frameBuf);

            SyncFontAtlas(h);
            _pool.Sync(blob, _canvas, _mirror);
        }

        void WarnOnNativePanic()
        {
            uint count = Native.yio_ffi_panic_count();
            if (count == _lastFfiPanicCount) return;
            GD.PrintErr($"[Yio] native panic caught by FFI guard (total {count}); frame state may be inconsistent");
            _lastFfiPanicCount = count;
        }

        // 字体 atlas 脏页：FontAtlasSync 协议（引擎无关）→ Image R8 → ImageTexture → resolver。
        // R8 单通道是 data 格式（无 sRGB 解码），直读 raw distance——对齐 Unity R8 linear 语义。
        unsafe void SyncFontAtlas(StageHandle* h)
        {
            FontAtlasSync.Pull(
                (buf, cap) => (int)Native.yio_stage_font_atlas_dirty_pages(h, buf, cap),
                (page, w, ph, buf, cap) => (int)Native.yio_stage_font_atlas_page(h, page, w, ph, buf, cap),
                () => Native.yio_stage_font_atlas_clear_dirty(h),
                (page, w, ph, buf, len) =>
                {
                    var img = Godot.Image.CreateFromData((int)w, (int)ph, false, Godot.Image.Format.R8,
                        new ReadOnlySpan<byte>(buf, 0, len).ToArray());
                    var tex = ImageTexture.CreateFromImage(img);
                    _fontPages[page] = tex;
                    Sprites.RegisterFontAtlasPage(FontAtlasPath.Format(page), tex);
                },
                msg => GD.PrintErr($"[GodotYioBackend] {msg}"));
        }

        /// <summary>当前镜像渲染对象数（CI smoke 树级/投影不分叉断言用——两引擎同量级）。</summary>
        public int MirrorCount => _pool.Count;

        /// <summary>dump 当前镜像池状态（Godot 渲染视角，批 B 验收探针用）。</summary>
        public string DumpMirrorState()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[MirrorPool] objs={_pool.Count} canvas={_canvas}");
            return sb.ToString();
        }

        public void Dispose()
        {
            _pool.Clear();
            _mirror.Dispose();
            Sprites.Clear();
            foreach (var kv in _fontPages) kv.Value?.Dispose();
            _fontPages.Clear();
            if (_frameBuf != null)
            {
                ArrayPool<byte>.Shared.Return(_frameBuf);
                _frameBuf = null;
            }
        }
    }
}
