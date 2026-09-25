# Commit message 生成规则

为 QuotaPeek 生成提交信息时，遵循 Conventional Commits，并与仓库根目录的 `.commitlintrc.json` 保持一致。

## 输出格式

- 只输出一条可直接用于 Git 提交的完整消息，不加解释、代码块、引号或 emoji。
- 标题格式为 `type(scope): 中文简述`；没有合适的单一 scope 时使用 `type: 中文简述`。
- 标题总长度不超过 72 个字符，简述以具体动作开头，例如“新增”“修复”“优化”“移除”“配置”。标题末尾不加句号。
- 描述和正文使用简体中文，保留 QuotaPeek、WPF、Codex、API、SQLite 等英文专有名词及代码标识符。
- `type` 和 `scope` 使用小写英文。不要照搬历史提交中的英文描述或不规范格式。

## 类型与范围

根据本次变更的主要目的选择一个 type：

| type | 用途 |
|---|---|
| feat | 新增用户可见功能 |
| fix | 修复错误 |
| refactor | 不改变外部行为的代码重构 |
| perf | 性能优化 |
| test | 新增或调整测试 |
| docs | 文档修改 |
| ci | CI/CD 流程修改 |
| build | 构建、打包或依赖修改 |
| chore | 开发工具、仓库配置等维护工作 |
| style | 不改变行为的代码格式调整，不用于 UI 功能修改 |
| revert | 回退已有提交 |

scope 按实际影响范围选择，可用 `app`、`ui`、`core`、`providers`、`codex`、`hone`、`storage`、`credentials`、`build`、`tests`、`docs`、`repo`。这些是建议，不是必须使用的固定列表。

## 内容依据

- 只描述提供的 diff 中实际包含的改动。有暂存改动时，以本次暂存内容为准。
- 标题概括主要变化及作用，不使用“更新代码”“一些修改”“优化功能”等空泛描述。
- 首次提交已经实现应用功能时使用 `feat(app)`，例如“实现 QuotaPeek 桌面额度监控组件”。
- 简单改动只写标题；需要补充原因或影响时，空一行后写简短正文，可使用 2–5 条 `- ` 列表。不要逐个罗列文件。
- 正文和 footer 前各空一行，不强制限制正文和 footer 的单行长度。
- 只有实际存在不兼容变更时，才在冒号前加 `!`，并在 footer 用 `BREAKING CHANGE:` 说明影响及迁移方式。
- 只引用已提供且确实相关的 Issue 编号。不要虚构功能、原因、性能数据或测试通过结论，不输出凭据或 API key。

## 示例

```text
feat(app): 实现 QuotaPeek 桌面额度监控组件

- 接入 Hone 和 Codex 额度读取
- 新增托盘菜单、折叠展开和低额度提醒
```

```text
fix(ui): 修复胶囊展开后的窗口位置偏移
```

```text
chore(repo): 配置提交信息生成规则
```
