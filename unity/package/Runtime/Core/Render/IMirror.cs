// IMirror：镜像渲染的引擎侧实现契约（Unity/Godot 各写一份）。
//
// 分工铁律：diff/复用/保活策略全在引擎无关的 MirrorPool（本目录），引擎侧只做
// 「创建/更新/销毁引擎渲染对象」。策略不变量（stale 销毁、parked 保活、新建强制
// FULL、visible=0 保活、mount 路由、#62 页盖章）的实现细节见 MirrorPool.Sync 注释——
// 新引擎后端照契约实现即可获得与 Unity 一致的镜像语义。

using System.Collections.Generic;

namespace Yio
{
    /// <summary>
    /// 引擎侧宿主：渲染对象工厂 + 材质/clip 面 + 图集页逐出心跳。
    /// 引擎句柄（root/挂载容器）以 <see cref="object"/> 透传——中性层不感知引擎类型；
    /// Unity 的 Transform 假 null 语义（销毁后 ==null）经 <see cref="IsContainerAlive"/> 判定，
    /// 不能在中性层用 !=null 表达。
    /// </summary>
    public interface IMirrorHost
    {
        /// <summary>在指定父级（屏幕 root 或挂载容器）下创建引擎渲染对象。</summary>
        IMirrorObject CreateObject(object parent);

        /// <summary>挂载容器是否存活（引擎对象销毁判定的引擎侧归口）。</summary>
        bool IsContainerAlive(object container);

        /// <summary>
        /// 材质侧 clip 链刷新（每 ctx 全量重设）。中性层对 blob 表里每个 ctx 调——
        /// 不依赖任何 lean 行被处理（idle 全 Skip 帧也刷；ctx 少、数组小）。
        /// </summary>
        void PushClipEntries(uint clipContext, List<ClipEntryView> entries);

        /// <summary>
        /// 图集页纹理逐出心跳（#62）：每帧 Sync 入口顺路扫一遍闲置页。
        /// 策略细节（宽限/盖章/字体豁免）封在引擎侧 resolver，无 resolver 时 no-op。
        /// </summary>
        void SweepPages();
    }

    /// <summary>引擎渲染对象（Unity：GO+Mesh+MPB；Godot：RenderingServer item 一类）。</summary>
    public interface IMirrorObject
    {
        /// <summary>
        /// #62 续命判据：本帧是否真的在屏（Unity activeInHierarchy——activeSelf 不够：
        /// 挂在被业务侧 SetActive(false) 的祖先下时 activeSelf 仍 true，页面会因此被
        /// 永久续命、永不逐出）。
        /// </summary>
        bool VisuallyActive { get; }

        /// <summary>#62 盖章续命本对象绑定的图集页（无绑定页 no-op）。Skip 帧不调用——「视觉与上帧相同」即绑定不变。</summary>
        void StampPage();

        /// <summary>显示/隐藏。幂等（引擎侧状态未变跳过）。</summary>
        void SetActive(bool active);

        /// <summary>
        /// 更新 header 级状态（位置/排序/材质/per-renderer uniforms）。
        /// 无论 HEADER 还是 FULL 帧都调用；引擎侧自读 blob 列（含 path→sprite 解析、
        /// program→材质选择）——所需要的一切都在 blob 行里。
        /// </summary>
        /// <param name="parent">本行应挂的父级（中性层已解析：挂载容器或屏幕 root）。</param>
        void UpdateHeader(FrameBlob blob, int i, object parent);

        /// <summary>
        /// 重建 mesh（仅 FULL 行）。含原始 AABB 存底（#66 补偿基——Header 帧不重建 mesh，
        /// 补偿永远从原始值起算，不得读当前 bounds）与 sprite 子区 UV 重映射。
        /// </summary>
        void UploadMesh(FrameBlob blob, int i);

        /// <summary>
        /// 非纯平移行的剔除 bounds 补偿（#66：引擎剔除器拿不到 shader 侧矩阵——旋转/
        /// 缩放走 uniform 时视觉 AABB 与剔除 AABB 错位）。幂等；纯平移行不调用。
        /// 无此问题的引擎（剔除基于实时数据）可空实现。
        /// </summary>
        void CompensateBounds(FrameBlob blob, int i);

        /// <summary>
        /// 解除挂载时归位：若本对象当前挂于 container 下，移回屏幕 root。
        /// （ClearMountContainer 遍历池内对象逐个问询。）
        /// </summary>
        void ReparentIfUnder(object container, object root);

        /// <summary>销毁引擎资源（edit/play 模式分流由引擎侧自理）。</summary>
        void Destroy();
    }
}
