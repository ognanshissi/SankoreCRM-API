using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SnakeCaseNaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_agencies_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropForeignKey(
                name: "FK_agencies_territories_TerritoryId",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropForeignKey(
                name: "FK_app_users_agencies_AgencyId",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetRoleClaims_app_roles_RoleId",
                schema: "administration",
                table: "AspNetRoleClaims");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserClaims_app_users_UserId",
                schema: "administration",
                table: "AspNetUserClaims");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserLogins_app_users_UserId",
                schema: "administration",
                table: "AspNetUserLogins");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserRoles_app_roles_RoleId",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserRoles_app_users_UserId",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUserTokens_app_users_UserId",
                schema: "administration",
                table: "AspNetUserTokens");

            migrationBuilder.DropForeignKey(
                name: "FK_password_histories_app_users_UserId",
                schema: "administration",
                table: "password_histories");

            migrationBuilder.DropForeignKey(
                name: "FK_permission_attributions_app_users_UserId",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropForeignKey(
                name: "FK_role_permissions_app_roles_RoleId",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_role_permissions_permissions_PermissionId",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropForeignKey(
                name: "FK_user_login_locations_app_users_UserId",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropForeignKey(
                name: "FK_user_profile_app_users_UserId",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_app_roles_RoleId",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropForeignKey(
                name: "FK_user_roles_app_users_UserId",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_roles",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_profile",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_login_locations",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_territories",
                schema: "administration",
                table: "territories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_tenant_notification_settings",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_role_permissions",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_permissions",
                schema: "administration",
                table: "permissions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_permission_attributions",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_password_histories",
                schema: "administration",
                table: "password_histories");

            migrationBuilder.DropPrimaryKey(
                name: "PK_outbox_messages",
                schema: "administration",
                table: "outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetUserTokens",
                schema: "administration",
                table: "AspNetUserTokens");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetUserRoles",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetUserLogins",
                schema: "administration",
                table: "AspNetUserLogins");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetUserClaims",
                schema: "administration",
                table: "AspNetUserClaims");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AspNetRoleClaims",
                schema: "administration",
                table: "AspNetRoleClaims");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_users",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_app_roles",
                schema: "administration",
                table: "app_roles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_agencies",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ProductSpecialities",
                schema: "administration",
                table: "ProductSpecialities");

            migrationBuilder.RenameTable(
                name: "ProductSpecialities",
                schema: "administration",
                newName: "product_specialities",
                newSchema: "administration");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "user_roles",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "user_roles",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "user_roles",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "RoleId",
                schema: "administration",
                table: "user_roles",
                newName: "role_id");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "administration",
                table: "user_roles",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "AssignedBy",
                schema: "administration",
                table: "user_roles",
                newName: "assigned_by");

            migrationBuilder.RenameColumn(
                name: "AssignedAt",
                schema: "administration",
                table: "user_roles",
                newName: "assigned_at");

            migrationBuilder.RenameIndex(
                name: "IX_user_roles_UserId_RoleId",
                schema: "administration",
                table: "user_roles",
                newName: "ix_user_roles_user_id_role_id");

            migrationBuilder.RenameIndex(
                name: "IX_user_roles_RoleId",
                schema: "administration",
                table: "user_roles",
                newName: "ix_user_roles_role_id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "user_profile",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "user_profile",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "JobTitle",
                schema: "administration",
                table: "user_profile",
                newName: "job_title");

            migrationBuilder.RenameColumn(
                name: "DefaultLanguage",
                schema: "administration",
                table: "user_profile",
                newName: "default_language");

            migrationBuilder.RenameColumn(
                name: "BirthDate",
                schema: "administration",
                table: "user_profile",
                newName: "birth_date");

            migrationBuilder.RenameColumn(
                name: "AdditionalEmail",
                schema: "administration",
                table: "user_profile",
                newName: "additional_email1");

            migrationBuilder.RenameColumn(
                name: "WorkNumber_IsPrimary",
                schema: "administration",
                table: "user_profile",
                newName: "work_number_is_primary");

            migrationBuilder.RenameColumn(
                name: "PersonalNumber_IsPrimary",
                schema: "administration",
                table: "user_profile",
                newName: "personal_number_is_primary");

            migrationBuilder.RenameColumn(
                name: "HomeNumber_IsPrimary",
                schema: "administration",
                table: "user_profile",
                newName: "home_number_is_primary");

            migrationBuilder.RenameIndex(
                name: "IX_user_profile_UserId",
                schema: "administration",
                table: "user_profile",
                newName: "ix_user_profile_user_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "user_login_locations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "user_login_locations",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "user_login_locations",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "OccuredAt",
                schema: "administration",
                table: "user_login_locations",
                newName: "occured_at");

            migrationBuilder.RenameIndex(
                name: "IX_user_login_locations_UserId",
                schema: "administration",
                table: "user_login_locations",
                newName: "ix_user_login_locations_user_id");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "administration",
                table: "territories",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Description",
                schema: "administration",
                table: "territories",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Code",
                schema: "administration",
                table: "territories",
                newName: "code");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "territories",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "administration",
                table: "territories",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "territories",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "RayonKm",
                schema: "administration",
                table: "territories",
                newName: "rayon_km");

            migrationBuilder.RenameColumn(
                name: "ProductSpecialities",
                schema: "administration",
                table: "territories",
                newName: "product_specialities");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "administration",
                table: "territories",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "administration",
                table: "territories",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_territories_TenantId_IsActive",
                schema: "administration",
                table: "territories",
                newName: "ix_territories_tenant_id_is_active");

            migrationBuilder.RenameIndex(
                name: "IX_territories_TenantId_Code",
                schema: "administration",
                table: "territories",
                newName: "ix_territories_tenant_id_code");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UseDefaultPlatformProvider",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "use_default_platform_provider");

            migrationBuilder.RenameColumn(
                name: "UpdatedBy",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "updated_by");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "SendingDomain",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "sending_domain");

            migrationBuilder.RenameColumn(
                name: "ReplyToEmail",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "reply_to_email");

            migrationBuilder.RenameColumn(
                name: "ProviderType",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "provider_type");

            migrationBuilder.RenameColumn(
                name: "MonthlyQuotaLimit",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "monthly_quota_limit");

            migrationBuilder.RenameColumn(
                name: "FromName",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "from_name");

            migrationBuilder.RenameColumn(
                name: "FromEmail",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "from_email");

            migrationBuilder.RenameColumn(
                name: "CurrentMonthUsageCount",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "current_month_usage_count");

            migrationBuilder.RenameColumn(
                name: "CurrentMonthStartedAt",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "current_month_started_at");

            migrationBuilder.RenameColumn(
                name: "CredentialVaultPath",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "credential_vault_path");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "created_at");

            migrationBuilder.RenameIndex(
                name: "IX_tenant_notification_settings_TenantId",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "ix_tenant_notification_settings_tenant_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "role_permissions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RoleId",
                schema: "administration",
                table: "role_permissions",
                newName: "role_id");

            migrationBuilder.RenameColumn(
                name: "PermissionId",
                schema: "administration",
                table: "role_permissions",
                newName: "permission_id");

            migrationBuilder.RenameColumn(
                name: "GrantedAt",
                schema: "administration",
                table: "role_permissions",
                newName: "granted_at");

            migrationBuilder.RenameIndex(
                name: "IX_role_permissions_RoleId_PermissionId",
                schema: "administration",
                table: "role_permissions",
                newName: "ix_role_permissions_role_id_permission_id");

            migrationBuilder.RenameIndex(
                name: "IX_role_permissions_PermissionId",
                schema: "administration",
                table: "role_permissions",
                newName: "ix_role_permissions_permission_id");

            migrationBuilder.RenameColumn(
                name: "Module",
                schema: "administration",
                table: "permissions",
                newName: "module");

            migrationBuilder.RenameColumn(
                name: "Description",
                schema: "administration",
                table: "permissions",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Code",
                schema: "administration",
                table: "permissions",
                newName: "code");

            migrationBuilder.RenameColumn(
                name: "Action",
                schema: "administration",
                table: "permissions",
                newName: "action");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "permissions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "permission_attributions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "permission_attributions",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "administration",
                table: "permission_attributions",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "permission_attributions",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "StartDate",
                schema: "administration",
                table: "permission_attributions",
                newName: "start_date");

            migrationBuilder.RenameColumn(
                name: "ScopeType",
                schema: "administration",
                table: "permission_attributions",
                newName: "scope_type");

            migrationBuilder.RenameColumn(
                name: "ScopeId",
                schema: "administration",
                table: "permission_attributions",
                newName: "scope_id");

            migrationBuilder.RenameColumn(
                name: "PermissionCode",
                schema: "administration",
                table: "permission_attributions",
                newName: "permission_code");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "administration",
                table: "permission_attributions",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "EndDate",
                schema: "administration",
                table: "permission_attributions",
                newName: "end_date");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "administration",
                table: "permission_attributions",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AssignedByUserId",
                schema: "administration",
                table: "permission_attributions",
                newName: "assigned_by_user_id");

            migrationBuilder.RenameIndex(
                name: "IX_permission_attributions_UserId_PermissionCode_ScopeId",
                schema: "administration",
                table: "permission_attributions",
                newName: "ix_permission_attributions_user_id_permission_code_scope_id");

            migrationBuilder.RenameIndex(
                name: "IX_permission_attributions_TenantId_UserId",
                schema: "administration",
                table: "permission_attributions",
                newName: "ix_permission_attributions_tenant_id_user_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "password_histories",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "password_histories",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "password_histories",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "SetAt",
                schema: "administration",
                table: "password_histories",
                newName: "set_at");

            migrationBuilder.RenameColumn(
                name: "PasswordSalt",
                schema: "administration",
                table: "password_histories",
                newName: "password_salt");

            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                schema: "administration",
                table: "password_histories",
                newName: "password_hash");

            migrationBuilder.RenameIndex(
                name: "IX_password_histories_UserId_SetAt",
                schema: "administration",
                table: "password_histories",
                newName: "ix_password_histories_user_id_set_at");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "outbox_messages",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RetryCount",
                schema: "administration",
                table: "outbox_messages",
                newName: "retry_count");

            migrationBuilder.RenameColumn(
                name: "ProcessedAt",
                schema: "administration",
                table: "outbox_messages",
                newName: "processed_at");

            migrationBuilder.RenameColumn(
                name: "PayloadJson",
                schema: "administration",
                table: "outbox_messages",
                newName: "payload_json");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                schema: "administration",
                table: "outbox_messages",
                newName: "occurred_at");

            migrationBuilder.RenameColumn(
                name: "LastError",
                schema: "administration",
                table: "outbox_messages",
                newName: "last_error");

            migrationBuilder.RenameColumn(
                name: "EventType",
                schema: "administration",
                table: "outbox_messages",
                newName: "event_type");

            migrationBuilder.RenameIndex(
                name: "IX_outbox_messages_ProcessedAt_OccurredAt",
                schema: "administration",
                table: "outbox_messages",
                newName: "ix_outbox_messages_processed_at_occurred_at");

            migrationBuilder.RenameColumn(
                name: "Value",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "value");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "LoginProvider",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "login_provider");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "RoleId",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "role_id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "user_id");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUserRoles_RoleId",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "ix_asp_net_user_roles_role_id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "ProviderDisplayName",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "provider_display_name");

            migrationBuilder.RenameColumn(
                name: "ProviderKey",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "provider_key");

            migrationBuilder.RenameColumn(
                name: "LoginProvider",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "login_provider");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUserLogins_UserId",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "ix_asp_net_user_logins_user_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserId",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "ClaimValue",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "claim_value");

            migrationBuilder.RenameColumn(
                name: "ClaimType",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "claim_type");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetUserClaims_UserId",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "ix_asp_net_user_claims_user_id");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "RoleId",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "role_id");

            migrationBuilder.RenameColumn(
                name: "ClaimValue",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "claim_value");

            migrationBuilder.RenameColumn(
                name: "ClaimType",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "claim_type");

            migrationBuilder.RenameIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "ix_asp_net_role_claims_role_id");

            migrationBuilder.RenameColumn(
                name: "Status",
                schema: "administration",
                table: "app_users",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Specialties",
                schema: "administration",
                table: "app_users",
                newName: "specialties");

            migrationBuilder.RenameColumn(
                name: "Email",
                schema: "administration",
                table: "app_users",
                newName: "email");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "app_users",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UserName",
                schema: "administration",
                table: "app_users",
                newName: "user_name");

            migrationBuilder.RenameColumn(
                name: "TwoFactorEnabled",
                schema: "administration",
                table: "app_users",
                newName: "two_factor_enabled");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "app_users",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "SpokenLanguages",
                schema: "administration",
                table: "app_users",
                newName: "spoken_languages");

            migrationBuilder.RenameColumn(
                name: "SecurityStamp",
                schema: "administration",
                table: "app_users",
                newName: "security_stamp");

            migrationBuilder.RenameColumn(
                name: "ReportToId",
                schema: "administration",
                table: "app_users",
                newName: "report_to_id");

            migrationBuilder.RenameColumn(
                name: "PhoneNumberConfirmed",
                schema: "administration",
                table: "app_users",
                newName: "phone_number_confirmed");

            migrationBuilder.RenameColumn(
                name: "PhoneNumber",
                schema: "administration",
                table: "app_users",
                newName: "phone_number");

            migrationBuilder.RenameColumn(
                name: "PasswordHash",
                schema: "administration",
                table: "app_users",
                newName: "password_hash");

            migrationBuilder.RenameColumn(
                name: "PasswordExpiresAt",
                schema: "administration",
                table: "app_users",
                newName: "password_expires_at");

            migrationBuilder.RenameColumn(
                name: "NormalizedUserName",
                schema: "administration",
                table: "app_users",
                newName: "normalized_user_name");

            migrationBuilder.RenameColumn(
                name: "NormalizedEmail",
                schema: "administration",
                table: "app_users",
                newName: "normalized_email");

            migrationBuilder.RenameColumn(
                name: "MfaEnabled",
                schema: "administration",
                table: "app_users",
                newName: "mfa_enabled");

            migrationBuilder.RenameColumn(
                name: "LockoutEnd",
                schema: "administration",
                table: "app_users",
                newName: "lockout_end");

            migrationBuilder.RenameColumn(
                name: "LockoutEnabled",
                schema: "administration",
                table: "app_users",
                newName: "lockout_enabled");

            migrationBuilder.RenameColumn(
                name: "LastLoginAt",
                schema: "administration",
                table: "app_users",
                newName: "last_login_at");

            migrationBuilder.RenameColumn(
                name: "IsSuperUser",
                schema: "administration",
                table: "app_users",
                newName: "is_super_user");

            migrationBuilder.RenameColumn(
                name: "IsAvailable",
                schema: "administration",
                table: "app_users",
                newName: "is_available");

            migrationBuilder.RenameColumn(
                name: "HotLeadsCount",
                schema: "administration",
                table: "app_users",
                newName: "hot_leads_count");

            migrationBuilder.RenameColumn(
                name: "FullName",
                schema: "administration",
                table: "app_users",
                newName: "full_name");

            migrationBuilder.RenameColumn(
                name: "FailedLoginAttempts",
                schema: "administration",
                table: "app_users",
                newName: "failed_login_attempts");

            migrationBuilder.RenameColumn(
                name: "EnableNotifications",
                schema: "administration",
                table: "app_users",
                newName: "enable_notifications");

            migrationBuilder.RenameColumn(
                name: "EmailConfirmed",
                schema: "administration",
                table: "app_users",
                newName: "email_confirmed");

            migrationBuilder.RenameColumn(
                name: "DeactivatedAt",
                schema: "administration",
                table: "app_users",
                newName: "deactivated_at");

            migrationBuilder.RenameColumn(
                name: "ConversionRate30D",
                schema: "administration",
                table: "app_users",
                newName: "conversion_rate30d");

            migrationBuilder.RenameColumn(
                name: "ConcurrencyStamp",
                schema: "administration",
                table: "app_users",
                newName: "concurrency_stamp");

            migrationBuilder.RenameColumn(
                name: "AgencyId",
                schema: "administration",
                table: "app_users",
                newName: "agency_id");

            migrationBuilder.RenameColumn(
                name: "ActiveLeadsCount",
                schema: "administration",
                table: "app_users",
                newName: "active_leads_count");

            migrationBuilder.RenameColumn(
                name: "AccountType",
                schema: "administration",
                table: "app_users",
                newName: "account_type");

            migrationBuilder.RenameColumn(
                name: "AccessFailedCount",
                schema: "administration",
                table: "app_users",
                newName: "access_failed_count");

            migrationBuilder.RenameIndex(
                name: "IX_app_users_TenantId_NormalizedEmail",
                schema: "administration",
                table: "app_users",
                newName: "ix_app_users_tenant_id_normalized_email");

            migrationBuilder.RenameIndex(
                name: "IX_app_users_TenantId_AgencyId",
                schema: "administration",
                table: "app_users",
                newName: "ix_app_users_tenant_id_agency_id");

            migrationBuilder.RenameIndex(
                name: "IX_app_users_AgencyId",
                schema: "administration",
                table: "app_users",
                newName: "ix_app_users_agency_id");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "administration",
                table: "app_roles",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Label",
                schema: "administration",
                table: "app_roles",
                newName: "label");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "app_roles",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "app_roles",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "NormalizedName",
                schema: "administration",
                table: "app_roles",
                newName: "normalized_name");

            migrationBuilder.RenameColumn(
                name: "IsSystem",
                schema: "administration",
                table: "app_roles",
                newName: "is_system");

            migrationBuilder.RenameColumn(
                name: "IsAssignable",
                schema: "administration",
                table: "app_roles",
                newName: "is_assignable");

            migrationBuilder.RenameColumn(
                name: "ConcurrencyStamp",
                schema: "administration",
                table: "app_roles",
                newName: "concurrency_stamp");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "administration",
                table: "agencies",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Description",
                schema: "administration",
                table: "agencies",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Code",
                schema: "administration",
                table: "agencies",
                newName: "code");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "agencies",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                schema: "administration",
                table: "agencies",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "TerritoryId",
                schema: "administration",
                table: "agencies",
                newName: "territory_id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "agencies",
                newName: "tenant_id");

            migrationBuilder.RenameColumn(
                name: "ParentAgencyId",
                schema: "administration",
                table: "agencies",
                newName: "parent_agency_id");

            migrationBuilder.RenameColumn(
                name: "IsDeleted",
                schema: "administration",
                table: "agencies",
                newName: "is_deleted");

            migrationBuilder.RenameColumn(
                name: "IsActive",
                schema: "administration",
                table: "agencies",
                newName: "is_active");

            migrationBuilder.RenameColumn(
                name: "CreatedBy",
                schema: "administration",
                table: "agencies",
                newName: "created_by");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                schema: "administration",
                table: "agencies",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AgencyType",
                schema: "administration",
                table: "agencies",
                newName: "agency_type");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TerritoryId",
                schema: "administration",
                table: "agencies",
                newName: "ix_agencies_territory_id");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TenantId_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                newName: "ix_agencies_tenant_id_parent_agency_id");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TenantId_IsDeleted",
                schema: "administration",
                table: "agencies",
                newName: "ix_agencies_tenant_id_is_deleted");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_TenantId_Code",
                schema: "administration",
                table: "agencies",
                newName: "ix_agencies_tenant_id_code");

            migrationBuilder.RenameIndex(
                name: "IX_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                newName: "ix_agencies_parent_agency_id");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "administration",
                table: "product_specialities",
                newName: "name");

            migrationBuilder.RenameColumn(
                name: "Code",
                schema: "administration",
                table: "product_specialities",
                newName: "code");

            migrationBuilder.RenameColumn(
                name: "Id",
                schema: "administration",
                table: "product_specialities",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "TenantId",
                schema: "administration",
                table: "product_specialities",
                newName: "tenant_id");

            migrationBuilder.RenameIndex(
                name: "IX_ProductSpecialities_TenantId_Code",
                schema: "administration",
                table: "product_specialities",
                newName: "ix_product_specialities_tenant_id_code");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_roles",
                schema: "administration",
                table: "user_roles",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_profile",
                schema: "administration",
                table: "user_profile",
                column: "additional_email");

            migrationBuilder.AddPrimaryKey(
                name: "pk_user_login_locations",
                schema: "administration",
                table: "user_login_locations",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_territories",
                schema: "administration",
                table: "territories",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_tenant_notification_settings",
                schema: "administration",
                table: "tenant_notification_settings",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_role_permissions",
                schema: "administration",
                table: "role_permissions",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_permissions",
                schema: "administration",
                table: "permissions",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_permission_attributions",
                schema: "administration",
                table: "permission_attributions",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_password_histories",
                schema: "administration",
                table: "password_histories",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_outbox_messages",
                schema: "administration",
                table: "outbox_messages",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_asp_net_user_tokens",
                schema: "administration",
                table: "AspNetUserTokens",
                columns: new[] { "user_id", "login_provider", "name" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_asp_net_user_roles",
                schema: "administration",
                table: "AspNetUserRoles",
                columns: new[] { "user_id", "role_id" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_asp_net_user_logins",
                schema: "administration",
                table: "AspNetUserLogins",
                columns: new[] { "login_provider", "provider_key" });

            migrationBuilder.AddPrimaryKey(
                name: "pk_asp_net_user_claims",
                schema: "administration",
                table: "AspNetUserClaims",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_asp_net_role_claims",
                schema: "administration",
                table: "AspNetRoleClaims",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_app_users",
                schema: "administration",
                table: "app_users",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_app_roles",
                schema: "administration",
                table: "app_roles",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_agencies",
                schema: "administration",
                table: "agencies",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_product_specialities",
                schema: "administration",
                table: "product_specialities",
                column: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_agencies_agencies_parent_agency_id",
                schema: "administration",
                table: "agencies",
                column: "parent_agency_id",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_agencies_territories_territory_id",
                schema: "administration",
                table: "agencies",
                column: "territory_id",
                principalSchema: "administration",
                principalTable: "territories",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "fk_app_users_agencies_agency_id",
                schema: "administration",
                table: "app_users",
                column: "agency_id",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                schema: "administration",
                table: "AspNetRoleClaims",
                column: "role_id",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_user_claims_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserClaims",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_user_logins_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserLogins",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                schema: "administration",
                table: "AspNetUserRoles",
                column: "role_id",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_user_roles_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserRoles",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserTokens",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_password_histories_app_users_user_id",
                schema: "administration",
                table: "password_histories",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_permission_attributions_users_user_id",
                schema: "administration",
                table: "permission_attributions",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_role_permissions_permissions_permission_id",
                schema: "administration",
                table: "role_permissions",
                column: "permission_id",
                principalSchema: "administration",
                principalTable: "permissions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_role_permissions_roles_role_id",
                schema: "administration",
                table: "role_permissions",
                column: "role_id",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_login_locations_app_users_user_id",
                schema: "administration",
                table: "user_login_locations",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_profile_app_users_user_id",
                schema: "administration",
                table: "user_profile",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_user_roles_roles_role_id",
                schema: "administration",
                table: "user_roles",
                column: "role_id",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_user_roles_users_user_id",
                schema: "administration",
                table: "user_roles",
                column: "user_id",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agencies_agencies_parent_agency_id",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropForeignKey(
                name: "fk_agencies_territories_territory_id",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropForeignKey(
                name: "fk_app_users_agencies_agency_id",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_role_claims_asp_net_roles_role_id",
                schema: "administration",
                table: "AspNetRoleClaims");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_user_claims_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserClaims");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_user_logins_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserLogins");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_user_roles_asp_net_roles_role_id",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_user_roles_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropForeignKey(
                name: "fk_asp_net_user_tokens_asp_net_users_user_id",
                schema: "administration",
                table: "AspNetUserTokens");

            migrationBuilder.DropForeignKey(
                name: "fk_password_histories_app_users_user_id",
                schema: "administration",
                table: "password_histories");

            migrationBuilder.DropForeignKey(
                name: "fk_permission_attributions_users_user_id",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropForeignKey(
                name: "fk_role_permissions_permissions_permission_id",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_role_permissions_roles_role_id",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropForeignKey(
                name: "fk_user_login_locations_app_users_user_id",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropForeignKey(
                name: "fk_user_profile_app_users_user_id",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropForeignKey(
                name: "fk_user_roles_roles_role_id",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropForeignKey(
                name: "fk_user_roles_users_user_id",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_roles",
                schema: "administration",
                table: "user_roles");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_profile",
                schema: "administration",
                table: "user_profile");

            migrationBuilder.DropPrimaryKey(
                name: "pk_user_login_locations",
                schema: "administration",
                table: "user_login_locations");

            migrationBuilder.DropPrimaryKey(
                name: "pk_territories",
                schema: "administration",
                table: "territories");

            migrationBuilder.DropPrimaryKey(
                name: "pk_tenant_notification_settings",
                schema: "administration",
                table: "tenant_notification_settings");

            migrationBuilder.DropPrimaryKey(
                name: "pk_role_permissions",
                schema: "administration",
                table: "role_permissions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_permissions",
                schema: "administration",
                table: "permissions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_permission_attributions",
                schema: "administration",
                table: "permission_attributions");

            migrationBuilder.DropPrimaryKey(
                name: "pk_password_histories",
                schema: "administration",
                table: "password_histories");

            migrationBuilder.DropPrimaryKey(
                name: "pk_outbox_messages",
                schema: "administration",
                table: "outbox_messages");

            migrationBuilder.DropPrimaryKey(
                name: "pk_asp_net_user_tokens",
                schema: "administration",
                table: "AspNetUserTokens");

            migrationBuilder.DropPrimaryKey(
                name: "pk_asp_net_user_roles",
                schema: "administration",
                table: "AspNetUserRoles");

            migrationBuilder.DropPrimaryKey(
                name: "pk_asp_net_user_logins",
                schema: "administration",
                table: "AspNetUserLogins");

            migrationBuilder.DropPrimaryKey(
                name: "pk_asp_net_user_claims",
                schema: "administration",
                table: "AspNetUserClaims");

            migrationBuilder.DropPrimaryKey(
                name: "pk_asp_net_role_claims",
                schema: "administration",
                table: "AspNetRoleClaims");

            migrationBuilder.DropPrimaryKey(
                name: "pk_app_users",
                schema: "administration",
                table: "app_users");

            migrationBuilder.DropPrimaryKey(
                name: "pk_app_roles",
                schema: "administration",
                table: "app_roles");

            migrationBuilder.DropPrimaryKey(
                name: "pk_agencies",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropPrimaryKey(
                name: "pk_product_specialities",
                schema: "administration",
                table: "product_specialities");

            migrationBuilder.RenameTable(
                name: "product_specialities",
                schema: "administration",
                newName: "ProductSpecialities",
                newSchema: "administration");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "user_roles",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "user_roles",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "user_roles",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "role_id",
                schema: "administration",
                table: "user_roles",
                newName: "RoleId");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "administration",
                table: "user_roles",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "assigned_by",
                schema: "administration",
                table: "user_roles",
                newName: "AssignedBy");

            migrationBuilder.RenameColumn(
                name: "assigned_at",
                schema: "administration",
                table: "user_roles",
                newName: "AssignedAt");

            migrationBuilder.RenameIndex(
                name: "ix_user_roles_user_id_role_id",
                schema: "administration",
                table: "user_roles",
                newName: "IX_user_roles_UserId_RoleId");

            migrationBuilder.RenameIndex(
                name: "ix_user_roles_role_id",
                schema: "administration",
                table: "user_roles",
                newName: "IX_user_roles_RoleId");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "user_profile",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "user_profile",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "job_title",
                schema: "administration",
                table: "user_profile",
                newName: "JobTitle");

            migrationBuilder.RenameColumn(
                name: "default_language",
                schema: "administration",
                table: "user_profile",
                newName: "DefaultLanguage");

            migrationBuilder.RenameColumn(
                name: "birth_date",
                schema: "administration",
                table: "user_profile",
                newName: "BirthDate");

            migrationBuilder.RenameColumn(
                name: "additional_email1",
                schema: "administration",
                table: "user_profile",
                newName: "AdditionalEmail");

            migrationBuilder.RenameColumn(
                name: "work_number_is_primary",
                schema: "administration",
                table: "user_profile",
                newName: "WorkNumber_IsPrimary");

            migrationBuilder.RenameColumn(
                name: "personal_number_is_primary",
                schema: "administration",
                table: "user_profile",
                newName: "PersonalNumber_IsPrimary");

            migrationBuilder.RenameColumn(
                name: "home_number_is_primary",
                schema: "administration",
                table: "user_profile",
                newName: "HomeNumber_IsPrimary");

            migrationBuilder.RenameIndex(
                name: "ix_user_profile_user_id",
                schema: "administration",
                table: "user_profile",
                newName: "IX_user_profile_UserId");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "user_login_locations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "user_login_locations",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "user_login_locations",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "occured_at",
                schema: "administration",
                table: "user_login_locations",
                newName: "OccuredAt");

            migrationBuilder.RenameIndex(
                name: "ix_user_login_locations_user_id",
                schema: "administration",
                table: "user_login_locations",
                newName: "IX_user_login_locations_UserId");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "administration",
                table: "territories",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "description",
                schema: "administration",
                table: "territories",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "code",
                schema: "administration",
                table: "territories",
                newName: "Code");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "territories",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "administration",
                table: "territories",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "territories",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "rayon_km",
                schema: "administration",
                table: "territories",
                newName: "RayonKm");

            migrationBuilder.RenameColumn(
                name: "product_specialities",
                schema: "administration",
                table: "territories",
                newName: "ProductSpecialities");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "administration",
                table: "territories",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "administration",
                table: "territories",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_territories_tenant_id_is_active",
                schema: "administration",
                table: "territories",
                newName: "IX_territories_TenantId_IsActive");

            migrationBuilder.RenameIndex(
                name: "ix_territories_tenant_id_code",
                schema: "administration",
                table: "territories",
                newName: "IX_territories_TenantId_Code");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "use_default_platform_provider",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "UseDefaultPlatformProvider");

            migrationBuilder.RenameColumn(
                name: "updated_by",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "UpdatedBy");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "sending_domain",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "SendingDomain");

            migrationBuilder.RenameColumn(
                name: "reply_to_email",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "ReplyToEmail");

            migrationBuilder.RenameColumn(
                name: "provider_type",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "ProviderType");

            migrationBuilder.RenameColumn(
                name: "monthly_quota_limit",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "MonthlyQuotaLimit");

            migrationBuilder.RenameColumn(
                name: "from_name",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "FromName");

            migrationBuilder.RenameColumn(
                name: "from_email",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "FromEmail");

            migrationBuilder.RenameColumn(
                name: "current_month_usage_count",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "CurrentMonthUsageCount");

            migrationBuilder.RenameColumn(
                name: "current_month_started_at",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "CurrentMonthStartedAt");

            migrationBuilder.RenameColumn(
                name: "credential_vault_path",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "CredentialVaultPath");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "ix_tenant_notification_settings_tenant_id",
                schema: "administration",
                table: "tenant_notification_settings",
                newName: "IX_tenant_notification_settings_TenantId");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "role_permissions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "role_id",
                schema: "administration",
                table: "role_permissions",
                newName: "RoleId");

            migrationBuilder.RenameColumn(
                name: "permission_id",
                schema: "administration",
                table: "role_permissions",
                newName: "PermissionId");

            migrationBuilder.RenameColumn(
                name: "granted_at",
                schema: "administration",
                table: "role_permissions",
                newName: "GrantedAt");

            migrationBuilder.RenameIndex(
                name: "ix_role_permissions_role_id_permission_id",
                schema: "administration",
                table: "role_permissions",
                newName: "IX_role_permissions_RoleId_PermissionId");

            migrationBuilder.RenameIndex(
                name: "ix_role_permissions_permission_id",
                schema: "administration",
                table: "role_permissions",
                newName: "IX_role_permissions_PermissionId");

            migrationBuilder.RenameColumn(
                name: "module",
                schema: "administration",
                table: "permissions",
                newName: "Module");

            migrationBuilder.RenameColumn(
                name: "description",
                schema: "administration",
                table: "permissions",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "code",
                schema: "administration",
                table: "permissions",
                newName: "Code");

            migrationBuilder.RenameColumn(
                name: "action",
                schema: "administration",
                table: "permissions",
                newName: "Action");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "permissions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "permission_attributions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "administration",
                table: "permission_attributions",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "start_date",
                schema: "administration",
                table: "permission_attributions",
                newName: "StartDate");

            migrationBuilder.RenameColumn(
                name: "scope_type",
                schema: "administration",
                table: "permission_attributions",
                newName: "ScopeType");

            migrationBuilder.RenameColumn(
                name: "scope_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "ScopeId");

            migrationBuilder.RenameColumn(
                name: "permission_code",
                schema: "administration",
                table: "permission_attributions",
                newName: "PermissionCode");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "administration",
                table: "permission_attributions",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "end_date",
                schema: "administration",
                table: "permission_attributions",
                newName: "EndDate");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "administration",
                table: "permission_attributions",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "assigned_by_user_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "AssignedByUserId");

            migrationBuilder.RenameIndex(
                name: "ix_permission_attributions_user_id_permission_code_scope_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "IX_permission_attributions_UserId_PermissionCode_ScopeId");

            migrationBuilder.RenameIndex(
                name: "ix_permission_attributions_tenant_id_user_id",
                schema: "administration",
                table: "permission_attributions",
                newName: "IX_permission_attributions_TenantId_UserId");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "password_histories",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "password_histories",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "password_histories",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "set_at",
                schema: "administration",
                table: "password_histories",
                newName: "SetAt");

            migrationBuilder.RenameColumn(
                name: "password_salt",
                schema: "administration",
                table: "password_histories",
                newName: "PasswordSalt");

            migrationBuilder.RenameColumn(
                name: "password_hash",
                schema: "administration",
                table: "password_histories",
                newName: "PasswordHash");

            migrationBuilder.RenameIndex(
                name: "ix_password_histories_user_id_set_at",
                schema: "administration",
                table: "password_histories",
                newName: "IX_password_histories_UserId_SetAt");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "outbox_messages",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "retry_count",
                schema: "administration",
                table: "outbox_messages",
                newName: "RetryCount");

            migrationBuilder.RenameColumn(
                name: "processed_at",
                schema: "administration",
                table: "outbox_messages",
                newName: "ProcessedAt");

            migrationBuilder.RenameColumn(
                name: "payload_json",
                schema: "administration",
                table: "outbox_messages",
                newName: "PayloadJson");

            migrationBuilder.RenameColumn(
                name: "occurred_at",
                schema: "administration",
                table: "outbox_messages",
                newName: "OccurredAt");

            migrationBuilder.RenameColumn(
                name: "last_error",
                schema: "administration",
                table: "outbox_messages",
                newName: "LastError");

            migrationBuilder.RenameColumn(
                name: "event_type",
                schema: "administration",
                table: "outbox_messages",
                newName: "EventType");

            migrationBuilder.RenameIndex(
                name: "ix_outbox_messages_processed_at_occurred_at",
                schema: "administration",
                table: "outbox_messages",
                newName: "IX_outbox_messages_ProcessedAt_OccurredAt");

            migrationBuilder.RenameColumn(
                name: "value",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "Value");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "login_provider",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "LoginProvider");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "AspNetUserTokens",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "role_id",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "RoleId");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "ix_asp_net_user_roles_role_id",
                schema: "administration",
                table: "AspNetUserRoles",
                newName: "IX_AspNetUserRoles_RoleId");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "provider_display_name",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "ProviderDisplayName");

            migrationBuilder.RenameColumn(
                name: "provider_key",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "ProviderKey");

            migrationBuilder.RenameColumn(
                name: "login_provider",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "LoginProvider");

            migrationBuilder.RenameIndex(
                name: "ix_asp_net_user_logins_user_id",
                schema: "administration",
                table: "AspNetUserLogins",
                newName: "IX_AspNetUserLogins_UserId");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "UserId");

            migrationBuilder.RenameColumn(
                name: "claim_value",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "ClaimValue");

            migrationBuilder.RenameColumn(
                name: "claim_type",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "ClaimType");

            migrationBuilder.RenameIndex(
                name: "ix_asp_net_user_claims_user_id",
                schema: "administration",
                table: "AspNetUserClaims",
                newName: "IX_AspNetUserClaims_UserId");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "role_id",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "RoleId");

            migrationBuilder.RenameColumn(
                name: "claim_value",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "ClaimValue");

            migrationBuilder.RenameColumn(
                name: "claim_type",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "ClaimType");

            migrationBuilder.RenameIndex(
                name: "ix_asp_net_role_claims_role_id",
                schema: "administration",
                table: "AspNetRoleClaims",
                newName: "IX_AspNetRoleClaims_RoleId");

            migrationBuilder.RenameColumn(
                name: "status",
                schema: "administration",
                table: "app_users",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "specialties",
                schema: "administration",
                table: "app_users",
                newName: "Specialties");

            migrationBuilder.RenameColumn(
                name: "email",
                schema: "administration",
                table: "app_users",
                newName: "Email");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "app_users",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "user_name",
                schema: "administration",
                table: "app_users",
                newName: "UserName");

            migrationBuilder.RenameColumn(
                name: "two_factor_enabled",
                schema: "administration",
                table: "app_users",
                newName: "TwoFactorEnabled");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "app_users",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "spoken_languages",
                schema: "administration",
                table: "app_users",
                newName: "SpokenLanguages");

            migrationBuilder.RenameColumn(
                name: "security_stamp",
                schema: "administration",
                table: "app_users",
                newName: "SecurityStamp");

            migrationBuilder.RenameColumn(
                name: "report_to_id",
                schema: "administration",
                table: "app_users",
                newName: "ReportToId");

            migrationBuilder.RenameColumn(
                name: "phone_number_confirmed",
                schema: "administration",
                table: "app_users",
                newName: "PhoneNumberConfirmed");

            migrationBuilder.RenameColumn(
                name: "phone_number",
                schema: "administration",
                table: "app_users",
                newName: "PhoneNumber");

            migrationBuilder.RenameColumn(
                name: "password_hash",
                schema: "administration",
                table: "app_users",
                newName: "PasswordHash");

            migrationBuilder.RenameColumn(
                name: "password_expires_at",
                schema: "administration",
                table: "app_users",
                newName: "PasswordExpiresAt");

            migrationBuilder.RenameColumn(
                name: "normalized_user_name",
                schema: "administration",
                table: "app_users",
                newName: "NormalizedUserName");

            migrationBuilder.RenameColumn(
                name: "normalized_email",
                schema: "administration",
                table: "app_users",
                newName: "NormalizedEmail");

            migrationBuilder.RenameColumn(
                name: "mfa_enabled",
                schema: "administration",
                table: "app_users",
                newName: "MfaEnabled");

            migrationBuilder.RenameColumn(
                name: "lockout_end",
                schema: "administration",
                table: "app_users",
                newName: "LockoutEnd");

            migrationBuilder.RenameColumn(
                name: "lockout_enabled",
                schema: "administration",
                table: "app_users",
                newName: "LockoutEnabled");

            migrationBuilder.RenameColumn(
                name: "last_login_at",
                schema: "administration",
                table: "app_users",
                newName: "LastLoginAt");

            migrationBuilder.RenameColumn(
                name: "is_super_user",
                schema: "administration",
                table: "app_users",
                newName: "IsSuperUser");

            migrationBuilder.RenameColumn(
                name: "is_available",
                schema: "administration",
                table: "app_users",
                newName: "IsAvailable");

            migrationBuilder.RenameColumn(
                name: "hot_leads_count",
                schema: "administration",
                table: "app_users",
                newName: "HotLeadsCount");

            migrationBuilder.RenameColumn(
                name: "full_name",
                schema: "administration",
                table: "app_users",
                newName: "FullName");

            migrationBuilder.RenameColumn(
                name: "failed_login_attempts",
                schema: "administration",
                table: "app_users",
                newName: "FailedLoginAttempts");

            migrationBuilder.RenameColumn(
                name: "enable_notifications",
                schema: "administration",
                table: "app_users",
                newName: "EnableNotifications");

            migrationBuilder.RenameColumn(
                name: "email_confirmed",
                schema: "administration",
                table: "app_users",
                newName: "EmailConfirmed");

            migrationBuilder.RenameColumn(
                name: "deactivated_at",
                schema: "administration",
                table: "app_users",
                newName: "DeactivatedAt");

            migrationBuilder.RenameColumn(
                name: "conversion_rate30d",
                schema: "administration",
                table: "app_users",
                newName: "ConversionRate30D");

            migrationBuilder.RenameColumn(
                name: "concurrency_stamp",
                schema: "administration",
                table: "app_users",
                newName: "ConcurrencyStamp");

            migrationBuilder.RenameColumn(
                name: "agency_id",
                schema: "administration",
                table: "app_users",
                newName: "AgencyId");

            migrationBuilder.RenameColumn(
                name: "active_leads_count",
                schema: "administration",
                table: "app_users",
                newName: "ActiveLeadsCount");

            migrationBuilder.RenameColumn(
                name: "account_type",
                schema: "administration",
                table: "app_users",
                newName: "AccountType");

            migrationBuilder.RenameColumn(
                name: "access_failed_count",
                schema: "administration",
                table: "app_users",
                newName: "AccessFailedCount");

            migrationBuilder.RenameIndex(
                name: "ix_app_users_tenant_id_normalized_email",
                schema: "administration",
                table: "app_users",
                newName: "IX_app_users_TenantId_NormalizedEmail");

            migrationBuilder.RenameIndex(
                name: "ix_app_users_tenant_id_agency_id",
                schema: "administration",
                table: "app_users",
                newName: "IX_app_users_TenantId_AgencyId");

            migrationBuilder.RenameIndex(
                name: "ix_app_users_agency_id",
                schema: "administration",
                table: "app_users",
                newName: "IX_app_users_AgencyId");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "administration",
                table: "app_roles",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "label",
                schema: "administration",
                table: "app_roles",
                newName: "Label");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "app_roles",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "app_roles",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "normalized_name",
                schema: "administration",
                table: "app_roles",
                newName: "NormalizedName");

            migrationBuilder.RenameColumn(
                name: "is_system",
                schema: "administration",
                table: "app_roles",
                newName: "IsSystem");

            migrationBuilder.RenameColumn(
                name: "is_assignable",
                schema: "administration",
                table: "app_roles",
                newName: "IsAssignable");

            migrationBuilder.RenameColumn(
                name: "concurrency_stamp",
                schema: "administration",
                table: "app_roles",
                newName: "ConcurrencyStamp");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "administration",
                table: "agencies",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "description",
                schema: "administration",
                table: "agencies",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "code",
                schema: "administration",
                table: "agencies",
                newName: "Code");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "agencies",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                schema: "administration",
                table: "agencies",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "territory_id",
                schema: "administration",
                table: "agencies",
                newName: "TerritoryId");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "agencies",
                newName: "TenantId");

            migrationBuilder.RenameColumn(
                name: "parent_agency_id",
                schema: "administration",
                table: "agencies",
                newName: "ParentAgencyId");

            migrationBuilder.RenameColumn(
                name: "is_deleted",
                schema: "administration",
                table: "agencies",
                newName: "IsDeleted");

            migrationBuilder.RenameColumn(
                name: "is_active",
                schema: "administration",
                table: "agencies",
                newName: "IsActive");

            migrationBuilder.RenameColumn(
                name: "created_by",
                schema: "administration",
                table: "agencies",
                newName: "CreatedBy");

            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "administration",
                table: "agencies",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "agency_type",
                schema: "administration",
                table: "agencies",
                newName: "AgencyType");

            migrationBuilder.RenameIndex(
                name: "ix_agencies_territory_id",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TerritoryId");

            migrationBuilder.RenameIndex(
                name: "ix_agencies_tenant_id_parent_agency_id",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TenantId_ParentAgencyId");

            migrationBuilder.RenameIndex(
                name: "ix_agencies_tenant_id_is_deleted",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TenantId_IsDeleted");

            migrationBuilder.RenameIndex(
                name: "ix_agencies_tenant_id_code",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_TenantId_Code");

            migrationBuilder.RenameIndex(
                name: "ix_agencies_parent_agency_id",
                schema: "administration",
                table: "agencies",
                newName: "IX_agencies_ParentAgencyId");

            migrationBuilder.RenameColumn(
                name: "name",
                schema: "administration",
                table: "ProductSpecialities",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "code",
                schema: "administration",
                table: "ProductSpecialities",
                newName: "Code");

            migrationBuilder.RenameColumn(
                name: "id",
                schema: "administration",
                table: "ProductSpecialities",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "tenant_id",
                schema: "administration",
                table: "ProductSpecialities",
                newName: "TenantId");

            migrationBuilder.RenameIndex(
                name: "ix_product_specialities_tenant_id_code",
                schema: "administration",
                table: "ProductSpecialities",
                newName: "IX_ProductSpecialities_TenantId_Code");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_roles",
                schema: "administration",
                table: "user_roles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_profile",
                schema: "administration",
                table: "user_profile",
                column: "additional_email");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_login_locations",
                schema: "administration",
                table: "user_login_locations",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_territories",
                schema: "administration",
                table: "territories",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_tenant_notification_settings",
                schema: "administration",
                table: "tenant_notification_settings",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_role_permissions",
                schema: "administration",
                table: "role_permissions",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_permissions",
                schema: "administration",
                table: "permissions",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_permission_attributions",
                schema: "administration",
                table: "permission_attributions",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_password_histories",
                schema: "administration",
                table: "password_histories",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_outbox_messages",
                schema: "administration",
                table: "outbox_messages",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetUserTokens",
                schema: "administration",
                table: "AspNetUserTokens",
                columns: new[] { "UserId", "LoginProvider", "Name" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetUserRoles",
                schema: "administration",
                table: "AspNetUserRoles",
                columns: new[] { "UserId", "RoleId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetUserLogins",
                schema: "administration",
                table: "AspNetUserLogins",
                columns: new[] { "LoginProvider", "ProviderKey" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetUserClaims",
                schema: "administration",
                table: "AspNetUserClaims",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AspNetRoleClaims",
                schema: "administration",
                table: "AspNetRoleClaims",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_users",
                schema: "administration",
                table: "app_users",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_app_roles",
                schema: "administration",
                table: "app_roles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_agencies",
                schema: "administration",
                table: "agencies",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ProductSpecialities",
                schema: "administration",
                table: "ProductSpecialities",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_agencies_agencies_ParentAgencyId",
                schema: "administration",
                table: "agencies",
                column: "ParentAgencyId",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_agencies_territories_TerritoryId",
                schema: "administration",
                table: "agencies",
                column: "TerritoryId",
                principalSchema: "administration",
                principalTable: "territories",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_app_users_agencies_AgencyId",
                schema: "administration",
                table: "app_users",
                column: "AgencyId",
                principalSchema: "administration",
                principalTable: "agencies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetRoleClaims_app_roles_RoleId",
                schema: "administration",
                table: "AspNetRoleClaims",
                column: "RoleId",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserClaims_app_users_UserId",
                schema: "administration",
                table: "AspNetUserClaims",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserLogins_app_users_UserId",
                schema: "administration",
                table: "AspNetUserLogins",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserRoles_app_roles_RoleId",
                schema: "administration",
                table: "AspNetUserRoles",
                column: "RoleId",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserRoles_app_users_UserId",
                schema: "administration",
                table: "AspNetUserRoles",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUserTokens_app_users_UserId",
                schema: "administration",
                table: "AspNetUserTokens",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_password_histories_app_users_UserId",
                schema: "administration",
                table: "password_histories",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_permission_attributions_app_users_UserId",
                schema: "administration",
                table: "permission_attributions",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_role_permissions_app_roles_RoleId",
                schema: "administration",
                table: "role_permissions",
                column: "RoleId",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_role_permissions_permissions_PermissionId",
                schema: "administration",
                table: "role_permissions",
                column: "PermissionId",
                principalSchema: "administration",
                principalTable: "permissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_login_locations_app_users_UserId",
                schema: "administration",
                table: "user_login_locations",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_profile_app_users_UserId",
                schema: "administration",
                table: "user_profile",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_app_roles_RoleId",
                schema: "administration",
                table: "user_roles",
                column: "RoleId",
                principalSchema: "administration",
                principalTable: "app_roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_user_roles_app_users_UserId",
                schema: "administration",
                table: "user_roles",
                column: "UserId",
                principalSchema: "administration",
                principalTable: "app_users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
