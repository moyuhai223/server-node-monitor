using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SNM.Master.Data;

/// <summary>Used only by `dotnet ef` at design time (migrations); the runtime path is configured in Program.cs.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SnmDbContext>
{
    public SnmDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "snm-design-time.db");
        var options = new DbContextOptionsBuilder<SnmDbContext>().UseSqlite($"Data Source={path}").Options;
        return new SnmDbContext(options);
    }
}
