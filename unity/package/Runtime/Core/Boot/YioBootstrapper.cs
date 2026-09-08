// YioBootstrapper：产物目录引导序列（引擎无关）。
//
// 产物目录布局契约（yio build 输出，跨引擎唯一）：
//   ui/{name}.pkg.bin        包二进制
//   atlas/{name}.atlas.json  图集 manifest
//   atlas/{page}.png         图集页纹理（引擎侧懒加载，不入本类——纹理 IO 归引擎）
//   fonts/{file}             字体字节
// 引擎侧经 <see cref="IYioLoadSource"/> 注入加载钩子（Unity Driver 的 virtual
// LoadTextFile/LoadBytes 即其实现——消费者 AB/Addressables 覆写链保持有效）。
// 失败语义：包加载失败抛（UIPackageException 原样穿透——产物契约破裂要阻断启动）；
// atlas/字体缺失走 warn 回调继续（单资源缺失不阻断整树启动）。

using System;
using System.Collections.Generic;

namespace Yio
{
    /// <summary>
    /// 产物加载钩子（按资源种类命名——Unity Driver 的四个 public virtual 即其实现，
    /// 消费者 AB/Addressables 覆写链经此保持生效）。null = 未命中。
    /// </summary>
    public interface IYioLoadSource
    {
        /// <summary>读文本（atlas/{name}.atlas.json）。</summary>
        string ReadText(string relPath);
        /// <summary>读任意字节（保留的通用口——特定种类走下面语义化方法）。</summary>
        byte[] ReadBytes(string relPath);
        /// <summary>读包二进制（产物布局 ui/{pkgName}.pkg.bin）。</summary>
        byte[] ReadPackageBytes(string pkgName);
        /// <summary>读字体字节（产物布局 fonts/{fontFile}，file 值已含扩展）。</summary>
        byte[] ReadFontBytes(string fontFile);
    }

    /// <summary>
    /// 引导序列：load packages → parse atlas manifests → push image sizes。
    /// 字体注册独立方法（共享宿主的 FontsRegistered 守卫是引擎资源层语义，归调用方）。
    /// </summary>
    public sealed class YioBootstrapper
    {
        readonly YioHost _host;
        readonly IYioLoadSource _io;
        readonly Action<string> _warn;

        public YioBootstrapper(YioHost host, IYioLoadSource io, Action<string> warn)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _warn = warn ?? (_ => { });
        }

        /// <summary>引导结果：图集 manifest 集（引擎侧据此 InitSprites 纹理懒加载）。</summary>
        public sealed class BootResult
        {
            public readonly List<AtlasManifest> Atlases;
            /// <summary>成功 LoadPackage 的包数（缺包经 warn 报告、不阻断）。</summary>
            public int PackagesLoaded;
            internal BootResult(List<AtlasManifest> atlases, int packagesLoaded)
            {
                Atlases = atlases;
                PackagesLoaded = packagesLoaded;
            }
        }

        /// <summary>按 runtime manifest 引导：包 → 图集 manifest → 图尺寸下推。</summary>
        public BootResult Boot(RuntimeManifest runtime)
        {
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));

            // 1. Load packages（typed LoadPackage path——失败抛 UIPackageException 穿透，
            //    产物契约破裂阻断启动）。
            int loaded = 0;
            foreach (var pkgName in runtime.packages ?? new List<string>())
            {
                byte[] bytes = _io.ReadPackageBytes(pkgName);
                if (bytes != null && bytes.Length > 0)
                {
                    _host.Context.LoadPackage(pkgName, bytes);
                    loaded++;
                }
                else
                    _warn($"Package not found: ui/{pkgName}.pkg.bin");
            }

            // 2. Parse atlas manifests（单份缺失/损坏 warn 继续）。
            var atlases = new List<AtlasManifest>();
            foreach (var atlasName in runtime.atlases ?? new List<string>())
            {
                string atlasJson = _io.ReadText($"atlas/{atlasName}.atlas.json");
                if (string.IsNullOrEmpty(atlasJson))
                {
                    _warn($"atlas.json not found: atlas/{atlasName}.atlas.json");
                    continue;
                }
                try { atlases.Add(AtlasManifest.ParseAtlas(atlasJson)); }
                catch (Exception e) { _warn($"Failed to parse atlas/{atlasName}.atlas.json: {e.Message}"); }
            }

            // 3. Push image sizes to core（一次 FFI，首 tick 前——核心用此算 Image 的
            //    aspect-ratio + 闭包 known.w/h；只传元数据，纹理上传归引擎）。
            var sizes = MergeSpriteSizes(atlases);
            if (sizes.Count > 0)
            {
                int n = sizes.Count;
                var paths = new string[n];
                var ws = new uint[n];
                var hs = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    paths[i] = sizes[i].key;
                    ws[i] = sizes[i].w;
                    hs[i] = sizes[i].h;
                }
                _host.SetImageSizes(paths, ws, hs);
            }

            return new BootResult(atlases, loaded);
        }

        /// <summary>
        /// 注册 manifest 字体清单（family/file/default/fallback）+ 回退链。
        /// 守卫（共享宿主已注册则跳过）归引擎调用方——那是资源宿主层语义。
        /// </summary>
        public void RegisterFonts(RuntimeManifest runtime)
        {
            if (runtime?.fonts == null) return;
            var fallbacks = new List<string>();
            foreach (var rf in runtime.fonts)
            {
                if (string.IsNullOrEmpty(rf.family) || string.IsNullOrEmpty(rf.file)) continue;
                byte[] bytes = _io.ReadFontBytes(rf.file);
                if (bytes != null)
                    _host.RegisterFont(rf.family, bytes, rf.@default);
                // Collect fallback families (registered or not — Rust side skips unregistered).
                if (rf.fallback)
                    fallbacks.Add(rf.family);
            }
            if (fallbacks.Count > 0)
                _host.SetFallbackFamilies(fallbacks);
        }

        /// <summary>
        /// Merge all atlas manifests' sprite entries into a deduplicated list of (key, width, height).
        /// Pure function — testable without any engine. Deduplication: first occurrence wins
        /// (atlases ordered by manifest list order).
        /// </summary>
        public static List<(string key, uint w, uint h)> MergeSpriteSizes(List<AtlasManifest> atlases)
        {
            var result = new List<(string, uint, uint)>();
            if (atlases == null) return result;
            var seen = new HashSet<string>();
            foreach (var atlas in atlases)
            {
                if (atlas?.sprites == null) continue;
                foreach (var kv in atlas.sprites)
                {
                    if (!seen.Add(kv.Key)) continue;
                    var orig = kv.Value.orig;
                    if (orig == null || orig.Length < 2) continue;
                    result.Add((kv.Key, (uint)orig[0], (uint)orig[1]));
                }
            }
            return result;
        }
    }
}
