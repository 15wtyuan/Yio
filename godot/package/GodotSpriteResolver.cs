// GodotSpriteResolver：自绘图集 sprite 解析（Godot 版，对仗 unity SpriteResolver）。
//
// 消费打包器产物 atlas/{name}.atlas.json（UV 表）+ atlas/{page}.png（页纹理，懒加载）。
// 纹理以 RID 持有（RenderingServer 直连——不建 Node/Sprite），页键 → ImageTexture
// 缓存。字体图集页独立字典（core 自产 SDF atlas，经 RegisterFontAtlasPage 注入），
// 结构上不参与逐出（与 Unity 版同语义）。
//
// #62 页纹理逐出（expireAfterAccess）Godot 首版未接：Sweep no-op、StampPage no-op
// （showcase 页数固定，页常驻可接受）；接逐出时补 last-used 表 + FreeRid。

using System;
using System.Collections.Generic;
using Godot;

namespace Yio
{
    /// <summary>Godot sprite lookup：纹理 RID + 图集内子区 UV + 原始像素尺寸。</summary>
    public struct GodotSpriteLookup
    {
        public Rid Tex;
        /// <summary>atlas 子区（u0, v0, u1, v1）——C# 侧线性重映射 mesh UV 用。</summary>
        public Vector4 UvRect;
        public int OrigW;
        public int OrigH;
        public bool Found;
        /// <summary>页键（atlasIdx, page）；null = 字体页/miss——逐出面（未接）用。</summary>
        public (int AtlasIdx, int Page)? PageKey;
    }

    public sealed class GodotSpriteResolver
    {
        // sprite_key → (atlasIdx, page, uvRect(u0,v0,u1,v1), origW, origH)。所有 atlas 合并一张全局表。
        Dictionary<string, (int atlasIdx, int page, Vector4 uv, int ow, int oh)> _sprites;
        // 页纹理缓存：(atlasIdx, page) → ImageTexture（.GetRid() 给 add_mesh）；null = 加载失败已缓存。
        readonly Dictionary<(int, int), ImageTexture> _pageCache = new();
        // 字体图集页：path → 全区域 lookup（RegisterFontAtlasPage 注入，优先于 sprite 表）。
        readonly Dictionary<string, GodotSpriteLookup> _fontPages = new();
        // 页文件名表：(atlasIdx, page) → png 文件名（懒加载用）。
        List<List<string>> _atlasPages;
        // 页懒加载委托：pageFileName（如 "icons.png"）→ 纹理。null = 全 miss。
        Func<string, ImageTexture> _loadPage;
        // miss 去重告警。
        readonly HashSet<string> _warned = new();

        public void Init(List<AtlasManifest> atlases, Func<string, ImageTexture> loadPage)
        {
            _sprites = new Dictionary<string, (int, int, Vector4, int, int)>();
            _pageCache.Clear();
            _fontPages.Clear();
            _warned.Clear();
            _loadPage = loadPage;
            _atlasPages = new List<List<string>>();
            if (atlases == null) return;

            for (int ai = 0; ai < atlases.Count; ai++)
            {
                var atlas = atlases[ai];
                _atlasPages.Add(atlas.pages ?? new List<string>());
                if (atlas.sprites == null) continue;
                foreach (var kv in atlas.sprites)
                {
                    var e = kv.Value;
                    if (e.uv == null || e.uv.Length < 4 || e.orig == null || e.orig.Length < 2)
                        continue;
                    _sprites[kv.Key] = (ai, e.page,
                        new Vector4(e.uv[0], e.uv[1], e.uv[2], e.uv[3]),
                        e.orig[0], e.orig[1]);
                }
            }
        }

        /// <summary>查 sprite（font 页优先）。miss 返 Found=false（调用方 fallback 白纹理）。</summary>
        public GodotSpriteLookup GetSprite(string path)
        {
            if (string.IsNullOrEmpty(path)) return default;
            if (_fontPages.TryGetValue(path, out var fp)) return fp;
            if (_sprites != null && _sprites.TryGetValue(path, out var s))
            {
                var look = new GodotSpriteLookup
                {
                    Found = true,
                    PageKey = (s.atlasIdx, s.page),
                    OrigW = s.ow,
                    OrigH = s.oh,
                    UvRect = s.uv,
                };
                if (TryGetPageTex(s.atlasIdx, s.page, out var tex))
                {
                    look.Tex = tex.GetRid();
                    return look;
                }
                // 页未加载成功：按未命中处理（fallback 白纹理，不崩）。
                if (_warned.Add(path))
                    GD.PrintErr($"[GodotSpriteResolver] page texture unavailable for '{path}'");
                look.Found = false;
                return look;
            }
            if (_warned.Add(path))
                GD.Print($"[GodotSpriteResolver] sprite miss: '{path}'");
            return default;
        }

        /// <summary>注册字体图集页（FontAtlasSync sink 调用）：path 全区域命中。</summary>
        public void RegisterFontAtlasPage(string path, ImageTexture tex)
        {
            _fontPages[path] = new GodotSpriteLookup
            {
                Found = true,
                Tex = tex.GetRid(),
                // 字体页整页 [0,1]（blob UV 已是页内归一坐标，无需重映射）。
                UvRect = new Vector4(0f, 0f, 1f, 1f),
            };
        }

        bool TryGetPageTex(int atlasIdx, int page, out ImageTexture tex)
        {
            if (_pageCache.TryGetValue((atlasIdx, page), out tex)) return tex != null;
            tex = null;
            if (_loadPage == null || _atlasPages == null
                || atlasIdx >= _atlasPages.Count || page >= _atlasPages[atlasIdx].Count)
                return false;
            string fileName = _atlasPages[atlasIdx][page];
            tex = _loadPage(fileName);
            _pageCache[(atlasIdx, page)] = tex; // null 也缓存（加载失败不重试）
            return tex != null;
        }

        public void Clear()
        {
            foreach (var kv in _pageCache)
                kv.Value?.Dispose();
            _pageCache.Clear();
            // 字体页归 backend 拥有（FontAtlasSync sink 建），此处只清表。
            _fontPages.Clear();
            _sprites?.Clear();
        }
    }
}
