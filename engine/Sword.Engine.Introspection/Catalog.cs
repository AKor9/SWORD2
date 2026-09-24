using Sword.Engine.Ecs;

namespace Sword.Engine.Introspection;

/// <summary>
/// Имена данных мира для агента и инструментов: компоненты, события, ресурсы и команды тика — по полному (стабильному)
/// имени или короткому (имя типа, без учёта регистра), если оно однозначно. Состав типов после сборки мира не меняется,
/// поэтому каталог собирается один раз (на потоке симуляции) и дальше читается с любого потока.
/// </summary>
public sealed class Catalog
{
    public Catalog(WorldReader world, IReadOnlyList<Type> commandTypes, IReadOnlyList<Runtime.TuningKnob>? tuning = null)
    {
        Components = world.Components;
        Events = world.Events;
        Resources = world.Resources;
        Commands = commandTypes;
        Stages = world.Stages;
        Tuning = tuning ?? [];
    }

    /// <summary>Имена данных мира симуляции — с её командами и ручками.</summary>
    public Catalog(Runtime.Simulation simulation) : this(simulation.World, simulation.CommandTypes, simulation.Tuning)
    {
    }

    /// <summary>
    /// Ручки симуляции (6е; <c>world.types</c> — у ресурса, в котором живут: имя, поле, диапазон, значение по умолчанию; изменение —
    /// <c>command.send TuningCommand</c>).
    /// </summary>
    public IReadOnlyList<Runtime.TuningKnob> Tuning { get; }

    public IReadOnlyList<DataTypeInfo> Components { get; }
    public IReadOnlyList<DataTypeInfo> Events { get; }
    public IReadOnlyList<ResourceInfo> Resources { get; }
    public IReadOnlyList<Type> Commands { get; }
    public IReadOnlyList<string> Stages { get; }

    /// <summary>Компонент по имени.</summary>
    /// <exception cref="ArgumentException">Нет такого или короткое имя неоднозначно — с перечнем.</exception>
    public DataTypeInfo Component(string name) => Resolve(Components, c => c.Type, name, "компонента");

    /// <summary>Команда тика по имени.</summary>
    public Type Command(string name) => Resolve(Commands, t => t, name, "команды тика");

    /// <summary>Ресурс по имени.</summary>
    public ResourceInfo Resource(string name) => Resolve(Resources, r => r.Type, name, "ресурса");

    /// <summary>Короткое имя для людей: имя типа, у обобщённого — с аргументами (<c>Added&lt;Fighter&gt;</c>).</summary>
    public static string ShortName(Type t) =>
        t.IsGenericType && t.Name.IndexOf('`') is > 0 and var tick
            ? $"{t.Name[..tick]}<{string.Join(", ", t.GetGenericArguments().Select(ShortName))}>"
            : t.Name;   // вложенный в обобщённый тип без своих параметров — просто имя

    /// <summary>
    /// Имя компонента для ответов: короткое, если оно у компонентов единственное, иначе полное — два компонента
    /// с одним коротким именем не затирают друг друга в ответе.
    /// </summary>
    public string DisplayName(DataTypeInfo component) =>
        Components.Count(c => ShortName(c.Type) == ShortName(component.Type)) == 1 ? ShortName(component.Type) : component.Name;

    private static T Resolve<T>(IReadOnlyList<T> all, Func<T, Type> type, string name, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        foreach (var item in all)
            if (string.Equals(type(item).FullName, name, StringComparison.Ordinal)) return item;
        var matches = all.Where(i => string.Equals(ShortName(type(i)), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type(i).FullName, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            throw new ArgumentException($"имя {what} «{name}» неоднозначно: {string.Join(", ", matches.Select(m => type(m).FullName))}");
        throw new ArgumentException($"нет {what} «{name}»; есть: {string.Join(", ", all.Select(i => ShortName(type(i))))}");
    }
}
