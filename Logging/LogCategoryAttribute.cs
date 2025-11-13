using Entities.Enums;

namespace Logging;

/// <summary>
/// Атрибут для указания категории логов, генерируемых классом
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = true)]
public class LogCategoryAttribute : Attribute
{
    /// <summary>
    /// Категория логов для класса
    /// </summary>
    public LogCategory Category { get; }

    /// <summary>
    /// Конструктор атрибута
    /// </summary>
    /// <param name="category">Категория логов</param>
    public LogCategoryAttribute(LogCategory category)
    {
        Category = category;
    }
}

