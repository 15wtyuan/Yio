// GodotMaterialCache：program × clip-ctx → canvas_item 材质 RID（Godot 版，对仗 unity MaterialManager）。
//
// Godot 侧纹理不经材质——canvas_item_add_mesh 的 texture 参数随绘制命令绑定 shader 的
// 内建 TEXTURE sampler，故材质 key = (program, mask_ctx)（Unity 版纹理也进 key 的原因是
// Unity 材质绑纹理；Godot 无此约束）。
//
// shader 组合展开（Godot 无 Unity multi_compile keyword——8 个源码变体，公共段字符串拼接）：
//   program 0（img/mesh）  unlit       ：vcol × tex
//   program 1（text SDF）  sdf         ：face 路径 + outline/underlay×3/glow/blur 效果面
//   program 2（bg-image）  bg          ：CSS source-over 合成（图 over 底色）
//   program 3（filter）    unlit_cf    ：unlit + 色彩矩阵
//   program 4（filter+bg） bg_cf       ：bg + 色彩矩阵
//   program 5（shadow）    shadow      ：圆角矩形 SDF × erfc 高斯边（不采纹理）
//   program 6（gradient）  gradient    ：背景渐变 stops/radial（不采纹理）
//   program 7（grad+cf）   gradient_cf ：渐变 + 色彩矩阵
//
// 语义逐段对仗 unity/package/Shaders/Yio-Unlit.shader（sRGB 语义 = Unity Gamma 路径：
// Godot 2D canvas 在 sRGB 空间合成，与 CSS 一致，vcol 不做 gamma 转换）。
// per-item uniform（cf 矩阵/文字效果/shadow/grad 参数）不在此层——Unity 走 MPB per-renderer，
// Godot 对仗 = 引擎侧每对象 clone 材质 RID（GodotMirrorObj 持有，见 UpdateHeader）。
//
// clip（mask_ctx>0）：ctx 独立材质实例 + 链 uniform 数组（entry 布局/形状判定逐式对仗
// Unity shader CLIPPED 段：frame0=(A,C,Tx,shapeKind) frame1=(B,D,Ty,rectKind)，
// 测试空间 = FRAGCOORD（canvas 无变换 + 窗口=设计分辨率时 1:1 design 坐标，y-down 同向）。

using System.Collections.Generic;
using Godot;

namespace Yio
{
    public sealed class GodotMaterialCache
    {
        const int MaxEntries = 4;        // core render::MAX_CLIP_CHAIN 同值（shader 数组定长）
        const int PolyVec4PerEntry = 8;  // 每 entry polygon 点槽：16 点 × 2 float = 8 vec4

        // —— 公共 shader 段（拼接进各 program 变体）——

