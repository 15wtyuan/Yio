// Yio.Core（引擎无关层）的 internal 暴露给引擎集成层与测试程序集。
// Yio.Runtime 是设计内的朋友：Driver 经 StagePtr / ctx._rootId / _registry 等 internal
// 驱动投影层——切分前它们同程序集，切分后经 InternalsVisibleTo 维持同等可见面
// （引擎集成层职责见 docs/design/public-api.md §11.3）。
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Yio.Runtime")]
[assembly: InternalsVisibleTo("Yio.Tests")]
[assembly: InternalsVisibleTo("Yio.Tests.PlayMode")]
