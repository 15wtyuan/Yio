// MirrorPool：渲染树 → 引擎渲染对象的镜像 diff 策略（引擎无关）。
//
// 策略不变量（#14 Godot 后端经同一份本类获得与 Unity 一致的语义，只写 IMirrorHost）：
//   - 双 dict keying：reuse_key>0 的 slot 节点按 reuse_key 复用（slot 换绑 item 时
//     NodeId 变但 reuse_key 不变 → 对象不销毁重建）；reuse_key=0 按 node_id keying。
//   - change_level 三分支：Skip=对象还在清 stale；Header=只更 header 不重建 mesh；
//     Full=重建 mesh。新建对象无视 blob 的 Header 强制 FULL（无 mesh 可更）。
//   - parked keepalive（skip 段 flags bit1）：保留对象并隐藏——park 剪整子树，
//     根（reuse_key>0）与后代（reuse_key=0）都要保，只保根则后代被 stale 销毁、
//     reactivate 重建（滚动每帧 churn）。无先验对象 → no-op（懒创建）。
//   - visible=0（世界锚点出屏 render_hidden）：保留对象、隐藏——与 display:none 的
//     「剪除条目」正交（那些节点根本不产行，走 stale 销毁路径）。
//   - mount 路由：mount_id≠0 且容器登记存活 → 挂载容器；容器已销毁 → 降级屏幕 root
//     （可见性降级不悬空）。
//   - #62 页续命：每帧 Sync 开头对 VisuallyActive 的对象盖章 + 逐出心跳——「闲置页
//     仍被画」的证据只能从镜像侧取。
//
// 引擎句柄（root/挂载容器/池内对象）以 object 透传；Unity Transform 假 null 经
// IMirrorHost.IsContainerAlive 判定。

using System.Collections.Generic;

namespace Yio
{
    /// <summary>
    /// 引擎无关镜像池：持 poolKey→引擎对象双 dict，每帧 Sync 按 blob 行走查
    /// （标 stale → skip 段清 stale/parked → lean 段建/更新/复用 → 余 stale 销毁）。
    /// </summary>
    public sealed class MirrorPool
    {
        readonly Dictionary<ulong, object> _poolByNodeId = new();
        readonly Dictionary<ulong, object> _poolByReuse = new();
        // world-space 挂载容器登记（#109 C8）：槽位 → 业务摆放的引擎容器（opaque）。
        readonly Dictionary<ulong, object> _mountContainers = new();
        // 每帧 stale 集（容量复用，零 per-frame 分配）。
        readonly HashSet<object> _stale = new();

        /// <summary>当前镜像中的对象数（两 dict 之和）。测试/调试用。</summary>
        public int Count => _poolByNodeId.Count + _poolByReuse.Count;

        /// <summary>池内全部对象（引擎侧诊断 dump 格式化用；internal 引擎层可见）。</summary>
        internal IEnumerable<object> Objs
        {
            get
            {
                foreach (var kv in _poolByNodeId) yield return kv.Value;
                foreach (var kv in _poolByReuse) yield return kv.Value;
            }
        }

        /// <summary>登记挂载容器（引擎集成层 BindWorldMount → backend 转发；句柄 opaque）。</summary>
        public void SetMountContainer(ulong slot, object container) =>
            _mountContainers[slot] = container;

        /// <summary>
        /// 解除挂载容器：先把容器下存活镜像对象挂回屏幕 root（core 侧 mount 已清 0，
        /// 本帧 Sync 的 UpdateHeader 会自然回落 root——这里先行归位防容器销毁连带销毁
        /// 镜像对象），再移除登记。容器本体的销毁归引擎集成层（容器是它建的）。
        /// </summary>
        public void ClearMountContainer(ulong slot, object root, IMirrorHost host)
        {
            if (!_mountContainers.TryGetValue(slot, out var c) || !host.IsContainerAlive(c))
            {
                _mountContainers.Remove(slot);
                return;
            }
            _mountContainers.Remove(slot);
            foreach (var o in _poolByNodeId.Values) ((IMirrorObject)o).ReparentIfUnder(c, root);
            foreach (var o in _poolByReuse.Values) ((IMirrorObject)o).ReparentIfUnder(c, root);
        }

        object ResolveParent(IMirrorHost host, object root, ulong mountId)
        {
            if (mountId != 0
                && _mountContainers.TryGetValue(mountId, out var c)
                && host.IsContainerAlive(c))
                return c;
            return root;
        }