        // clip 链（全变体携带；clip_count=0 时循环零次——uniform 分支，2D canvas 可接受）。
        // entry 判定：rectKind（frame1.w：1 直角 AABB / 2 圆角各向异性 SDF）与 shapeKind
        //（frame0.w：1 circle SDF / 2 polygon crossing）双独立；链上全部 entry 过才保留。
        // 圆角 SDF 归一化到半径空间再 × min(rx,ry) 回像素量纲（1 design px AA 带）——
        // 与 core hit gate / Unity shader 同一几何语义，改须两侧同步。
        const string ClipUniforms = @"
uniform vec4 clip_frame0[4];   // (A, C, Tx, shapeKind)
uniform vec4 clip_frame1[4];   // (B, D, Ty, rectKind)
uniform vec4 clip_rect[4];     // (w, h, poly_count, _)
uniform vec4 clip_radii0[4];   // (tl_rx, tl_ry, tr_rx, tr_ry)
uniform vec4 clip_radii1[4];   // (br_rx, br_ry, bl_rx, bl_ry)
uniform vec4 clip_circle[4];   // (cx, cy, r, _)
uniform vec4 clip_poly[32];    // 每 entry 8 vec4 × 2 点
uniform float clip_count;
";
        const string ClipApply = @"
    // designPos：FRAGCOORD（canvas 无变换、窗口=设计分辨率 → 1:1 design 坐标，y-down）。
    for (int e = 0; e < 4; e++) {
        if (e >= int(clip_count)) break;
        vec2 design_pos = FRAGCOORD.xy;
        vec2 lp = vec2(dot(clip_frame0[e].xy, design_pos) + clip_frame0[e].z,
                       dot(clip_frame1[e].xy, design_pos) + clip_frame1[e].z);
        float shape_kind = clip_frame0[e].w;
        float rect_kind = clip_frame1[e].w;
        float keep = 1.0;
        if (rect_kind > 1.5) {
            vec2 half2 = clip_rect[e].xy * 0.5;
            vec2 rel = lp - half2;
            vec4 r_top = clip_radii0[e];
            vec4 r_bot = clip_radii1[e];
            vec2 r = (rel.y < 0.0)
                ? ((rel.x < 0.0) ? r_top.xy : r_top.zw)
                : ((rel.x < 0.0) ? r_bot.zw : r_bot.xy);
            r = max(r, vec2(1e-4));
            vec2 q = abs(rel) - (half2 - r);
            vec2 qn = q / r;
            float sdist = length(max(qn, vec2(0.0))) + min(max(qn.x, qn.y), 0.0) - 1.0;
            float sdf = sdist * min(r.x, r.y);
            keep = 1.0 - smoothstep(0.0, 1.0, sdf);
        } else if (rect_kind > 0.5) {
            vec2 wh = clip_rect[e].xy;
            keep = step(0.0, lp.x) * step(0.0, lp.y) * step(lp.x, wh.x) * step(lp.y, wh.y);
        }
        if (keep > 0.0) {
            if (shape_kind > 1.5) {
                int n = int(clip_rect[e].z);
                int pbase = e * 8;
                int inside = 0;
                int j = n - 1;
                for (int k = 0; k < 16; k++) {
                    if (k >= n) break;
                    vec4 seg_a = clip_poly[pbase + k / 2];
                    vec2 a = ((k & 1) == 0) ? seg_a.xy : seg_a.zw;
                    vec4 seg_b = clip_poly[pbase + j / 2];
                    vec2 b = ((j & 1) == 0) ? seg_b.xy : seg_b.zw;
                    if ((a.y > lp.y) != (b.y > lp.y)) {
                        float t = (lp.y - a.y) / (b.y - a.y);
                        if (lp.x < a.x + t * (b.x - a.x)) inside ^= 1;
                    }
                    j = k;
                }
                keep = float(inside);
            } else if (shape_kind > 0.5) {
                vec2 d_c = lp - clip_circle[e].xy;
                keep = 1.0 - smoothstep(0.0, 1.0, length(d_c) - clip_circle[e].z);
            }
        }
        COLOR.a *= keep;
    }
";

        // 色彩矩阵（CSS filter 定义在 sRGB 空间——Godot canvas 即 sRGB，直接施加）。
        // 每行 = (m00, m01, m02, rowOffset)——Unity 拆 _CF0..3 + _CFOff 的 20 float 同源布局，
        // offset 并进 .w 少一个 uniform。
        const string CfUniforms = @"
uniform vec4 cf_m0 = vec4(1.0, 0.0, 0.0, 0.0);
uniform vec4 cf_m1 = vec4(0.0, 1.0, 0.0, 0.0);
uniform vec4 cf_m2 = vec4(0.0, 0.0, 1.0, 0.0);
";
        const string CfApply = @"
    COLOR.rgb = vec3(
        dot(cf_m0.xyz, COLOR.rgb) + cf_m0.w,
        dot(cf_m1.xyz, COLOR.rgb) + cf_m1.w,
        dot(cf_m2.xyz, COLOR.rgb) + cf_m2.w);
";

        // —— 各 program 变体（效果 uniform 段 + 主 fragment）——

        const string UnlitCode = @"
shader_type canvas_item;
" + ClipUniforms + @"
void fragment() {
    COLOR = COLOR * texture(TEXTURE, UV);
" + ClipApply + @"
}
";

