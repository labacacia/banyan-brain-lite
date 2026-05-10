English | [中文版](./ols-surface-reference.cn.md)

# OLS.Root API Surface Reference

> Reflected from OLS.Root.* `1.0.0-alpha.1` (net10.0). Source: Nexus `nuget-hosted` on `nexus-lc01.starfield.com.au`. Used by `Banyan.Identity` to design the SQLite identity store.

## Store interfaces (we must implement these)

### From OLS.Root.Core (`OLS.Root.Core.Stores`)

| Interface | Methods (abridged) |
|---|---|
| `IUserStore<TUser>` | Create / Update / Delete / FindByIdAsync / FindByNameAsync / Get-Set UserName / NormalizedUserName |
| `IUserPasswordStore<TUser>` | Set/GetPasswordHashAsync, HasPasswordAsync |
| `IUserEmailStore<TUser>` | Set/GetEmail, EmailConfirmed, NormalizedEmail, FindByEmailAsync |
| `IUserLockoutStore<TUser>` | LockoutEnd, LockoutEnabled, AccessFailedCount (Increment/Reset) |
| `IUserRoleStore<TUser>` | Add/Remove, GetRolesAsync, IsInRoleAsync |
| `IUserTwoFactorStore<TUser>` | Get/SetTwoFactorEnabled |
| `IRoleStore<TRole>` | Create / Update / Delete / FindByIdAsync / FindByNameAsync / Get-Set Name / NormalizedName |

### From OLS.Root.Authentication (`OLS.Root.Authentication.Stores`)

| Interface | Methods |
|---|---|
| `IRefreshTokenStore<TUser>` | CreateAsync, FindByHashAsync, RevokeAsync(tokenId, replacedByTokenId), RevokeAllForUserAsync |

### From OLS.Root.Oidc (`OLS.Root.Oidc.Stores`)

| Interface | Methods |
|---|---|
| `IClientStore` | FindByClientIdAsync |
| `IAuthorizationCodeStore` | StoreAsync, ConsumeAsync (single-use) |
| `IDeviceCodeStore` | StoreAsync / UpdateAsync / FindByDeviceCodeAsync / FindByUserCodeAsync / RemoveAsync |
| `IReferenceTokenStore` | StoreAsync, FindByHashAsync, RevokeAsync(hash), RevokeAllAsync(subject, client) |

## Model fields (input for SQLite schema)

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
> The list-typed fields imply join tables.

### `AuthorizationCode`
`Code (PK), ClientId, SubjectId, RedirectUri, CodeChallenge, CodeChallengeMethod, Nonce, State,
RequestedScopes: IReadOnlyList<string>, CreatedAt, ExpiresAt, IsExpired (computed)`

### `DeviceCode`
`Code (device_code), UserCode, ClientId, SubjectId, RequestedScopes, IsAuthorized, IsDenied,
LastPolledAt, Interval, CreatedAt, ExpiresAt, IsExpired (computed)`

### `StoredToken` (reference token)
`TokenHash (PK), SubjectId, ClientId, Scopes (string), CreatedAt, ExpiresAt, IsRevoked, IsActive`

## Options

| Type | Notable fields |
|---|---|
| `IdentityOptions` | `Lockout`, `Password`, `SignIn`, `User` |
| `PasswordOptions` | `RequiredLength`, `RequiredUniqueChars`, `RequireDigit`, `RequireLowercase`, `RequireUppercase`, `RequireNonAlphanumeric` |
| `LockoutOptions` | `AllowedForNewUsers`, `DefaultLockoutTimeSpan`, `MaxFailedAccessAttempts` |
| `JwtOptions` | `Issuer`, `Audience`, `AccessTokenExpiry`, `ClockSkew`, `SigningAlgorithm`, `SigningKey: SecurityKey` |
| `RefreshTokenOptions` | `Expiry`, `RotationEnabled` |
| `OidcOptions` | `IssuerUri`, `DefaultAccessTokenLifetime`, `DeviceCodeLifetime`, `DevicePollingInterval`, `DeviceVerificationUri`, `ShowApiScopes`, `SigningCredentials`, `SupportedScopes` |

## DI extension methods

| Call | Effect |
|---|---|
| `services.AddOlsIdentityCore(o => …)` | Returns `IdentityBuilder`; configures `IdentityOptions`. Stores must be registered separately. |
| `services.AddOlsIdentityTelemetry()` | Activity / metric source registration |
| `services.AddOlsAuthentication(o => …)` | JWT validation + sign-in manager |
| `services.AddRefreshTokenStore()` | Marker — actual store impl provided by us |
| `services.AddOlsAuthorisation(o => …)` | RBAC + dynamic policy provider |
| `services.AddOlsOidc(o => …)` | OIDC server pipeline (token/authorize/discovery) |
| `endpoints.MapOlsOidcEndpoints()` | Routes `/connect/token`, `/connect/authorize`, `/.well-known/openid-configuration`, etc. |
