# Yio AI 大赛静态评审站

## 目录说明

- `index.html`：评审官网首页。
- `styles.css`：官网视觉样式。
- `app.js`：Demo 切换、源码读取和滚动呈现。
- `yio-demo/`：独立 Yio 工作区。
- `yio-demo/ui/xingque/main.html`：游戏主界面。
- `yio-demo/ui/xingque/inventory.html`：背包界面。
- `yio-demo/ui/xingque/leaderboard.html`：排行榜界面。

官网 iframe 直接读取 `yio-demo` 中的页面，没有另做一套只用于展示的 UI 副本。

## 本地预览官网

在仓库根目录运行任意静态 HTTP 服务，例如：

```powershell
python -m http.server 4173 --directory contest
```

浏览器打开：

```text
http://127.0.0.1:4173/
```

不能直接双击 `index.html` 验收，因为浏览器会限制 `file://` 页面读取源码文件，源码展示面板将无法工作。

## Yio 围栏验证

在仓库根目录运行：

```powershell
unity/package/Editor/Tools/yio.exe check contest/yio-demo --format json
```

预期结果：`success: true`，`errors: 0`，`warnings: 0`。

## Yio 本地预览

```powershell
unity/package/Editor/Tools/yio.exe preview contest/yio-demo
```

该命令用于开发期逐页查看 Yio Preview。评审官网自身无需运行 Yio 服务。

## 静态部署

推荐将 `contest/` 目录作为站点根目录部署到 Cloudflare Pages：

1. 连接 Yio GitHub 仓库。
2. Framework preset 选择 `None`。
3. Build command 留空。
4. Build output directory 填写 `contest`。
5. 部署完成后，将域名填写进申报材料的体验入口。

GitHub Pages 也可使用，但需要把 Pages 发布目录指向 `contest/`，或在工作流中复制该目录作为发布产物。

## Unity 素材补充

当前官网已预留三张 Unity 实机截图位置。获得 Unity 环境后：

1. 将三页打包并在 Showcase 场景加载。
2. 分别截取主界面、背包和排行榜的 16:9 运行画面。
3. 把图片放进 `contest/assets/`。
4. 将 `index.html` 中的 `.shot-placeholder` 替换为图片与说明。
5. 录制一段自然语言需求到 Unity 运行结果的完整流程视频。

## 本次生成记录

1. 首轮采用冷色科幻方案，经反馈后确认其过于接近科技后台。
2. 第二轮改为“东方幻想冒险”，采用暖宣纸、墨青、朱砂与旧金。
3. 设计主界面、背包、排行榜三种典型信息架构。
4. 仅使用 Yio 围栏支持的 HTML 标签与 CSS 属性生成源码。
5. 第一轮 `yio check` 检出多背景渐变、`:last-child` 和画序声明问题。
6. 删除越界语法并显式声明定位层级。
7. 第二轮校验通过后，将真实结果写入评审官网。
8. 依据视觉反馈重构三页世界观、排版、容器和官网整体主题，再次执行围栏验证。
9. 参考现代 RPG 的边缘 HUD、图标导航、装备槽和赛季榜单结构，将 Demo 从网页卡片布局重构为游戏运行时界面。
10. 从 Showcase 复用 game-icons.net 图标素材，并在评审官网保留 CC BY 3.0 署名。

## 图标许可

Demo 图标来自 [game-icons.net](https://game-icons.net/)，按 Creative Commons Attribution 3.0 许可使用。原始素材与署名信息沿用 Yio Showcase 的现有约定。
