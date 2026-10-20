// GodotMirrorHost：镜像渲染的 Godot 实现（RenderingServer 直连，零 Node）。
//
// 分工对仗 UnityMirrorHost：diff/复用/保活策略全在引擎无关 MirrorPool（csharp/core），
// 本文件只做「创建/更新/销毁引擎渲染对象」。与 Unity 版（GO+MeshFilter+MeshRenderer+MPB）
// 的模型差异：
//   - 渲染对象 = CanvasItem RID；mesh = ArrayMesh（canvas_item_add_mesh 命令持 mesh RID，
//     surface 内容更新即时生效——FULL 帧无需重提交 draw 命令，仅纹理变化时 clear+re-add）。
//   - 线性矩阵（rotate/scale）直接进 CanvasItemSetTransform 的 Transform2D——Godot 2D
//     仿射原生，无 Unity「transform 只带平移、线性部分走 shader _ObjM」的绕路。
//   - 画序 = canvas_item_set_draw_index（sort_key 直映射；Unity 是 MeshRenderer.sortingOrder）。
//   - 节点 alpha = canvas_item_set_modulate（Unity 是 MPB _Alpha；canvas 管线自动乘进
//     顶点色，等价）。
//   - 剔除（#66）：Godot canvas item 无 Unity 式 renderer.bounds 剔除，CompensateBounds
//     空实现（IMirror 契约明确允许「剔除基于实时数据的引擎」空实现）。

using System.Collections.Generic;
using Godot;

namespace Yio
{
    /// <summary>Godot 镜像宿主：CanvasItem 工厂 + 材质面 + clip/逐出心跳（首版 no-op）。</summary>
    public sealed class GodotMirrorHost : IMirrorHost
    {
        internal readonly GodotMaterialCache Materials;
        internal readonly GodotSpriteResolver Sprites;
        /// <summary>纯色兜底纹理（1×1 白，对仗 Unity Texture2D.whiteTexture）。</summary>
        internal readonly Rid WhiteTex;
        // ImageTexture 包装对象必须持引用：只留 RID 的话 C# GC 会释放 native 纹理
        //（GodotObject 终结器释放 RID——散链接同程序集后全字段持有是铁律）。
        readonly ImageTexture _whiteTexOwner;

        /// <summary>跨 Stage 排序基址（多 Stage 时由驱动下发；单 Stage 恒 0）。</summary>
        internal int SortBase = 0;

        public GodotMirrorHost(GodotMaterialCache materials, GodotSpriteResolver sprites)
        {
            Materials = materials;
            Sprites = sprites;
            var img = Godot.Image.CreateFromData(1, 1, false, Godot.Image.Format.Rgba8,
                new byte[] { 255, 255, 255, 255 });
            _whiteTexOwner = ImageTexture.CreateFromImage(img);
            WhiteTex = _whiteTexOwner.GetRid();
        }

        public IMirrorObject CreateObject(object parent) =>
            new GodotMirrorObj(this, parent);

        public bool IsContainerAlive(object container) => container is Rid r && r.IsValid;

        /// <summary>
        /// clip 链推送：中性 MirrorPool 每帧对 blob 表里每个 ctx 全量重设（idle 全 Skip
        /// 帧也刷）——经 cache 写该 ctx 的共享 + per-item 材质。
        /// </summary>
        public void PushClipEntries(uint clipContext, List<ClipEntryView> entries) =>
            Materials.SetClipEntries(clipContext, entries);

        /// <summary>#62 页逐出心跳：首版 no-op（Godot 侧逐出未接，页常驻）。</summary>
        public void SweepPages() { }

        internal void Dispose()
        {
            // 经包装对象释放(而非裸 FreeRid)——终结器不再重复释放同一 RID。
            _whiteTexOwner?.Dispose();
        }
    }

    /// <summary>Godot 渲染对象：CanvasItem RID + ArrayMesh 包装。</summary>
    public sealed class GodotMirrorObj : IMirrorObject
    {
        readonly GodotMirrorHost _host;
        readonly Rid _item;
        readonly ArrayMesh _mesh;
        // draw 命令当前绑定的纹理 RID（变化时 clear+re-add）。default = 未提交。
        Rid _lastTex;
        Rid _parent;
        bool _visible = true;
        // UpdateHeader 解析的 sprite look——UploadMesh 的 UV 重映射复用（调用序恒 Header 先）。
        GodotSpriteLookup _look;

        internal GodotMirrorObj(GodotMirrorHost host, object parent)
        {
            _host = host;
            _item = RenderingServer.CanvasItemCreate();
            _mesh = new ArrayMesh();
            SetParentRid((Rid)parent);
        }

        public bool VisuallyActive => _item.IsValid && _visible;

        /// <summary>#62 盖章：首版 no-op（Godot 侧逐出未接）。</summary>
        public void StampPage() { }

        public void SetActive(bool active)
        {
            if (_visible == active) return;
            _visible = active;
            RenderingServer.CanvasItemSetVisible(_item, active);
        }