        // SDF 文本：face + outline/underlay×3/glow/blur（效果参数=0 时自然退化纯 face——
        // 与 Unity MPB 全零路径同语义；非零时 MirrorObj 经 per-item 材质写入）。
        const string SdfCode = @"
shader_type canvas_item;
" + ClipUniforms + @"
uniform float gradient_scale = 13.0;
uniform float face_dilate = 0.0;
uniform float outline_width = 0.0;
uniform vec4 outline_color = vec4(0.0);
uniform vec4 underlay_offset0 = vec4(0.0); uniform float underlay_softness0 = 0.0; uniform vec4 underlay_color0 = vec4(0.0);
uniform vec4 underlay_offset1 = vec4(0.0); uniform float underlay_softness1 = 0.0; uniform vec4 underlay_color1 = vec4(0.0);
uniform vec4 underlay_offset2 = vec4(0.0); uniform float underlay_softness2 = 0.0; uniform vec4 underlay_color2 = vec4(0.0);
uniform float glow_power = 0.0;
uniform vec4 glow_color = vec4(0.0);
uniform float blur_width = 0.0;
void fragment() {
    vec4 vcol = COLOR;
    float d = texture(TEXTURE, UV).r;
    vec2 uv_dx = dFdx(UV);
    vec2 uv_dy = dFdy(UV);
    float px_size = inversesqrt(abs(uv_dx.x * uv_dy.y - uv_dx.y * uv_dx.x));
    float tex_w = 1.0 / TEXTURE_PIXEL_SIZE.x;
    float scale = px_size * (1.3333 * gradient_scale) / tex_w;
    float edge = (d - 0.5) * scale + face_dilate * 0.5 * scale;
    float face_soft = max(blur_width, 1.0);
    float face = clamp(edge / face_soft + 0.5, 0.0, 1.0);
    vec3 rgb = vcol.rgb;
    float a = face * vcol.a;
    // underlay×3：偏移重采 + softness 软化，over 合成画 face 下（采样在 i.uv - offUv，
    // shadow 落 +offset 方向——对齐 Unity 修正后的方向语义）。
    if (underlay_color0.a > 0.001) {
        vec2 off_uv = underlay_offset0.x * uv_dx + underlay_offset0.y * uv_dy;
        float dd = texture(TEXTURE, UV - off_uv).r;
        float de = (dd - 0.5) * scale + face_dilate * 0.5 * scale;
        float usoft = max(underlay_softness0, 1.0);
        float um = clamp(de / usoft + 0.5, 0.0, 1.0);
        float ua = underlay_color0.a * um;
        rgb = mix(rgb, underlay_color0.rgb, ua * (1.0 - a));
        a += ua * (1.0 - a);
    }
    if (underlay_color1.a > 0.001) {
        vec2 off_uv = underlay_offset1.x * uv_dx + underlay_offset1.y * uv_dy;
        float dd = texture(TEXTURE, UV - off_uv).r;
        float de = (dd - 0.5) * scale + face_dilate * 0.5 * scale;
        float usoft = max(underlay_softness1, 1.0);
        float um = clamp(de / usoft + 0.5, 0.0, 1.0);
        float ua = underlay_color1.a * um;
        rgb = mix(rgb, underlay_color1.rgb, ua * (1.0 - a));
        a += ua * (1.0 - a);
    }
    if (underlay_color2.a > 0.001) {
        vec2 off_uv = underlay_offset2.x * uv_dx + underlay_offset2.y * uv_dy;
        float dd = texture(TEXTURE, UV - off_uv).r;
        float de = (dd - 0.5) * scale + face_dilate * 0.5 * scale;
        float usoft = max(underlay_softness2, 1.0);
        float um = clamp(de / usoft + 0.5, 0.0, 1.0);
        float ua = underlay_color2.a * um;
        rgb = mix(rgb, underlay_color2.rgb, ua * (1.0 - a));
        a += ua * (1.0 - a);
    }
    // outline：居中描边，单坡道（cov = saturate(halfW + 0.5 - |edge|)，带宽精确）。
    if (outline_width > 0.001) {
        float half_w = outline_width * 0.5;
        float cov = clamp(half_w + 0.5 - abs(edge), 0.0, 1.0);
        float oa = cov * outline_color.a;
        rgb = mix(rgb, outline_color.rgb, oa);
        rgb = mix(rgb, outline_color.rgb, oa * (1.0 - a));
        a += oa * (1.0 - a) * clamp(0.5 - edge, 0.0, 1.0);
    }
    // glow：edge 外晕开，曲线衰减到 glow_ext。
    if (glow_color.a > 0.001) {
        float out_dist = max(-edge, 0.0);
        float glow_ext = max(glow_power, 1.0);
        float gm = pow(clamp(1.0 - out_dist / glow_ext, 0.0, 1.0), 2.0);
        float ga = gm * glow_color.a;
        rgb = mix(rgb, glow_color.rgb, ga * (1.0 - a));
        a += ga * (1.0 - a);
    }
    COLOR = vec4(rgb, a);
" + ClipApply + @"
}
";

