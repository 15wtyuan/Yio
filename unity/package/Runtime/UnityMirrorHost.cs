using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Yio
{
    /// <summary>
    /// 镜像渲染的 Unity 实现：UnityMirrorObj 工厂 + 材质/clip 推送 + 页逐出心跳。
    /// 引擎无关策略在 MirrorPool（Yio.Core）；本类只实现 IMirrorHost/IMirrorObject。
    /// </summary>
    public sealed class UnityMirrorHost : IMirrorHost
    {
        internal readonly MaterialManager _mm;
        internal readonly SpriteResolver _sprites;
        internal readonly Texture _fallback;

        /// <summary>
        /// A4 跨 Stage 排序基址（hub 按 stage 层序分配；同相机下各 stage 的 sort_key
        /// 都从 0 起编，不偏移会互相穿插）。Driver 每帧经 backend.SetSortBase 下发。
        /// </summary>
        internal int SortBase;

        public UnityMirrorHost(MaterialManager mm, SpriteResolver sprites, Texture fallback)
        {
            _mm = mm;
            _sprites = sprites;
            _fallback = fallback;
        }

        public IMirrorObject CreateObject(object parent) => new UnityMirrorObj(this, (Transform)parent);

        public bool IsContainerAlive(object container) => container is Transform t && t != null;

        public void PushClipEntries(uint clipContext, List<ClipEntryView> entries) =>
            _mm?.SetClipEntries(clipContext, entries);

        public void SweepPages() => _sprites?.Sweep();

        /// <summary>
        /// 诊断：dump 池内所有活 GO 的状态（node_id / localPosition / mesh bounds）。
        /// 按 F8 调用，在「好」「坏」两种布局各 dump 一次对比，定位 Unity 侧状态泄漏。
        /// </summary>
        public string DumpState(MirrorPool pool)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[MirrorPool] objs={pool.Count}");
            sb.AppendLine("  nodeId    sort  pos(x,y)            meshBounds(center,size)");
            foreach (var o in pool.Objs)
                DumpObj(sb, o as UnityMirrorObj);
            return sb.ToString();
        }

        void DumpObj(StringBuilder sb, UnityMirrorObj ro)
        {
            if (ro?.Go == null) { sb.AppendLine("  [obj] (null GO)"); return; }
            var p = ro.Go.transform.localPosition;
            int sort = ro.Mr != null ? ro.Mr.sortingOrder : 0;
            var b = ro.Mesh != null ? ro.Mesh.bounds : new Bounds();
            // 材质 clip 诊断：CLIPPED keyword + 链 entry 数 + 每 entry kind 回读
            // （材质实际值——shapeKind 1=circle 2=polygon；与 blob [clip table] 段对拍，
            // 不一致 = 刷新链路断了，一致但视觉旧 = 渲染侧没吃到）。
            string matInfo = "";
            if (ro.Mr != null && ro.Mr.sharedMaterial != null)
            {
                var m = ro.Mr.sharedMaterial;
                bool clipped = m.IsKeywordEnabled("CLIPPED");
                int clipCount = Mathf.RoundToInt(m.GetFloat("_ClipCount"));
                string kinds = "";
                if (clipped && clipCount > 0)
                {
                    var f0 = m.GetVectorArray("_ClipFrame0");
                    var f1 = m.GetVectorArray("_ClipFrame1");
                    for (int e = 0; e < clipCount && e < 4; e++)
                        kinds += (int)f1[e].w == 0 ? "-" : ((int)f1[e].w == 2 ? "R" : "r");
                    kinds += "/";
                    for (int e = 0; e < clipCount && e < 4; e++)
                        kinds += (int)f0[e].w == 0 ? "-" : ((int)f0[e].w == 1 ? "c" : "p");
                }
                matInfo = $" clip={clipped} entries={clipCount} kinds=[{kinds}]";
            }
            sb.AppendLine($"  nid={ro.LastNodeId} sort={sort} pos=({p.x:F0},{p.y:F0}) mb=(({b.center.x:F0},{b.center.y:F0}),({b.size.x:F0},{b.size.y:F0})) active={ro.Go.activeSelf}{matInfo}");
        }
    }

    /// <summary>
    /// Unity 渲染对象：GO+MeshFilter+MeshRenderer+Mesh+MPB 包装。header 更新/mesh 上传/
    /// bounds 补偿全在引擎侧；调用序来自中性 MirrorPool。
    /// 公共字段（Go/LastNodeId）名称是 MirrorPoolTests 反射读取面——保持不动。
    /// </summary>
    public sealed class UnityMirrorObj : IMirrorObject
    {
        public GameObject Go;
        public MeshFilter Mf;
        public MeshRenderer Mr;
        public Mesh Mesh;
        public bool Stale;                  // 已废：stale 跟踪移入中性池（保留字段防反射断言编译错——无测试读它）
        public ulong LastNodeId;            // 诊断：最近绑定的 node_id（DumpState 打印；不做复用校验——复用换绑是 reuse_key 池化的正常行为）

        // #62 页纹理逐出：本 obj 当前绑定的图集页键（null=非页承载：字体页/miss/纯色）。
        // UpdateHeader 每 lean 帧随 look 重写；Skip 帧不重写——但 Skip 行本就「视觉与上帧
        // 相同」，绑定不变是精确语义。
        public (int, int)? BoundPage;

        // #66：mesh 原始（未补偿）AABB。FULL 帧上传后 RecalculateBounds 时存底；剔除补偿
        // 永远基于此值重算。不能读 Mesh.bounds 顶替：Header 帧不重建 mesh，Mesh.bounds 里
        // 是上一帧「已补偿值」，再乘一次线性矩阵得 AABB(L·AABB(L·B)) ≠ AABB(L·B)——滚动中
        // 的旋转/缩放节点每帧叠加（scale<1 几何级缩小、45° 无界膨胀）。缓存底也让 L 本身
        // 变化的帧（transform 动画）从原始 bounds 直算，无叠加残留。
        public Bounds RawMeshBounds;

        // buffer 复用（500 节点静态压测 GC 缓解）：每 obj 持可复用 List，UploadMesh 每帧
        // Clear+fill 后用 Mesh.SetVertices(List) 等 overload 上传——List<T>.Clear() 保留
        // Capacity，故 warm-up 后零 per-frame 数组 alloc。
        readonly List<Vector3> VList = new();
        readonly List<UnityEngine.Vector2> UvList = new();
        readonly List<UnityEngine.Color> CList = new();
        readonly List<int> IList = new();
        // cached MaterialPropertyBlock for per-renderer uniforms (_ObjM, _CF, _Alpha).
        // Lazy-init; consolidated into single SetPropertyBlock per frame.
        MaterialPropertyBlock Mpb;

        readonly UnityMirrorHost _host;
        // UpdateHeader 解析的 sprite look——UploadMesh 的 UV 重映射复用（UpdateHeader 恒先于 UploadMesh）。
        SpriteLookup _look;

        internal UnityMirrorObj(UnityMirrorHost host, Transform root)
        {
            _host = host;
            var go = new GameObject("yio_node");
            // ExecuteAlways 下镜像 GO 是运行时派生产物，标 DontSaveInEditor 防被存进场景
            // （否则 EditMode Sync 产出的 GO 会 dirty 场景、Play/Stop 与 domain reload 累积残留）。
            go.hideFlags = HideFlags.DontSaveInEditor;
            go.transform.SetParent(root, false);
            go.layer = root.gameObject.layer;  // YioUI
            Mf = go.AddComponent<MeshFilter>();
            Mr = go.AddComponent<MeshRenderer>();
            Mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            Mesh.hideFlags = HideFlags.DontSaveInEditor;  // Mesh 是独立 Object，也别存盘
            Mesh.MarkDynamic();
            Mf.sharedMesh = Mesh;
            Go = go;
        }

        public bool VisuallyActive => Go != null && Go.activeInHierarchy;

        public void StampPage()
        {
            // 无 resolver（测试路径）no-op——与旧实现「sprites==null 跳过盖章」同语义。
            if (BoundPage.HasValue && _host._sprites != null)
                _host._sprites.StampPage(BoundPage.Value);
        }

        public void SetActive(bool active)
        {
            if (Go == null || Go.activeSelf == active) return;
            Go.SetActive(active);
        }

        /// 更新 GO header（position/rotation/scale + sortingOrder + clip + material + per-renderer uniforms）。
        /// 无论 HEADER 还是 FULL 路径均调用；仅 SKIP 跳过。
        /// v10：不再有 kind==2 text 单独材质路径——所有节点统一走 program+tex 选材。
        public void UpdateHeader(FrameBlob blob, int i, object parent)
        {
            LastNodeId = blob.NodeId(i); // 新建 + 复用均更新（slot 换绑时 node_id 变）

            // 解决图资源（path_idx → path → SpriteResolver.GetSprite）。
            // 文本节点的 font-atlas path（yio://font-atlas/...）由 RegisterFontAtlasPage 注册
            // 进 SpriteResolver，GetSprite 命中返 SpriteLookup——tex 回落 fallback（whiteTexture，
            // 由 SyncFontAtlas 换贴真实 atlas）。
            _look = default;
            Texture tex = _host._fallback;
            uint pathIdx = blob.PathIdx(i);
            if (pathIdx != 0 && _host._sprites != null)
            {
                string path = blob.ReadPath(pathIdx);
                if (!string.IsNullOrEmpty(path))
                {
                    _look = _host._sprites.GetSprite(path);
                    if (_look.found) tex = _look.tex;
                }
            }
            // #62：绑定页键随 look 刷新（null=字体页/miss/纯色，不参与续命）。
            BoundPage = _look.PageKey;

            // flatten：所有节点挂 parent（挂载行除外——中性池已解析到业务 3D 容器）。
            // pure 和非 pure 统一 GO localPosition=(Mtx,Mty)（world translate 进 GO transform）。
            // 非纯平移的 scale/rotate 进 _ObjectMatrix（无 translate）。translate 进 GO
            // localPosition；但 renderer.bounds = GO 平移 × 未旋转 mesh ≠ 旋转后真实
            // AABB——剔除补偿见 CompensateBounds（#66）。
            var parentT = (Transform)parent;
            Go.transform.SetParent(parentT, false);
            if (Go.layer != parentT.gameObject.layer)
                Go.layer = parentT.gameObject.layer; // 挂载行随容器层（场景层 → 3D 深度遮挡）
            bool pure = blob.IsPureTranslation(i);
            Go.transform.localPosition = new Vector3(blob.Mtx(i), blob.Mty(i), 0f);
            Go.transform.localRotation = Quaternion.identity;
            Go.transform.localScale = Vector3.one;

            Mr.sortingOrder = (int)blob.SortKey(i) + _host.SortBase;

            uint maskCtx = blob.MaskContext(i);

            // 材质：按 program+texture 选（包括 text 节点，program=1，tex 由 SyncFontAtlas 注入 font atlas）。
            Material mat = _host._mm?.Get((int)blob.Program(i), tex, maskCtx, !pure);
            if (mat != null) Mr.sharedMaterial = mat;

            // 合并 per-renderer uniform（MPB 一次 SetPropertyBlock，避免 _ObjM/_CF/_Alpha 互相覆盖）。
            // _ObjM：非纯平移时传 scale/rotate 矩阵（纯平移 = shader 默认 identity，不设）。
            // _CF：ColorFilter（program 3/4）传 5 Vector；其他不设。
            // _Alpha：每帧无条件设（alpha 剥离顶点色）。
            float alpha = blob.Alpha(i);
            bool hasFilter = blob.Program(i) == 3 || blob.Program(i) == 4;

            Mpb ??= new MaterialPropertyBlock();
            Mr.GetPropertyBlock(Mpb);
            if (!pure)
            {
                // _ObjectMatrix 只 scale/rotate（translate 进 GO localPosition，renderer.bounds 自动 world）。
                var objM = Matrix4x4.identity;
                objM[0, 0] = blob.Ma(i); objM[0, 1] = blob.Mc(i);
                objM[1, 0] = blob.Mb(i); objM[1, 1] = blob.Md(i);
                Mpb.SetVector("_ObjM0", objM.GetRow(0));
                Mpb.SetVector("_ObjM1", objM.GetRow(1));
                Mpb.SetVector("_ObjM2", objM.GetRow(2));
                Mpb.SetVector("_ObjM3", objM.GetRow(3));
            }
            // ColorFilter（program=3=filter 无图 / 4=filter+bg-image 双 keyword）：
            // 矩阵 20 float 拆 5 Vector MPB SetVector。
            if (hasFilter)
            {
                float[] cf = blob.ColorMatrix(i);
                Mpb.SetVector("_CF0", new Vector4(cf[0],  cf[1],  cf[2],  cf[3]));
                Mpb.SetVector("_CF1", new Vector4(cf[5],  cf[6],  cf[7],  cf[8]));
                Mpb.SetVector("_CF2", new Vector4(cf[10], cf[11], cf[12], cf[13]));
                Mpb.SetVector("_CF3", new Vector4(cf[15], cf[16], cf[17], cf[18]));
                Mpb.SetVector("_CFOff", new Vector4(cf[4], cf[9], cf[14], cf[19]));
            }
            // SDF 文字效果（program=1 ALPHA_MASK）：读 effect_block 列 → per-renderer MPB。
            // 非 text 节点不设（material 默认全 0 = 纯 face）。effect 参数变化经
            // header_hash 走 ChangeLevel::Header（不重建 mesh，仅刷新 MPB）。
            // _UnderlayOffset 是 float4（shader 取 .xy 做像素偏移，.zw 兜 0）。
            if (blob.Program(i) == 1)
            {
                float[] eb = blob.EffectBlock(i);
                Mpb.SetFloat("_OutlineWidth", eb[0]);
                Mpb.SetVector("_OutlineColor", new Vector4(eb[1], eb[2], eb[3], eb[4]));
                for (int s = 0; s < 3; s++)
                {
                    int b = 5 + s * 7; // underlay 槽起点：[5]/[12]/[19]
                    Mpb.SetVector("_UnderlayOffset" + s, new Vector4(eb[b], eb[b + 1], 0, 0));
                    Mpb.SetFloat("_UnderlaySoftness" + s, eb[b + 2]);
                    Mpb.SetVector("_UnderlayColor" + s, new Vector4(eb[b + 3], eb[b + 4], eb[b + 5], eb[b + 6]));
                }
                Mpb.SetFloat("_GlowPower", eb[26]);
                Mpb.SetVector("_GlowColor", new Vector4(eb[27], eb[28], eb[29], eb[30]));
                Mpb.SetFloat("_BlurWidth", eb[31]);
            }
            // box-shadow blur（program=5 SHADOW_BLUR）：读 shadow_params 列 → per-renderer MPB。
            // shadow_params = [halfSize.xy, radius, σ, inset, _pad]。非 shadow 节点全零，
            // 故仅 program==5 时设（避免给非 shadow 节点误设全零 uniform）。参数变化经
            // header_hash 走 ChangeLevel::Header（不重建 mesh，仅刷新 MPB，照 effect 同路径）。
            if (blob.Program(i) == 5)
            {
                float[] sp = blob.ShadowParams(i);
                Mpb.SetVector("_ShadowHalfSize", new Vector4(sp[0], sp[1], 0, 0));
                Mpb.SetFloat("_ShadowRadius", sp[2]);
                Mpb.SetFloat("_ShadowSigma", sp[3]);
                Mpb.SetFloat("_ShadowInset", sp[4]);
            }
            // 背景渐变（program=6 GRADIENT / 7 GRADIENT+COLOR_FILTER）：读 grad_params 列 →
            // per-renderer MPB。stops 拆 8 组 Vector4(rgba) + Float(pos)——MPB 只覆盖 Properties
            // 声明过的属性（_ObjectMatrix 踩坑同源），ShaderLab 不支持数组属性，故逐槽
            // SetVector/SetFloat（照 _Underlay* 编号属性先例）。未用槽填「末 stop 色 @pos=1」
            // ——shader 无需 count uniform，8 槽段搜索自然退化到末 stop。
            if (blob.Program(i) == 6 || blob.Program(i) == 7)
            {
                float[] gp = blob.GradParams(i);
                int n = Mathf.Min((int)gp[10], 8);
                if (n < 1) n = 1;
                Mpb.SetFloat("_GradKind", gp[0]);
                Mpb.SetVector("_GradGeom", new Vector4(gp[2], gp[3], gp[4], gp[5]));
                Mpb.SetVector("_GradGeom2", new Vector4(gp[6], gp[7], gp[8], gp[9]));
                // 末 stop（未用槽的填充源；radial 远端 / linear clamp 端点色）。
                int last = 12 + (n - 1) * 5;
                Vector4 lastCol = new Vector4(gp[last], gp[last + 1], gp[last + 2], gp[last + 3]);
                for (int s = 0; s < 8; s++)
                {
                    int b = 12 + s * 5;
                    bool used = s < n;
                    Mpb.SetVector("_GradStop" + s, used
                        ? new Vector4(gp[b], gp[b + 1], gp[b + 2], gp[b + 3])
                        : lastCol);
                    Mpb.SetFloat("_GradPos" + s, used ? gp[b + 4] : 1f);
                }
            }
            Mpb.SetFloat("_Alpha", alpha);
            // _ObjT：节点 design 平移（Mtx,Mty）——clip 链测试空间是 design 坐标，
            // blob 顶点已 re-base 到本地（见 _ObjT 注释），shader 侧补回。
            Mpb.SetVector("_ObjT", new Vector4(blob.Mtx(i), blob.Mty(i), 0f, 0f));
            Mr.SetPropertyBlock(Mpb);
        }

        /// 上传 mesh / 重建 mesh（仅 FULL 路径调用）。
        /// v10：不再有 kind==2 text 分支——所有节点统一走 mesh 上传（核心自产 atlas，word mesh 已含字形 UV）。
        public void UploadMesh(FrameBlob blob, int i)
        {
            // mesh 上传（顶点已 re-base 到本地）。
            var seg = blob.ReadMesh(i);
            UploadMeshBuffers(seg);
            Mesh.RecalculateBounds();
            RawMeshBounds = Mesh.bounds; // 原始 AABB 存底（补偿前）
            // look.found → RemapMeshUvToSprite 把全图 UV 重映射到 sprite 在 atlas 的子区；
            // 未命中（path_idx=0 纯色 / 查不到）则跳过，mesh 沿用 blob 全图 UV [0,1] + fallback 纹理。
            if (_look.found)
                RemapMeshUvToSprite(_look.uvRect);
        }

        /// <summary>
        /// #66：非纯平移节点的剔除 bounds 补偿。rotate/scale 走 _ObjM shader 矩阵、GO 只带
        /// 平移分量 → Unity renderer.bounds = GO 平移 × 未旋转 mesh，与真实视觉 AABB（线性矩阵
        /// 旋转后的四边形）中心错位且范围偏小——旋转条横放假 bounds 竖直方向仅 h px，旋转 45°
        /// 后真实竖直 ≈ w·sinθ → 滚动中真身进视口而假 bounds 在外，被 SRP 错误剔除（滚动容器
        /// 内旋转连线消失）。补偿：bounds 置「线性矩阵 × 顶点 AABB」的 AABB（仍在 GO 本地系，
        /// 随 GO 平移）→ 剔除结果 = T × AABB(L·verts) = 真实世界 AABB。z 向原样透传（2D）。
        /// </summary>
        public void CompensateBounds(FrameBlob blob, int i)
        {
            // 从缓存的原始 AABB 算（非 Mesh.bounds——那里可能是上帧已补偿值，再乘会叠加）。
            Bounds b = RawMeshBounds;
            Vector3 c = b.center;
            Vector3 e = b.extents;
            float ma = blob.Ma(i), mb = blob.Mb(i), mc = blob.Mc(i), md = blob.Md(i);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    float x = c.x + sx * e.x, y = c.y + sy * e.y;
                    float rx = ma * x + mc * y;
                    float ry = mb * x + md * y;
                    if (rx < minX) minX = rx;
                    if (rx > maxX) maxX = rx;
                    if (ry < minY) minY = ry;
                    if (ry > maxY) maxY = ry;
                }
            Mesh.bounds = new Bounds(
                new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, c.z),
                new Vector3(maxX - minX, maxY - minY, e.z * 2f));
        }

        public void ReparentIfUnder(object container, object root)
        {
            if (Go != null && Go.transform.parent == (Transform)container)
                Go.transform.SetParent((Transform)root, false);
        }

        public void Destroy()
        {
            DestroyObj(Mesh);   // new Mesh() 是独立 UnityEngine.Object，须显式销毁，否则泄漏
            DestroyObj(Go);
        }

        /// buffer 复用：从 MeshSegment 填本 obj 持有的可复用 List，再走 SetVertices(List) 等 overload。
        /// List<T>.Clear() 保留 Capacity → warm-up 后每帧零数组 alloc。
        /// 注意：SetVertices(List) 要求 list 长度 == 顶点数；Clear()+Add 精确填到 Verts.Length 即满足。
        void UploadMeshBuffers(MeshSegment seg)
        {
            int vc = seg.Verts.Length;
            // Clear 保留 capacity，再填（避免每帧 new List / new 数组）。
            var v = VList; v.Clear();
            var uv = UvList; uv.Clear();
            var c = CList; c.Clear();
            var idx = IList; idx.Clear();
            // 预扩一次（首次或更大 mesh 时）；后续 Clear 不收缩，零 alloc。
            if (v.Capacity < vc) { v.Capacity = vc; uv.Capacity = vc; c.Capacity = vc; }
            int ic = seg.Idx.Length;
            if (idx.Capacity < ic) idx.Capacity = ic;

            for (int i = 0; i < vc; i++)
            {
                // MeshSegment 是引擎无关类型（YioVector2/YioColor）——此处显式转 Unity 类型上传。
                v.Add(new Vector3(seg.Verts[i].X, seg.Verts[i].Y, 0f));
                uv.Add(new UnityEngine.Vector2(seg.Uvs[i].X, seg.Uvs[i].Y));
                c.Add(new UnityEngine.Color(seg.Colors[i].R, seg.Colors[i].G, seg.Colors[i].B, seg.Colors[i].A));
            }
            for (int i = 0; i < ic; i++) idx.Add((int)seg.Idx[i]);

            Mesh.Clear();                 // Unity 要求 SetVertices 前清空，否则顶点数变更报错
            Mesh.SetVertices(v);
            Mesh.SetUVs(0, uv);
            Mesh.SetColors(c);
            Mesh.SetTriangles(idx, 0);
        }

        /// 把 mesh UV（core 产 [0,1] 全图 UV）线性映射到 sprite 在 atlas 页内的子区。
        /// uvRect 是打包器算好的 atlas 子区——Unity Rect (x=u0, y=v0, w=u1-u0, h=v1-v0)。
        /// 不内缩半纹素：padding 下 bilinear 边缘 fringe 几乎不可见；开 atlas enableAlphaDilation（边缘像素复制进 padding）防 bleed。
        void RemapMeshUvToSprite(UnityEngine.Rect uvRect)
        {
            // blob UV v 已翻转（TL.v=1），线性映射进子区保持翻转。
            // 直接改 UvList（UploadMeshBuffers 已填），不复 alloc 新 List。
            var uvs = UvList;
            for (int i = 0; i < uvs.Count; i++)
                uvs[i] = new UnityEngine.Vector2(
                    uvRect.x + uvs[i].x * uvRect.width,
                    uvRect.y + uvs[i].y * uvRect.height);
            Mesh.SetUVs(0, uvs);
        }

        // Edit-mode-safe 销毁：Driver 可能挂 [ExecuteAlways]，Sync/Clear 会在 Edit mode 跑；
        // Object.Destroy 在 Edit mode 非法（须 DestroyImmediate）。
        static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
