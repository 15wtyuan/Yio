// FontAtlasSync：核心字体图集脏页拉取协议（引擎无关单点）。
//
// 协议（stage 级 / host 级 FFI 同构，仅句柄不同）：
//   ① 拉脏页列表（cap 16；通常 ≤8 页，单字体极少超 16）→ 超出告警截断；
//   ② 双调法取页数据：先探所需字节数（buf=null）→ 分配 → 再调填 w/h/bytes；
//   ③ 页数据交引擎 sink 上传纹理（R8、必须 linear——见 Unity 实现处注释）；
//   ④ 清脏页标记。
// 页是 append-only（native 侧不重排，旧字形 UV 永不变），脏页只可能是新页或原页扩容。
//
// 引擎侧经 delegate 注入 FFI 适配（host/stage 句柄闭包）与纹理上传；Godot 后端复用
// 同一份协议实现，只写自己的 sink。

using System;
using System.Buffers;

namespace Yio
{
    /// <summary>
    /// Font-atlas image_path construction. Must match Rust render::font_atlas_path
    /// (image_path field in blob). Changing the format here requires changing both sides.
    /// </summary>
    public static class FontAtlasPath
    {
        public static string Format(uint page) => $"yio://font-atlas/p{page}";
    }

    /// <summary>脏页拉取协议（引擎无关）。上传细节（纹理格式/linear/缓存策略）归引擎 sink。</summary>
    public static unsafe class FontAtlasSync
    {
        /// <summary>单次拉取的脏页上限（超出告警截断——通常 ≤8 页，单字体极少超 16）。</summary>
        public const int MaxDirty = 16;

        /// <summary>拉脏页列表：写入 buf，返回总数（可 > cap，调用方截断）。</summary>
        public delegate int DirtyPagesFn(uint* buf, nuint cap);

        /// <summary>取页数据：buf=null 探所需字节数（不写 w/h）；否则填 w/h/bytes，返回写入字节数。</summary>
        public delegate int GetPageFn(uint page, uint* w, uint* h, byte* buf, nuint cap);

        /// <summary>页上传 sink：buf 是租用缓冲（len 有效字节），同步消费（fixed 钉住上传）。</summary>
        public delegate void UploadFn(uint page, uint w, uint h, byte[] buf, int len);

        /// <summary>协议告警出口（引擎侧接自家日志）。</summary>
        public delegate void WarnFn(string message);

        public static void Pull(DirtyPagesFn dirtyPages, GetPageFn getPage, Action clearDirty,
                                UploadFn upload, WarnFn warn)
        {
            uint* dirtyPtr = stackalloc uint[MaxDirty];
            int n = dirtyPages(dirtyPtr, (nuint)MaxDirty);
            if (n <= 0) return;
            if (n > MaxDirty)
            {
                warn($"font atlas dirty pages ({n}) exceed MaxDirty ({MaxDirty}); skipping extras");
                n = MaxDirty;
            }

            for (int i = 0; i < n; i++)
            {
                uint page = dirtyPtr[i];
                uint w = 0, h = 0;
                // 探所需字节数（buf_len=0, buf=null → 返 needed 不写 w/h/pixels）。
                int needed = getPage(page, &w, &h, null, (nuint)0);
                if (needed <= 0) continue;

                byte[] buf = ArrayPool<byte>.Shared.Rent(needed);
                try
                {
                    fixed (byte* pBuf = buf)
                    {
                        int got = getPage(page, &w, &h, pBuf, (nuint)needed);
                        if (got != needed) continue;
                    }
                    upload(page, w, h, buf, needed);
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }
            clearDirty();
        }
    }
}
