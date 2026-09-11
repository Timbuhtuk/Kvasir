using Entities;
using System.Linq.Expressions;

namespace Application.Interfaces;

/// <summary>
/// Generic repository interface for CRUD operations
/// </summary>
public interface IGenericRepository<T> where T : class, IEntity
{
    /// <summary>
    /// Returns a queryable collection of entities
    /// </summary>
    /// <returns>Queryable collection of entities</returns>
    IQueryable<T> Query();

    /// <summary>
    /// Gets an entity by its ID
    /// </summary>
    /// <param name="id">Entity ID</param>
    /// <returns>Entity if found, null otherwise</returns>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    Task<T?> GetByIdAsync(int? id);

    /// <summary>
    /// Gets all entities as a queryable collection
    /// </summary>
    /// <returns>Queryable collection of all entities</returns>
    Task<IQueryable<T>> GetAllAsync();

    /// <summary>
    /// Adds a new entity to the database
    /// </summary>
    /// <param name="entity">Entity to add</param>
    /// <returns>The added entity with generated ID</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when entity is null</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">Thrown when concurrency conflict occurs</exception>
    Task<T> AddAsync(T entity);

    /// <summary>
    /// Updates an existing entity in the database
    /// </summary>
    /// <param name="entity">Entity to update</param>
    /// <returns>The updated entity</returns>
    /// <exception cref="System.ArgumentNullException">Thrown when entity is null</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">Thrown when concurrency conflict occurs</exception>
    Task<T> UpdateAsync(T entity);

    /// <summary>
    /// Removes an entity from the database
    /// </summary>
    /// <param name="entity">Entity to remove</param>
    /// <exception cref="System.ArgumentNullException">Thrown when entity is null</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">Thrown when concurrency conflict occurs</exception>
    Task RemoveAsync(T entity);

    /// <summary>
    /// Removes multiple entities from the database
    /// </summary>
    /// <param name="entities">List of entities to remove</param>
    /// <exception cref="System.ArgumentNullException">Thrown when entities is null</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">Thrown when concurrency conflict occurs</exception>
    Task RemoveRangeAsync(List<T> entities);

    /// <summary>
    /// Saves all pending changes to the database
    /// </summary>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateException">Thrown when database operation fails</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">Thrown when concurrency conflict occurs</exception>
    Task SaveChangesAsync();
}

