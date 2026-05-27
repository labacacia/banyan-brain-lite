[English](./client-integration-profile.md) | 中文

> **另请参阅：** [Pro 客户端配置文件](../../pro/docs/client-integration-profile.md) · [Ent 客户端配置文件](../../ent/docs/client-integration-profile.md)

# Banyan 客户端集成配置文件

本配置文件使客户集成在 Banyan Lite、Pro 和 Ent 之间保持可移植性。应用程序代码应依赖这些设置和能力名称，而不是依赖各版本特定的启动命令或管理员 API。

## 可移植客户端设置

| 设置 | Lite 值 | 可移植性规则 |
| --- | --- | --- |
| `BANYAN_EDITION` | `lite` | 选择版本适配器。在应用程序代码中保持此项为唯一的版本切换开关。 |
| `BANYAN_BASE_URL` | `banyan web` 时为 `http://localhost:5180`，或 `banyan serve` 的 URL | 内存平面的基础 URL。 |
| `BANYAN_MEMORY_PLANE` | `http-rest`、`nps-memory-node` 或 `mcp-http` | 对于需要迁移到 Pro 或 Ent 的客户端，优先使用 `nps-memory-node`。 |
| `BANYAN_AUTH_MODE` | `anonymous`、`nid-writes-required` 或 `nid-all-required` | 即使 Lite 允许匿名读取，生产环境客户端也应使用 NID（节点身份标识）认证。 |
| `BANYAN_TENANT_ID` | 未设置 | Lite 是单节点模式。在客户端配置中保留此设置，但不要将其作为安全边界发送。 |
| `BANYAN_WORKSPACE_ID` | 命名空间，如 `user-alice`、`project-foo` 或 `shared` | 将命名空间作为工作区级别的可移植字段处理。 |

## 可移植内存操作

| 操作 | 客户端意图 | Lite 端点 |
| --- | --- | --- |
| `memory.remember` | 存储持久化的用户、项目或代理内存。 | `POST /api/memory` |
| `memory.search` | 为当前轮次召回相关内存。 | `GET /api/memory/search` 或 NWP（节点线协议）`POST /api/memory/query` |
| `memory.update` | 更正已有的内存条目。 | `PUT /api/memory/{id}` |
| `memory.forget` | 对内存做逻辑删除，同时保留审计追踪。 | `DELETE /api/memory/{id}` |
| `memory.schema` | 发现 NPS（节点协议规范）内存节点的数据结构。 | `GET /.schema` |
| `memory.manifest` | 发现节点能力。 | `GET /.nwm` |

## Lite 版本配置文件

Lite 是独立的、离线优先的配置文件。它可以作为 Web UI、纯 NWP 内存节点、stdio MCP（模型上下文协议）或流式 HTTP MCP 运行。

```bash
export BANYAN_EDITION=lite
export BANYAN_BASE_URL=http://localhost:5180
export BANYAN_MEMORY_PLANE=nps-memory-node
export BANYAN_AUTH_MODE=nid-writes-required
export BANYAN_WORKSPACE_ID=user-alice
```

推荐启动方式：

```bash
banyan web --nid-auth writes-required
```

当客户端只需要 NPS 内存节点接口时，使用 `banyan serve --nid-auth writes-required`。

## 切换指引

- Lite 迁移至 Pro：保留 NID 认证和 NPS 内存节点请求结构。将工作区边界从客户端提供的命名空间迁移至由 Pro 强制执行的 NID 派生的组织/工作区范围。
- Lite 迁移至 Ent：保留相同的内存操作名称和能力期望，但将受信任的代理流量通过 Ent NPS 网关路由。将 Ent HTTP 租户路由视为管理和兼容性 API，而非可移植的代理内存平面。
- 不要在客户端库中硬编码 `localhost`、`/api/memory/search` 或匿名访问。通过上述配置文件设置来绑定这些值。

## 机器可读配置文件

仓库根目录包含 `banyan.integration.json`，其中包含与上述相同的版本配置文件，供工具、安装程序和冒烟测试使用。
