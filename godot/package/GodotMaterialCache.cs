// GodotMaterialCache：program → canvas_item 材质 RID（Godot 版，对仗 unity MaterialManager）。
//
// Godot 侧纹理不经材质——canvas_item_add_mesh 的 texture 参数随绘制命令绑定 shader 的
// 内建 TEXTURE sampler，故材质 key 只有 program（Unity 版纹理/clip 进 key 的原因是 Unity
// 材质绑纹理；Godot 无此约束，材质面更小）。
//
// shader 代码内嵌字符串（不落 .gdshader 资源）：免 Godot 资源导入步骤，CI headless
// 直接可用；语义逐段对仗 unity/package/Shaders/Yio-Unlit.shader 对应 keyword 变体。
//
// 首版覆盖（#14 批 B 第一步）：
//   program 0（img/mesh）  → unlit：vcol × tex（白纹理 = 纯色直通）
//   program 1（text SDF）  → sdf：face 路径（outline/underlay/glow/blur 效果面后续批；
//                            effect 参数全 0 时 Unity 路径也退化为纯 face，语义对齐）
//   program 2（bg-image）  → bg_composite：CSS source-over 合成（图 over 底色）
//   program 3/4（filter）  → unlit 兜底（色彩矩阵效果缺失，底色/图仍在——filter 页后续批）
//   program 5/6/7（shadow/gradient）→ invisible 兜底：这几类的 UV 是 core 几何编码
//                            （非纹理坐标），任何纹理采样兜底都会渲染成实心错块——
//                            错误渲染比缺失更糟，先丢弃，后续批补真 shader。
// clip（mask_ctx>0）：首版不裁剪（内容穿帮，滚动容器溢出可见）——SDF 链 shader 后续批。
//
// Godot 2D canvas 在 sRGB 空间合成（与 CSS 一致，等价 Unity Gamma 路径）——shader 不做
// vcol gamma 转换（Unity shader 的 Linear 分支在 Godot 无对应面）。

using Godot;

namespace Yio
{
    public sealed class GodotMaterialCache
    {
        // vcol(tint，CSS sRGB 值) × 纹理。白纹理 → 纯色直通。canvas_item 默认 blend
        // (mix，非预乘) 对齐 Unity SrcAlpha/OneMinusSrcAlpha。
        const string UnlitCode = @"
shader_type canvas_item;
void fragment() {
    COLOR = COLOR * texture(TEXTURE, UV);
}
";

        // SDF 文本 face 路径：TEXTURE.r = encoded distance（0.5 中心、inside>0.5）。
        // edge 换算逐式对齐 Yio-Unlit.shader ALPHA_MASK 段（scale = pxSize·(4/3·GS)/texW，
        // pxSize = uv→screen 面积导数倒数）；_GradientScale 默认 13（=SPREAD 12+1）。
        const string SdfCode = @"
shader_type canvas_item;
uniform float gradient_scale = 13.0;
uniform float face_dilate = 0.0;
uniform float blur_width = 0.0;
void fragment() {
    vec4 vcol = COLOR;
    float d = texture(TEXTURE, UV).r;
    vec2 uvDx = dFdx(UV);
    vec2 uvDy = dFdy(UV);
    float pxSize = inversesqrt(abs(uvDx.x * uvDy.y - uvDx.y * uvDx.x));
    float texW = 1.0 / TEXTURE_PIXEL_SIZE.x;
    float scale = pxSize * (1.3333 * gradient_scale) / texW;
    float edge = (d - 0.5) * scale + face_dilate * 0.5 * scale;
    float faceSoft = max(blur_width, 1.0);
    float face = clamp(edge / faceSoft + 0.5, 0.0, 1.0);
    COLOR = vec4(vcol.rgb, face * vcol.a);
}
";

        // Container+bg-image：CSS background 合成 = 图(tex) over 底色(vcol)。
        // 无底色时 a=tex.a、rgb 直通（等价 unlit 图直通）；有不透明底色时 a=1。
        const string BgCompositeCode = @"
shader_type canvas_item;
void fragment() {
    vec4 vcol = COLOR;
    vec4 tex = texture(TEXTURE, UV);
    float bgA = tex.a + vcol.a * (1.0 - tex.a);
    vec3 bgRgb = (tex.rgb * tex.a + vcol.rgb * vcol.a * (1.0 - tex.a)) / max(bgA, 1e-6);
    COLOR = vec4(bgRgb, bgA);
}
";

        // 不支持 program 的丢弃兜底（见类注释：错误渲染比缺失更糟）。
        const string InvisibleCode = @"
shader_type canvas_item;
void fragment() {
    COLOR = vec4(0.0);
}
";

        readonly Rid[] _byProgram = new Rid[8];
        readonly Shader _unlit;
        readonly Shader _sdf;
        readonly Shader _bg;
        readonly Shader _invisible;

        public GodotMaterialCache()
        {
            _unlit = new Shader { Code = UnlitCode };
            _sdf = new Shader { Code = SdfCode };
            _bg = new Shader { Code = BgCompositeCode };
            _invisible = new Shader { Code = InvisibleCode };
            _byProgram[0] = MaterialFor(_unlit);
            _byProgram[1] = MaterialFor(_sdf);
            _byProgram[2] = MaterialFor(_bg);
            _byProgram[3] = MaterialFor(_unlit);   // filter 无图：底色/顶点色仍在，矩阵效果缺失
            _byProgram[4] = MaterialFor(_unlit);   // filter+bg-image：同上
            _byProgram[5] = MaterialFor(_invisible);
            _byProgram[6] = MaterialFor(_invisible);
            _byProgram[7] = MaterialFor(_invisible);
        }

        static Rid MaterialFor(Shader shader)
        {
            Rid mat = RenderingServer.MaterialCreate();
            RenderingServer.MaterialSetShader(mat, shader.GetRid());
            return mat;
        }

        /// <summary>program（blob u8 列）→ 材质 RID。未知 program 落 invisible。</summary>
        public Rid Get(int program)
        {
            if (program >= 0 && program < _byProgram.Length && _byProgram[program].IsValid)
                return _byProgram[program];
            return _byProgram[0];
        }

        public void Clear()
        {
            for (int i = 0; i < _byProgram.Length; i++)
            {
                if (_byProgram[i].IsValid)
                {
                    RenderingServer.FreeRid(_byProgram[i]);
                    _byProgram[i] = default;
                }
            }
            _unlit?.Dispose();
            _sdf?.Dispose();
            _bg?.Dispose();
            _invisible?.Dispose();
        }
    }
}