        const string BgCode = @"
shader_type canvas_item;
" + ClipUniforms + @"
void fragment() {
    vec4 vcol = COLOR;
    vec4 tex = texture(TEXTURE, UV);
    float bg_a = tex.a + vcol.a * (1.0 - tex.a);
    vec3 bg_rgb = (tex.rgb * tex.a + vcol.rgb * vcol.a * (1.0 - tex.a)) / max(bg_a, 1e-6);
    COLOR = vec4(bg_rgb, bg_a);
" + ClipApply + @"
}
";

        const string UnlitCfCode = @"
shader_type canvas_item;
" + ClipUniforms + CfUniforms + @"
void fragment() {
    COLOR = COLOR * texture(TEXTURE, UV);
" + CfApply + ClipApply + @"
}
";

        const string BgCfCode = @"
shader_type canvas_item;
" + ClipUniforms + CfUniforms + @"
void fragment() {
    vec4 vcol = COLOR;
    vec4 tex = texture(TEXTURE, UV);
    float bg_a = tex.a + vcol.a * (1.0 - tex.a);
    vec3 bg_rgb = (tex.rgb * tex.a + vcol.rgb * vcol.a * (1.0 - tex.a)) / max(bg_a, 1e-6);
    COLOR = vec4(bg_rgb, bg_a);
" + CfApply + ClipApply + @"
}
";

        // box-shadow：圆角矩形 SDF × 0.5·erfc 高斯指示（长尾不截断，贴浏览器柔散）。
        // UV 由 core 编码为「顶点 − 形状中心」（像素量纲）；inset 翻符号做内阴影。
        const string ShadowCode = @"
shader_type canvas_item;
" + ClipUniforms + @"
uniform vec4 shadow_half_size = vec4(0.0);
uniform float shadow_radius = 0.0;
uniform float shadow_sigma = 0.0;
uniform float shadow_inset = 0.0;
float erfc_approx(float x) {
    float z = abs(x);
    float t = 1.0 / (1.0 + 0.3275911 * z);
    float r = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
    float y = exp(-z * z) * r;
    return (x >= 0.0) ? y : 2.0 - y;
}
void fragment() {
    vec2 p = UV;
    float qx = abs(p.x) - shadow_half_size.x + shadow_radius;
    float qy = abs(p.y) - shadow_half_size.y + shadow_radius;
    float shadow_sdf = length(max(vec2(qx, qy), vec2(0.0))) + min(max(qx, qy), 0.0) - shadow_radius;
    float sig = max(shadow_sigma, 0.0001);
    float ind = 0.5 * erfc_approx(shadow_sdf / (sig * 1.41421356));
    float a_out = (shadow_inset > 0.5) ? (1.0 - ind) : ind;
    COLOR = vec4(COLOR.rgb, COLOR.a * a_out);
" + ClipApply + @"
}
";