        /// <summary>header 级更新：parent/变换/画序/modulate/材质/纹理。</summary>
        public void UpdateHeader(FrameBlob blob, int i, object parent)
        {
            var p = (Rid)parent;
            if (_parent != p) SetParentRid(p);

            // 完整 world affine → Transform2D（列向量约定：p' = origin + x·xAxis + y·yAxis，
            // 与 blob 六元组 x'=a·x+c·y+tx / y'=b·x+d·y+ty 逐项对应）。
            // 顶点已 re-base 到节点本地——矩阵把本地映到 design world（canvas 坐标 1:1）。
            RenderingServer.CanvasItemSetTransform(_item, new Transform2D(
                new Vector2(blob.Ma(i), blob.Mb(i)),
                new Vector2(blob.Mc(i), blob.Md(i)),
                new Vector2(blob.Mtx(i), blob.Mty(i))));
            RenderingServer.CanvasItemSetDrawIndex(_item, (int)blob.SortKey(i) + _host.SortBase);
            // 节点 alpha：canvas 管线自动 modulate × 顶点色（等价 Unity col.a *= _Alpha）。
            RenderingServer.CanvasItemSetModulate(_item, new Color(1f, 1f, 1f, blob.Alpha(i)));

            // 材质：program 3-7 的 per-item 参数（色彩矩阵/文字效果/shadow/grad）走
            // per-item clone 材质（Unity MPB 的对仗——共享材质 uniform 无 per-renderer 通道）；
            // 其余（含 program 0/1/2 无效果）走 (program, ctx) 共享缓存。
            byte prog = blob.Program(i);
            uint maskCtx = blob.MaskContext(i);
            Rid mat;
            if (NeedsItemMaterial(blob, i, prog))
            {
                EnsureItemMaterial(prog, maskCtx);
                WriteItemParams(blob, i, prog);
                mat = _itemMat;
            }
            else
            {
                mat = _host.Materials.Get(prog, maskCtx);
            }
            RenderingServer.CanvasItemSetMaterial(_item, mat);

            // 纹理解析：path_idx → path → sprite（字体页优先）；miss → 白纹理。
            _look = default;
            Rid tex = _host.WhiteTex;
            uint pathIdx = blob.PathIdx(i);
            if (pathIdx != 0)
            {
                string path = blob.ReadPath(pathIdx);
                if (!string.IsNullOrEmpty(path))
                {
                    _look = _host.Sprites.GetSprite(path);
                    if (_look.Found) tex = _look.Tex;
                }
            }

            // draw 命令：mesh RID 引用不变——几何更新（FULL）无需重提交；仅纹理变化时
            // clear + re-add（add_mesh 的 texture 参数绑定 shader 内建 TEXTURE）。
            if (_lastTex != tex)
            {
                RenderingServer.CanvasItemClear(_item);
                RenderingServer.CanvasItemAddMesh(_item, _mesh.GetRid(), null, null, tex);
                _lastTex = tex;
            }
        }

        // —— per-item 效果材质（program 3-7 / program 1 带效果；Unity MPB 的对仗）——

        Rid _itemMat;

        /// <summary>program 3-7 恒 per-item（core 产 program 即带参数）；1 仅效果非零时。</summary>
        static bool NeedsItemMaterial(FrameBlob blob, int i, byte prog)
        {
            if (prog >= 3 && prog <= 7) return true;
            if (prog != 1) return false;
            float[] eb = blob.EffectBlock(i);
            for (int k = 0; k < eb.Length; k++)
                if (eb[k] != 0f) return true;
            return false;
        }

        void EnsureItemMaterial(int program, uint ctx)
        {
            if (_itemMat.IsValid) return;
            _itemMat = _host.Materials.CreateItemMaterial(program, ctx);
        }

