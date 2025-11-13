using Entities;
using System.Linq.Expressions;

namespace Application.Interfaces;

/// <summary>
/// Generic repository interface for CRUD operations
/// </summary>
public interface IGenericRepository<T> where T : class, IEntity
{
    IQueryable<T> Query();

    Task<T?> GetByIdAsync(int? id);

    Task<IQueryable<T>> GetAllAsync();

    Task<T> AddAsync(T entity);

    Task<T> UpdateAsync(T entity);

    Task RemoveAsync(T entity);

    Task RemoveRangeAsync(List<T> entities);

    Task SaveChangesAsync();
}

