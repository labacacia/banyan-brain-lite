[English Version](./ols-surface-reference.md) | 中文版

# OLS.Root API 表面参考

> 反射自 OLS.Root.* `1.0.0-alpha.1`（net10.0），来源 `nexus-lc01.starfield.com.au` 的 `nuget-hosted` 仓库。`Banyan.Identity` 据此设计 SQLite 身份存储。

## 必须实现的 Store 接口

### OLS.Root.Core (`OLS.Root.Core.Stores`)

| 接口 | 方法（精简） |
|---|---|
| `IUserStore<TUser>` | Create / Update / Delete / FindByIdAsync / FindByNameAsync / Get-Set UserName / NormalizedUserName |
| `IUserPasswordStore<TUser>` | Set/GetPasswordHashAsync, HasPasswordAsync |
| `IUserEmailStore<TUser>` | Set/GetEmail, EmailConfirmed, NormalizedEmail, FindByEmailAsync |
| `IUserLockoutStore<TUser>` | LockoutEnd, LockoutEnabled, AccessFailedCount（Increment/Reset） |
| `IUserRoleStore<TUser>` | Add / Remove / GetRolesAsync / IsInRoleAsync |
| `IUserTwoFactorStore<TUser>` | Get/SetTwoFactorEnabled |
| `IRoleStore<TRole>` | Create / Update / Delete / FindByIdAsync / FindByNameAsync / Get-Set Name / NormalizedName |

### OLS.Root.Authentication (`OLS.Root.Authentication.Stores`)

| 接口 | 方法 |
|---|---|
| `IRefreshTokenStore<TUser>` | CreateAsync, FindByHashAsync, RevokeAsync(tokenId, replacedByTokenId), RevokeAllForUserAsync |

### OLS.Root.Oidc (`OLS.Root.Oidc.Stores`)

| 接口 | 方法 |
|---|---|
| `IClientStore` | FindByClientIdAsync |
| `IAuthorizationCodeStore` | StoreAsync, ConsumeAsync（一次性消费） |
| `IDeviceCodeStore` | StoreAsync / UpdateAsync / FindByDeviceCodeAsync / FindByUserCodeAsync / RemoveAsync |
| `IReferenceTokenStore` | StoreAsync, FindByHashAsync, RevokeAsync(hash), RevokeAllAsync(subject, client) |

## 模型字段（用于设计 SQLite 表）

### `IdentityUser` (Core.Models)
`Id, UserName, NormalizedUserName, Email, NormalizedEmail, EmailConfirmed, PasswordHash, SecurityStamp, ConcurrencyStamp, PhoneNumber, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnd (DateTimeOffset?), LockoutEnabled, AccessFailedCount`

### `IdentityRole`
`Id, Name, NormalizedName, ConcurrencyStamp`

### `RefreshToken` (Authentication.Models)
`Id, UserId, TokenHash, CreatedAt, ExpiresAt, IsRevoked, IsActive, ReplacedByTokenId`

### `OidcClient` (Oidc.Models)
`ClientId, ClientName, IsEnabled, RequireClientSecret, RequirePkce, SlidingRefreshTokenExpiry,
AccessTokenLifetime, AuthorizationCodeLifetime, RefreshTokenLifetime,
HashedSecrets: IList<string>, AllowedGrantTypes: IList<string>, AllowedScopes: IList<string>,
RedirectUris, PostLogoutRedirectUris, AllowedCorsOrigins`
> 列表字段隐含联表。

### `AuthorizationCode`
`Code (PK), ClientId, SubjectId, RedirectUri, CodeChallenge, CodeChallengeMethod, Nonce, State,
RequestedScopes: IReadOnlyList<string>, CreatedAt, ExpiresAt, IsExpired (computed)`

### `DeviceCode`
`Code (device_code), UserCode, ClientId, SubjectId, RequestedScopes, IsAuthorized, IsDenied,
LastPolledAt, Interval, CreatedAt, ExpiresAt, IsExpired (computed)`

### `StoredToken`（reference token）
`TokenHash (PK), SubjectId, ClientId, Scopes (string), CreatedAt, ExpiresAt, IsRevoked, IsActive`

## Options

| 类型 | 重要字段 |
|---|---|
| `IdentityOptions` | `Lockout` / `Password` / `SignIn` / `User` |
| `PasswordOptions` | `RequiredLength`, `RequiredUniqueChars`, `RequireDigit`, `RequireLowercase`, `RequireUppercase`, `RequireNonAlphanumeric` |
| `LockoutOptions` | `AllowedForNewUsers`, `DefaultLockoutTimeSpan`, `MaxFailedAccessAttempts` |
| `JwtOptions` | `Issuer`, `Audience`, `AccessTokenExpiry`, `ClockSkew`, `SigningAlgorithm`, `SigningKey: SecurityKey` |
| `RefreshTokenOptions` | `Expiry`, `RotationEnabled` |
| `OidcOptions` | `IssuerUri`, `DefaultAccessTokenLifetime`, `DeviceCodeLifetime`, `DevicePollingInterval`, `DeviceVerificationUri`, `ShowApiScopes`, `SigningCredentials`, `SupportedScopes` |

## DI 扩展方法

| 调用 | 作用 |
|---|---|
| `services.AddOlsIdentityCore(o => …)` | 返回 `IdentityBuilder`，配置 `IdentityOptions`，store 需另行注册 |
| `services.AddOlsIdentityTelemetry()` | Activity / metric source 注册 |
| `services.AddOlsAuthentication(o => …)` | JWT 校验 + SignInManager |
| `services.AddRefreshTokenStore()` | 占位标记，store 实现由我们提供 |
| `services.AddOlsAuthorisation(o => …)` | RBAC + 动态 policy provider |
| `services.AddOlsOidc(o => …)` | OIDC server pipeline（token/authorize/discovery） |
| `endpoints.MapOlsOidcEndpoints()` | 路由 `/connect/token`, `/connect/authorize`, `/.well-known/openid-configuration` 等 |
