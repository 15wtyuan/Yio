// 暴露 Yio.Bindings 的 internal（csbindgen 生成的 Native / StageHandle）给 Yio.Core + Yio.Runtime + Yio.Tests。
// csbindgen 默认生成 internal 类型；投影层（Yio.Core：YioHost/NodeFactory/StyleMirror）、
// 引擎层（Yio.Runtime：UnityYioBackend）+ 路由测（Yio.Tests，BuildStage）
// 需跨程序集调用 Native.yio_stage_new/load_html/free。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yio.Core")]
[assembly: InternalsVisibleTo("Yio.Runtime")]
[assembly: InternalsVisibleTo("Yio.Tests")]
