// MirrorPool 引擎无关策略测试（fake host——#14 Godot 后端与 Unity 共享同一份策略，
// 这些不变量就是「镜像语义」本身：新建强制 FULL、stale 销毁、Skip 保活、parked 隐藏
// 保活、reuse_key 池化、visible=0 保活、mount 路由降级）。
// v15 blob 构造镜像 yio_ffi_c/src/blob.rs（同 FrameBlobTests 的 BuildBlob 家族）。

using System;
using System.Collections.Generic;
using Xunit;

namespace Yio.Tests.Core
{
    public class MirrorPolicyTests
    {
        // —— v15 blob 构造（lean 行 + skip 段；mesh arena 空——策略不解析 mesh 内容）——

        sealed class Row
        {
            public ulong Id; public uint Reuse; public bool Visible = true;
            public byte Level = 2; public ulong Mount;
        }
        sealed class Skip
        {
            public ulong Id; public uint Reuse; public bool Parked;
        }

        static byte[] Blob(Row[] lean, Skip[] skip = null)
        {
            skip ??= Array.Empty<Skip>();
            int n = lean.Length;
            int[] elemSizes = { 8, 8, 1, 4, 4, 4, 4, 4, 4, 4, 4, 4, 1, 4, 4, 4, 1, 1, 4, 8, 4 };
            int numCols = elemSizes.Length;
            int colOff = 16 + numCols * 4 + 32;
            var offs = new int[numCols];
            for (int i = 0; i < numCols; i++) { offs[i] = colOff; colOff += elemSizes[i] * n; }
            int fatArenaOff = colOff;

            var b = new List<byte>();
            b.AddRange(BitConverter.GetBytes(0x314F4959u)); // magic
            b.AddRange(BitConverter.GetBytes(15u));          // version
            b.AddRange(BitConverter.GetBytes((uint)(n + skip.Length)));
            b.AddRange(BitConverter.GetBytes((uint)skip.Length));
            foreach (var o in offs) b.AddRange(BitConverter.GetBytes(o));
            b.AddRange(BitConverter.GetBytes(fatArenaOff));  // mesh/clip/path/fat arena 全空
            b.AddRange(BitConverter.GetBytes(0));
            b.AddRange(BitConverter.GetBytes(fatArenaOff));
            b.AddRange(BitConverter.GetBytes(0));
            b.AddRange(BitConverter.GetBytes(fatArenaOff));
            b.AddRange(BitConverter.GetBytes(0));
            b.AddRange(BitConverter.GetBytes(fatArenaOff));
            b.AddRange(BitConverter.GetBytes(0));

            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(lean[i].Id));   // node_id
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(-1L));          // parent_id
            for (int i = 0; i < n; i++) b.Add((byte)(lean[i].Visible ? 1 : 0));          // visible
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(1f));           // alpha
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // sort_key
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // mask_context
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(1f));           // m_a
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0f));           // m_b
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0f));           // m_c
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(1f));           // m_d
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0f));           // m_tx
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0f));           // m_ty
            for (int i = 0; i < n; i++) b.Add(1);                                        // payload_kind=Mesh
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // mesh_off
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // mesh_len
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // path_idx
            for (int i = 0; i < n; i++) b.Add(0);                                        // program
            for (int i = 0; i < n; i++) b.Add(lean[i].Level);                            // change_level
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(lean[i].Reuse));// reuse_key
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(lean[i].Mount));// mount_id
            for (int i = 0; i < n; i++) b.AddRange(BitConverter.GetBytes(0u));           // fat_off

            foreach (var s in skip)
            {
                b.AddRange(BitConverter.GetBytes(s.Id));
                b.AddRange(BitConverter.GetBytes(s.Reuse));
                b.Add((byte)(s.Parked ? 0x02 : 0x00));
                b.AddRange(new byte[3]);
            }
            return b.ToArray();
        }

        // —— fake 引擎件 ——

        sealed class FakeObj : IMirrorObject
        {
            public object Parent;
            public bool Active = true;
            public bool Destroyed;
            public int HeaderUpdates, MeshUploads;
            public bool VisuallyActive => Active && !Destroyed;
            public void StampPage() { }
            public void SetActive(bool active)
            {
                if (Destroyed || Active == active) return;
                Active = active;
            }
            public void UpdateHeader(FrameBlob blob, int i, object parent) { HeaderUpdates++; Parent = parent; }
            public void UploadMesh(FrameBlob blob, int i) => MeshUploads++;
            public void CompensateBounds(FrameBlob blob, int i) { }
            public void ReparentIfUnder(object container, object root) { if (Parent == container) Parent = root; }
            public void Destroy() => Destroyed = true;
        }

        sealed class FakeHost : IMirrorHost
        {
            public readonly List<FakeObj> Created = new();
            public readonly HashSet<object> DeadContainers = new();
            public int Sweeps;
            public IMirrorObject CreateObject(object parent) { var o = new FakeObj { Parent = parent }; Created.Add(o); return o; }
            public bool IsContainerAlive(object container) => !DeadContainers.Contains(container);
            public void PushClipEntries(uint clipContext, List<ClipEntryView> entries) { }
            public void SweepPages() => Sweeps++;
        }

        static readonly object Root = new();
        static readonly object Mount7 = new();

        // —— 不变量 ——

        [Fact]
        public void NewObjectForcedFull_EvenWhenBlobSaysHeader()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            // 首见的行 change_level=1（Header）——新建无 mesh 可更，必须强制 FULL。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100, Level = 1 } })), Root, host);
            Assert.Single(host.Created);
            Assert.Equal(1, host.Created[0].MeshUploads);
        }

        [Fact]
        public void StaleEntryDestroyedOnDisappearance()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100 } })), Root, host);
            // 帧 2：node 100 消失（不在 lean 也不在 skip 段）→ stale 销毁。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 200 } })), Root, host);
            Assert.True(host.Created[0].Destroyed);
            Assert.Equal(2, host.Created.Count); // 200 新建
            Assert.Equal(1, pool.Count);
        }

        [Fact]
        public void SkipSegmentKeepsObject_NoHeaderUpdate()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100 } })), Root, host);
            Assert.Equal(1, host.Created[0].HeaderUpdates);
            // 帧 2：node 100 进 skip 段（非 parked）——对象保留、不销毁、不更新 header。
            pool.Sync(new FrameBlob(Blob(Array.Empty<Row>(), new[] { new Skip { Id = 100 } })), Root, host);
            Assert.False(host.Created[0].Destroyed);
            Assert.Equal(1, host.Created[0].HeaderUpdates);
            Assert.True(host.Created[0].Active);
            Assert.Equal(1, pool.Count);
        }

        [Fact]
        public void ParkedKeepsObjectHidden_ReactivatesWithoutRecreate()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100, Reuse = 5 } })), Root, host);
            Assert.Single(host.Created);

            // 帧 2：parked（skip 段 flags bit1）——保留 + 隐藏。
            pool.Sync(new FrameBlob(Blob(Array.Empty<Row>(),
                new[] { new Skip { Id = 100, Reuse = 5, Parked = true } })), Root, host);
            Assert.False(host.Created[0].Destroyed);
            Assert.False(host.Created[0].Active);

            // 帧 3：同 reuse_key 回到 active——原对象复活，不重建。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 200, Reuse = 5 } })), Root, host);
            Assert.Single(host.Created);
            Assert.True(host.Created[0].Active);
            Assert.Equal(1, pool.Count);
        }

        [Fact]
        public void ReuseKeyPoolsObjectAcrossNodeIdChange()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100, Reuse = 5 } })), Root, host);
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 200, Reuse = 5 } })), Root, host);
            // slot 换绑：NodeId 变但 reuse_key 不变 → 同一对象（不销毁重建）。
            Assert.Single(host.Created);
            Assert.False(host.Created[0].Destroyed);
        }

        [Fact]
        public void VisibleZeroKeepsObjectHidden()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100 } })), Root, host);
            // 帧 2：visible=0（render_hidden）——保留 + 隐藏 + 不更新 header。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100, Visible = false } })), Root, host);
            Assert.False(host.Created[0].Destroyed);
            Assert.False(host.Created[0].Active);
            Assert.Equal(1, host.Created[0].HeaderUpdates);
        }

        [Fact]
        public void MountRouting_RegisteredDeadAndUnregistered()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.SetMountContainer(7, Mount7);
            // 登记 + 存活 → 挂容器。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 1, Mount = 7 } })), Root, host);
            Assert.Same(Mount7, host.Created[0].Parent);

            // 容器死亡 → 降级屏幕 root（可见性降级不悬空）。
            host.DeadContainers.Add(Mount7);
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 2, Mount = 7 } })), Root, host);
            Assert.Same(Root, host.Created[1].Parent);

            // 未登记的 mount_id → root。
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 3, Mount = 42 } })), Root, host);
            Assert.Same(Root, host.Created[2].Parent);
        }

        [Fact]
        public void StaleBlobRejected_NoSideEffects()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 100 } })), Root, host);
            Assert.Single(host.Created);

            // magic 损坏 → 早退：不创建也不销毁（上一帧对象维持不动比误销毁更安全）。
            var raw = Blob(new[] { new Row { Id = 200 } });
            raw[0] = 0xFF;
            pool.Sync(new FrameBlob(raw), Root, host);
            Assert.Single(host.Created);
            Assert.False(host.Created[0].Destroyed);
        }

        [Fact]
        public void ClearDestroysAll()
        {
            var host = new FakeHost();
            var pool = new MirrorPool();
            pool.Sync(new FrameBlob(Blob(new[] { new Row { Id = 1 }, new Row { Id = 2 } })), Root, host);
            Assert.Equal(2, pool.Count);
            pool.Clear();
            Assert.Equal(0, pool.Count);
            Assert.True(host.Created[0].Destroyed);
            Assert.True(host.Created[1].Destroyed);
        }
    }
}