        /// <summary>per-item uniform 写入（每帧重写——与 Unity MPB 每帧 SetPropertyBlock 同节奏）。</summary>
        void WriteItemParams(FrameBlob blob, int i, byte prog)
        {
            Rid m = _itemMat;
            if (prog == 1)
            {
                float[] eb = blob.EffectBlock(i);
                RenderingServer.MaterialSetParam(m, "outline_width", eb[0]);
                RenderingServer.MaterialSetParam(m, "outline_color", new Color(eb[1], eb[2], eb[3], eb[4]));
                for (int s = 0; s < 3; s++)
                {
                    int b = 5 + s * 7;
                    RenderingServer.MaterialSetParam(m, $"underlay_offset{s}", new Vector4(eb[b], eb[b + 1], 0f, 0f));
                    RenderingServer.MaterialSetParam(m, $"underlay_softness{s}", eb[b + 2]);
                    RenderingServer.MaterialSetParam(m, $"underlay_color{s}", new Color(eb[b + 3], eb[b + 4], eb[b + 5], eb[b + 6]));
                }
                RenderingServer.MaterialSetParam(m, "glow_power", eb[26]);
                RenderingServer.MaterialSetParam(m, "glow_color", new Color(eb[27], eb[28], eb[29], eb[30]));
                RenderingServer.MaterialSetParam(m, "blur_width", eb[31]);
            }
            else if (prog == 3 || prog == 4)
            {
                // 20 float 矩阵拆 3 行 (rgb 行 + 行 offset)——行 0/1/2 各取 cf[0..3]+off 等。
                float[] cf = blob.ColorMatrix(i);
                RenderingServer.MaterialSetParam(m, "cf_m0", new Vector4(cf[0], cf[1], cf[2], cf[4]));
                RenderingServer.MaterialSetParam(m, "cf_m1", new Vector4(cf[5], cf[6], cf[7], cf[9]));
                RenderingServer.MaterialSetParam(m, "cf_m2", new Vector4(cf[10], cf[11], cf[12], cf[14]));
            }
            else if (prog == 5)
            {
                float[] sp = blob.ShadowParams(i);
                RenderingServer.MaterialSetParam(m, "shadow_half_size", new Vector4(sp[0], sp[1], 0f, 0f));
                RenderingServer.MaterialSetParam(m, "shadow_radius", sp[2]);
                RenderingServer.MaterialSetParam(m, "shadow_sigma", sp[3]);
                RenderingServer.MaterialSetParam(m, "shadow_inset", sp[4]);
            }
            else if (prog == 6 || prog == 7)
            {
                float[] gp = blob.GradParams(i);
                int n = Mathf.Min((int)gp[10], 8);
                if (n < 1) n = 1;
                RenderingServer.MaterialSetParam(m, "grad_kind", gp[0]);
                RenderingServer.MaterialSetParam(m, "grad_geom", new Vector4(gp[2], gp[3], gp[4], gp[5]));
                RenderingServer.MaterialSetParam(m, "grad_geom2", new Vector4(gp[6], gp[7], gp[8], gp[9]));
                int last = 12 + (n - 1) * 5;
                var lastCol = new Color(gp[last], gp[last + 1], gp[last + 2], gp[last + 3]);
                for (int s = 0; s < 8; s++)
                {
                    int b = 12 + s * 5;
                    bool used = s < n;
                    RenderingServer.MaterialSetParam(m, $"grad_stop{s}",
                        used ? new Color(gp[b], gp[b + 1], gp[b + 2], gp[b + 3]) : lastCol);
                    RenderingServer.MaterialSetParam(m, $"grad_pos{s}", used ? gp[b + 4] : 1f);
                }
            }
        }

        /// <summary>重建 mesh（仅 FULL 行调用）。UV 命中 sprite 时重映射进图集子区。</summary>
        public void UploadMesh(FrameBlob blob, int i)        {
            var seg = blob.ReadMesh(i);
            int vc = seg.Verts.Length;
            var verts = new Vector2[vc];
            var uvs = new Vector2[vc];
            var colors = new Color[vc];
            for (int v = 0; v < vc; v++)
            {
                verts[v] = new Vector2(seg.Verts[v].X, seg.Verts[v].Y);
                uvs[v] = new Vector2(seg.Uvs[v].X, seg.Uvs[v].Y);
                colors[v] = new Color(seg.Colors[v].R, seg.Colors[v].G, seg.Colors[v].B, seg.Colors[v].A);
            }
            if (_look.Found)
            {
                // blob UV 是全图 [0,1]——线性映射进 sprite 子区（对仗 RemapMeshUvToSprite）。
                float u0 = _look.UvRect.X, v0 = _look.UvRect.Y;
                float du = _look.UvRect.Z - u0, dv = _look.UvRect.W - v0;
                for (int v = 0; v < vc; v++)
                    uvs[v] = new Vector2(u0 + uvs[v].X * du, v0 + uvs[v].Y * dv);
            }
            var idx = new int[seg.Idx.Length];
            for (int k = 0; k < idx.Length; k++) idx[k] = (int)seg.Idx[k];

            var arr = new Godot.Collections.Array();
            arr.Resize((int)Mesh.ArrayType.Max);
            arr[(int)Mesh.ArrayType.Vertex] = verts;
            arr[(int)Mesh.ArrayType.TexUV] = uvs;
            arr[(int)Mesh.ArrayType.Color] = colors;
            arr[(int)Mesh.ArrayType.Index] = idx;
            _mesh.ClearSurfaces();
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        }

        /// <summary>#66 剔除补偿：Godot canvas 无 bounds 剔除，空实现（契约允许）。</summary>
        public void CompensateBounds(FrameBlob blob, int i) { }

        public void ReparentIfUnder(object container, object root)
        {
            var c = (Rid)container;
            if (_parent == c) SetParentRid((Rid)root);
        }

        public void Destroy()
        {
            if (_itemMat.IsValid) _host.Materials.ReleaseItemMaterial(_itemMat);
            if (_item.IsValid) RenderingServer.FreeRid(_item);
            _mesh.Dispose();
        }

        void SetParentRid(Rid parent)
        {
            _parent = parent;
            RenderingServer.CanvasItemSetParent(_item, parent);
        }
    }
}