        /// <summary>
        /// 镜像 diff：遍历 blob 节点 → 经 host 建/更新/复用引擎对象。
        /// v10 起所有节点统一走 mesh 路径（核心自绘字体产 Mesh，text 不再单分一道）。
        /// </summary>
        public void Sync(FrameBlob blob, object root, IMirrorHost host)
        {
            // 防御：陈旧/非当前 blob 直接早退（magic+version 校验）。不做清理——上一帧的
            // 对象维持不动比误销毁更安全；调用方应自检 IsValid 再 Sync。
            if (!blob.IsValid) return;

            // #62 镜像侧续命：Skip 行不进 lean 段（变更帧零 GetSprite），「闲置页仍被画」
            // 的证据只能从镜像取。先盖章收本帧证据，再 Sweep 裁决。池 ≤ 数百量级，
            // 每帧迭代纳秒级；盖章只写缓存命中项，零分配。
            foreach (var o in _poolByNodeId.Values) if (((IMirrorObject)o).VisuallyActive) ((IMirrorObject)o).StampPage();
            foreach (var o in _poolByReuse.Values) if (((IMirrorObject)o).VisuallyActive) ((IMirrorObject)o).StampPage();
            host.SweepPages();

            // ① 全标 stale
            _stale.Clear();
            foreach (var o in _poolByNodeId.Values) _stale.Add(o);
            foreach (var o in _poolByReuse.Values) _stale.Add(o);

            // clip 链数组刷新：每帧为 blob 表里**每个** ctx 全量重设（不依赖 lean 行——
            // idle 全 Skip 帧也刷；ctx 少、数组小，开销可忽略）。
            foreach (uint clipCtx in blob.ClipContextIds())
                host.PushClipEntries(clipCtx, blob.ReadClipEntries(clipCtx));

            // ② v15 skip 段先走：Skip 行 = 「对象还在，清 stale」；parked keepalive =
            //    保留对象并隐藏。这行不进 SOA（16B/条）——引擎侧不读 header/mesh。
            for (int s = 0; s < blob.SkipCount; s++)
            {
                bool parked = blob.SkipParked(s);
                uint rk = blob.SkipReuseKey(s);
                ulong poolKey = rk != 0 ? rk : blob.SkipNodeId(s);
                Dictionary<ulong, object> pool = rk != 0 ? _poolByReuse : _poolByNodeId;
                if (pool.TryGetValue(poolKey, out var oK))
                {
                    var m = (IMirrorObject)oK;
                    _stale.Remove(oK);
                    if (parked) m.SetActive(false);
                }
            }

            // ③ lean 段遍历（Header/Full 行；Skip 分支已被 skip 段接管）。
            int n = blob.LeanCount;
            for (int i = 0; i < n; i++)
            {
                ulong id = blob.NodeId(i);
                uint reuseKey = blob.ReuseKey(i);    // 虚拟列表
                ulong poolKey = reuseKey != 0 ? reuseKey : id;
                Dictionary<ulong, object> pool = reuseKey != 0 ? _poolByReuse : _poolByNodeId;

                // visible=0（世界锚点出屏的 render_hidden）：保留镜像对象、隐藏。
                if (!blob.Visible(i))
                {
                    if (pool.TryGetValue(poolKey, out var oH))
                    {
                        _stale.Remove(oH);
                        ((IMirrorObject)oH).SetActive(false);
                    }
                    continue;
                }
                byte kind = blob.PayloadKind(i);
                byte level = blob.ChangeLevel(i);   // 1=Header 2=Full（Skip 不在此）

                // v10：kind 只有 1=Mesh（文本核心自产 atlas 走同路径）。
                if (kind != 1) continue;

                // 确保对象存在；新建无 mesh → 强制 FULL（无视 blob 的 HEADER）。
                IMirrorObject m;
                if (!pool.TryGetValue(poolKey, out var o))
                {
                    m = host.CreateObject(ResolveParent(host, root, blob.MountId(i)));
                    o = m;
                    pool[poolKey] = o;
                    level = 2; // 强制 FULL
                }
                else
                {
                    m = (IMirrorObject)o;
                }
                _stale.Remove(o);
                m.SetActive(true); // reactivate parked→active（幂等）

                m.UpdateHeader(blob, i, ResolveParent(host, root, blob.MountId(i)));
                if (level == 2)
                    m.UploadMesh(blob, i);
                // #66：非纯平移行的 bounds 补偿须在 upload 之后（RecalculateBounds 会覆盖）
                // 且从 RawMeshBounds 缓存底重算——Header 帧不重建 mesh，读当前 bounds
                // 会拿到上帧已补偿值再乘一次 L（非幂等）。
                if (!blob.IsPureTranslation(i)) m.CompensateBounds(blob, i);
            }

            // ④ 余 stale 销毁（两个 dict）
            var dead1 = new List<ulong>();
            foreach (var kv in _poolByNodeId) if (_stale.Contains(kv.Value)) dead1.Add(kv.Key);
            foreach (var id in dead1) { ((IMirrorObject)_poolByNodeId[id]).Destroy(); _poolByNodeId.Remove(id); }
            var dead2 = new List<ulong>();
            foreach (var kv in _poolByReuse) if (_stale.Contains(kv.Value)) dead2.Add(kv.Key);
            foreach (var id in dead2) { ((IMirrorObject)_poolByReuse[id]).Destroy(); _poolByReuse.Remove(id); }
        }

        /// <summary>销毁全部镜像对象并清池（Dispose 路径）。</summary>
        public void Clear()
        {
            foreach (var o in _poolByNodeId.Values) ((IMirrorObject)o).Destroy();
            _poolByNodeId.Clear();
            foreach (var o in _poolByReuse.Values) ((IMirrorObject)o).Destroy();
            _poolByReuse.Clear();
            _stale.Clear();
        }
    }
}
