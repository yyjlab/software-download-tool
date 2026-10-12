# 界面规范

统一采用 MahApps 风格的轻量 Windows 工具界面；仅借鉴视觉，不引入控件库。

- 共用颜色、字体和控件样式统一在 `App.xaml` 管理。新增页面优先复用，不另建局部主题。
- 浅灰页面和侧栏、白色内容区、蓝色强调；黄色只用于风险提示。
- 字体优先使用 Microsoft YaHei UI，Segoe UI 作为后备；页面标题用 `PageTitleTextStyle`（19 半粗体），列表名称用 `ListTitleTextStyle`（14 半粗体），说明和状态为 12 常规字重（`BodyTextStyle`）。容量数字保留 Segoe UI。
- 文字统一使用 Display 排版，抗锯齿跟随系统设置；开启布局取整、像素对齐和 PerMonitorV2 DPI 适配，不对窗口整体做位图拉伸。
- 按钮小圆角 6，卡片圆角 8；保留细边框，不使用胶囊、大阴影、渐变或装饰性动画。
- 主要操作用 `PrimaryButtonStyle`，刷新、打开目录等用 `SecondaryButtonStyle`；勾选框用 `ToolCheckBoxStyle`。
- 页面头部高 92、底部高 48，左右边距 28；同排操作水平对齐，同组按钮统一尺寸。
- 保留悬停、按下、键盘焦点、禁用状态。完整诊断信息可放在悬停提示中，风险说明和确认不可省略。
- 样式修改不改变安装、清理规则、默认勾选、按钮启用条件或确认流程。
