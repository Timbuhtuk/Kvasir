using Application.Interfaces;
using Entities;
using Logging;
using System.Linq.Expressions;

namespace Infrastructure.Repository;

/// <summary>
/// Generic repository implementation for CRUD operations
/// </summary>
public class GenericRepository<T>(DiscordMusicDBContext context) : IGenericRepository<T>
    where T : class, IEntity
{
    protected readonly DiscordMusicDBContext context = context;

    public IQueryable<T> Query() =>
        context.Set<T>().AsQueryable();

    public virtual async Task<T?> GetByIdAsync(int? id) =>
        await context.Set<T>().FindAsync(id);

    public virtual async Task<IQueryable<T>> GetAllAsync() =>
        await Task.FromResult(context.Set<T>().AsQueryable());

    public virtual async Task<T> AddAsync(T entity) {
        try
        {
            await context.Set<T>().AddAsync(entity);
            await context.SaveChangesAsync();
            string entityName = typeof(T).Name;
            await Logger.AddLog($"{entityName} (id: {entity.Id}) added");
            return entity;
        }
        catch (Exception ex)
        {
            string entityName = typeof(T).Name;
            await Logger.AddLog($"Error adding {entityName}: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            throw;
        }
    }

    public virtual async Task<T> UpdateAsync(T entity) {
        try
        {
            context.Set<T>().Update(entity);
            await context.SaveChangesAsync();
            string entityName = typeof(T).Name;
            await Logger.AddLog($"{entityName} (id: {entity.Id}) updated");
            return entity;
        }
        catch (Exception ex)
        {
            string entityName = typeof(T).Name;
            await Logger.AddLog($"Error updating {entityName} (id: {entity.Id}): {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            throw;
        }
    }

    public virtual async Task RemoveAsync(T entity) {
        try
        {
            string entityName = typeof(T).Name;
            int entityId = entity.Id;
            context.Set<T>().Remove(entity);
            await context.SaveChangesAsync();
            await Logger.AddLog($"{entityName} (id: {entityId}) removed");
        }
        catch (Exception ex)
        {
            string entityName = typeof(T).Name;
            await Logger.AddLog($"Error removing {entityName} (id: {entity.Id}): {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            //throw;
        }
    }

    public virtual async Task RemoveRangeAsync(List<T> entities) {
        try
        {
            string entityName = typeof(T).Name;
            int count = entities.Count;
            context.Set<T>().RemoveRange(entities);
            await context.SaveChangesAsync();
            await Logger.AddLog($"{count} {entityName} entities removed");
        }
        catch (Exception ex)
        {
            string entityName = typeof(T).Name;
            await Logger.AddLog($"Error removing {entities.Count} {entityName} entities: {ex.Message}", Microsoft.Extensions.Logging.LogLevel.Error, exception: ex);
            throw;
        }
    }

    public virtual async Task SaveChangesAsync() =>
        await context.SaveChangesAsync();
}
