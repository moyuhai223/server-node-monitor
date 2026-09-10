using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SNM.Master.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NickName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Avatar = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TokenVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedLogins = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PasswordChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Metrics1d",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Samples = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvg = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuMax = table.Column<int>(type: "INTEGER", nullable: false),
                    MemUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    MemUsedMaxMb = table.Column<long>(type: "INTEGER", nullable: false),
                    SwapUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskUsedMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskTotalMb = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Load1Avg = table.Column<int>(type: "INTEGER", nullable: false),
                    Load1Max = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metrics1d", x => new { x.NodeId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "Metrics1h",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Samples = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvg = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuMax = table.Column<int>(type: "INTEGER", nullable: false),
                    MemUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    MemUsedMaxMb = table.Column<long>(type: "INTEGER", nullable: false),
                    SwapUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskUsedMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskTotalMb = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Load1Avg = table.Column<int>(type: "INTEGER", nullable: false),
                    Load1Max = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metrics1h", x => new { x.NodeId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "Metrics1m",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Ts = table.Column<long>(type: "INTEGER", nullable: false),
                    Samples = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuAvg = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuMax = table.Column<int>(type: "INTEGER", nullable: false),
                    MemUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    MemUsedMaxMb = table.Column<long>(type: "INTEGER", nullable: false),
                    SwapUsedAvgMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskUsedMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DiskTotalMb = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsAvg = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBpsMax = table.Column<long>(type: "INTEGER", nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Load1Avg = table.Column<int>(type: "INTEGER", nullable: false),
                    Load1Max = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Metrics1m", x => new { x.NodeId, x.Ts });
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AdminRemark = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AgentKey = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    KeyRotatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CountryCodeOverride = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    CountryCodeAuto = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IntervalMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Hostname = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Os = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Kernel = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Arch = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CpuModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CpuCores = table.Column<int>(type: "INTEGER", nullable: false),
                    MemTotalMb = table.Column<long>(type: "INTEGER", nullable: false),
                    SwapTotalMb = table.Column<long>(type: "INTEGER", nullable: false),
                    DisksJson = table.Column<string>(type: "TEXT", nullable: true),
                    NetIfs = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Virt = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AgentVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    BootTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRegisterAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRemoteIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TrafficLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TrafficResetDay = table.Column<int>(type: "INTEGER", nullable: false),
                    TrafficCountMode = table.Column<int>(type: "INTEGER", nullable: false),
                    Vendor = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Price = table.Column<decimal>(type: "TEXT", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    BillingCycleMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AutoRenew = table.Column<bool>(type: "INTEGER", nullable: false),
                    RenewUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    AlertsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CpuAlertPct = table.Column<int>(type: "INTEGER", nullable: true),
                    TrafficAlertPct = table.Column<int>(type: "INTEGER", nullable: true),
                    OfflineAlertSec = table.Column<int>(type: "INTEGER", nullable: true),
                    DiskAlertPct = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    RuleMask = table.Column<int>(type: "INTEGER", nullable: false),
                    MinSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTestAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TrafficDaily",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficDaily", x => new { x.NodeId, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "TrafficMonthly",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BilledBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CountMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ResetDay = table.Column<int>(type: "INTEGER", nullable: false),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficMonthly", x => new { x.NodeId, x.PeriodStart });
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 44, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReplacedByHash = table.Column<string>(type: "TEXT", maxLength: 44, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_AdminUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AlertEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Rule = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Value = table.Column<double>(type: "REAL", nullable: false),
                    Threshold = table.Column<double>(type: "REAL", nullable: false),
                    DedupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Notified = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertEvents_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AlertStates",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rule = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Consecutive = table.Column<int>(type: "INTEGER", nullable: false),
                    ResolveCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FiringSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CooldownUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastValue = table.Column<double>(type: "REAL", nullable: false),
                    OpenEventId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertStates", x => new { x.NodeId, x.Rule, x.Subject });
                    table.ForeignKey(
                        name: "FK_AlertStates_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstallTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 43, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstallTokens_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NodeIps",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    Family = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPublic = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeIps", x => new { x.NodeId, x.Address });
                    table.ForeignKey(
                        name: "FK_NodeIps_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrafficState",
                columns: table => new
                {
                    NodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    PrevRx = table.Column<long>(type: "INTEGER", nullable: false),
                    PrevTx = table.Column<long>(type: "INTEGER", nullable: false),
                    PrevAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BootTimeAtPrevUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PrevConnectionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrafficState", x => x.NodeId);
                    table.ForeignKey(
                        name: "FK_TrafficState_Nodes_NodeId",
                        column: x => x.NodeId,
                        principalTable: "Nodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChannelName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Ok = table.Column<bool>(type: "INTEGER", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ElapsedMs = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_AlertEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "AlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotificationDeliveries_NotificationChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "NotificationChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Username",
                table: "AdminUsers",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_NodeId_StartedAt",
                table: "AlertEvents",
                columns: new[] { "NodeId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_StartedAt",
                table: "AlertEvents",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_Status",
                table: "AlertEvents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InstallTokens_NodeId",
                table: "InstallTokens",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_InstallTokens_Token",
                table: "InstallTokens",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Metrics1d_Ts",
                table: "Metrics1d",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_Metrics1h_Ts",
                table: "Metrics1h",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_Metrics1m_Ts",
                table: "Metrics1m",
                column: "Ts");

            migrationBuilder.CreateIndex(
                name: "IX_NodeIps_LastSeenAt",
                table: "NodeIps",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_SortOrder",
                table: "Nodes",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "UX_Nodes_AgentKey",
                table: "Nodes",
                column: "AgentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Nodes_PublicName",
                table: "Nodes",
                column: "PublicName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_ChannelId",
                table: "NotificationDeliveries",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_CreatedAt",
                table: "NotificationDeliveries",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDeliveries_EventId",
                table: "NotificationDeliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertStates");

            migrationBuilder.DropTable(
                name: "InstallTokens");

            migrationBuilder.DropTable(
                name: "Metrics1d");

            migrationBuilder.DropTable(
                name: "Metrics1h");

            migrationBuilder.DropTable(
                name: "Metrics1m");

            migrationBuilder.DropTable(
                name: "NodeIps");

            migrationBuilder.DropTable(
                name: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TrafficDaily");

            migrationBuilder.DropTable(
                name: "TrafficMonthly");

            migrationBuilder.DropTable(
                name: "TrafficState");

            migrationBuilder.DropTable(
                name: "AlertEvents");

            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropTable(
                name: "AdminUsers");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
