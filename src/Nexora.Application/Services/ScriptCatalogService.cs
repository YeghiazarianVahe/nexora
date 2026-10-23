using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Domain;

namespace Nexora.Application.Services;

public interface IScriptCatalogService
{
    Task<IReadOnlyList<ScriptDto>> ListAsync(bool enabledOnly, CancellationToken ct = default);
    Task<Result<ScriptDto>> CreateAsync(Actor actor, ScriptInput input, CancellationToken ct = default);
    Task<Result<ScriptDto>> UpdateAsync(Actor actor, Guid id, ScriptInput input, CancellationToken ct = default);
    Task<Result> DeleteAsync(Actor actor, Guid id, CancellationToken ct = default);
}

public sealed class ScriptCatalogService(INexoraDb db, IAuditService audit, ISystemClock clock) : IScriptCatalogService
{
    public async Task<IReadOnlyList<ScriptDto>> ListAsync(bool enabledOnly, CancellationToken ct = default)
    {
        IQueryable<ScriptDefinition> q = db.Scripts.AsNoTracking();
        if (enabledOnly) q = q.Where(s => s.Enabled);
        return (await q.OrderBy(s => s.Name).ToListAsync(ct)).Select(s => s.ToDto()).ToList();
    }

    public async Task<Result<ScriptDto>> CreateAsync(Actor actor, ScriptInput input, CancellationToken ct = default)
    {
        if (Validate(input) is { } err) return Result<ScriptDto>.Fail(ErrorKind.Validation, err);
        if (await db.Scripts.AnyAsync(s => s.Name == input.Name.Trim(), ct))
            return Result<ScriptDto>.Fail(ErrorKind.Conflict, "A script with that name already exists.");

        var now = clock.UtcNow;
        var script = new ScriptDefinition
        {
            Id = Guid.NewGuid(), Name = input.Name.Trim(), Description = input.Description.Trim(),
            Body = input.Body, Sha256 = Hash(input.Body), Enabled = input.Enabled,
            CreatedBySid = actor.Sid, CreatedUtc = now, UpdatedUtc = now,
        };
        db.Scripts.Add(script);
        audit.Add(actor, "script.create", "Script", script.Id.ToString(), null, script.Sha256, new { script.Name });
        await db.SaveChangesAsync(ct);
        return Result<ScriptDto>.Success(script.ToDto());
    }

    public async Task<Result<ScriptDto>> UpdateAsync(Actor actor, Guid id, ScriptInput input, CancellationToken ct = default)
    {
        if (Validate(input) is { } err) return Result<ScriptDto>.Fail(ErrorKind.Validation, err);
        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (script is null) return Result<ScriptDto>.Fail(ErrorKind.NotFound, "Script not found.");
        if (await db.Scripts.AnyAsync(s => s.Name == input.Name.Trim() && s.Id != id, ct))
            return Result<ScriptDto>.Fail(ErrorKind.Conflict, "A script with that name already exists.");

        var oldHash = script.Sha256;
        script.Name = input.Name.Trim();
        script.Description = input.Description.Trim();
        script.Body = input.Body;
        script.Sha256 = Hash(input.Body);
        script.Enabled = input.Enabled;
        script.UpdatedUtc = clock.UtcNow;
        audit.Add(actor, "script.update", "Script", id.ToString(), oldHash, script.Sha256, new { script.Name, script.Enabled });
        await db.SaveChangesAsync(ct);
        return Result<ScriptDto>.Success(script.ToDto());
    }

    public async Task<Result> DeleteAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        var script = await db.Scripts.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (script is null) return Result.Fail(ErrorKind.NotFound, "Script not found.");
        db.Scripts.Remove(script);
        audit.Add(actor, "script.delete", "Script", id.ToString(), script.Sha256, null, new { script.Name });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string? Validate(ScriptInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 100) return "Name is required (max 100 characters).";
        if (i.Description is { Length: > 500 }) return "Description is too long (max 500).";
        if (string.IsNullOrWhiteSpace(i.Body) || i.Body.Length > 20_000) return "Body is required (max 20000 characters).";
        return null;
    }
}
