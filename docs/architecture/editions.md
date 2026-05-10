English | [中文版](./editions.cn.md)

# Banyan Editions

Banyan Brain Lite is the open-source, single-node edition of Banyan.

Lite is designed for:

- local agent memory;
- offline-first deployments;
- small single-node installations;
- demos and integration testing;
- embedded agent runtimes.

Lite ships as a self-contained runtime with SQLite-backed memory storage, local operator tooling, NID-based agent authentication, and optional web and MCP interfaces.

Commercial editions are available for teams that need production or enterprise capabilities beyond Lite.

## Commercial edition boundary

### Banyan Pro

Banyan Pro is the production-oriented edition for a **single enterprise customer deployment**.

Pro is single-enterprise and single-tenant by product scope. It supports multiple users, roles, departments, workspaces, projects, agents, sessions, and shared memory pools inside one customer organization.

Pro is intended for teams that need:

- production memory-node deployment;
- organization/workspace/session scoped memory access;
- NID-authenticated service boundaries;
- persistent NCP transport;
- shared memory pools and memory governance inside one enterprise;
- integration with Ivy and other internal services.

Pro does not provide SaaS platform-level multi-tenancy, tenant billing, tenant quotas, tenant onboarding/offboarding, cross-customer administration, or shared multi-customer hosting assumptions.

### Banyan Ent

Banyan Ent is the enterprise-grade edition for advanced deployment, compliance, and integration requirements.

Ent is intended for teams that need:

- self-hosted-first deployment;
- dedicated cloud deployment;
- advanced audit and compliance controls;
- SSO / LDAP / Active Directory integration;
- advanced backup and restore;
- private model and private vector-store options;
- high availability and disaster recovery planning;
- advanced connector and model gateway architecture;
- complex enterprise or group-company structures when required.

If a real customer requires multi-organization or group-company isolation, that should be evaluated under Ent first, not assumed as part of Pro.

Commercial editions are maintained separately from this open-source Lite repository.

For commercial licensing or enterprise deployment, contact INNO LOTUS PTY LTD.

## Public repository boundary

This repository documents and ships the Lite edition only. It should contain public-safe Lite runtime documentation, examples, recipes, and release notes.

Internal implementation details, private repository topology, unreleased product strategy, and commercial source code are intentionally not documented here. Lite may describe public edition boundaries at a high level so users understand when they should move to a commercial edition.