        // 背景渐变：UV = box 局部像素坐标（core 编码 raw 直通）；stops 预乘插值反预乘
        //（CSS 渐变语义，rgba→transparent 无灰边）；末步与 vcol source-over 合成。
        // 未用槽由 MirrorObj 填「末 stop 色 @pos=1」——段搜索自然退化，无需 count uniform。
        const string GradCommon = @"
uniform float grad_kind = 0.0;
uniform vec4 grad_geom = vec4(1.0, 0.0, 0.0, 1.0);   // linear: (dir.xy, t0, inv_span)
uniform vec4 grad_geom2 = vec4(0.0, 0.0, 1.0, 1.0);  // radial: (center.xy, radii.xy)
uniform vec4 grad_stop0 = vec4(1.0); uniform float grad_pos0 = 0.0;
uniform vec4 grad_stop1 = vec4(1.0); uniform float grad_pos1 = 1.0;
uniform vec4 grad_stop2 = vec4(1.0); uniform float grad_pos2 = 1.0;
uniform vec4 grad_stop3 = vec4(1.0); uniform float grad_pos3 = 1.0;
uniform vec4 grad_stop4 = vec4(1.0); uniform float grad_pos4 = 1.0;
uniform vec4 grad_stop5 = vec4(1.0); uniform float grad_pos5 = 1.0;
uniform vec4 grad_stop6 = vec4(1.0); uniform float grad_pos6 = 1.0;
uniform vec4 grad_stop7 = vec4(1.0); uniform float grad_pos7 = 1.0;
vec4 grad_sample(vec2 p) {
    float gt;
    if (grad_kind > 0.5) {
        vec2 gd = (p - grad_geom2.xy) / max(grad_geom2.zw, vec2(1e-4, 1e-4));
        gt = length(gd);
    } else {
        gt = clamp((dot(p, grad_geom.xy) - grad_geom.z) * grad_geom.w, 0.0, 1.0);
    }
    vec4 stops[8];
    float poss[8];
    stops[0] = grad_stop0; poss[0] = grad_pos0;
    stops[1] = grad_stop1; poss[1] = grad_pos1;
    stops[2] = grad_stop2; poss[2] = grad_pos2;
    stops[3] = grad_stop3; poss[3] = grad_pos3;
    stops[4] = grad_stop4; poss[4] = grad_pos4;
    stops[5] = grad_stop5; poss[5] = grad_pos5;
    stops[6] = grad_stop6; poss[6] = grad_pos6;
    stops[7] = grad_stop7; poss[7] = grad_pos7;
    vec4 gv = stops[0];
    for (int s = 0; s < 7; s++) {
        if (gt >= poss[s] && gt < poss[s + 1]) {
            float gf = clamp((gt - poss[s]) / max(poss[s + 1] - poss[s], 1e-5), 0.0, 1.0);
            vec4 gpa = vec4(stops[s].rgb * stops[s].a, stops[s].a);
            vec4 gpb = vec4(stops[s + 1].rgb * stops[s + 1].a, stops[s + 1].a);
            vec4 gpm = mix(gpa, gpb, gf);
            gv = vec4(gpm.rgb / max(gpm.a, 1e-4), gpm.a);
        }
    }
    if (gt >= poss[7]) gv = stops[7];
    return gv;
}
";
        const string GradientCode = @"
shader_type canvas_item;
" + ClipUniforms + GradCommon + @"
void fragment() {
    vec4 vcol = COLOR;
    vec4 gv = grad_sample(UV);
    float g_a = gv.a + vcol.a * (1.0 - gv.a);
    vec3 g_rgb = (gv.rgb * gv.a + vcol.rgb * vcol.a * (1.0 - gv.a)) / max(g_a, 1e-6);
    COLOR = vec4(g_rgb, g_a);
" + ClipApply + @"
}
";
        const string GradientCfCode = @"
shader_type canvas_item;
" + ClipUniforms + GradCommon + CfUniforms + @"
void fragment() {
    vec4 vcol = COLOR;
    vec4 gv = grad_sample(UV);
    float g_a = gv.a + vcol.a * (1.0 - gv.a);
    vec3 g_rgb = (gv.rgb * gv.a + vcol.rgb * vcol.a * (1.0 - gv.a)) / max(g_a, 1e-6);
    COLOR = vec4(g_rgb, g_a);
" + CfApply + ClipApply + @"
}
";

