[English Version](./editions.md) | 中文版

# Banyan 版本说明

Banyan Brain Lite 是 Banyan 的开源、单节点版本。

Lite 适合：

- 本地 agent memory；
- 离线优先部署；
- 小型单节点安装；
- demo 和集成测试；
- 嵌入式 agent runtime。

Lite 作为自包含 runtime 发布，提供 SQLite-backed 记忆存储、本地 operator 工具、基于 NID 的 agent 鉴权，以及可选的 Web 与 MCP 接口。

商业版本面向需要以下能力的团队：

- 多租户部署；
- 外部信任基础设施；
- 共享知识与记忆治理；
- 企业部署控制；
- 更高级的审计、编排和运维拓扑。

商业版本与本开源 Lite 仓库分开维护。

如需商业授权或企业部署，请联系 INNO LOTUS PTY LTD。

## 公共仓库边界

本仓库只文档化并发布 Lite 版本。这里应只包含适合公开的 Lite runtime 文档、示例、recipes 和 release notes。

内部路线图、商业版本架构、未发布产品策略和私有仓库拓扑不在本仓库中公开记录。