        readonly Shader[] _shaders = new Shader[8];
        // 材质缓存：key (program, ctx) → RID。ctx=0 无 clip（clip_count=0）。
        readonly Dictionary<(int prog, uint ctx), Rid> _cache = new();
        // per-ctx clip 链数组（新建材质时带上；SetClipEntries 刷新该 ctx 已缓存实例）。
        readonly Dictionary<uint, ClipArrays> _clipByCtx = new();
        // per-item 效果材质登记：RID → 所属 ctx（SetClipEntries 刷新时须覆盖——
        // 这些材质不在 _cache，但节点既带效果又带祖先裁剪时 clip 链同样要新）。
        readonly Dictionary<Rid, uint> _itemMats = new();

        sealed class ClipArrays
        {
            public Vector4[] F0 = new Vector4[MaxEntries];
            public Vector4[] F1 = new Vector4[MaxEntries];
            public Vector4[] Rect = new Vector4[MaxEntries];
            public Vector4[] R0 = new Vector4[MaxEntries];
            public Vector4[] R1 = new Vector4[MaxEntries];
            public Vector4[] Circ = new Vector4[MaxEntries];
            public Vector4[] Poly = new Vector4[MaxEntries * PolyVec4PerEntry];
            public int Count;
        }

        public GodotMaterialCache()
        {
            string[] codes = { UnlitCode, SdfCode, BgCode, UnlitCfCode, BgCfCode, ShadowCode, GradientCode, GradientCfCode };
            for (int i = 0; i < 8; i++)
                _shaders[i] = new Shader { Code = codes[i] };
        }

        /// <summary>program 的 Shader 资源（MirrorObj 建 per-item clone 材质用）。</summary>
        public Shader GetShader(int program) =>
            (program >= 0 && program < _shaders.Length) ? _shaders[program] : _shaders[0];

        /// <summary>
        /// 取 (program, ctx) 材质。共享实例（无 per-item 效果参数的节点用）；
        /// ctx>0 时材质带该 ctx 的 clip 链（首帧路径：MirrorPool 先 PushClipEntries 后 Get）。
        /// </summary>
        public Rid Get(int program, uint maskContext)
        {
            var key = (program, maskContext);
            if (_cache.TryGetValue(key, out var rid)) return rid;
            rid = RenderingServer.MaterialCreate();
            RenderingServer.MaterialSetShader(rid, GetShader(program).GetRid());
            if (maskContext > 0 && _clipByCtx.TryGetValue(maskContext, out var c))
                ApplyClip(rid, c);
            _cache[key] = rid;
            return rid;
        }

        /// <summary>
        /// 建 per-item 效果材质（MirrorObj 持有；Unity MPB per-renderer 的对仗）。带 ctx 的
        /// clip 链初值；后续链刷新经登记表覆盖。ReleaseItemMaterial 归还。
        /// </summary>
        public Rid CreateItemMaterial(int program, uint maskContext)
        {
            Rid rid = RenderingServer.MaterialCreate();
            RenderingServer.MaterialSetShader(rid, GetShader(program).GetRid());
            if (maskContext > 0 && _clipByCtx.TryGetValue(maskContext, out var c))
                ApplyClip(rid, c);
            _itemMats[rid] = maskContext;
            return rid;
        }

        public void ReleaseItemMaterial(Rid mat)
        {
            if (!mat.IsValid) return;
            _itemMats.Remove(mat);
            RenderingServer.FreeRid(mat);
        }

        /// <summary>
        /// 注册某 mask_ctx 的 clip 链（多 entry 交集语义）。ClipEntryView 列表 → shader 数组
        /// 布局（转换逐式对仗 unity MaterialManager.SetClipEntries），写 dict + 刷新该 ctx
        /// 已缓存材质。调用顺序对 MirrorPool 不构成约束（同 Unity 双路覆盖语义）。
        /// </summary>
        public void SetClipEntries(uint maskContext, List<ClipEntryView> entries)
        {
            var c = new ClipArrays();
            int n = Mathf.Min(entries.Count, MaxEntries);
            for (int e = 0; e < n; e++)
            {
                var en = entries[e];
                // 双独立 kind：frame0.w = shapeKind（0 无 / 1 circle / 2 polygon），
                // frame1.w = rectKind（0 无 / 1 直角 / 2 圆角）。
                float shapeKind = !en.HasShape ? 0f : (en.ShapeKind == 1 ? 2f : 1f);
                float rectKind = !en.HasRect ? 0f : (en.HasRadii ? 2f : 1f);
                c.F0[e] = new Vector4(en.A, en.C, en.Tx, shapeKind);
                c.F1[e] = new Vector4(en.B, en.D, en.Ty, rectKind);
                c.Rect[e] = new Vector4(en.W, en.H, en.Poly.Length, 0f);
                // Radii [TL, TR, BR, BL] 各 (rx, ry)：r0 = TL+TR、r1 = BR+BL。
                c.R0[e] = new Vector4(en.Radii[0].X, en.Radii[0].Y, en.Radii[1].X, en.Radii[1].Y);
                c.R1[e] = new Vector4(en.Radii[2].X, en.Radii[2].Y, en.Radii[3].X, en.Radii[3].Y);
                c.Circ[e] = new Vector4(en.CircleCx, en.CircleCy, en.CircleR, 0f);
                int slot = e * PolyVec4PerEntry;
                for (int k = 0; k + 1 < en.Poly.Length && k < PolyVec4PerEntry * 2; k += 2)
                    c.Poly[slot + k / 2] = new Vector4(en.Poly[k].X, en.Poly[k].Y, en.Poly[k + 1].X, en.Poly[k + 1].Y);
                // 奇数点数：末点重复进尾槽（crossing 判定用 poly_count 截断，重复点不参与）。
                if ((en.Poly.Length & 1) == 1 && en.Poly.Length <= PolyVec4PerEntry * 2)
                {
                    var last = en.Poly[en.Poly.Length - 1];
                    c.Poly[slot + (en.Poly.Length - 1) / 2] = new Vector4(last.X, last.Y, last.X, last.Y);
                }
            }
            c.Count = n;
            _clipByCtx[maskContext] = c;
            foreach (var kv in _cache)
                if (kv.Key.ctx == maskContext) ApplyClip(kv.Value, c);
            foreach (var kv in _itemMats)
                if (kv.Value == maskContext) ApplyClip(kv.Key, c);
        }

        void ApplyClip(Rid mat, ClipArrays c)
        {
            RenderingServer.MaterialSetParam(mat, "clip_frame0", c.F0);
            RenderingServer.MaterialSetParam(mat, "clip_frame1", c.F1);
            RenderingServer.MaterialSetParam(mat, "clip_rect", c.Rect);
            RenderingServer.MaterialSetParam(mat, "clip_radii0", c.R0);
            RenderingServer.MaterialSetParam(mat, "clip_radii1", c.R1);
            RenderingServer.MaterialSetParam(mat, "clip_circle", c.Circ);
            RenderingServer.MaterialSetParam(mat, "clip_poly", c.Poly);
            RenderingServer.MaterialSetParam(mat, "clip_count", (float)c.Count);
        }

        public void Clear()
        {
            foreach (var kv in _cache)
                if (kv.Value.IsValid) RenderingServer.FreeRid(kv.Value);
            _cache.Clear();
            foreach (var kv in _itemMats)
                if (kv.Key.IsValid) RenderingServer.FreeRid(kv.Key);
            _itemMats.Clear();
            _clipByCtx.Clear();
            for (int i = 0; i < _shaders.Length; i++)
            {
                _shaders[i]?.Dispose();
                _shaders[i] = null;
            }
        }
    }
}
